# KB-008：分组列表布局与封面/滚动交互

- 状态：Accepted（原 KB-008 于 2026-11-10、KB-009 于 2026-11-11 获 owner 批准，KB-011/012 于
  2026-11-12 获批准；原 KB-010 于 2026-11-11 起草；
  2026-11-19 合并原 KB-008/009/010/011/012，**原 KB-009～KB-012 已并入本文**）
- 日期：2026-11-10（初版）/ 2026-11-19（合并）
- 关联：ADR-005（播放列表显示格式）、ADR-006（配置与媒体库分离）、ADR-024（主题令牌）、
  `docs/kb/KB-014-header-pinning-and-toolbar-refresh.md`（表头固定与来源布局记忆）
- 适用范围：`PlaylistViewLayout`、`MainWindow` 列构建 / 分组视图 / 行模板 / 底部进度条与音量区、
  `MainWindow.NowPlaying.cs`（自动居中）、`CoverImageService` / `FileCoverCache`

本文合并了原“分组列表布局与持久化”“分组判定与滚动条/封面加载”“封面加载生命周期与自动居中”
“滚动条卡死排查”“巨大封面与状态栏双击”五份笔记。分组视图的历史实现见 `docs/kb/KB-003-wpf-dark-theme-pitfalls.md`。

## 1. 分组列表布局（foobar2000 风格：左侧封面栏 + 右侧歌曲列）

DataGrid 不支持单元格跨行，无法做出“封面纵向覆盖整个专辑、歌曲列在右侧”的效果，因此：

- **DataGrid 只负责不分组（平铺表格）视图**；分组视图是一个独立的 `ListView`（`GroupedList`），
  与 DataGrid 共享同一个 `Tracks` 集合但使用各自的 `ListCollectionView`
  （`TracksView` 不分组，`GroupedTracksView` 按 `AlbumGroupKey` 分组，排序一致）。
- 每个专辑由自定义 `GroupItem` 模板绘制（`GroupStyle.ContainerStyle`）：
  - 顶部 30px 单行分组头：专辑名（Accent、SemiBold）+ 专辑艺术家 + 分隔线 + 年份（右对齐）；
  - 下面两列 Grid：左列宽 `GroupCoverColumnWidth = GroupCoverSize + 16`，放封面
    （`Margin=8`、尺寸 = `GroupCoverSize`、`VerticalAlignment=Top`），封面自然纵向覆盖
    该专辑全部歌曲；右列是 `ItemsPresenter`，即该专辑的歌曲行。
  - 结果就是封面在左、歌曲列在右，封面高度随专辑歌曲数延展，与 foobar2000 一致。
- 歌曲行是 `ListViewItem`（`GroupedTrackItem` 模板）= 横向 `ItemsControl`，
  每列一个 cell：cell 来自 `TrackRowViewModel.Cells`（`RebuildGroupedView()` 按当前布局生成：
  可见列、去掉 `cover`、宽度 = `max(MinWidth, 列宽)`）。cell 模板通过
  `CellTemplateProvider` 从 Window 的 `Cell.*` 资源解析，复用 DataGrid 的同一套模板。
- 分组视图上方有独立表头行（“封面”栏 + 各列），列头点击调用 `SortBy` 排序并显示 ▲/▼；
  “封面”栏右缘的 `Thumb` 拖动改变 `GroupCoverSize`，`DragCompleted` 时
  `PersistGroupCoverSize()` 写入布局（媒体库视图存 settings，播放列表存 ViewLayoutJson）。
- `GroupCoverSize` 表示封面实际尺寸（48–320），列宽 = 尺寸 + 左右各 8px。
- 分割线：`GroupedTrackItem` 模板 Border 底边 1px（`RowSeparatorBrush`），ListViewItem 横向
  拉伸，因此线一直延伸到列表右缘；平铺视图同样把底边放到 `DataGridRow` 模板（不再放单元格），
  这样线也覆盖最后一列右侧的空白。`RowSeparatorBrush` 五主题各一值：Pure Studio `#EDF0F5`、
  Nordic Ice `#EDF2F4`、Warm Paper `#EFECE5`、Slate Studio `#233044`、Midnight Vinyl `#20232A`。
- 封面 512px 解码（`TrackRowViewModel.GroupCoverImage`），分组虚拟化卸载时清空引用；
  封面调大后列总宽可能超过窗口，超出部分裁切（分组视图无横向滚动）。
- 分组/平铺切换：规则见第 2 节；`ApplySortAndGrouping()` 同时刷新两个 `ListCollectionView`
  并重建分组列头/cell；播放顺序也按实际显示的视图取（`GetPlaybackOrder()` 用
  `IsGroupedViewActive` 判断）。

## 2. 分组判定与组内排序

### 2.1 分组只在“专辑 / 专辑艺术家”排序时显示

- 规则：`IsGroupedViewActive = GroupByAlbum && PlaylistSortPolicy.SupportsAlbumGrouping(CurrentLayout.SortColumn)`。
  `SupportsAlbumGrouping` 只接受 `Album` 与 `AlbumArtist` 两个排序属性（`PlaylistSortPolicy`，有单元测试）。
- 其它排序（标题、艺术家、时间、年份、默认的 `RowNumber` 顺序……）一律显示平铺 `DataGrid`，
  **忠实按所选顺序排序，不再把不连续的同一专辑歌曲合并成组**。
- 两个 `ListCollectionView` 仍然共用 `Tracks`：平铺视图永远不分组；分组视图只在有效分组时
  挂 `PropertyGroupDescription(AlbumGroupKey)`，否则仅按同一主排序排列（与平铺视图顺序一致）。
- 播放顺序（`GetPlaybackOrder()`）跟随“当前实际显示”的视图，避免显示顺序与“下一首”不一致。
- “视图 → 按专辑分组”复选框仍然是用户意图开关；排序切到非专辑列时列表自动回到平铺，
  勾选状态下切回专辑/专辑艺术家排序会自动恢复分组。
- 兼容性：旧布局 `sortColumn` 为空（默认顺序）时现在显示平铺视图，不再默认合并分组；
  这是该规则有意带来的行为变化。

### 2.2 分组内默认按“碟片号 → 曲目号”升序，可在列与布局中关闭

- `PlaylistViewLayout.GroupSortByDiscTrack`（默认 `true`，JSON 字段 `groupSortByDiscTrack`）：
  - 分组有效时，排序描述为：专辑/专辑艺术家主排序（按用户升降序）→ `AlbumGroupKey`（同方向，
    保证同一专辑严格连续）→ `DiscNumberSort` 升序 → `TrackNumberSort` 升序 → `RowNumber` 升序兜底。
  - 关闭后跳过碟片/曲目两级，组内保持播放列表当前顺序（`RowNumber`）。
  - 专辑主排序为降序时，仅组间顺序反转；组内碟片/曲目始终升序。
- “列与布局”对话框新增复选框“分组内按碟片号 / 曲目号排序”与说明文字；该开关按播放列表保存
  （媒体库布局保存在配置里），应用显示预设和列调整时保留该值（与 `GroupCoverSize` 同样的处理）。
- 旧配置没有该字段时按 `true` 处理（JSON 反序列化默认值），无需迁移。

### 2.3 追加修正（2026-11-11）：按“时间”等列排序不持久、箭头错误、二次点击无法倒序

- 现象（owner 反馈）：点击“时间”列头后列表按时间升序显示，但表头箭头仍停在“专辑艺术家”，
  再点“时间”不会切换降序。
- 根因 1（排序属性被清空）：`ViewLayoutFactory.Normalize` 校验 `SortColumn`（存的是行属性名，
  如 `DurationSort`）时拿它和**列 key**（`duration`）直接比较。只有属性名恰好在忽略大小写后等于
  列 key 的排序（`Title`/`Artist`/`Album`/`AlbumArtist`/`Genre`/`Codec`/`Comment`）能存活；
  `DurationSort`/`TrackNumberSort`/`YearSort`/`BitrateSort`/… 会在每次 `PersistCurrentLayout`
  时被清成 null。于是第一次点击虽然排了序，但持久化后 `SortColumn` 变 null（或回退），
  箭头无从显示，第二次点击又被当成“新排序”而永远是升序。
- 根因 2（重建列后视觉顺序丢失）：`RebuildColumns` 为了解决 DataGrid 列重建崩溃会先
  `TrackGrid.ItemsSource = null` 再重新绑定。DataGrid 在 ItemsSource 重新绑定时会清空
  `TracksView.SortDescriptions`，因此重启后即使布局里保存了排序，列表视觉上仍是默认顺序。
- 修复：
  1. `ColumnCatalog.ColumnDescriptor` 增加 `SortProperty` 字段，成为“列 key ↔ 行排序属性”的唯一映射
     （`ColumnCatalog.FindBySortProperty`）；`TrackSortMap`、`ColumnsWindow` 全部改为从 catalog 派生，
     `ViewLayoutFactory.Normalize` 用映射校验“排序列是否可见”。
  2. `MainWindow.RebuildColumns` 在重新绑定 ItemsSource 后调用 `MainViewModel.ReapplySort()`
     重新应用当前排序；`SortBy` 现在也会触发 `SortChanged`，分组表头点击切换排序后
     平铺 DataGrid 的箭头同步刷新。
- 验证（应用内临时诊断，未提交）：
  - 预置 `sortColumn=DurationSort` 启动：列表按时间升序、箭头在“时间”列；
  - 从“专辑”分组视图切到“时间”排序：自动回到平铺、按时间升序、箭头在“时间”；
  - 连续两次排序：第二次变为降序，箭头 ▼，`settings.json` 持久化 `DurationSort/true`。

## 3. 封面加载生命周期（三条触发路径 + 缓存可靠性）

### 3.1 背景（2026-11-11 owner 反馈）

1. 拖动列表滚动条时，列表封面加载有时不生效（部分专辑封面一直停在灰色占位块）。
2. 播放按钮周围出现多余的虚线焦点框。
3. 播放器上方的状态栏（“就绪”处）改为显示当前播放曲目信息，去掉右侧媒体库/列表统计；
   并新增“播放歌曲位于当前浏览列表时，自动跳转并把该行显示在列表中央”。

### 3.2 根因

- **根因 A：分组视图的封面只在 `Loaded` 事件里加载，虚拟化下的生命周期不可靠。**
  分组视图是 `ListView` + `GroupStyle` + `VirtualizingPanel.IsVirtualizingWhenGrouping="True"`。
  `GroupItem` 容器在测量 / 虚拟化复用的不同阶段，`Loaded`、`DataContextChanged`、`IsVisibleChanged`
  的触发时机与次数并不一致：快速拖动 / 跳跃滚动时，部分容器在 `Loaded` 触发时还没有可用的
  `DataContext`（分组），或者根本没有再触发 `Loaded`，导致封面加载请求从未发出，
  封面栏保持灰色占位（`CoverPlaceholderBrush`）。
  实测（临时诊断，未提交）：一次滚动跳跃会创建约 50 个 `GroupItem` 容器的 DataContext，
  但只触发个位数 `Loaded`；可见分组中确实存在“只有 DataContext、没有 Loaded”的容器。
- **根因 B：内存负缓存把“瞬时失败”永久记为“没有封面”。**
  `CoverImageService` 原来在任何异常/解码失败时执行 `Remember(memoryKey, null)`：
  一次临时的文件锁、半写文件或解码失败，会让该专辑封面在**本次运行内永远空白**，
  后续滚动回来也不会重试。同时，同一个封面的并发请求没有合并：快速滚动时同一专辑的多行/分组
  会重复排队提取与解码，拉长其他封面出现的时间。
- **根因 C：封面缓存文件不是原子写入。**
  `FileCoverCache.WriteAsync` 直接写目标文件；并发请求或另一个解码宽度（128/420/512）同时读取时
  可能读到写了一半的图片，解码失败后再被 B 的负缓存永久记住。
  `Resolve` 只要文件存在就返回，0 字节残留文件同样会走上“解码失败 → 负缓存”的路径。

### 3.3 修复

1. **加载触发改为三条路径**（`MainWindow.xaml` + `MainWindow.xaml.cs`）：
   - 分组封面 Border 同时监听 `Loaded`、`IsVisibleChanged`、`DataContextChanged`；
   - `IsVisibleChanged` 是唯一在所有容器生命周期路径都会触发的事件：容器进入可见树时加载，
     离开可见树时释放该组的大封面（`GroupCoverImage = null`），避免滚动整库后内存持续增长；
   - `DataContextChanged` 处理容器被复用时的新分组（同时释放旧分组的引用）；
   - 三条路径都会经过 `LoadGroupCoverAsync` 的“已有图直接返回”判断，配合并发合并不会重复解码。
2. **`CoverImageService` 可靠性改造**：
   - 同一 `(decodeWidth, coverKey)` 的并发请求用 `Lazy<Task<BitmapSource?>>` 合并为一次提取 + 解码
     （single-flight），完成后从字典移除；
   - 只有“封面缓存确认没有封面”（负缓存标记命中、无可用图片字节）才记住 `null`；
     瞬时提取/解码失败只记 `Warn`，不写入内存缓存，下次请求会重试；
   - 内存 LRU（300 张）行为保持不变。
3. **`FileCoverCache` 原子写入**：先写 `*.tmp<guid>`，再 `File.Move(..., overwrite: true)` 替换；
   `Resolve` 忽略 0 字节文件；提取/写入异常不再在缓存层吞掉，改为向上抛出，由图像服务决定不缓存失败结果。

### 3.4 已知限制（2026-11-19 补充）

- 冷缓存下快速滚动到尚未请求过的专辑时，该组封面可能短暂停在灰色占位；它不会永久空白：
  请求完成后按可见顺序逐步补齐，离开可见区域的容器会释放大封面（见 3.3 第 1 条）。
- 内存负缓存只记录“确认没有封面”的专辑，瞬时提取/解码失败不会被永久记住（见 3.3 第 2 条）。

## 4. 分组封面尺寸绑定加固（“巨大封面”）

### 4.1 现象

owner 反馈并附截图（v0.1.0，150% DPI，窗口约 1677×1027 DIP）：按专辑分组的媒体库中，偶尔有某个
专辑组的封面在左侧封面栏里被渲染成约 512 DIP 的巨大方形，把该组的歌曲列整体推到右侧；其他组正常。
日志里没有绑定错误，也没有 `[WARN]` / `[ERROR]`。从 owner 的运行日志可以确定发生时间点：封面大小
刚被拖动（116.7 DIP，连续 `Group cover size:` 日志）之后，正在播放曲目触发自动居中、列表滚动到
该组附近时出现。本机按同样操作（同一媒体库、同样窗口与 DPI）连续测试未能重现，推断与分组容器
回收 / 重新挂树的时序有关。

### 4.2 原因

`AlbumGroupContainer`（GroupItem 模板）里两处关键绑定都使用了祖先相对源：

- 封面栏列宽：`{Binding DataContext.GroupCoverColumnWidth, RelativeSource={RelativeSource AncestorType=Window}}`
- 封面尺寸：`{Binding DataContext.GroupCoverSize, RelativeSource={RelativeSource AncestorType=Window}}`

WPF 分组虚拟化的 GroupItem 容器在回收 / 重新生成时，可能先进入模板展开与测量、之后才挂到 Window 的
可视化树上（或挂树期间祖先链短暂不完整）。祖先相对源一旦在解析时找不到 Window，绑定会保持未解析值：
`Width` / `Height` 变成 `NaN`，`ColumnDefinition.Width` 退回 `Auto`。封面 Border 于是按其内容
（`GroupCoverImage`，解码宽度 512px）的自然尺寸撑开——在 96 DPI 下约 512 DIP，正好对应截图里约
528 DIP 的巨大方块；同时列宽退回 Auto 后不再约束封面栏，歌曲列被推到右侧。之后即使容器挂树完成，
祖先绑定也不会可靠地重新解析，异常状态就会一直保留。

### 4.3 修复

**A. 封面尺寸改为“可继承附加依赖属性”（根因修复）**

新增 `src/EchoGarage.App/Views/GroupCoverMetrics.cs`：

- 附加属性 `CoverSizeProperty`（`double`，默认 `PlaylistViewLayout.DefaultGroupCoverSize = 96`），
  `FrameworkPropertyMetadataOptions.Inherits`，强制转换时把 `NaN` / `Infinity` 归为默认值并夹紧到
  `[PlaylistViewLayout.MinGroupCoverSize = 48, MaxGroupCoverSize = 320]`。
- 值从 Window 向下继承（挂在 `GroupedList` 上即可），模板内绑定改为
  `{Binding Path=(views:GroupCoverMetrics.CoverSize), RelativeSource={RelativeSource Self}}`
  ——不依赖祖先链，容器回收 / 延迟挂树都不会失效。
- 封面栏 `ColumnDefinition` 改为 `Width="Auto"`，宽度完全由封面 Border（固定尺寸）决定，
  不再依赖第二个可能失效的绑定。
- `MainWindow` 在启动时与 `MainViewModel.GroupCoverSize` 变化时把值写入 `GroupedList`
  （`ApplyGroupCoverMetrics()`），拖动表头滑柄仍实时生效。

**B. 组容器加载时自愈并留证**

`OnGroupCoverLoaded` 在请求封面之外调用 `RepairGroupCoverSizeIfNeeded`：若元素实际宽度与期望值
相差超过 0.5 DIP（或为 `NaN`），写
`[WARN] Album group cover size was not applied (width=…, expected=…, group=…)`
并把 `Width` / `Height` 修正为期望值。这样即使未来出现新的时序问题，界面也会自愈，日志可直接定位。

### 4.4 局限

- 未能稳定复现原始“祖先绑定失效”的时序，A 方案是根因级修复（去掉依赖），B 方案是兜底自愈与取证。
  若再次出现，日志中的 `Album group cover size was not applied` 会给出实际宽度、期望值与分组名。
- 若未来新增其他依赖 `RelativeSource AncestorType=Window` 的模板内绑定，应优先考虑可继承附加属性
  或 `ElementName` 之外的方式，避免同类容器时序问题。

## 5. “正在播放”封面加载态

- 旧行为：切歌后旧封面一直显示到新封面解码完成，容易误以为封面没换。
- 新行为：
  - `PlayRowAsync` 一开始就调用 `BeginNowPlayingCoverLoad`：若新曲目路径与当前封面路径不同，
    立刻 `CurrentCover = null`；随后异步加载 420px 封面。
  - 版本令牌 `_nowPlayingCoverVersion`：每次切歌/停止自增；慢速解码返回时若版本过期或 `CurrentRow`
    已变化直接丢弃，不会把旧歌封面盖到新歌上；只有最新请求能清除 `IsNowPlayingCoverLoading`。
  - 面板左上角新增 `IsNowPlayingCoverLoading` 对应的旋转弧线（Accent 圆弧 + 淡轨道圆，主题令牌着色），
    使用 `DataTrigger` + Storyboard；淡入延迟 120ms，缓存命中时几乎不闪。
  - 无封面且有当前歌曲时显示 ♪ 占位符（`ShowCoverPlaceholder`）；停止/清空时封面与加载态一起复位。
  - 标签编辑刷新当前曲目时也走同一路径（保留旧图直到新图更替，不显示加载动画）。
- 应用内验证（临时诊断，未提交）：`--play-file A` → 1.8s 后切到 B 并人为延迟封面解码，
  截图确认旧封面已移除、旋转加载动画可见；日志确认版本丢弃逻辑生效。

## 6. 滚动条滑块最小可视高度（MinThumbTrack）

- 需求：列表右侧竖向滚动条在大库下按比例计算出的滑块只有约 6–8px，太难点。
- 实测：只给 `Thumb.MinHeight = 12` 时 `ActualHeight` 变成 12，但 WPF 仍按 Track 算出的
  arrange slot（约 8.5px）给 Thumb 加 layout clip，屏幕上依然只有约 7px——因此不能只改 MinHeight。
- 方案：`MinThumbTrack : Track`（`EchoGarage.App/Services/MinThumbTrack.cs`），在竖向 ScrollBar 模板中
  替代默认 `Track`：
  1. 先 `base.ArrangeOverride`；
  2. `computed = trackLength * ViewportSize / (Maximum - Minimum + ViewportSize)`；
  3. `actual = max(computed, MinThumbLength)`，`fraction = clamp((Value - Minimum) / range)`；
  4. `thumb.Arrange(new Rect(0, fraction * (trackLength - actual), width, actual))`。
- 效果：arrange slot 本身就是最小长度，无 layout clip；滑块按比例移动、滚到底部时底边与轨道末端齐平，
  不会溢出；横向滚动条继续使用默认 `Track`（不受影响）。
- XAML 中 `MinThumbLength="14"`：共享 Thumb 模板的 Border 上下各留 1px 内边距，因此屏幕上实际着色高度
  ≈ 12px，约为此前可视高度（约 6–7px）的两倍。若以后调整 Thumb 模板边距，需要同步调整该值。
- 已知取舍：Track 内部拖拽换算仍使用它自己的比例长度，视觉槽位比内部值长，
  拖动时值与鼠标位移有极轻微偏差（像素级），实测不影响使用；滚动/点击翻页行为不变。

## 7. 分组列表拖动滚动条后“卡住抽搐”的排查与防御性修复

### 7.1 现象

owner 反馈：在按专辑分组（foobar2000 风格分组列表）的媒体库中随便拖动一下滚动条后，列表会卡住并
上下小幅抽搐，之后拖动滚动条滑块或滚动鼠标滚轮都无法改变列表显示位置。发生在 0.1.0 的新构建
（播放信息状态栏 / 播放行自动居中 / 封面加载生命周期那一版）。运行日志里没有任何 `[ERROR]` / `[WARN]`，
应用仍可正常关闭。

### 7.2 复现尝试（均未复现）

- 使用 owner 的真实媒体库（2825 首）与独立数据目录副本；
- UIAutomation 定位列表 + 真实鼠标事件（`SetCursorPos` + `mouse_event`）操作滚动条：拖动滑块、
  拖动中上下抖动、慢速 1~2px 微移、大幅跳跃后以微小移动收尾、点击滑轨翻页、滚轮连续滚动、
  拖到最顶端/最底端等；
- 以“播放《Demo Track 01》并自动居中”为初始状态（与 owner 场景一致），150% DPI、
  相同窗口尺寸（1677×930 DIP）；
- 程序化把列表从顶部到底部逐段扫描（每 300 DIP 一步，共 660+ 步），并用 watchdog 检测 UI 线程
  是否卡死；Debug 与 Release 构建均未出现卡住或滚动循环。

结论：无法在本机稳定复现，触发条件很可能与具体鼠标手势/拖动时序有关，属于时序敏感问题。

### 7.3 机理分析（基于 dotnet/wpf 源码）

1. `ScrollViewer` 的命令队列（`MakeVisible` / `SetVerticalOffset` …）每次 `LayoutUpdated` 只执行一条命令；
   `VirtualizingStackPanel.MakeVisible()` 在偏移改变后会再次调用 `ScrollOwner.MakeVisible(...)`
   （源码注释：“When layout gets updated it may happen that visual is obscured by a ScrollBar…”）。
   如果目标矩形始终无法满足，就会每帧重试——表现为列表小幅抖动、用户输入被下一次重试覆盖。
2. 像素滚动 + 分组虚拟化 + 容器回收（Recycling）时，组尺寸依赖估算；滚动过程中 `ExtentHeight`
   会变化（本机实测拖动一次可变化数千到上万 DIP），`VirtualizingStackPanel.OnAnchorOperation()`
   在“期望视口间距”与“实际间距”不一致时会调整偏移并重新测量、再次排队重试，直到收敛。
   若锚定容器在期间被回收、或估算一直变化，就可能不收敛。
3. 本应用正好命中高风险组合：分组虚拟化 + `ScrollUnit=Pixel`（`Controls.xaml` 中 DataGrid 样式设置的
   像素滚动，分组列表同样以像素为单位）+ 150% DPI（大量小数 DIP 偏移）。WPF 源码在
   `SetVerticalOffsetImpl()` 中专门注释过：“像布局取整开启时一样，把偏移取整到像素边界以避免收敛到
   小数偏移的死循环”，说明小数偏移的锚定收敛是 WPF 已知的脆弱点。

### 7.4 采取的防御性修改

1. **自动居中不再使用合成的 `BringIntoView(Rect)`**（用一个超大负 top 矩形“骗”居中的写法），
   改为标准 `ScrollIntoView` + 直接 `ScrollToVerticalOffset` 的像素修正（最多 5 次）。
   这样去掉了应用里唯一的非常规 BringIntoView/MakeVisible 请求源。
2. **自动居中在用户操作滚动条期间推迟**：当左键按住且鼠标捕获位于 `ScrollBar` 内
   （拖拽 Thumb 或翻页按钮）时不抢滚动偏移，每 200ms 重试，最多 30 次；松开后立即居中。
3. **分组封面释放延迟到 Background**：`IsVisibleChanged` 里的 `ReleaseGroupCover` 由同步改为
   `Dispatcher.BeginInvoke(Background)` 并二次校验可见性/数据上下文，避免在布局或容器回收过程中
   同步修改绑定、反复触发 measure。
4. **分组列表增加 `VirtualizingPanel.CacheLength=2`**：减少快速拖动时的容器回收/重新生成，
   降低“锚定容器被回收”的概率。
5. **分组列表启用 `UseLayoutRounding=True`**：对应上面 WPF 源码中的小数偏移规避路径；150% DPI 下尤其相关。
6. **滚动异常检测与自愈**：1 秒内超过 200 次 `ScrollChanged` 视为可疑“滚动风暴”，写
   `[WARN] Suspicious scroll burst: ...`（包含 offset / extent / viewport / 来源列表与累计位移），
   并尝试把当前偏移吸附到整数 DIP 一次（正常拖动/滚轮远低于该阈值，不影响正常使用）。

### 7.5 局限

- 未能复现原始卡住现象，因此以上修改是“消除高风险路径 + 自愈/取证”的防御性修复，
  不保证根因一定在其中。
- 若再次出现，日志中应出现 `[WARN] Suspicious scroll burst`，可直接看到哪个列表、偏移/范围/视口
  以及累计位移，用于判断是否为自循环。
- 后续若仍复现，候选方向：分组列表改用 `ScrollUnit=Item`（需重新评估分组虚拟化性能与体验）、
  改为 Standard 虚拟化模式、或移除自定义 `MinThumbTrack`（最小滑块）后再对比。

### 7.6 参考

- [ScrollViewer.cs（dotnet/wpf）](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/ScrollViewer.cs)
- [VirtualizingStackPanel.cs（dotnet/wpf）](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/VirtualizingStackPanel.cs)

## 8. 播放信息状态栏、播放行自动居中与双击跳转

### 8.1 状态栏改为“播放信息条”

- 位置：主窗口底部、传输栏上方原 30px 状态栏。
- 左侧（`NowPlayingStatusText`）：有当前曲目时显示 `♪ 标题 — 艺术家 — 专辑`（缺失字段自动跳过），
  单行 `CharacterEllipsis` + ToolTip；没有当前曲目时显示“就绪”。
- 右侧：删除 `LibrarySummary`（媒体库 N 首 / N 个文件夹 / N 个播放列表）统计；
  保留扫描进度条，瞬时状态消息（扫描、导入、错误等）只在 `StatusMessage != "就绪"` 时显示
  （`HasStatusHint`），4 秒后自动回到不显示。
- 媒体库统计仍可在右侧“正在播放”面板与设置中查看，不丢失信息。

### 8.2 播放行自动居中

- 需求：播放歌曲位于当前正在浏览的列表时，列表自动滚动，并把该行显示在视口中央。
- 实现（`MainWindow.NowPlaying.cs`，partial class）：
  - `CurrentRowChanged` 与 `ApplyLayout()`（切源、列布局变化）都会调用 `ScrollToCurrentRow()`
    → `ScheduleCenterCurrentRow()`，用 `DispatcherPriority.Loaded` 延迟一帧，让列表先完成容器生成；
  - 仅当 `CurrentRow` 属于当前视图的集合（分组视图 `GroupedList.Items` / 平铺 `TrackGrid.Items`）时执行，
    浏览其它播放列表时不打扰滚动位置；
  - 平铺视图用 `DataGrid.ItemContainerGenerator`；分组视图的顶层容器是 `GroupItem`，
    需在视觉树中按 `DataContext` 找到 `ListViewItem`；
  - 先 `ScrollIntoView` 保证容器存在，再用像素级 `ScrollToVerticalOffset` 修正把行推到中央：
    `MaxCenterCorrectionAttempts = 5` 次、`MaxCenterDeferAttempts = 30` 次（200ms 间隔），
    滚动条拖动期间推迟、松开后立即居中（见第 7 节）；逻辑滚动（`CanContentScroll=True`）下用
    视口高度换算每个滚动单位对应像素。
- 行为说明：双击播放、上一首/下一首、自动连播、切到包含当前歌曲的播放列表时，列表会居中到该行；
  改列宽/列顺序引起的布局重建也会重新居中（沿用原 `ScrollToCurrentRow` 的调用点）。
- 已知限制：自动居中在“用户正在手动滚动”时不会阻止滚动（下一次切歌/布局变化时才再次居中）；
  如 owner 希望“仅当行不在视口内才居中”，可以再调整策略。

### 8.3 状态栏双击跳转（2026-11-12）

- 状态栏 `NowPlayingStatus` TextBlock 增加 `Cursor="Hand"`、`Background="Transparent"`
  （整块区域可命中，原先点在文字间隙不触发）与 `MouseLeftButtonDown` 处理；
  ToolTip 补充“双击跳转到正在播放的歌曲”（仅在播放时显示）。
- 处理逻辑（`MainWindow.NowPlaying.cs` `OnNowPlayingStatusMouseLeftButtonDown`）：
  双击（`ClickCount >= 2`）→ 取 `MainViewModel.CurrentRow`：
  - 没有正在播放：状态栏提示“当前没有正在播放的歌曲。”；
  - 正在播放但不在当前列表：提示“正在播放的歌曲不在当前列表中。”，不改变滚动位置；
  - 在列表中：走与切歌自动居中相同的 `ScheduleCenterCurrentRow()`（Dispatcher 延后、
    滚动条交互期间让路、像素级居中修正），并写 `[DEBUG]` 日志。

### 8.4 传输栏按钮不再显示虚线焦点框

- 原因：WPF 默认 `FocusVisualStyle` 会在获得键盘焦点的按钮内画虚线矩形（播放按钮为圆形，虚线框尤其显眼）。
- 处理：`Controls.xaml` 的 `TransportButton` 样式增加 `FocusVisualStyle = {x:Null}`（`PlayButton` 继承它），
  悬停/按下/禁用反馈保持不变。
- 取舍：键盘 Tab 到传输按钮时不再有虚线焦点提示；如以后需要可访问性焦点提示，
  建议改为主题令牌色的细描边（不使用系统虚线框）。

## 9. 启动时布局被默认值覆盖的坑

- 现象：用户自定义的列宽 / 排序在重启后丢失，`settings.json` 里变回“标准”预设。
- 原因：`InitializeAsync` → `SelectSourceAsync` 开头会先 `PersistCurrentLayout()` 保存
  “上一个来源”的布局；但首次启动时窗口还没建列，`CaptureLayoutFromGrid()` 返回 null，
  `CurrentLayout` 还是内存里的默认布局（标准 / 96px），于是默认值被写回
  `Settings.LibraryLayout`，随后再读取时已是被覆盖的值。
- 修复：`PersistCurrentLayout` 在“需要从视图抓取、但视图还没有列”时直接返回，不写设置；
  首次选择来源时也不再保存（此时 `SelectedSource` 为空）。副作用：若用户升级前已经丢失
  过自定义列宽，需要重新调整一次。
- 相关日志：`Persist layout: captured=... selected=...`、`Persist layout skipped: ...`、
  `Layout changed: groupCoverSize=...`、`Applying layout: ...`（均为 `[DEBUG]`）。
- 来源切换导致的布局记忆错位（双向绑定 SelectedItem 抢跑 + `_activeSource` write-through 修复）
  见 `docs/kb/KB-014-header-pinning-and-toolbar-refresh.md`。

## 10. 无播放时的进度条空状态

- 现象：启动后未播放时，进度条看起来“已满”、Thumb 停在最右侧。
- 原因：滑块 `Minimum=0`、`Maximum` 绑定 `DurationSeconds`，无歌曲时两者都是 0；WPF `Track`
  在 `Minimum == Maximum` 时把 Thumb 放在右端、把 Decrease 区画满，所以像已播放完。
- 修复：`MainViewModel.SeekMaximum => DurationSeconds > 0 ? DurationSeconds : 1`，`Maximum`
  绑定它；空闲时 `Maximum=1, Value=0`，Thumb 在最左侧、轨道为空。`DurationSeconds` 变化时
  手动通知 `SeekMaximum`。
- 同时把进度条 `IsEnabled` 绑定到 `HasCurrentTrack`，无播放时不可拖动（避免拖动 Thumb 把
  `Value` 的 OneWay 绑定顶掉）；Slider 样式补了 `IsEnabled=False` 时 `Opacity=0.45`。

## 11. 音量百分比与静音状态

- 滑块右侧显示 `VolumeText`：始终显示四舍五入的百分比（如 `70%`）；静音时数值不变，
  静音状态由文字颜色 + 删除线表达，而不是把文案改成“静音”。
- 静音按钮：`AutomationId="VolumePercentButton"`，内容就是该 `VolumeText`；
  静音时 `VolumeText` 前景用 `DangerBrush` 并加 `TextDecorations=Strikethrough`，
  按钮背景保持透明（不再是 `MuteGlyph` 图标 + `AccentSoftBrush` 背景的旧实现）。
- `ToolTip` 绑定 `MuteToolTip`（静音 / 取消静音）；`IsMuted` 只是会话状态，不写入
  `settings.json`（`Volume` 仍持久化）；静音时 `Player.Volume=0`，取消静音恢复 `Volume/100`。
- 相关 `AutomationId`：`VolumeSlider`、`VolumePercentButton`（没有 `VolumeText` / `MuteButton` 这两个 id）。
  音量滚轮步进 5% 的实现见 `docs/kb/KB-014-header-pinning-and-toolbar-refresh.md`。

## 12. 验证

- 单元测试：
  - `ViewLayoutTests.Normalize_ClampsGroupedCoverSize_AndRoundTrips`（默认值 / clamp / 序列化 / Clone）；
  - `PlaylistSortPolicyTests`：仅 `Album`/`AlbumArtist` 允许分组，其它排序（含 null）不允许；
  - `ViewLayoutTests.GroupSortByDiscTrack_DefaultsToTrueAndRoundTrips`：默认 true、Clone/Serialize 往返保持 false；
  - `ViewLayoutTests.Normalize_KeepsSortPropertyForVisibleSortableColumns`、`SerializeRoundTrip_PreservesDurationSort`；
  - 封面缓存回归：`CoverCache_ResolveIgnoresZeroLengthFile_AndReextracts`、
    `CoverCache_WritesAtomicallyAndLeavesNoTempFiles`（`tests/EchoGarage.Tests/TagAndCoverTests.cs`）。
  - 历史计数：2026-11-11 为 80 个（79 通过，1 个 HKCU 注册表测试在受限会话跳过）；
    2026-11-12 为 82 个（81 通过 + 1 跳过）。当前测试总数以 `ADR-index.md` 批准记录为准。
- 手工/截图验证：
  - 分组布局（浅色/深色）：分组头单行、封面在左侧封面栏纵向覆盖整组歌曲、歌曲列在右侧、
    行间 1px 分割线到列表右缘；拖动“封面”栏右缘后重启检查 `libraryLayout.groupCoverSize` 保持。
  - 平铺视图（取消分组）：DataGrid 正常、分割线覆盖整行；2825 首库默认顺序 → 平铺；
    按“专辑”排序 → 分组、组内碟片/曲目升序；按“标题”排序 → 平铺且不合并不连续专辑。
  - 封面尺寸：真实鼠标拖到最小（48.7 DIP）/最大（320 DIP），各滚动 60 / 80 格触发容器回收，
    截图逐一检查无巨大封面；宽度 320 持久化后重启 `Applying layout: groupCoverSize=320.0 …`。
  - 封面加载：2825 首 + 150% DPI，启动、连续切换 5 次排序、模拟拖动滚动条 160 步到中/底部，
    可见分组封面全部加载；滚动返回后仍正确；冷缓存首次提取 1,300+ 张无 `WARN`/`ERROR`；
    离开可见区域后释放（内存不随整库滚动增长）。
  - 滚动条：2825 首下滑块可视高度约 12px（改前约 6px），末端贴底不溢出（探针 + 应用截图）。
  - 状态栏/焦点框/居中：截图确认 `♪ 曲名 — 艺术家 — 专辑`、右侧无媒体库统计、播放按钮无虚线框；
    平铺与分组视图下播放后当前行位于列表中轴附近（居中前后偏移 ≤ 2px）。
  - 状态栏双击：滚动回顶部后双击 → 列表滚回并居中该行（日志 `Centered now-playing row: … error=…->0.3px`）；
    切到不含该曲的播放列表后双击 → 仅状态栏提示，滚动位置不变。
  - 无播放进度条：截图确认 Thumb 在最左侧、轨道为空；播放中真实鼠标点击跳转并检查 `Seeked to` 日志。
  - 截图产物只写 `artifacts/` 或 `.scratch/`（`docs/screenshots/` 仅保留 README 展示用的 JPG，
    见仓库 `AGENTS.md` §9；界面验证截图不新增到 `docs/screenshots/`）。

## 13. 修订记录

- 2026-11-10：KB-008 初版（分组封面栏、布局持久化、进度条空状态、音量/静音）；2026-11-11 更新。
- 2026-11-11：KB-009 初版（分组判定、组内碟片/曲目排序、MinThumbTrack、播放封面加载态）；
  同日追加排序持久化修正 §2.3。
- 2026-11-12：KB-011（滚动条卡住排查 + 防御性修复）、KB-012（巨大封面绑定加固 + 状态栏双击）；
  owner 批准。
- 2026-11-19：owner 要求简化合并文档：原 KB-009/010/011/012 全文并入本文
  （**原 KB-009～KB-012 已并入本文**），标题改为「分组列表布局与封面/滚动交互」；
  修正 `RowSeparatorBrush` 为五主题取值；按实现重写静音 UI 描述（无 `MuteGlyph`、
  `VolumePercentButton`、DangerBrush + 删除线）；自动居中“最多修正 3 次”改为代码实际的
  5 次修正 / 30 次推迟（200ms）；删除 KB-010 的旧状态说明与
  “重新生成 docs/screenshots”旧说明；修正 KB-012 的旧状态措辞为 Accepted。
