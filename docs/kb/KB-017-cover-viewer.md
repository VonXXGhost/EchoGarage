# KB-017 封面大图浏览窗口（双击「正在播放」封面放大查看）

- 状态：**Accepted**（owner 批准：2026-11-16）
- 日期：2026-11-16
- 相关：右栏「正在播放」面板（`MainWindow.xaml` Album panel）、封面缓存（`ICoverCache` / `CoverImageService`）、
  五套主题令牌（`docs/kb/KB-015-theme-token-map.md`）

## 1. 背景与需求

右栏「正在播放」当前以 300 DIP 高的固定区域显示 420px 解码封面，用户无法看清专辑封面细节。
owner 需求原文：**“双击后可放大浏览封面”**。

约束与既有约定：

- 无封面时双击不得有任何动作（不弹空窗口）。
- 浏览窗口必须跟随主题热切换，颜色只用 `DynamicResource` 主题令牌。
- 不改变原有播放 / 列表 / 封面加载行为；双击封面不触发其它手势。
- 窗口关闭后不得残留事件订阅（避免视图模型 → 窗口的引用泄漏）。
- 浏览期间切歌时窗口内容同步更新。

## 2. 交互设计

| 操作 | 行为 |
| --- | --- |
| 双击右栏封面区域 | 打开封面浏览窗口；当前无封面时静默忽略（仅 Debug 日志） |
| 滚轮（视口内） | 以光标为锚缩放，范围 0.25x–4x；100% 表示“适应窗口” |
| 左键拖拽（视口内） | 平移图片；内容小于视口的方向保持居中 |
| 双击视口 | 复位为适应窗口（与“适应窗口”按钮一致） |
| 「适应窗口」按钮 | 缩放回 100%，偏移归零 |
| 「－」/「＋」按钮 | 以视口中心为锚按 1.25 倍步进缩放 |
| Esc / 「关闭」按钮 | 关闭窗口 |
| 窗口已打开时再次双击封面 | 恢复最小化（如有）并激活已有窗口，不重复创建 |
| 浏览中切歌 / 封面变化 | 标题、副标题、封面同步更新；新封面到达后复位缩放 |
| 新封面加载中 | 视口提示“正在加载封面…”，加载完成自动显示 |
| 主题切换 | 窗口背景、边框、文字、按钮悬停色即时跟随 |

样式细节：

- 顶部信息栏：曲目标题（半粗）、艺术家副标题，右侧缩放百分比与操作按钮；使用 `FlatButton`。
- 中部视口：1px 主题描边 + 封面底色；空 / 加载状态显示居中的提示文字。
- 底部提示行：“滚轮缩放 · 拖拽平移 · 双击图片复位 · Esc 关闭”。
- 窗口非模态（`Show()`），不阻塞播放与列表操作。

## 3. 窗口生命周期与事件退订

### 3.1 创建与单实例

- 由 `MainWindow.NowPlaying.cs` 的 `OnNowPlayingCoverMouseLeftButtonDown` 创建：
  `new CoverViewerWindow(_vm, _host.Covers, _host.Logger) { Owner = this }`，随后 `Show()`。
- `MainWindow` 用 `private CoverViewerWindow? _coverViewer` 保持唯一实例：
  - 已打开则激活（必要时从最小化恢复）；
  - `Closed` 事件把字段清空并退订该事件。
- `Owner` 设为主窗口：主窗口关闭时 WPF 自动关闭浏览窗口；窗口居中于主窗口。

### 3.2 订阅与退订

| 订阅 | 时机 | 退订时机 |
| --- | --- | --- |
| `MainViewModel.PropertyChanged` | 构造函数（`InitializeComponent` 之后） | `OnClosed` 覆盖方法 |
| 主题标题栏（`ThemeManager.ThemeApplied`） | 基类 `ThemedWindow.OnSourceInitialized` | 基类 `Closed` 处理，本窗口无需重复 |

- 过滤属性：`CurrentCover`、`IsNowPlayingCoverLoading`、`CurrentRow`、`CurrentTitle`、`CurrentSubtitle`。
- **防泄漏**：`OnClosed` 里先置 `_closed = true`，再 `-=` 事件，然后调用 `base.OnClosed(e)`。
- **过期异步结果**：每次封面变化 `_coverVersion++`；异步高清解码完成后若版本不一致或窗口已关闭则丢弃结果，
  不会把旧图写进已更新或已关闭的窗口。
- 窗口只持有 `MainViewModel` / `ICoverCache` / `IAppLogger` 引用，不订阅其它全局静态事件，关闭即可回收。

## 4. 主题令牌使用

| 用途 | 资源键（`DynamicResource`） |
| --- | --- |
| 窗口背景 | `WindowBackgroundBrush` |
| 顶部 / 底部工具条背景 | `HeaderBackgroundBrush` |
| 分隔线、按钮分隔线 | `BorderBrush` |
| 视口底色 | `CoverBoxBrush` |
| 视口描边 | `CoverStrokeBrush` |
| 主文字（标题、按钮） | `TextPrimaryBrush`（`FlatButton` 样式自带） |
| 副标题 / 缩放百分比 | `TextSecondaryBrush` |
| 空状态提示 / 底部提示 | `TextMutedBrush` |
| 字体 | `UiFont`（`StaticResource`） |

- 窗口根元素为 `views:ThemedWindow`，标题栏随当前深浅主题切换（基类 DWM 处理）。
- 代码与 XAML 中没有硬编码颜色；按钮复用 `Resources/Controls.xaml` 的 `FlatButton`。
- 主题热切换由 `ThemeManager.Apply` 替换合并字典实现，`DynamicResource` 自动刷新，无需窗口重建。

## 5. 缩放 / 平移实现

视口 `Grid`（`Viewport`）铺满图片区域，`Image` 设置：

- `Stretch="Uniform"`：100% 即按比例适配视口；
- `IsHitTestVisible="False"`：鼠标事件统一由视口处理；
- `RenderTransformOrigin="0.5,0.5"` + `TransformGroup(ScaleTransform, TranslateTransform)`：
  缩放围绕视口中心，平移以 DIP 偏移表达，便于与光标锚点公式配合。

缩放数学：

- 设旧缩放 `s`、新缩放 `s'`、`k = s'/s`；光标相对视口中心的位移 `d`；平移量 `t`（X/Y 独立）。
- 保持光标下图片内容不动：`t' = k·t + (1-k)·d`。
- 百分比显示为相对“适应窗口”的缩放（100% = 适应窗口）；按钮以视口中心（`d = 0`）走同一公式。

平移限制：

- 内容显示尺寸 `scaledW = fitW·s`、`scaledH = fitH·s`（`fitW/H` 由位图宽高比与视口尺寸算出）；
- 某方向 `scaled ≤ 视口` 时该方向偏移强制为 0（居中，不能拖走）；
- 否则允许偏移上限 `±(scaled - 视口)/2`，保证图片边缘不进入视口内侧。
- 窗口缩放（`SizeChanged`）后重新计算并钳制偏移，不会把图片拖出可视区。

## 6. 高清封面回退策略

- 右栏现有加载链路只按 `DecodePixelWidth = 420` 解码并进入 `CoverImageService` 的 300 条 LRU；
  直接放大到 4x 会发虚。
- 浏览窗口打开后先显示 `MainViewModel.CurrentCover`（420px 回退图），立即可用；
- 随后在后台线程用 `ICoverCache.Resolve(track.CoverKey)` 取得已解压的封面缓存文件
  （`FileCoverCache` 按原始分辨率保存），`Task.Run` + `BitmapImage(DecodePixelWidth = 1200, OnLoad)` 解码并 `Freeze`，
  回到 UI 线程替换 `Image.Source`（宽高比一致，缩放/平移不跳变）。
- 只 **读取** 封面缓存（`Resolve`），不写入 `CoverImageService` 缓存、不改变现有封面加载行为；
- 解码失败（缓存被清理、文件被移动等）写 `Warn` 日志并保留 420px 回退图，浏览不中断；
  找不到缓存文件写 `Debug` 日志。

## 7. 代码位置

- 新增 `src/EchoGarage.App/Views/CoverViewerWindow.xaml`（布局 / 主题令牌 / 交互提示）
- 新增 `src/EchoGarage.App/Views/CoverViewerWindow.xaml.cs`（生命周期、同步、缩放平移、高清解码）
- 修改 `src/EchoGarage.App/Views/MainWindow.xaml`：右栏封面 `Border` 增加
  `MouseLeftButtonDown`、`Cursor`、`ToolTip`、`AutomationProperties.AutomationId`、`x:Name`（仅该区域）
- 修改 `src/EchoGarage.App/Views/MainWindow.NowPlaying.cs`：双击 handler、单实例字段与 Closed 清理
- 无 csproj 改动（SDK 风格 WPF 项目默认包含新增 `Page` / `Compile` 项）

## 8. 手工验收步骤

> 需要真实界面与播放；自动化测试不覆盖本窗口（避免抢占前台窗口输入）。

1. 启动程序，媒体库中存在带封面的歌曲。双击右栏「正在播放」封面区域 → 打开“封面浏览”窗口，
   图片按比例适配窗口，标题 / 艺术家与右栏一致。
2. 在视口内滚轮向上缩放：以光标为中心放大，百分比上升；持续放大到上限停在 400%。
   滚轮向下缩小到下限停在 25%，图片保持居中。
3. 放大到 150% 以上后按住左键拖拽：图片跟随鼠标移动；拖到边缘后不能继续拖出视口。
4. 双击视口（或点「适应窗口」）→ 回到 100% 且居中。
5. 按 Esc 关闭窗口；再次双击封面可重新打开。窗口打开时再双击封面 → 只激活原窗口，不出现第二个窗口。
6. 打开窗口后切歌（列表双击 / 空格 / 下一首）：
   - 封面先显示加载提示，随后新封面出现且标题更新；
   - 若新歌无封面 → 显示“当前歌曲没有可显示的封面”，窗口不崩溃；
   - 若当前无播放 → 显示“未在播放”。
7. 无封面歌曲（占位 ♪）双击右栏封面 → 无窗口弹出，日志有 Debug 记录。
8. 依次切换五套主题（纯净极简白 / 温润纸感白 / 极简冰青白 / 午夜黑胶 / 深海蓝灰）：
   窗口背景、边框、文字、按钮悬停色、标题栏即时跟随，无残留旧色。
9. 打开窗口后关闭主窗口 → 浏览窗口随之关闭，程序正常退出（无异常日志）。
10. 高 DPI（150% / 200%）下重复步骤 1–4：缩放和拖拽跟手，图片清晰（应走 1200px 大图而非 420px）。
11. 最小化封面窗口后再次双击封面 → 窗口恢复并激活。

## 9. 风险与缓解

| 风险 | 缓解 |
| --- | --- |
| 420px 回退图在窗口刚打开时短暂可见 | 打开即显示回退图避免空白；高清解码在后台完成并替换 |
| 高清解码读取期间封面缓存被清理 / 迁移 | `File.OpenRead` 异常被捕获 → `Warn` 日志 + 保留回退图；不崩溃 |
| 切歌极快时旧高清图覆盖新封面 | `_coverVersion` 版本号丢弃过期结果；订阅在 `OnClosed` 退订 |
| 主窗口关闭时浏览窗口未收尾 | `Owner` 关系由 WPF 自动关闭；`OnClosed` 统一退订 |
| 大图内存占用 | 单张 1200px 冻结位图约数 MB，且仅窗口打开期间持有；窗口关闭即释放 |
| 拖拽钳制在“适应窗口”时不可平移，用户误以为拖拽失效 | 内容小于视口时天然居中；放大后即可拖拽；底部提示写明操作 |
| `Resolve` 与 `CoverImageService` 解码的封面不一致（理论） | 两者都基于同一 `CoverKey` 缓存文件；不一致时宽高比变化仅影响钳制，不影响浏览 |
| 焦点在按钮上时 Esc 是否关闭 | 窗口级 `PreviewKeyDown` 统一处理 Esc，不依赖焦点元素 |

## 10. 未决问题

1. 是否需要“另存封面 / 打开所在文件夹”等扩展操作？当前按最小需求实现，不引入写操作。
2. 缩放步进（滚轮 1.25x、按钮 1.25x）与上限 4x 是否需要做成设置项？当前为固定值，等 owner 验收手感后再定。
3. 是否需要在无封面时双击给出状态栏提示？当前按“无动作”处理（避免干扰）。
4. 高清解码宽度 1200px 是否合适（4K 屏 4x 放大仍可能略糊）？可根据 owner 验收反馈调整为 1600px。

## 11. 备选方案（未采用）

- **模态 `ShowDialog()`**：会阻塞主窗口播放操作，且重复打开流程复杂；不符合“浏览”定位。
- **直接复用 420px 回退图**：实现最简单，但 4x 放大明显发虚，不满足“放大浏览”的核心价值。
- **把 1200px 大图放进 `CoverImageService` 的 LRU**：会挤占列表缩略图缓存（300 条上限）并改变现有封面加载行为；
  改为只读 `ICoverCache.Resolve` + 窗口内自行解码。
- **滚动条 + 缩略图导航**：超出本次需求范围，增加交互复杂度。

## 12. 验证记录

- 2026-11-16：Debug 全量构建通过（0 错误；仅他人新增测试文件的一条 xUnit1025 警告，与本提案无关）；
  `scripts/test.ps1` 全量 xUnit v3 进程内测试 **582 项全部通过（0 失败、0 跳过）**。
- 2026-11-16：Release 全量构建通过（`scripts/build.ps1 -Configuration Release`，0 错误）。
- 构建 / 测试在 `.scratch/build.lock` 串行锁下执行，避免多任务共享 `bin/obj` 的并发冲突。
- 说明：全量套件曾出现一次 `ScannerTests.Progress_IsReported` 偶发失败（扫描进度回调时序），
  该测试单独运行 3/3 通过、复跑全量套件通过；扫描器不在本提案改动范围，确认与封面浏览窗口无关。
- 未运行 UI 自动化（按 AGENTS 约定，不抢占前台窗口），第 8 节手工验收步骤待 owner 执行。
- 示例歌曲 / 专辑名均使用 dummy 值（`Demo Artist` / `Demo Album A` / `Demo Track 01`），不含真实作品信息。
