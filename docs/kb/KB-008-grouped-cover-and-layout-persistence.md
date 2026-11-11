# KB-008：分组列表布局（封面+单行标签+分割线）、布局持久化、进度条空状态与音量/静音

- 状态：已批准（Accepted，2026-11-10，owner 指示记录为 KB，不需要 ADR；2026-11-11 更新）
- 日期：2026-11-10
- 关联：ADR-005（播放列表显示格式）、ADR-006（配置与媒体库分离）
- 适用范围：`PlaylistViewLayout`、`MainWindow` 列构建 / 分组头 / 行模板、底部进度条与音量区

## 1. 分组列表布局（v2：单行标签 + 封面与歌曲列表并列）

用户反馈旧分组头“封面 96px + 标题/艺术家/年份 3 行堆叠”信息密度太低，目标是：

- 分组头只占一行：专辑名（Accent 色、SemiBold）+ 专辑艺术家 + 分隔线 + 年份（右对齐），
  高 30px，底部 1px 分割线；不再在分组头里放封面。
- 封面放到该专辑**第一行歌曲**的“封面”格中，与歌曲列表并列。该行高度 =
  `GroupCoverSize + 17`（上下各 8px 边距 + 1px 底部分割线），其余行仍为 32px；
  这样封面在视觉上位于分组标题下方、紧挨歌曲列表，而不是把整块头部撑高。
- 实现：`MainViewModel.ApplySortAndGrouping()` 每次刷新后调用
  `UpdateAlbumGroupStarts()`，遍历 `_tracksView.Groups` 把每组第一行的
  `TrackRowViewModel.IsAlbumGroupStart` 置 true；`Controls.xaml` 的 `DataGridRow`
  样式用 `DataTrigger` 命中该属性，把行高绑定到 `GroupCoverRowHeight`。
- 封面列在分组模式下使用 `Cell.CoverGrouped` 模板（`IsAlbumGroupStart` 时显示
  `GroupCoverImage`，尺寸 512px 解码）；`GroupCoverCell` 样式把 Cell 的 Padding 设为 0，
  封面 Border `Margin="8"` 正好落在“封面”列内。
- 封面尺寸仍由“封面”列宽驱动：`GroupCoverSize` 表示封面实际显示尺寸，
  列宽 = `GroupCoverSize + 16`（左右各 8px 内边距）；拖动列宽时
  `GroupCoverSize = 列宽 - 16`。范围 48–320，随播放列表布局持久化。
- 歌曲之间加 1px 浅色分割线：`DataGridCell` 模板的 Border 使用
  `BorderBrush="{DynamicResource RowSeparatorBrush}" BorderThickness="0,0,0,1"`；
  `RowSeparatorBrush` 在两个主题字典中分别定义（浅色 `#EDF0F4`、深色 `#1E242C`）。
- 封面调大后列总宽可能超过窗口，出现横向滚动条属正常。

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
- UI：`--screenshot` 截分组布局（浅色 / 深色）：分组头单行、封面在第一行歌曲左侧、
  行间 1px 分割线；拖动封面列后重启检查 `libraryLayout.groupCoverSize` 保持。
- UI Automation 读行高：分组首行应为 `GroupCoverSize + 17`，其余 32；调度行文本位置确认
  封面与歌曲同排。
- 无播放进度条截图确认 Thumb 在最左侧、轨道为空；播放中用真实鼠标点击跳转并检查
  `Seeked to` 日志。
