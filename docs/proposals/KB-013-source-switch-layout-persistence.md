# KB-013：来源切换后的布局记忆（分组丢失问题）

状态：待 owner 批准（提案）
日期：2026-11-12
相关：ADR-005（每个播放列表独立记忆显示格式）、KB-009（分组智能判定）、KB-012（分组封面绑定）

## 1. 背景与症状

owner 反馈（2026-11-12）：

> 已分组显示的播放列表，在切换到其他列表（比如我这里是切换到默认播放列表），然后再切回来时，会丢失分组显示。我希望各个播放列表能较为稳定地记忆其显示状态。

截图对比：切换前媒体库按专辑分组（左侧封面栏、每组歌曲行）；切到默认播放列表再切回后，同一媒体库变成扁平列表，且列布局也变成了播放列表的默认列（没有“专辑艺术家”列）。

现场 `settings.json` 证据：`libraryLayout.sortColumn` 被写成了空字符串（分组关闭），`groupCoverSize=96`；`library.db` 中默认播放列表的 `ViewLayoutJson` 反而变成了 `sortColumn: "Album"` 的分组布局——也就是说两个来源的布局互相被写乱了。

## 2. 根因

1. 左侧来源列表使用双向绑定：
   `MainWindow.xaml:423` `SelectedItem="{Binding SelectedSource, Mode=TwoWay}"`，另有 `SelectionChanged="OnSourceSelectionChanged"`。
   用户点击某个来源时，WPF 先通过绑定把 `SelectedSource` 更新成**新来源**，随后才触发 `OnSelectedSourceChanged` → `SelectSourceAsync`。
2. 旧代码 `MainViewModel.SelectSourceAsync` 在切换前调用 `PersistCurrentLayout()`，其中保存目标是按 `SelectedSource` 判定的——此时它已经是**新来源**，于是**旧视图的布局被写进新来源**（布局“错位”）。
3. 切进播放列表时，`SelectSourceAsync` 用 `ViewLayoutFactory.Deserialize(source.Playlist.ViewLayoutJson)` 读取布局，而 `UpdatePlaylistLayoutAsync` 只写数据库、不更新内存里的 `Playlist` 对象；因此下次再进入该播放列表时读到的仍是旧值（从未配置过的播放列表是 `null` → `CreateDefault()` 的扁平布局）。
4. 连锁结果：媒体库（分组 Album）→ 默认播放列表（内存布局为扁平默认）→ 切回媒体库时，把扁平布局写进 `Settings.LibraryLayout`，分组就此丢失；同时播放列表的数据库行被写入了媒体库的分组布局。

日志特征（owner 日志，修复前）：每次切换都会出现
`Persist layout: captured=null captureFromView=True selected=<新来源>`，随后
`Layout changed` ×2；切回媒体库时 `Centered now-playing row: view=flat ... extent=90400.0`（扁平 2825 行 × 32 DIP），说明分组确实已丢失。

## 3. 修复

- `MainViewModel` 新增字段 `_activeSource`：记录**当前实际显示中**的来源，与 `SelectedSource`（已经被绑定提前改成新来源）分离。
- `SelectSourceAsync`：切换前使用 `PersistCurrentLayout(null, destination: _activeSource)` 显式把旧布局保存到旧来源；加载成功并 `ApplyTracks` 后设置 `_activeSource = source`。
- `PersistCurrentLayout` 新增可选参数 `destination`（为空时回退 `_activeSource ?? SelectedSource`）；保存播放列表布局时，在写数据库的同时把序列化 JSON 回写到内存 `Playlist.ViewLayoutJson`（write-through），保证再次进入该列表时读到最新布局。
- `ReloadSourcesAsync` 在重建来源节点后，用 `Matches` 把 `_activeSource` 重新绑定到新节点实例，避免节点被替换后布局仍然写到旧对象。
- 保存时机保持不变：切换来源、修改排序、列编辑 / 应用预设、拖动分组封面尺寸、关闭窗口；媒体库与文件夹共用 `Settings.LibraryLayout`，每个播放列表各自写 `ViewLayoutJson`（符合 ADR-005）。
- 日志字段由 `selected=<来源>` 改为 `source=<保存目标来源>`，便于以后排查布局错位。

## 4. 验证

隔离数据根 `.scratch\switch-bug`（owner 配置副本；`volume=0`、自动扫描关闭；`libraryLayout.sortColumn="Album"`；默认播放列表 `ViewLayoutJson` 置空）：

- 修复前：媒体库（分组）→ 默认播放列表 → 全部音乐，最后 `GroupedList` 消失、`settings.sortColumn=''`、日志 `view=flat ... extent=90400.0`（稳定复现 owner 症状）。
- 修复后：同一流程最后 `GroupedList` 存在（33 个分组）、`settings.sortColumn='Album'`、日志 `Centered now-playing row: view=grouped ... extent=211156.0`。
- 播放列表自己的记忆：在默认播放列表点击“专辑”表头后，日志为
  `Persist layout: captured=provided ... source=Playlist` 与 `captured=null ... source=Playlist`；
  切到媒体库再切回播放列表，播放列表仍是分组视图；`library.db` 中该播放列表的 `ViewLayoutJson` 含 `"sortColumn": "Album"`。
- 构建：0 警告 0 错误；单元测试：82 项 = 81 通过 / 1 跳过（HKCU 注册表）/ 0 失败。

## 5. 局限与后续

- 只记忆布局状态（列可见性、宽度、顺序、排序、分组封面尺寸、组内碟片-曲目排序），**不记忆滚动位置与选中项**；如需要可作为后续需求。
- 文件监听触发的 `RefreshCurrentSourceAsync` 仍以 `SelectedSource` 取来源，切换瞬间可能刷新到新来源的数据（不会破坏布局持久化，但列表内容可能短暂替换）；后续可加“切换中跳过”保护。
- 扁平视图点击“专辑 / 专辑艺术家”表头会按 KB-009 的规则启用分组；这是预期行为，不是布局丢失。
