# KB-010：封面加载生命周期、播放信息状态栏与播放行自动居中

- 状态：**待 owner 批准**（提案，2026-11-11 起草；批准后移动到 `docs/kb/`）
- 日期：2026-11-11
- 适用版本：v0.1.0 后续迭代
- 涉及文件：`CoverImageService`、`FileCoverCache`、`MainWindow`（分组封面事件、状态栏、自动居中）、
  `MainViewModel`（`NowPlayingStatusText` / `HasStatusHint`）、`Controls.xaml`（TransportButton）、
  `MainWindow.NowPlaying.cs`（新增，居中辅助）
- 与既有文档的关系：KB-009 第 3、4 节（滚动条滑块、播放封面加载态）仍然有效；
  本文第 1 节修正 KB-009/008 中“分组封面只在分组容器 Loaded 时加载”的实现方式。

## 1. 背景（2026-11-11 owner 反馈）

1. 拖动列表滚动条时，列表封面加载有时不生效（部分专辑封面一直停在灰色占位块）。
2. 播放按钮周围出现多余的虚线焦点框。
3. 播放器上方的状态栏（“就绪”处）改为显示当前播放曲目信息，去掉右侧媒体库/列表统计；
   并新增“播放歌曲位于当前浏览列表时，自动跳转并把该行显示在列表中央”。

## 2. 封面加载问题的根因与修复

### 2.1 根因 A：分组视图的封面只在 `Loaded` 事件里加载，虚拟化下的生命周期不可靠

- 分组视图是 `ListView` + `GroupStyle` + `VirtualizingPanel.IsVirtualizingWhenGrouping="True"`。
  `GroupItem` 容器在测量 / 虚拟化复用的不同阶段，`Loaded`、`DataContextChanged`、`IsVisibleChanged`
  的触发时机与次数并不一致：快速拖动 / 跳跃滚动时，部分容器在 `Loaded` 触发时还没有可用的
  `DataContext`（分组），或者根本没有再触发 `Loaded`，导致封面加载请求从未发出，
  封面栏保持灰色占位（`CoverPlaceholderBrush`）。
- 实测（临时诊断，未提交）：一次滚动跳跃会创建约 50 个 `GroupItem` 容器的 DataContext，
  但只触发个位数 `Loaded`；可见分组中确实存在“只有 DataContext、没有 Loaded”的容器。

### 2.2 根因 B：内存负缓存把“瞬时失败”永久记为“没有封面”

- `CoverImageService` 原来在任何异常/解码失败时执行 `Remember(memoryKey, null)`：
  一次临时的文件锁、半写文件或解码失败，会让该专辑封面在**本次运行内永远空白**，
  后续滚动回来也不会重试。
- 同时，同一个封面的并发请求没有合并：快速滚动时同一专辑的多行/分组会重复排队提取与解码，
  拉长其他封面出现的时间。

### 2.3 根因 C：封面缓存文件不是原子写入

- `FileCoverCache.WriteAsync` 直接写目标文件；并发请求或另一个解码宽度（128/420/512）同时读取时
  可能读到写了一半的图片，解码失败后再被 2.2 的负缓存永久记住。
- `Resolve` 只要文件存在就返回，0 字节残留文件同样会走上“解码失败 → 负缓存”的路径。

### 2.4 修复

1. **加载触发改为三条路径**（`MainWindow.xaml` + `MainWindow.xaml.cs`）：
   - 分组封面 Border 同时监听 `Loaded`、`IsVisibleChanged`、`DataContextChanged`；
   - `IsVisibleChanged` 是唯一在所有容器生命周期路径都会触发的事件：容器进入可见树时加载，
     离开可见树时释放该组的大封面（`GroupCoverImage = null`），避免滚动整库后内存持续增长；
   - `DataContextChanged` 处理容器被复用时的新分组（同时释放旧分组的引用）；
   - 三条路径都会经过 `LoadGroupCoverAsync` 的“已有图直接返回”判断，配合 2 的并发合并不会重复解码。
2. **`CoverImageService` 可靠性改造**：
   - 同一 `(decodeWidth, coverKey)` 的并发请求用 `Lazy<Task<BitmapSource?>>` 合并为一次提取 + 解码
     （single-flight），完成后从字典移除；
   - 只有“封面缓存确认没有封面”（负缓存标记命中、无可用图片字节）才记住 `null`；
     瞬时提取/解码失败只记 `Warn`，不写入内存缓存，下次请求会重试；
   - 内存 LRU（300 张）行为保持不变。
3. **`FileCoverCache` 原子写入**：先写 `*.tmp<guid>`，再 `File.Move(..., overwrite: true)` 替换；
   `Resolve` 忽略 0 字节文件；提取/写入异常不再在缓存层吞掉，改为向上抛出，由图像服务决定不缓存失败结果。

## 3. 状态栏改为“播放信息条”

- 位置：主窗口底部、传输栏上方原 30px 状态栏。
- 左侧（`NowPlayingStatusText`）：有当前曲目时显示 `♪ 标题 — 艺术家 — 专辑`（缺失字段自动跳过），
  单行 `CharacterEllipsis` + ToolTip；没有当前曲目时显示“就绪”。
- 右侧：删除 `LibrarySummary`（媒体库 N 首 / N 个文件夹 / N 个播放列表）统计；
  保留扫描进度条，瞬时状态消息（扫描、导入、错误等）只在 `StatusMessage != "就绪"` 时显示
  （`HasStatusHint`），4 秒后自动回到不显示。
- 媒体库统计仍可在右侧“正在播放”面板与设置中查看，不丢失信息。

## 4. 播放行自动居中

- 需求：播放歌曲位于当前正在浏览的列表时，列表自动滚动，并把该行显示在视口中央。
- 实现（`MainWindow.NowPlaying.cs`，partial class）：
  - `CurrentRowChanged` 与 `ApplyLayout()`（切源、列布局变化）都会调用 `ScrollToCurrentRow()`
    → `ScheduleCenterCurrentRow()`，用 `DispatcherPriority.Loaded` 延迟一帧，让列表先完成容器生成；
  - 仅当 `CurrentRow` 属于当前视图的集合（分组视图 `GroupedList.Items` / 平铺 `TrackGrid.Items`）时执行，
    浏览其它播放列表时不打扰滚动位置；
  - 平铺视图用 `DataGrid.ItemContainerGenerator`；分组视图的顶层容器是 `GroupItem`，
    需在视觉树中按 `DataContext` 找到 `ListViewItem`；
  - 先 `ScrollIntoView` 保证容器存在，再用“行上方半个视口高的 `BringIntoView` 矩形”把行推到中央，
    最后按像素误差最多修正 3 次；逻辑滚动（`CanContentScroll=True`）下用视口高度换算每个滚动单位对应像素。
- 行为说明：双击播放、上一首/下一首、自动连播、切到包含当前歌曲的播放列表时，列表会居中到该行；
  改列宽/列顺序引起的布局重建也会重新居中（沿用原 `ScrollToCurrentRow` 的调用点）。

## 5. 传输栏按钮不再显示虚线焦点框

- 原因：WPF 默认 `FocusVisualStyle` 会在获得键盘焦点的按钮内画虚线矩形（播放按钮为圆形，虚线框尤其显眼）。
- 处理：`Controls.xaml` 的 `TransportButton` 样式增加 `FocusVisualStyle = {x:Null}`（`PlayButton` 继承它），
  悬停/按下/禁用反馈保持不变。
- 取舍：键盘 Tab 到传输按钮时不再有虚线焦点提示；如以后需要可访问性焦点提示，
  建议改为主题令牌色的细描边（不使用系统虚线框）。

## 6. 验证

- 单元测试：82 个（81 通过 + 1 个真实注册表测试在受限会话跳过，0 失败）。
  新增封面缓存回归测试：`CoverCache_ResolveIgnoresZeroLengthFile_AndReextracts`、
  `CoverCache_WritesAtomicallyAndLeavesNoTempFiles`（`tests/EchoGarage.Tests/TagAndCoverTests.cs`）。
- 封面加载（应用内临时诊断，未提交）：
  - 2825 首真实库 + owner 配置（150% DPI）：启动、连续切换 5 次排序、模拟拖动滚动条 160 步到中/底部，
    可见分组封面全部加载；滚动返回后仍正确；冷缓存（空封面缓存目录）首次提取 1,300+ 张封面
    无 `WARN`/`ERROR`。
  - 分组封面在离开可见区域后释放，不再随滚动整库累积大图。
- 状态栏/焦点框/居中：`--screenshot` 截图确认左侧 `♪ 曲名 — 艺术家 — 专辑`、右侧无媒体库统计、
  播放按钮无虚线框；平铺与分组视图下播放后当前行位于列表中轴附近（日志输出居中前后偏移误差 ≤ 2px）。
- 文档截图（`docs/screenshots/main.png`、`dark.png`）在本次修改后重新生成。

## 7. 已知限制与后续

- 封面首次提取仍受磁盘与音频文件读取速度影响；冷缓存下滚动到新专辑时可能会有短暂占位，
  但不会再永久空白，且会按可见顺序逐步补齐。
- 自动居中在“用户正在手动滚动”时不会阻止滚动（下一次切歌/布局变化时才再次居中）；
  如 owner 希望“仅当行不在视口内才居中”，可以再调整策略。
- 键盘焦点提示取舍见第 5 节；如需要可增加自定义焦点描边。
