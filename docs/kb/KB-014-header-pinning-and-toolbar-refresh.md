# KB-014：表头固定、来源布局记忆与顶栏/图标

- 状态：Accepted（2026-11-14 批准；2026-11-19 合并原 KB-013、原 KB-014，
  **原 KB-013 已并入本文**）
- 日期：2026-11-12（来源布局记忆）/ 2026-11-13（表头固定与顶栏改进）/ 2026-11-19（合并）
- 相关：ADR-005（每个播放列表独立记忆显示格式）、`docs/kb/KB-008-grouped-cover-and-layout-persistence.md`
  （分组列表布局、封面尺寸与自动居中）
- 适用范围：`PlaylistColumn` / `ViewLayoutFactory`、`MainWindow` 分组表头与行模板、来源切换布局持久化、
  顶栏菜单、窗口图标、音量区滚轮

## 1. 背景

2026-11-12 owner 反馈：已分组显示的媒体库播放列表，切到其它列表（如默认播放列表）再切回来时，
分组显示与列布局丢失；希望每个播放列表稳定记忆自己的显示状态。

2026-11-13 owner 在“分组表头分隔线 + 列宽拖拽”之后提出五项改进：

1. 表头拖拽右侧需要边缘限制，不能把列拖出可视范围；
2. 表头需要区分“左固定 / 右固定”。目前所有列都是左固定；如果把“时间”设为右固定，
   拖动它左侧的边界线时应向左压缩“专辑”的空间，而不是像现在这样移动右侧空间。
   这种特殊调整逻辑只影响左 / 右固定的边界表头；
3. 鼠标悬浮在音量条区域时，滚轮应以 5% 为步进调节音量；
4. 重新设计程序图标：不要黑底，以三角形播放按钮为概念；
5. 移除左上角品牌文字（`EchoGarage v0.1.0`），只保留菜单，并且鼠标移到菜单按钮上就自动弹出菜单。

## 2. 来源切换后的布局记忆（原 KB-013）

### 2.1 症状

截图对比：切换前媒体库按专辑分组（左侧封面栏、每组歌曲行）；切到默认播放列表再切回后，
同一媒体库变成扁平列表，且列布局也变成了播放列表的默认列（没有“专辑艺术家”列）。

现场 `settings.json` 证据：`libraryLayout.sortColumn` 被写成了空字符串（分组关闭），
`groupCoverSize=96`；`library.db` 中默认播放列表的 `ViewLayoutJson` 反而变成了
`sortColumn: "Album"` 的分组布局——也就是说两个来源的布局互相被写乱了。

### 2.2 根因

1. 左侧来源列表使用双向绑定：`MainWindow.xaml` 中 `SelectedItem="{Binding SelectedSource, Mode=TwoWay}"`
   （2026-11-12 记录为 `MainWindow.xaml:423`；行号随 XAML 变动，以符号为准），
   另有 `SelectionChanged="OnSourceSelectionChanged"`。用户点击某个来源时，WPF 先通过绑定把
   `SelectedSource` 更新成**新来源**，随后才触发 `OnSelectedSourceChanged` → `SelectSourceAsync`。
2. 旧代码 `MainViewModel.SelectSourceAsync` 在切换前调用 `PersistCurrentLayout()`，其中保存目标是按
   `SelectedSource` 判定的——此时它已经是**新来源**，于是**旧视图的布局被写进新来源**（布局“错位”）。
3. 切进播放列表时，`SelectSourceAsync` 用 `ViewLayoutFactory.Deserialize(source.Playlist.ViewLayoutJson)`
   读取布局，而 `UpdatePlaylistLayoutAsync` 只写数据库、不更新内存里的 `Playlist` 对象；
   因此下次再进入该播放列表时读到的仍是旧值（从未配置过的播放列表是 `null` → `CreateDefault()` 的扁平布局）。
4. 连锁结果：媒体库（分组 Album）→ 默认播放列表（内存布局为扁平默认）→ 切回媒体库时，
   把扁平布局写进 `Settings.LibraryLayout`，分组就此丢失；同时播放列表的数据库行被写入了媒体库的分组布局。

日志特征（修复前）：每次切换都会出现
`Persist layout: captured=null captureFromView=True selected=<新来源>`，随后 `Layout changed` ×2；
切回媒体库时 `Centered now-playing row: view=flat ... extent=90400.0`（扁平 2825 行 × 32 DIP），
说明分组确实已丢失。

### 2.3 修复

- `MainViewModel` 新增字段 `_activeSource`：记录**当前实际显示中**的来源，与 `SelectedSource`
  （已经被绑定提前改成新来源）分离。
- `SelectSourceAsync`：切换前使用 `PersistCurrentLayout(null, destination: _activeSource)` 显式把旧布局
  保存到旧来源；加载成功并 `ApplyTracks` 后设置 `_activeSource = source`。
- `PersistCurrentLayout` 新增可选参数 `destination`（为空时回退 `_activeSource ?? SelectedSource`）；
  保存播放列表布局时，在写数据库的同时把序列化 JSON 回写到内存 `Playlist.ViewLayoutJson`（write-through），
  保证再次进入该列表时读到最新布局。
- `ReloadSourcesAsync` 在重建来源节点后，用 `Matches` 把 `_activeSource` 重新绑定到新节点实例，
  避免节点被替换后布局仍然写到旧对象。
- 保存时机保持不变：切换来源、修改排序、列编辑 / 应用预设、拖动分组封面尺寸、关闭窗口；
  媒体库与文件夹共用 `Settings.LibraryLayout`，每个播放列表各自写 `ViewLayoutJson`（符合 ADR-005）。
- 日志字段由 `selected=<来源>` 改为 `source=<保存目标来源>`，便于以后排查布局错位。

### 2.4 验证

隔离数据根 `.scratch\switch-bug`（owner 配置副本；`volume=0`、自动扫描关闭；
`libraryLayout.sortColumn="Album"`；默认播放列表 `ViewLayoutJson` 置空）：

- 修复前：媒体库（分组）→ 默认播放列表 → 全部音乐，最后 `GroupedList` 消失、
  `settings.sortColumn=''`、日志 `view=flat ... extent=90400.0`（稳定复现 owner 症状）。
- 修复后：同一流程最后 `GroupedList` 存在（33 个分组）、`settings.sortColumn='Album'`、
  日志 `Centered now-playing row: view=grouped ... extent=211156.0`。
- 播放列表自己的记忆：在默认播放列表点击“专辑”表头后，日志为
  `Persist layout: captured=provided ... source=Playlist` 与 `captured=null ... source=Playlist`；
  切到媒体库再切回播放列表，播放列表仍是分组视图；`library.db` 中该播放列表的 `ViewLayoutJson`
  含 `"sortColumn": "Album"`。
- 构建：0 警告 0 错误；单元测试（2026-11-12 快照）：82 项 = 81 通过 / 1 跳过（HKCU 注册表）/ 0 失败。

### 2.5 局限

- 只记忆布局状态（列可见性、宽度、顺序、排序、分组封面尺寸、组内碟片-曲目排序），
  **不记忆滚动位置与选中项**；如需要可作为后续需求。
- 文件监听触发的 `RefreshCurrentSourceAsync` 仍以 `SelectedSource` 取来源，切换瞬间可能刷新到新来源的
  数据（不会破坏布局持久化，但列表内容可能短暂替换）；后续可加“切换中跳过”保护。
- 扁平视图点击“专辑 / 专辑艺术家”表头会按 KB-008 第 2 节的规则启用分组；这是预期行为，不是布局丢失。

## 3. 表头左右固定与列宽拖拽边界

### 3.1 列固定模型（Core）

- `EchoGarage.Core.Models.ColumnPin`：`Left = 0`（默认，从封面栏之后左流）、`Right = 1`（贴列表右缘）。
- `PlaylistColumn.Pin` 随布局序列化；`Clone()` 一并复制。
- `ViewLayoutFactory.Normalize`：隐藏列强制 `Pin = Left`（隐藏列没有可视边界，保留右固定只会让旧布局
  在恢复可见时行为怪异）。旧布局没有该字段时反序列化为默认 `Left`，向后兼容。

### 3.2 表头与行的左右两块（App）

- `GroupedColumnHeaderViewModel` 增加 `Pin`、`IsLeftPinned`、`IsRightPinned`、`ShowResizeGripper`、
  `ShowLeftBoundaryGripper`；`TrackRowViewModel` 增加 `RightCells`。
- `MainViewModel.GroupedColumns` 拆为 `GroupedLeftColumns` / `GroupedRightColumns`；
  `RebuildGroupedView` 按 `Pin` 分派表头与行单元格。右固定块最后一个列不显示右缘滑柄
  （它的右缘就是列表边界，拖它会脱离锚点）。
- 表头 Grid 改为三列：封面栏（`GroupCoverColumnWidth`）/ 左流（`*`，左对齐）/ 右固定块（`Auto`，右对齐）。
- 行模板改为 Grid 两列：左流 + 右固定块（`RightCells`），两者共用 `GroupedRowCell` 模板，
  宽度绑定表头实例的 `DisplayWidth`（见 3.4），拖动时实时同步。

### 3.3 右固定块的左边界

- 第一个右固定列额外显示一个左缘滑柄（`ShowLeftBoundaryGripper`）。拖动它时修改的是
  **该右固定列的宽度**（`OnGroupedBoundaryThumbDragDelta`）：向左拖变宽、右块原地不动，左侧列被压缩
  —— 即 owner 要求的“向左压缩专辑的空间”。松手后只持久化这一列（`PersistGroupedColumnWidth`）。
- 由于右块贴着视口右缘、右缘滑柄隐藏，右固定列不能通过“移动右块”而越界。

### 3.4 左右固定块贴合（显示层填充）

- 左右两块之间原先会留出左流 `*` 未填满的空白。现在右固定块非空时，**最后一个可见左固定列**的
  显示宽度吸收这段剩余宽度，使左右两块贴合并共用一条分界线；逻辑列宽（持久化值）不变。
- `GroupedColumnHeaderViewModel` 增加 `FillWidth`（INPC，显示专用）与 `DisplayWidth => Width + FillWidth`；
  表头单元格（`GroupedHeaderCell`）和行单元格（`GroupedRowCell`）都绑定 `DisplayWidth`。
- 填充量由 `EchoGarage.Core.Services.GroupedColumnWidths.ComputeTrailingFill(availableWidth, leftWidths)`
  计算：容差 0.01 DIP，左流已填满或溢出时返回 0（溢出仍按既有行为在右块左缘裁切，不压缩列）。
- 可用宽度 = 表头 Grid 实际宽 − 封面栏宽 − 右侧滚动条 inset − 右固定列宽度和，
  与行内容里左流实际拿到的宽度一致。
- 刷新时机：`GroupedHeaderGrid.SizeChanged`、左右列重建（`MainViewModel.GroupedColumnsRebuilt`）、
  列宽/边界/封面拖拽过程中、每次 `UpdateGroupedHeaderInset` 之后、`GroupCoverSize` 变化后。
- 滑柄语义：贴合后最后一个左固定列的右缘滑柄与右块左缘的分界线重合，两个滑柄统一走
  `ResizeFirstRightColumn`（`OnGroupedBoundaryThumbDragDelta` 与 `OnGroupedColumnThumbDragDelta`
  的 `IsSeamResizeGripper` 分支）：拖动只修改**第一个右固定列的宽度**，右块右缘钉住列表右缘，
  左流显示宽度反向吸收；松手持久化该右固定列宽度。否则修改左列自身宽度会被显示填充抵消，
  拖动没有视觉反馈。
- 仅在分组视图且左右两侧都有列时生效；非分组视图、持久化宽度与列宽拖拽上限逻辑不受影响。

### 3.5 边缘钳制（`MaxAllowedGroupedColumnWidth`）

- 左固定列：上限 = 左流实际可用宽度（`GroupedHeaderGrid.ColumnDefinitions[1].ActualWidth`）
  减去同侧其它列宽度和。
- 右固定列：上限 = 列表宽 − 封面栏宽 − 右侧滚动条 inset − 左侧各列**最小宽度之和** −
  其它右固定列宽度和；这样向左拖动边界时最多把专辑压到自己的最小宽度，不会整列被裁掉。
- 两端都取 `max(当前宽度, 上限)`：空间本来就装不下时，拖动不会反而让列缩小
  （旧公式 `Math.Max(MinWidth, available)` 会导致该问题，真机测试已复现并修正）。
- 封面栏滑柄同样钳制：`groupCoverSize` 不超过“列表宽 − 16 − 内容区最小宽度（160）”与 48–320 的公共范围。

### 3.6 右侧固定块与滚动条对齐

- `UpdateGroupedHeaderInset()` 在视觉树里递归查找分组列表的竖直 `ScrollBar`，可见时把右固定表头的
  右边距设为滚动条实际宽度，使表头右块与行内容右缘对齐；在应用布局、`GroupedList.SizeChanged`、
  以及节流（250 ms）的滚动事件中刷新，变化时写 DEBUG 日志 `Grouped header inset: …`。

## 4. 音量区滚轮

- 音量区 StackPanel 监听 `PreviewMouseWheel`，按 5% 对齐
  （`Math.Round((Volume + step) / 5) * 5`，clamp 0–100），向上滚动自动取消静音；
  事件标记为已处理，不会连带滚动列表。
- 静音状态的显示与持久化见 `docs/kb/KB-008-grouped-cover-and-layout-persistence.md` 第 11 节。

## 5. 顶栏菜单（2026-11-14 最终实现）

- 移除左上角品牌文字，只保留菜单。原实现是 3 个按钮 + `ContextMenu`，`MouseEnter` 时打开对应菜单；
  但菜单打开后 `ContextMenu` 的 Popup 会捕获鼠标，底层按钮收不到 `MouseEnter`，从“视图”移到
  “播放列表”时不会切换。
- **最终改用原生 WPF `<Menu>` + `TopLevelMenuItem` 样式**（`Controls.xaml`），由 WPF 原生菜单栏处理
  “子菜单打开时悬停其它顶级项即切换”；弹层样式（圆角、阴影、勾选符号 `CheckGlyph`、快捷键文字）
  沿用主题，`FileMenu`/`PlaylistMenu`/`ViewMenu` 三个 ContextMenu 资源删除，子项与命令保持不变。
- 注意：早期 `docs/kb/KB-003-wpf-dark-theme-pitfalls.md` 曾记录“本项目不使用顶层 Menu”，
  已被本节取代；该结论现已从 KB-003 删除。

## 6. 窗口图标（2026-11-14 最终实现）

- 最终版：**蓝色渐变圆角方块 + 白色播放三角**，`src/EchoGarage.App/Resources/echogarage.ico`；
  16–256 共 9 个尺寸，csproj 与窗口引用不变。发布版 exe 用 `ExtractAssociatedIcon` 验证为蓝色 32×32 图标。
- 历史：原设计的“透明底深色圆环 + 播放三角”在深色任务栏上对比度不足、看起来像未加载，
  已被最终版推翻（不要按圆环版本实现或验收）。

## 7. 验证

- 构建：`dotnet build EchoGarage.sln -nodeReuse:false -p:UseSharedCompilation=false` 0 警告 0 错误。
- 单元测试（各阶段快照）：2026-11-13 为 84 项 = 83 通过 / 1 跳过（HKCU 注册表）/ 0 失败，
  新增 `Normalize_PreservesRightPinOnlyForVisibleColumns`、`SerializeRoundTrip_PreservesColumnPin`；
  贴合补充后为 90 项全部通过（新增 `GroupedColumnWidthsTests` 6 项：空列表、单列/多列填充、
  精确填满、溢出、负可用宽度）；2026-11-14 最终为 106 项全部通过。当前总数以 `ADR-index.md` 记录为准。
- 真机鼠标验证（隔离数据根 `.scratch\switch-bug`，音量 0，自动化前 10 s 系统通知 + 聊天提示，
  结束后恢复前台与光标）：
  - 音量滚轮：0% → 5%（settings.json `volume=0.05`）；
  - 拖拽边缘限制：窄窗口（列表约 489 DIP）里把“标题”向右拖 200 DIP，宽度保持 180 不变，
    日志 `Grouped column drag clamp: … max=180.0`；
  - 右固定边界：拖动右固定“时间”的左边界 100 DIP，日志 `Grouped column width: duration=162.0`
    （另一轮窄窗口测试在空间不足时停在 78，即左侧列最小宽度之和 282 之后不再压缩）；
  - inset 生效：日志 `inset=17.3`，右固定块与行内容右缘对齐；
  - 菜单悬停：鼠标移入“文件”按钮即展开菜单；
  - 截图：`28-wide-pins.png`（1677×1027，标题 380、专辑 170、时间右固定 162）确认左右分块、
    分界线与行/表头对齐正常。
- 左右固定块贴合（2026-11-13 补充）：真机截图（隔离数据根 `.scratch\fillcheck`，ffmpeg 生成的
  5 首测试音频，窗口 1600×900，专辑左固定 + 时间右固定）：表头与行的“专辑”单元格一直延伸到
  右固定“时间”左缘，中间不再有空白；专辑逻辑宽度仍为 170（显示宽度 170 + 填充 177），
  行内容与表头保持对齐。分界线滑柄的拖动语义未做真机鼠标自动化，留给 owner 手动确认手感。
- 验证截图仅写入 `artifacts/verify/`（或 `.scratch/`），不进入仓库。

## 8. 局限与后续

- 窄窗口下如果把大量列设为右固定，左侧列会先被压缩到各自最小宽度，再停止变宽；
  窗口更窄时仍可能出现列裁切（与之前行为一致，没有横向滚动条）。
- 左右块贴合填充只作用于显示宽度：最后一个左固定列在很窄的窗口里可能显示得比持久化宽度大很多
  （甚至超过 1600 的拖拽上限），但不会写回布局；切回非分组视图或其它来源时仍按逻辑宽度显示。
- 列固定方式按来源布局记忆；切换来源时不会重新推断，仍遵循“每个播放列表 / 媒体库独立记忆显示格式”
  的既有规则（ADR-005）。
- 本改动不涉及数据库 schema。

## 9. 修订记录

- 2026-11-12：KB-013 初版（来源切换布局记忆：`_activeSource`、write-through、`Matches`）。
- 2026-11-13：KB-014 初版（列固定、左右块贴合、边缘钳制、音量滚轮、菜单悬停、图标重做）；
  补充左右固定块贴合与 `GroupedColumnWidthsTests`。
- 2026-11-14：顶栏改为原生 `<Menu>`；图标最终版改为蓝色渐变圆角方块（推翻透明底圆环设计）；
  owner 批准，全部单元测试 106 项通过。
- 2026-11-19：owner 要求简化合并文档：原 KB-013 全文并入本文（**原 KB-013 已并入本文**），
  标题改为「表头固定、来源布局记忆与顶栏/图标」；`MainWindow.xaml:423` 行号引用改为
  “符号为准”（绑定行号已变动）；图标只保留最终蓝色渐变圆角方块描述（旧圆环仅作历史说明）；
  统一状态为 Accepted；删除 KB-013 中的旧状态/旧编号引用。
