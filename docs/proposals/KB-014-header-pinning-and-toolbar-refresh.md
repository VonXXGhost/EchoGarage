# KB-014 分组表头左右固定、列宽拖拽边界与顶栏/图标刷新

> 状态：提案（待 owner 批准后移入 `docs/kb/`）
> 日期：2026-11-13
> 相关：KB-008（分组封面尺寸与布局持久化）、KB-012（表头分隔线与列宽拖拽的祖先绑定加固）、ADR-005（每个播放列表独立记忆显示格式）

## 背景

owner 在分组表头分隔线 + 列宽拖拽（commit d13a0d3）之后提出五项改进：

1. 表头拖拽右侧需要边缘限制，不能把列拖出可视范围；
2. 表头需要区分“左固定 / 右固定”。目前所有列都是左固定；如果把“时间”设为右固定，拖动它左侧的边界线时应向左压缩“专辑”的空间，而不是像现在这样移动右侧空间。这种特殊调整逻辑只影响左 / 右固定的边界表头；
3. 鼠标悬浮在音量条区域时，滚轮应以 5% 为步进调节音量；
4. 重新设计程序图标：不要黑底，以三角形播放按钮为概念；
5. 移除左上角品牌文字（`EchoGarage v0.1.0`），只保留菜单，并且鼠标移到菜单按钮上就自动弹出菜单。

## 实现

### 列固定模型（Core）

- `EchoGarage.Core.Models.ColumnPin`：`Left = 0`（默认，从封面栏之后左流）、`Right = 1`（贴列表右缘）。
- `PlaylistColumn.Pin` 随布局序列化；`Clone()` 一并复制。
- `ViewLayoutFactory.Normalize`：隐藏列强制 `Pin = Left`（隐藏列没有可视边界，保留右固定只会让旧布局在恢复可见时行为怪异）。旧布局没有该字段时反序列化为默认 `Left`，向后兼容。

### 表头与行的左右两块（App）

- `GroupedColumnHeaderViewModel` 增加 `Pin`、`IsLeftPinned`、`IsRightPinned`、`ShowResizeGripper`、`ShowLeftBoundaryGripper`；`TrackRowViewModel` 增加 `RightCells`。
- `MainViewModel.GroupedColumns` 拆为 `GroupedLeftColumns` / `GroupedRightColumns`；`RebuildGroupedView` 按 `Pin` 分派表头与行单元格。右固定块最后一个列不显示右缘滑柄（它的右缘就是列表边界，拖它会脱离锚点）。
- 表头 Grid 改为三列：封面栏（`GroupCoverColumnWidth`）/ 左流（`*`，左对齐）/ 右固定块（`Auto`，右对齐）。
- 行模板改为 Grid 两列：左流 + 右固定块（`RightCells`），两者共用 `GroupedRowCell` 模板，宽度仍绑定表头实例的 `Width`，拖动时实时同步。

### 右固定块的左边界

- 第一个右固定列额外显示一个左缘滑柄（`ShowLeftBoundaryGripper`）。拖动它时修改的是**该右固定列的宽度**（`OnGroupedBoundaryThumbDragDelta`）：向左拖变宽、右块原地不动，左侧列被压缩 —— 即 owner 要求的“向左压缩专辑的空间”。松手后只持久化这一列（`PersistGroupedColumnWidth`）。
- 由于右块贴着视口右缘、右缘滑柄隐藏，右固定列不能通过“移动右块”而越界。

### 边缘钳制（`MaxAllowedGroupedColumnWidth`）

- 左固定列：上限 = 左流实际可用宽度（`GroupedHeaderGrid.ColumnDefinitions[1].ActualWidth`）减去同侧其它列宽度和。
- 右固定列：上限 = 列表宽 − 封面栏宽 − 右侧滚动条 inset − 左侧各列**最小宽度之和** − 其它右固定列宽度和；这样向左拖动边界时最多把专辑压到自己的最小宽度，不会整列被裁掉。
- 两端都取 `max(当前宽度, 上限)`：空间本来就装不下时，拖动不会反而让列缩小（旧公式 `Math.Max(MinWidth, available)` 会导致该问题，真机测试已复现并修正）。
- 封面栏滑柄同样钳制：`groupCoverSize` 不超过“列表宽 − 16 − 内容区最小宽度（160）”与 48–320 的公共范围。

### 右侧固定块与滚动条对齐

- `UpdateGroupedHeaderInset()` 在视觉树里递归查找分组列表的竖直 `ScrollBar`，可见时把右固定表头的右边距设为滚动条实际宽度，使表头右块与行内容右缘对齐；在应用布局、`GroupedList.SizeChanged`、以及节流（250 ms）的滚动事件中刷新，变化时写 DEBUG 日志 `Grouped header inset: …`。

### 其余三项

- 音量滚轮：音量区 StackPanel 监听 `PreviewMouseWheel`，按 5% 对齐（`Math.Round((Volume + step) / 5) * 5`，clamp 0–100），向上滚动自动取消静音；事件标记为已处理，不会连带滚动列表。
- 菜单悬停：菜单按钮的 `MouseEnter` 复用点击时的 `OpenMenuButtonMenu`（同一按钮的菜单已展开时直接返回，避免闪烁）；点击行为保留。
- 图标：重做 `src/EchoGarage.App/Resources/echogarage.ico`，透明底、深色圆环 + 实心右向播放三角，16/20/24/32/40/48/64/128 用 BMP 条目、256 用 PNG 条目（9 个尺寸），符合 owner 的概念图。

## 验证

- 构建：`dotnet build EchoGarage.sln -nodeReuse:false -p:UseSharedCompilation=false` 0 警告 0 错误。
- 单元测试：84 项 = 83 通过 / 1 跳过（HKCU 注册表）/ 0 失败；新增 `Normalize_PreservesRightPinOnlyForVisibleColumns`、`SerializeRoundTrip_PreservesColumnPin`。
- 真机鼠标验证（隔离数据根 `.scratch\switch-bug`，音量 0，自动化前 10 s 系统通知 + 聊天提示，结束后恢复前台与光标）：
  - 音量滚轮：0% → 5%（settings.json `volume=0.05`）；
  - 拖拽边缘限制：窄窗口（列表约 489 DIP）里把“标题”向右拖 200 DIP，宽度保持 180 不变，日志 `Grouped column drag clamp: … max=180.0`；
  - 右固定边界：拖动右固定“时间”的左边界 100 DIP，日志 `Grouped column width: duration=162.0`（另一轮窄窗口测试在空间不足时停在 78，即左侧列最小宽度之和 282 之后不再压缩）；
  - inset 生效：日志 `inset=17.3`，右固定块与行内容右缘对齐；
  - 菜单悬停：鼠标移入“文件”按钮即展开菜单；
  - 截图：`28-wide-pins.png`（1677×1027，标题 380、专辑 170、时间右固定 162）确认左右分块、分界线与行/表头对齐正常。
- 图标：`icon-256.png` 目视确认透明底圆环 + 播放三角；构建后嵌入 exe。

## 局限与后续

- 窄窗口下如果把大量列设为右固定，左侧列会先被压缩到各自最小宽度，再停止变宽；窗口更窄时仍可能出现列裁切（与之前行为一致，没有横向滚动条）。
- 列固定方式按来源布局记忆；切换来源时不会重新推断，仍遵循“每个播放列表 / 媒体库独立记忆显示格式”的既有规则（ADR-005）。
- 本改动不涉及数据库 schema。
