# KB-012：分组封面尺寸绑定加固与状态栏双击跳转（提案）

- 状态：待 owner 批准（Proposed，2026-11-12）
- 日期：2026-11-12
- 相关：KB-008（分组封面布局与持久化）、KB-010（封面加载生命周期 / 状态栏播放信息 / 播放行自动居中）

## 背景与现象

### 1. 分组视图偶发“巨大封面”

owner 反馈并附截图（v0.1.0，150% DPI，窗口约 1677×1027 DIP）：按专辑分组的媒体库中，偶尔有某个专辑组的封面在左侧封面栏里被渲染成约 512 DIP 的巨大方形，把该组的歌曲列整体推到右侧；其他组正常。日志里没有绑定错误，也没有 `[WARN]` / `[ERROR]`。

从 owner 的运行日志可以确定发生时间点：封面大小刚被拖动（116.7 DIP，连续 `Group cover size:` 日志）之后，正在播放曲目触发自动居中、列表滚动到该组附近时出现。本机按同样操作（同一媒体库、同样窗口与 DPI）连续测试未能重现，推断与分组容器回收 / 重新挂树的时序有关。

### 2. 状态栏双击跳转（owner 新需求）

owner 要求：双击状态栏左侧“正在播放”信息（`♪ 标题 — 艺术家 — 专辑`）时，如果该曲位于正在浏览的播放列表中，就自动跳转显示位置到该歌曲所在处，并显示在中心位置（与切歌时的自动居中一致）。此前状态栏文本没有点击行为。

## 原因分析（巨大封面）

`AlbumGroupContainer`（GroupItem 模板）里两处关键绑定都使用了祖先相对源：

- 封面栏列宽：`{Binding DataContext.GroupCoverColumnWidth, RelativeSource={RelativeSource AncestorType=Window}}`
- 封面尺寸：`{Binding DataContext.GroupCoverSize, RelativeSource={RelativeSource AncestorType=Window}}`

WPF 分组虚拟化的 GroupItem 容器在回收 / 重新生成时，可能先进入模板展开与测量、之后才挂到 Window 的可视化树上（或挂树期间祖先链短暂不完整）。祖先相对源一旦在解析时找不到 Window，绑定会保持未解析值：`Width` / `Height` 变成 `NaN`，`ColumnDefinition.Width` 退回 `Auto`。封面 Border 于是按其内容（`GroupCoverImage`，解码宽度 512px）的自然尺寸撑开——在 96 DPI 下约 512 DIP，正好对应截图里约 528 DIP 的巨大方块；同时列宽退回 Auto 后不再约束封面栏，歌曲列被推到右侧。之后即使容器挂树完成，祖先绑定也不会可靠地重新解析，异常状态就会一直保留。

## 修复方案

### A. 封面尺寸改为“可继承附加依赖属性”（根因修复）

新增 `src/EchoGarage.App/Views/GroupCoverMetrics.cs`：

- 附加属性 `CoverSizeProperty`（`double`，默认 `PlaylistViewLayout.DefaultGroupCoverSize = 96`），`FrameworkPropertyMetadataOptions.Inherits`，强制转换时把 `NaN` / `Infinity` 归为默认值并夹紧到 `[PlaylistViewLayout.MinGroupCoverSize = 48, MaxGroupCoverSize = 320]`。
- 值从 Window 向下继承（挂在 `GroupedList` 上即可），模板内绑定改为 `{Binding Path=(views:GroupCoverMetrics.CoverSize), RelativeSource={RelativeSource Self}}`——不依赖祖先链，容器回收 / 延迟挂树都不会失效。
- 封面栏 `ColumnDefinition` 改为 `Width="Auto"`，宽度完全由封面 Border（固定尺寸）决定，不再依赖第二个可能失效的绑定。
- `MainWindow` 在启动时与 `MainWindowViewModel.GroupCoverSize` 变化时把值写入 `GroupedList`（`ApplyGroupCoverMetrics()`），拖动表头滑柄仍实时生效。

### B. 组容器加载时自愈并留证

`OnGroupCoverLoaded` 在请求封面之外调用 `RepairGroupCoverSizeIfNeeded`：若元素实际宽度与期望值相差超过 0.5 DIP（或为 `NaN`），写 `[WARN] Album group cover size was not applied (width=…, expected=…, group=…)` 并把 `Width` / `Height` 修正为期望值。这样即使未来出现新的时序问题，界面也会自愈，日志可直接定位。

### C. 状态栏双击跳转

- 状态栏 `NowPlayingStatus` TextBlock 增加 `Cursor="Hand"`、`Background="Transparent"`（整块区域可命中，原先点在文字间隙不触发）与 `MouseLeftButtonDown` 处理；ToolTip 补充“双击跳转到正在播放的歌曲”（仅在播放时显示）。
- 处理逻辑（`MainWindow.NowPlaying.cs`）：双击（`ClickCount >= 2`）→ 取 `MainViewModel.CurrentRow`：
  - 没有正在播放：状态栏提示“当前没有正在播放的歌曲。”；
  - 正在播放但不在当前列表：提示“正在播放的歌曲不在当前列表中。”，不改变滚动位置；
  - 在列表中：走与切歌自动居中相同的 `ScheduleCenterCurrentRow()`（Dispatcher 延后、滚动条交互期间让路、像素级居中修正），并写 `[DEBUG]` 日志。

## 验证

- 真实鼠标拖动封面宽度到最小（48.7 DIP）与最大（320 DIP），分别滚动 60 / 80 个滚轮格（触发分组容器回收），截图逐一检查：所有封面尺寸一致，无巨大封面；日志无 `[WARN]` / `[ERROR]`。
- 封面宽度 320 DIP 持久化后重启：启动日志 `Applying layout: groupCoverSize=320.0 …`，`--screenshot` 渲染正确。
- 状态栏双击：找到播放行（分组视图），先把列表滚回顶部并确认播放行不在视口内，双击状态栏 → 列表滚回并居中该行（日志 `Centered now-playing row: … error=…->0.3px`）；切换到不含该曲的“默认播放列表”后双击 → 仅状态栏提示，滚动位置不变（日志 `playing row is not in the current list`）。
- 单元测试 82 项：81 通过、1 跳过（HKCU 注册表相关，需交互桌面）。

## 局限与后续

- 未能稳定复现原始“祖先绑定失效”的时序，A 方案是根因级修复（去掉依赖），B 方案是兜底自愈与取证。若再次出现，日志中的 `Album group cover size was not applied` 会给出实际宽度、期望值与分组名。
- 若未来新增其他依赖 `RelativeSource AncestorType=Window` 的模板内绑定，应优先考虑可继承附加属性或 `ElementName` 之外的方式，避免同类容器时序问题。

## 参考

- `src/EchoGarage.App/Views/GroupCoverMetrics.cs`
- `src/EchoGarage.App/Views/MainWindow.xaml`（AlbumGroupContainer、状态栏）
- `src/EchoGarage.App/Views/MainWindow.xaml.cs`（ApplyGroupCoverMetrics、RepairGroupCoverSizeIfNeeded）
- `src/EchoGarage.App/Views/MainWindow.NowPlaying.cs`（OnNowPlayingStatusMouseLeftButtonDown）
- `docs/kb/KB-008-grouped-cover-and-layout-persistence.md`、`docs/kb/KB-010-cover-loading-lifecycle-statusbar-and-auto-center.md`
