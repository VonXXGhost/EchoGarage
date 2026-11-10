# KB-008：分组封面尺寸、布局持久化、进度条空状态与音量/静音

- 状态：已批准（Accepted，2026-11-10，owner 指示记录为 KB，不需要 ADR；2026-11-11 更新）
- 日期：2026-11-10
- 关联：ADR-005（播放列表显示格式）、ADR-006（配置与媒体库分离）
- 适用范围：`PlaylistViewLayout`、`MainWindow` 列构建与分组头、底部进度条与音量区

## 1. 分组封面尺寸（GroupCoverSize）

- `PlaylistViewLayout` 增加 `GroupCoverSize`（double，默认 96，范围 48–320），表示**实际显示的
  封面边长**，随布局一起序列化：媒体库视图存 `settings.json` 的 `libraryLayout`，普通播放列表
  存各自 `ViewLayoutJson`；旧 JSON 没有该字段时按 96 处理（`ViewLayoutFactory.Normalize` 里
  `<= 0` 视为缺失，超出范围则 clamp）。
- 分组模式下封面列宽 = `GroupCoverSize + 16`（左右各 8px 内边距）；列的最小 / 最大宽度相应为
  64 / 336。拖动表头右缘时 `GroupCoverSize = 列宽 - 16`。该值**不写回**普通模式下的封面列宽，
  取消分组后逐行封面仍用原列宽（行内图片固定 24px，与行高 32px 匹配）。
- 对齐：分组头模板 `Padding="8,10"`，与 `DataGridColumnHeader` 的 `Padding="8,0"` 一致，因此
  封面在封面列内的左右留白相等，正好和“封面”表头文字对齐，右缘不会越过分栏线。
- 实时联动：`MainWindow` 用 `DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, ...)`
  监听封面列的宽度变化，写入 `MainViewModel.GroupCoverSize`（ObservableProperty）；
  分组头模板的封面 `Border` 通过
  `{Binding DataContext.GroupCoverSize, RelativeSource={RelativeSource AncestorType=Window}}`
  绑定宽高，所以拖动时封面跟手变化。
- 持久化：拖动停下后 600ms 防抖调用 `PersistCurrentLayout(CaptureLayoutFromGrid())`；
  `CaptureLayoutFromGrid` 在分组模式下把 `封面列 ActualWidth - 16` 写进 `GroupCoverSize`，
  普通列宽照旧。关闭窗口 / 切换播放列表仍走原有保存路径。
- 列与布局对话框只编辑列，`GroupCoverSize` 原样透传；应用预设时保留当前值（预设不包含
  封面尺寸，`ColumnPreset` 未增加字段）。
- 分组头封面单独解码：`TrackRowViewModel.GroupCoverImage` 以 512px 请求
  （`CoverImageService` 按 `尺寸|coverKey` 缓存，LRU 300），在 320 DIP、150% DPI 下仍清晰；
  分组头虚拟化卸载时（模板根 `Border.Unloaded`）清空引用，避免滚过所有专辑后大图常驻。
- 注意：封面调大后列总宽可能超过窗口，出现横向滚动条属正常；用户可加宽窗口或隐藏其他列。

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
  `Value` 的 OneWay 绑定顶掉）；Slider 样式补了 `IsEnabled=False` 时 `Opacity=0.45`，
  空状态一眼可辨。

## 4. 音量百分比与静音状态

- 滑块右侧显示 `VolumeText`：未静音显示四舍五入百分比（如 `70%`），静音时显示“静音”。
- 静音按钮：`Content` 绑定 `MuteGlyph`（🔊 / 🔇）、`ToolTip` 绑定 `MuteToolTip`
  （静音 / 取消静音）；按钮样式带 `DataTrigger`，`IsMuted=True` 时背景用
  `AccentSoftBrush`、前景用 `AccentBrush`，状态一眼可见。
- `IsMuted` 只是会话状态，不写入 `settings.json`（`Volume` 仍持久化）；静音时
  `Player.Volume=0`，取消静音恢复 `Volume/100`。
- 相关 `AutomationId`：`VolumeSlider`、`VolumeText`、`MuteButton`，方便 UI 自动化验证。

## 5. 验证

- 单元测试：`ViewLayoutTests.Normalize_ClampsGroupedCoverSize_AndRoundTrips`（默认值 / clamp /
  序列化 / Clone）。
- UI：`--screenshot` 分别截默认 96px 和拖动后 176px 的分组封面，检查封面左右留白相等、
  与“封面”表头对齐且不越过分栏线；截无播放时的进度条，确认 Thumb 在最左侧、轨道为空。
- 静音状态截图确认“静音”文字 + 高亮按钮；重启后 `libraryLayout.groupCoverSize` 保持。
- 鼠标拖拽用 UI Automation 定位表头 `Thumb` 后 `SetCursorPos + mouse_event` 模拟，
  拖完读 `settings.json` 确认保存。
