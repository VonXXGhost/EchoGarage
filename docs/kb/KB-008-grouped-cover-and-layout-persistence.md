# KB-008：分组列表布局（foobar2000 风格封面栏）、布局持久化、进度条空状态与音量/静音

- 状态：已批准（Accepted，2026-11-10，owner 指示记录为 KB，不需要 ADR；2026-11-11 更新）
- 日期：2026-11-10
- 关联：ADR-005（播放列表显示格式）、ADR-006（配置与媒体库分离）
- 适用范围：`PlaylistViewLayout`、`MainWindow` 列构建 / 分组视图 / 行模板、底部进度条与音量区

> 更新（2026-11-11）：本文第 1 节中“分组/平铺视图可见性由 `GroupByAlbum` 直接控制”的规则
> 已被 [KB-009](KB-009-grouping-scrollbar-and-cover-loading.md) 第 1 条取代——分组只在按
> “专辑”或“专辑艺术家”排序时显示。本文其余内容（封面栏、封面尺寸持久化、分割线、进度条/音量）仍有效。

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
  这样线也覆盖最后一列右侧的空白。`RowSeparatorBrush` 浅色 `#EDF0F4`、深色 `#1E242C`。
- 封面 512px 解码（`TrackRowViewModel.GroupCoverImage`），分组虚拟化卸载时清空引用；
  封面调大后列总宽可能超过窗口，超出部分裁切（分组视图无横向滚动）。
- 分组/平铺切换：规则见 KB-009 第 1 条——`IsGroupedViewActive = GroupByAlbum && 按“专辑/专辑艺术家”排序`；
  `ApplySortAndGrouping()` 同时刷新两个 `ListCollectionView` 并重建分组列头/cell；播放顺序也按实际
  显示的视图取（`GetPlaybackOrder()` 用 `IsGroupedViewActive` 判断）。

## 2. 启动时布局被默认值覆盖的坑

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

## 3. 无播放时的进度条空状态

- 现象：启动后未播放时，进度条看起来“已满”、Thumb 停在最右侧。
- 原因：滑块 `Minimum=0`、`Maximum` 绑定 `DurationSeconds`，无歌曲时两者都是 0；WPF `Track`
  在 `Minimum == Maximum` 时把 Thumb 放在右端、把 Decrease 区画满，所以像已播放完。
- 修复：`MainViewModel.SeekMaximum => DurationSeconds > 0 ? DurationSeconds : 1`，`Maximum`
  绑定它；空闲时 `Maximum=1, Value=0`，Thumb 在最左侧、轨道为空。`DurationSeconds` 变化时
  手动通知 `SeekMaximum`。
- 同时把进度条 `IsEnabled` 绑定到 `HasCurrentTrack`，无播放时不可拖动（避免拖动 Thumb 把
  `Value` 的 OneWay 绑定顶掉）；Slider 样式补了 `IsEnabled=False` 时 `Opacity=0.45`。

## 4. 音量百分比与静音状态

- 滑块右侧显示 `VolumeText`：未静音显示四舍五入百分比（如 `70%`），静音时显示“静音”。
- 静音按钮：`Content` 绑定 `MuteGlyph`（🔊 / 🔇）、`ToolTip` 绑定 `MuteToolTip`
  （静音 / 取消静音）；按钮样式带 `DataTrigger`，`IsMuted=True` 时背景用
  `AccentSoftBrush`、前景用 `AccentBrush`。
- `IsMuted` 只是会话状态，不写入 `settings.json`（`Volume` 仍持久化）；静音时
  `Player.Volume=0`，取消静音恢复 `Volume/100`。
- 相关 `AutomationId`：`VolumeSlider`、`VolumeText`、`MuteButton`，方便 UI 自动化验证。

## 5. 验证

- 单元测试：`ViewLayoutTests.Normalize_ClampsGroupedCoverSize_AndRoundTrips`（默认值 / clamp /
  序列化 / Clone）。
- UI：`--screenshot` 截分组布局（浅色 / 深色）：分组头单行、封面在左侧封面栏纵向覆盖
  整组歌曲、歌曲列在右侧、行间 1px 分割线到列表右缘；拖动“封面”栏右缘后重启检查
  `libraryLayout.groupCoverSize` 保持。
- 平铺视图（取消分组）用 `--screenshot` 验证 DataGrid 正常、分割线覆盖整行。
- 无播放进度条截图确认 Thumb 在最左侧、轨道为空；播放中用真实鼠标点击跳转并检查
  `Seeked to` 日志。
