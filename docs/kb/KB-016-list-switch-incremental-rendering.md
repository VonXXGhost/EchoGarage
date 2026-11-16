# KB-016 列表切换性能：行单元 / 次要文本延迟物化 + 批量 Reset

- 状态：**Accepted**（owner 批准：2026-11-16）
- 日期：2026-11-16
- 相关：`MainViewModel.ApplyTracks` / `ApplySortAndGrouping` / `RebuildGroupedView`、
  `TrackRowViewModel`、新增 `BulkObservableCollection<T>`、分组列表虚拟化（KB-009 / KB-010）、
  行次要信息（ADR-013）、`GroupedColumnsRebuilt` 表头填充流程

## 1. 背景与现状问题

列表 / 播放列表切换在媒体库达到数千行时出现明显停顿，即便窗口里只显示几十行。热点有三处：

1. **`ApplyTracks` 逐行写入**：`Tracks.Clear()` + 每行一次 `Tracks.Add(row)`，共 `N+1` 次
   `CollectionChanged`。`Tracks` 同时被平铺 `ListCollectionView`（`TracksView`）与分组
   `ListCollectionView`（`GroupedTracksView`）监听，每次通知都会让两个视图重新排序 / 过滤 /
   生成容器，切换成本随行数非线性放大。
2. **`RebuildGroupedView` 为全部行急切创建单元**：每次布局 / 分组 / 来源变化，都会对
   `Tracks` 里的**每一行**（包括从未显示、被虚拟化回收的行）创建 `Cells` 与 `RightCells`，
   每行每列一个 `TrackCellViewModel`（`N × 可见列数` 个对象），随后再赋值给行。分组 `ListView`
   实际只物化屏幕内的少量行，其余分配全是浪费。布局写入路径还经常连续触发两次重建
   （`CurrentLayout` 替换 → `OnCurrentLayoutChanged` 一次，随后的 `ApplySortAndGrouping` 又一次），
   造成双倍分配。
3. **`ApplySecondaryTexts` 急切计算全部行**：为所有行调用 `ApplySecondaryFormats`，立即执行
   `BuildTokenValues()`（每行约 20 个键的字典）并对每个可见列做格式模板展开，即使该行从未显示；
   每次重建都重复一遍。

结构性目标：把 `O(N × 列数)` 的行内对象分配与文本格式化降为
`O(实际显示行数 × 列数)`，并把 `N+1` 次集合通知合并为 1 次，同时不改变排序 / 分组 / 过滤 /
选中恢复 / 正在播放标记 / 封面按需加载 / 表头填充等既有行为。

## 2. 设计

### 2.1 `BulkObservableCollection<T>.ReplaceAll`

新增 `src/EchoGarage.App/Services/BulkObservableCollection.cs`：

- `sealed class BulkObservableCollection<T> : ObservableCollection<T>`；
- `ReplaceAll(IEnumerable<T> items)`：
  - 先物化输入（`items as IList<T> ?? items.ToList()`），并处理“把自己传进来”的情况；
  - `CheckReentrancy()` 后直接改 `Items`（`Clear` + `Add` 循环，不加锁、不发中间通知）；
  - 最后只发一次 `PropertyChanged(nameof(Count))` + `PropertyChanged("Item[]")` +
    `CollectionChanged(Reset)`。
- `Tracks` 属性类型由 `ObservableCollection<TrackRowViewModel>` 改为
  `BulkObservableCollection<TrackRowViewModel>`。属性的 get-only 实例语义不变：
  `TracksView` / `GroupedTracksView` 仍绑定同一个实例，不替换引用。
- `ApplyTracks` 先在局部 `List<TrackRowViewModel>` 中创建全部行（保留正在播放行的识别、
  `CurrentRow` 指向、`UpdateNowPlayingMetadata`、`BeginNowPlayingCoverLoad`），再一次性
  `Tracks.ReplaceAll(rows)`；`_tracksVersion++` 使分组重建签名必然变化。

### 2.2 行单元（`Cells` / `RightCells`）延迟物化

`TrackRowViewModel`：

- `Cells` / `RightCells` 改为**只读 getter**：首次被绑定访问时按当前 `GroupedCellLayout`
  构建并缓存（`_cells ??= _cellLayout.BuildCells(this, right: false)`）。未被显示的行永远不分配。
- 新增 `internal void SetCellLayout(GroupedCellLayout layout)`：
  - 与当前布局引用相同 → 直接返回（无变化零开销）；
  - 记录共享布局引用；只有该行**曾经被绑定过**（`_cellsObserved` / `_rightCellsObserved`）
    才清缓存并 `OnPropertyChanged(nameof(Cells))` / `nameof(RightCells)`，让已显示的容器重读；
    从未显示的行只更新一个引用，不发通知、不分配。
- 新增 `internal bool HasResolvedCells`：供日志统计“实际物化行数”，用于验证延迟生效。

新增 `internal sealed class GroupedCellLayout`（与 `TrackRowViewModel` 同文件）：

- 不可变快照：左流 / 右固定块的 `GroupedColumnHeaderViewModel` 数组 + 对应
  `DataTemplate` 数组；`static Empty` 共享实例；
- `BuildCells(row, right)` 只在调用时创建该行的 `TrackCellViewModel` 数组；
  无列时返回共享的 `Array.Empty<TrackCellViewModel>()`。

### 2.3 次要文本延迟计算

`TrackRowViewModel`：

- `SecondaryTexts` 改为 getter 缓存：`_secondaryTexts ??= BuildSecondaryTexts()`；
  内容为空时返回共享静态空字典；未被显示的行不执行 `BuildTokenValues` 与模板展开。
- `ApplySecondaryFormats(formats)`：与当前格式表**引用相同则短路**；否则仅记录引用、清缓存，
  且只在已观察行上发 `OnPropertyChanged(nameof(SecondaryTexts))`。
- `UpdateTrack(track)`：清文本缓存后发空属性名通知（一次通知让所有绑定重读并按需重建），
  不再立即重算；行单元只持有行 / 表头引用，不需要失效。

`MainViewModel.ResolveSecondaryFormats()`：按当前布局生成次要格式表，并把
`key\u001f format` 拼接为签名；签名未变时**复用同一字典实例**，使“无关的排序 / 布局刷新”
不再让所有行失效。

### 2.4 分组重建去重（失效签名）

`MainViewModel.RebuildGroupedView()` 开头计算 `BuildGroupedRebuildSignature(grouped)`：

```
_tracksVersion | G/F | ShowSecondaryInfo | SortColumn | SortDescending | GroupSortByDiscTrack
| 每列: Key : 可见 : Pin : Width(不变文化) : SecondaryFormat
```

与上次签名相同直接返回。签名覆盖重建产物依赖的全部输入：

- `_tracksVersion`：`ApplyTracks` 更换行集合后必然重建（新行需要新布局引用）；
- 平铺 / 分组模式、排序字段与方向、组内碟片-曲目排序开关：影响表头排序箭头与分组；
- 每列的可见性、左右固定、宽度、次要格式：影响表头与单元集合。

这一签名同时消除了两类重复重建：`CurrentLayout` 替换触发的
`OnCurrentLayoutChanged` + `ApplySortAndGrouping` 连续调用，以及列宽拖动 / 分组 pin 时
`PersistCurrentLayout` 与显式重建的叠加。仅当重建真正被跳过时才不触发
`GroupedColumnsRebuilt`；没有输入的重复调用本身不产生可见状态变化。

### 2.5 轻量耗时日志

- `ApplyTracks`：每次切换一条 `Info`：
  `List switch: <Kind> '<Name>' rows=N, buildRows=…ms, replaceAll=…ms, sortGroup=…ms, filter=…ms, total=…ms`。
- `RebuildGroupedView`：每次真正重建一条 `Debug`：
  `RebuildGroupedView: grouped rows=N, cols=L+R, boundRows=…, secondary=…ms, elapsed=…ms`
  （`boundRows` 是实际物化单元的行数，可用来验证延迟物化；平铺模式为
  `RebuildGroupedView: flat rows=N, secondary=…ms, elapsed=…ms`）。
- 切歌 / 标签更新等高频路径不新增日志；每日志写入均为 O(1) 字符串拼接。

## 3. 失效策略矩阵

| 变化 | 失效动作 | 实际代价 |
| --- | --- | --- |
| 切换来源 / 播放列表（行集合变化） | `_tracksVersion++` → 签名变化 → 重建；新行默认未物化 | O(N) 建行 + 1 次 Reset；单元在显示时创建 |
| 列增删 / 可见性 / 顺序 / 宽度 / 左右固定 | `CurrentLayout` 替换 → 签名变化 → 重建表头与布局引用 | 已绑定行各发一次 PropertyChanged；未绑定行仅换引用 |
| 次要信息开关 / 次要格式模板变化 | 格式表签名变化 → 新字典；重建签名同时变化 | 已显示行重算一次；未显示行不计算 |
| 仅排序 / 分组切换 | 签名变化（排序字段、模式） | 重建表头排序箭头；行内容不重建 |
| 重复调用（布局写两次） | 签名相同 → 直接返回 | O(列数) 字符串比较 |
| 行内标签更新（`UpdateTrack`） | 清文本缓存 + 空名通知，不改单元 | 仅该行的可见绑定重读 |
| 仅封面大小变化（`PersistGroupCoverSize`） | 签名不含封面尺寸 → 不重建分组行 | 封面栏由窗口自身绑定刷新 |

## 4. 预期收益与度量方式

被消除的 `O(N)` / `O(N × 列数)` 工作：

1. `Tracks.Clear()` + `N × Tracks.Add` 的 `N+1` 次集合通知 → 1 次 `Reset`
   （两个 `ListCollectionView` 的排序 / 过滤 / 容器生成只跑一次）。
2. `RebuildGroupedView` 中 `N × (左列数 + 右列数)` 个 `TrackCellViewModel` 对象
   → `可见行数 × 列数`（通常 30–60 行），且布局未变时为零。
3. `ApplySecondaryTexts` 对全部行的 `BuildTokenValues()`（每行约 20 项字典）
   + 每列模板格式化 → 仅已显示行。
4. 布局 / pin / 宽度拖动路径的重复重建 → 签名去重后只重建一次。

度量方式：

- 看 `List switch:` 日志的 `buildRows` / `replaceAll` / `sortGroup` / `filter` / `total` 分段，
  对比改动前后同一来源、同一行数的切换（建议 `Demo Playlist A`，1000 / 5000 行两档）；
- 看 `RebuildGroupedView:` 日志的 `boundRows` 与 `rows` 比例，确认未显示行未物化；
- 用内存 / GC 分配计数（如 `dotnet-counters` 或运行时诊断）对比切换时分配量；
- 观察切换后首次滚动是否出现可感知的掉帧，必要时记录滚动期间 `boundRows` 增量。

## 5. 风险与缓解

| 风险 | 说明 | 缓解 |
| --- | --- | --- |
| 滚动首帧成本 | 单元 / 次要文本从“切换时一次性算完”变成“每行首次显示时算”，快速滚动时可能更抖 | 每行对象很小（数组 + 若干 `TrackCellViewModel`），构建为纯内存操作、无 IO；先测再决定是否预热可见区 |
| 缓存内存 | 已绑定行会缓存自己的单元与文本，滚动过的行会保留到列表切换为止（行对象本身被 `Tracks` 持有） | 单元为引用型小对象，`N × 列数 × 几十 B` 量级；播放列表切换即整体丢弃。后续可在容器回收时释放（需了解容器生命周期，本轮不做） |
| 虚拟化回收 | `ListView` 回收容器后行对象仍在 `Tracks` 中，其缓存不会自动释放；再次显示直接复用缓存 | 复用是收益；内存上限见上一行。`SetCellLayout` 只对已观察行发通知，回收行不会再触发额外分配 |
| 表头填充 | 布局未变时不触发 `GroupedColumnsRebuilt`，窗口的 `UpdateGroupedHeaderFill` 不执行 | 窗口在尺寸变化时自行重算填充（既有路径），签名未变说明列宽 / 结构一致，无需重算 |
| 绑定行为 | `Cells` / `RightCells` / `SecondaryTexts` 由可 set 属性改为只读属性 | XAML 只读取绑定，不写回；仓库内无代码赋值（仅旧 `RebuildGroupedView` 赋值，已迁移）。`TrackCellViewModel` 仍是任意的实例快照，`ContentControl` 绑定语义不变 |
| 选中 / 播放行 | 重建行对象后 `IsPlaying`、`CurrentRow`、多选恢复路径 | 相关逻辑保持原顺序；`ReplaceAll` 后 `ApplyFilter` / 多选恢复流程不变 |
| 日志噪声 | 每次切换 1 条 Info、每次重建 1 条 Debug | 频率与“切换 / 布局操作”一致；不进入逐行路径 |
| 回滚 | 本次改动集中在 4 个文件 | 回滚点见第 7 节；无 schema / 设置格式变化，回滚不需数据迁移 |

## 6. 手工验收步骤

环境：`EchoGarageData` 使用测试副本（含 1000+ 行媒体库，曲目用 dummy 值），不改动
`D:\Temp\EchoGarage-UAT`。

1. 启动后切换到媒体库，再依次切换 2–3 个播放列表与文件夹：界面不卡顿，标题 / 副标题 /
   行号 / 正在播放标记正确；日志出现带分段耗时的 `List switch:`。
2. 在平铺视图与「按专辑分组」之间切换；分组表头列宽、排序箭头、左右固定块位置正确，
   表头填充（右侧对齐）无偏移。
3. 拖动分组表头列宽 / 切换左右固定 / 编辑列布局 / 应用预设：行跟随变宽变窄，不出现空白单元
   或错位；同一操作日志中的重建次数不再重复翻倍。
4. 打开「次要信息」并给若干列设置次要格式（如 `Demo Album A` / `Demo Track 01` 字段模板）：
   可见行显示两行文本；关闭开关后恢复单行；修改模板后可见行立即更新，未显示行滚动到时也正确。
5. 播放 `Demo Track 01` 后切换播放列表再切回：正在播放行的 ▶ 标记、当前播放面板、封面按需
   加载均正确；切换后选中行（若有）恢复。
6. 在多选若干行后切换列表、再返回：多选与选中恢复行为与改动前一致。
7. 使用搜索框 / 智能过滤：过滤后行数、副标题与行内单元正确；清除过滤后恢复。
8. 编辑某首歌曲标签并保存：该行标题 / 次要文本立即刷新，其余行不受影响。
9. 顶部/底部快速来回切换 10 次：无异常日志、无花屏，内存无持续增长（任务管理器观察
   一次 GC 后回落）。
10. 滚动到列表中部再切换来源：切换完成后从顶部重新显示，滚动条范围正确。

## 7. 代码位置与回滚点

- 修改 `src/EchoGarage.App/ViewModels/MainViewModel.cs`：
  `ApplyTracks`（批量构建 + `ReplaceAll` + Info 日志）、`RebuildGroupedView`（签名去重 +
  共享 `GroupedCellLayout` + Debug 日志）、`ResolveSecondaryFormats` / `ApplySecondaryTexts`
  （共享格式表）、字段 `_tracksVersion` / `_groupedRebuildSignature` / `_secondaryFormats` /
  `_secondaryFormatsSignature`、`Tracks` 属性类型。
- 修改 `src/EchoGarage.App/ViewModels/TrackRowViewModel.cs`：`Cells` / `RightCells` /
  `SecondaryTexts` 延迟 getter、`SetCellLayout`、`HasResolvedCells` /
  `HasResolvedSecondaryTexts`、`GroupedCellLayout`。
- 新增 `src/EchoGarage.App/Services/BulkObservableCollection.cs`。
- 无 schema、设置、主题令牌、XAML 模板改动。

回滚点（按文件独立回滚，无数据迁移）：

1. `ApplyTracks` 恢复为 `Tracks.Clear()` + 逐行 `Tracks.Add`（保留 Info 日志不影响功能）；
2. `RebuildGroupedView` 恢复为直接给 `row.Cells` / `row.RightCells` 赋 `List<TrackCellViewModel>`，
   删除签名去重（此时 `Cells` 需恢复 setter）；
3. `TrackRowViewModel` 恢复将 `SecondaryTexts` 作为 set 属性并立即重算；
4. 删除 `BulkObservableCollection.cs`、`GroupedCellLayout`，`Tracks` 属性类型改回
   `ObservableCollection<TrackRowViewModel>`。

## 8. 验证记录

- Debug 全量构建通过（`EchoGarage.Core` / `Infrastructure` / `App` / `Tests`，0 警告 0 错误）；
  xUnit v3 进程内测试 `Total: 579, Errors: 0, Failed: 0, Skipped: 0`。
- Release 全量构建通过（`EchoGarage.Core` / `Infrastructure` / `App` / `Tests`，0 警告 0 错误）。
- 未运行 UI 自动化（遵守“不抢占前台窗口”约定）；第 6 节手工验收步骤待 owner 执行。
- 文档中的歌曲 / 专辑 / 艺术家均为 dummy 值（`Demo Artist` / `Demo Album A` /
  `Demo Track 01` / `Demo Playlist A`），不含真实作品信息。
