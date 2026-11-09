# KB-003：WPF 深色主题实现中的坑

- 状态：已批准（Accepted，2026-11-09，owner）
- 日期：2026-11-09
- 环境：.NET 10 WPF，PerMonitorV2 DPI

## 1. TextBox 有固定高度时文字“消失”

自定义 `TextBox` 模板时，如果 `Border` 使用 `Padding="8,6"` 且控件 `Height="28"`，
内容区只剩约 14px，`TextBoxView` 被裁掉，视觉上完全空白（数据其实已经绑定成功，很容易误判为绑定失败）。

正确写法：`Padding="8,0"`，垂直居中交给 `VerticalContentAlignment="Center"`，模板中
`<ScrollViewer x:Name="PART_ContentHost"/>` 放在带 `Padding="{TemplateBinding Padding}"` 的 Border 里。

## 2. DataGrid 没有 `LoadingRowGroup` 事件

`DataGrid` 只有 `LoadingRow` / `UnloadingRow`，分组头没有对应的加载事件（`DataGridRowGroupHeader` 在
`System.Windows.Controls` 且事件不通过 DataGrid 暴露）。

加载分组头封面：在 `GroupStyle.HeaderTemplate` 的根元素上用 `Loaded="OnGroupHeaderLoaded"`，
在处理器里取 `((FrameworkElement)sender).DataContext as CollectionViewGroup`，再对 `Items[0]` 的
`TrackRowViewModel` 触发封面加载。

## 3. 分组 + 虚拟化

`DataGrid` 开启分组后必须同时设置 `VirtualizingPanel.IsVirtualizingWhenGrouping="True"`，
否则几千首会一次性实例化所有行。

## 4. 高 DPI 下外部截屏不可靠

在 150% 缩放的屏幕上，外部 `PrintWindow` 得到的是按 DIP 尺寸裁剪后的位图，内容看起来“放大且被截断”，
不能用于判断布局。改用应用内 `RenderTargetBitmap`（见 `MainWindow.SaveScreenshot` / `--screenshot` 开关），
按 DIP 1:1 渲染。

## 5. 深色标题栏

WPF 没有内置的深色标题栏属性，需要对 HWND 调用 `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE = 20)`，
在旧版本上回退到属性 19；失败时保持系统默认即可。对话框统一继承 `ThemedWindow`。

## 6. 自定义 ComboBox / ContextMenu 样式

- `ComboBox` 的 `Popup` 必须 `AllowsTransparency=True` 且给 `MinWidth` 绑定 `ActualWidth`，否则下拉框宽度会塌陷。
- `ContextMenu` 不在可视树内，不会自动继承 `DataContext`。代码里打开菜单前要显式 `menu.DataContext = vm`。
- 自绘 `MenuItem` 的模板只处理 SubmenuItem 即可；本项目不使用顶层 `Menu`，菜单栏用普通按钮 + ContextMenu 实现，
  避免为 TopLevelHeader/SubmenuHeader 维护两套模板。

## 7. 窗口尺寸与多显示器

`Window.Width/Height` 是 DIP；保存 / 恢复时要与 `SystemParameters.WorkArea`（也是 DIP）比较并夹取，
否则换到小屏后窗口会出现在屏幕外。恢复位置时若越界，回退到居中。

## 8. “保存列宽”必须在正确时机读 DataGrid

自绘列时，列宽只存在于 `DataGridColumn.Width/ActualWidth`。如果用“切换列表前保存”的思路，
要区分三种情况：

- **启动早期**：Grid 尚未构建列（`Columns.Count == 0`），此时抓取会把所有列标成隐藏，
  下次加载就只剩一列。必须直接返回 `null`，保留原布局。
- **应用预设 / 列设置后**：界面仍是旧列，若抓取实际宽度会覆盖新预设的宽度。应显式跳过抓取。
- **正常切换 / 排序 / 关闭**：此时抓取是安全的，且能保存用户拖动的宽度和列顺序。

实现上通过 `Func<PlaylistViewLayout?>` 由窗口提供“当前列布局快照”，并在
`PersistCurrentLayout(..., captureFromView: false)` 时跳过。

## 9. 运行中重建 `DataGrid.Columns` 会崩

现象：切换播放列表（或应用预设）时清空并重建 `DataGrid.Columns`，偶尔抛
`ArgumentOutOfRangeException: Index was out of range`，堆栈在
`DataGridCellsPanel.ArrangeOverride`。这是行单元格与列集合不同步的经典问题。

解决：

1. 重建前先 `ItemsSource = null` 卸掉所有行，重建列后再重新绑定（本项目用
   `BindingOperations.SetBinding` 恢复绑定）。
2. 只在“可见列 / 顺序 / 宽度”真的变化时才重建：用列签名比较，切换播放列表但布局相同则不重建。
3. 重建时保存并恢复 `SelectedItem`（卸载 ItemsSource 会把选中项清空）。

验证方式：UI Automation 选中侧栏 9 个来源反复切换、通过“预设”菜单把列数从 7 切到 5，
日志中不再出现 `Unhandled UI exception`，窗口保持响应。
