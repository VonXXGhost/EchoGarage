# ADR-023：右栏「播放可视化」（分段频谱图 + 渲染帧率上限）

- 状态：**Accepted**（2026-11-15，owner；2026-11-15 实现完成并发布 Release 验收版本；2026-11-16 并入原 ADR-023-A1；2026-11-22 并入独占模式输出缓冲区 / 格式协商修订）
- 日期：2026-11-15
- 相关：ADR-009（WASAPI 音频管线）、ADR-012（测试播放静音）、ADR-015（波形进度条：渲染范式与参数风格）、ADR-024（五套主题）、KB-006（主题令牌）、KB-006（主题令牌映射）

## 0. 决策摘要

1. 删除右栏「正在播放」面板的标题行、格式行与文件路径行，只保留封面与「曲名 / 艺术家 / 专辑」，
   并去掉基础信息里的年份；《视图》菜单关闭面板时仍然整列隐藏。
2. 右栏下方新增「播放可视化」子面板：第一阶段只实现**分段频谱图（Spectrum Analyzer）**，
   竖条 + 底部亮色顶部对齐的竖直渐变 + 峰值保持线 + dB 网格 + 频率刻度，风格对齐 owner 在
   foobar2000 中使用的视觉化配置（参考图：40 段、实心柱、右侧 0…−50 dB 刻度、底部 50 Hz…23 kHz 几何级数标签）。
3. 可视化方式做成可扩展枚举（`VisualizerKind`：`None` / `Spectrum`），设置里可切换；
   关闭时右栏下方留空，后续形态（LED 分段、波形等）只加枚举值与渲染分支。
4. 频谱参数全部可配置并随配置一起持久化：分段数、FFT 点数、频率范围、频段聚合方式、
   显示下限、频谱斜率（FFT Scale）、释放速度、峰值保持、刻度开关。
5. 数据获取采用**音频线程无锁写环形缓冲 + UI 渲染帧拉取分析**的模型，不引入后台分析线程、
   不引入新依赖（NAudio 2.2.1 已在使用）；抽头点在重采样 / 声道转换之后、音量之前，
   因此静音或音量 0 时仍然有画面（同时满足 ADR-012 的静音验收要求）。
6. 渲染用自定义 `FrameworkElement` + `OnRender`，所有颜色来自主题令牌；
   实测默认参数（32 段 / FFT 4096）下渲染帧率 140.2 FPS、每帧 OnRender 0.17 ms；
   最坏配置（96 段 / FFT 8192）142.0 FPS、0.21 ms，均无丢帧，远高于 60 FPS 红线。
7. **渲染帧率上限可配置**（30 / 60 / 120 / 144 / 180 / 不限制，默认**不限制**）：节流在真实分析帧之前整帧跳过，
   `dt` 仍取真实经过时间，因此“弹道速度与帧率无关”的语义不变（2026-11-16，并入原 ADR-023-A1）。
8. **独占模式缓冲与格式协商**（2026-11-22 修订）：独占模式缓冲按 `10 / 20 / 50 / 100 ms` 逐级回退，
   使音频回调节奏与共享模式一致（约 100 次/秒），可视化数据更新不再出现约 100 ms 的阶梯；
   同时按设备能力协商输出格式（源采样率优先、支持 PCM 24-in-32），以
   `WasapiOut.OutputWaveFormat` 为准展示有效位深与容器位深。详见 §4.3、§18。

## 1. 背景与问题

右栏「正在播放」面板目前是「标题 + 300px 封面 + 曲名 / 艺术家 / 专辑·年份 + 分隔线 + 格式行 + 路径行」，
在播放时信息重复（格式与路径在状态栏、属性对话框里都能看到），而封面以下的大片区域是空的。
owner 要求：

1. 去掉标题、格式行、路径行，只保留封面和基础歌曲信息，并且**不显示年份**；
2. 利用下方空余空间做「播放可视化」，可视化方式将来可切换，第一阶段只做分段频谱图，
   外观参考其在 foobar2000 里使用的配置；
3. 生成参数（分段数、FFT Size、FFT Scale 等）尽量可设置，便于调试；
4. 动态效果必须稳定在 60 FPS 以上。

## 2. 目标与非目标

### 2.1 目标

- 面板精简：右栏不再出现「正在播放」标题、格式 / 采样率 / 码率行、文件路径行、年份。
- 分段频谱图：竖条高度随实时频谱起伏；峰值保持线；dB 刻度；频率标签；主题化配色；
  深浅两套主题都正确。
- 参数可调并持久化：分段数、FFT 点数、频率范围、聚合方式、显示下限、斜率、释放、峰值保持、刻度开关；
  设置页改动立即生效。
- 稳定 ≥60 FPS；窗口被遮挡 / 最小化时不燃烧 CPU。
- 可开关：不需要可视化时可以选择「关闭」。
- 渲染帧率上限：可在 30 / 60 / 120 / 144 / 180 / 不限制之间切换，默认不限制（保持原行为），在设置页可改、即时生效、随配置持久化。

### 2.2 非目标（第一阶段）

- 不做多种形态的并行渲染（只留切换入口与枚举）；不做 LED / 点阵分段柱、波形、示波器等其它形态。
- 不做左右声道分离、麦克风 / 环回采集（只做当前播放输出的抽头）。
- 不做频谱截图导出、颜色自定义（颜色只跟主题走）。
- 不改播放链路本身的行为（抽头只旁路读取，不改变样本数据、时序与音量语义）。

## 3. 现状（代码事实）

- 音频管线（ADR-009）：`WasapiAudioPlayer.StartOutput` 里
  `SharedAudioFileReader → Convert（WdlResampling + 声道转换）→ VolumeSampleProvider → SampleToWaveProvider → WasapiOut`；
  此前没有任何采样回调，播放链路对外只有位置 / 状态事件。
- 右栏面板在 `MainWindow.xaml` 的 `Border Grid.Column=2` → `ScrollViewer` → `StackPanel` 内，
  列宽默认 320（240…520 可拖），可视宽度约 283 DIP。
- `ScrollViewer` 用无限高度测量内容，`StackPanel` 的 `*` 行拿不到「剩余高度」；
  要让可视化占满剩余空间，必须让内容 Grid 的 `MinHeight` 绑定 `ScrollViewer.ViewportHeight`。
- 自定义渲染已有成熟范式（ADR-015 `WaveformSeekBar`）：设备像素对齐、 Brush 依赖属性 + 主题令牌、
  按缓存键重建几何。主题字典（ADR-024 的五套 `Resources/Themes/*Theme.xaml`）目前没有渐变刷。
- 设置持久化：`AppSettings` 的嵌套设置类（`NetworkSettings` / `WebTagSettings`）都有
  `static Normalize`，`JsonSettingsService.Normalize` 在 Load / Save / Export / Import 四条路径上统一兜底。
- 测试工程只引用 Core + Infrastructure（不引用 App），因此纯计算与数据源可测，控件不可测。

## 4. 调研与选型

### 4.1 参考实现（foobar2000 频谱 + 通用做法）

owner 的参考图：40 根实心竖条、竖直渐变（底部深蓝 → 顶部亮蓝）、顶部细峰值线、
右侧 0 / −10 / … / −50 dB 水平网格、底部 50 Hz…23 kHz 的几何级数频率标签（相邻约 1.17 倍）。
市面成熟实现（Web Audio `AnalyserNode`、foobar2000、各类音频可视化库）的通用口径：

- 频段按**几何级数**划分（等倍频程），低频段窄、高频段宽；
- **加窗**（Hann 为主）抑制频谱泄漏，窗的相干增益用于幅度标定；
- 频段聚合用**平均功率**而不是「取最强 bin」，否则高频段会因 bin 多而整体虚高；
- 显示域用 dBFS，常见显示范围 −70…0 dB；
- 音乐 / 粉噪能量密度随频率约 −3 dB/oct 下降，因此用「斜率补偿」（foobar2000 的 FFT Scale）
  把画面拉平；
- 为了好看，柱体在 dB 域做弹道：attack 接近瞬时，release 按 **dB/s** 下落；
  Web Audio 的平滑等价于幅度域一阶 IIR，换算后与 60 / 120 / 144 Hz 无关的关键是**用 dt 而不是固定步长**。

### 4.2 渲染方案选型（WPF）

| 方案 | 结论 |
| --- | --- |
| 每段一个 `Rectangle` + 绑定 | 否：128 个矩形 @30 FPS 就可能吃掉约 30% CPU |
| `WriteableBitmap` 逐像素 | 只在需要像素级 LED 时才有必要，实心柱状用不上 |
| `DrawingVisual` 宿主 | 备选：只触发渲染失效、不触发布局，适合将来布局抖动时升级 |
| `FrameworkElement.OnRender` + `InvalidateVisual`（本项目 WaveformSeekBar 同款） | **采用**：64 段量级的几何开销可忽略，代码与既有控件一致 |

同步方式选 **`CompositionTarget.Rendering` + 拉取（pull-on-render）**：每次渲染 tick 向数据源要一帧。
好处：与渲染帧天然对齐（不会重复算 / 丢帧）、dt 直接来自 `RenderingEventArgs.RenderingTime`、
窗口被遮挡或最小化时 WPF 不再出帧 → 分析自动停摆，不空烧 CPU；也不需要跨线程发布与生命周期管理。

### 4.3 采样抽头选型

| 方案 | 结论 |
| --- | --- |
| WASAPI loopback 采集 | 否：多一条设备链路、有漂移，独占模式还拿不到 |
| `ISampleProvider` 旁路抽头 | **采用**：与播放样本同源、零漂移，共享 / 独占两种模式都可用 |

抽头位置取「重采样 + 声道转换之后、音量之前」：可视化看到的是解码后的真实频谱，
且音量调小 / 静音时画面照常（ADR-012 要求自动化测试先静音）。

实测（`.scratch` 探针）：

- **共享模式**：NAudio `WasapiOut` 回调固定约 **10 ms**（每次 960 个交错样本，约 100 次/秒），
  与请求的 `bufferMilliseconds` 无关。
- **独占模式（2026-11-22 更正）**：回调周期 ≈ **请求的缓冲区时长**。旧实现固定 100 ms，
  每次 `Read` 9600 个样本、约 7.5 次/秒，采样抽头写入的最大间隔约 104 ms——频谱柱每约 100 ms
  才更新一次，肉眼即“帧数低”。原结论“取 100 / 50 / 30 / 20 都按约 10 ms 回调”只对共享模式成立。
  修订后独占模式改为 `10 / 20 / 50 / 100 ms` 逐级尝试（失败回退），使回调节奏与共享模式一致；
  详见 §18。

两种模式都不需要时间轴 pacing、jitter buffer 或独立分析线程，最近一段样本总是足够新。

## 5. 数据模型与持久化

```
AppSettings
└─ Visualization: VisualizationSettings              // 新增（Normalize 兜底）
   ├─ Kind: VisualizerKind { None, Spectrum }        // 默认 Spectrum
   ├─ FrameRateLimit: VisualizerFrameRate            // 渲染帧率上限，默认 Unlimited
   │    { Unlimited = 0, Fps30 = 30, Fps60 = 60, Fps120 = 120, Fps144 = 144, Fps180 = 180 }
   └─ Spectrum: SpectrumSettings
      ├─ BandCount = 32            （8…96）
      ├─ FftSize = 4096            （1024 / 2048 / 4096 / 8192）
      ├─ MinFrequency = 40 Hz      （20…200）
      ├─ MaxFrequency = 18000 Hz   （500…24000，且 ≥ 4 × Min）
      ├─ Summation = Rms           （Rms / Peak）
      ├─ FloorDb = −50 dBFS        （−100…−20，上限固定 0）
      ├─ SlopeDbPerOctave = +3     （−12…12，参考频率 1 kHz）
      ├─ ReleaseDbPerSecond = 45   （3…120）
      ├─ ShowPeaks = true
      ├─ PeakHoldMs = 800          （0…3000）
      ├─ PeakFallDbPerSecond = 30  （3…240）
      ├─ ShowGraticule = true
      └─ ShowFrequencyLabels = true
```

- `SpectrumOptions` 是从设置生成的**不可变快照**（`required init`，含预算好的 `BandEdges`），
  参数变化时整体替换；分析器按引用比较决定是否重建 processor。
- `SpectrumFrame` 是一帧的渲染输入：`Levels[]` / `Peaks[]`（0..1，NaN = 不画峰值）/ `FrameId` / `IsSilent`。
- 设置升级兼容：老 `settings.json` 没有 `visualization` 字段时按默认值补全并写回；
  越界值由 `Normalize` 钳制，未知枚举回退为 `Spectrum`。
- **帧率上限（2026-11-16，并入原 ADR-023-A1）**：`FrameRateLimit` 挂在 `VisualizationSettings` 而不是
  `SpectrumSettings`——限制的是**渲染循环**，不是频谱分析参数；阶段二的其它可视化形态可复用同一上限。
  枚举数值即 FPS（`Unlimited = 0`），渲染层只需要一个整数上限；`Normalize` 用 `!Enum.IsDefined` 回退
  `Unlimited`（手改成 `"frameRateLimit": 42` 不会产生负间隔 / 不节流），绑定层
  `VisualizerFrameRateExtensions.ToFps()` 对未定义值返回 0（双保险）。序列化兼容：`JsonDefaults` 注册了
  `JsonStringEnumConverter`，落盘为 `"frameRateLimit": "Fps60"`；旧配置缺该字段 → 默认 `Unlimited`；
  旧程序读到该字段会当未知属性忽略，回滚无迁移成本。

## 6. 算法与口径

### 6.1 流水线

```
WASAPI 线程：交错样本 →（下混单声道）→ 环形缓冲（32768，2 的幂）
UI 渲染帧：取最新 N 个样本 → 周期 Hann 窗 → FFT → 频段聚合（dBFS）→ 斜率补偿
          → 弹道（attack / release / 峰值保持）→ 0..1 柱高 → OnRender
```

- 音频线程只做算术与一次 `Volatile.Write` 写指针：不分配、不加锁、不阻塞。
- UI 线程取「最新 FFT 长度的样本」：丢帧只会跳过错过的片段，不会累积延迟。
- **帧率上限节流（2026-11-16，并入原 ADR-023-A1）**：`SpectrumView` 在真实分析帧之前做整帧跳过——距上次
  分析帧不足 `FrameRateTolerance / 上限` 秒（`FrameRateTolerance = 0.95`）就 `return`，既不调用 `NextFrame`
  也不 `InvalidateVisual`；被跳过的 tick **不推进** `_lastRenderingTime`，下一个真实分析帧的 `dt` 仍是真实
  经过时间。`Source` / `Options` / `FrameRateLimit` 变化时调用 `ResetFrameTiming()` 重置计时，切换后下一
  tick 立即出帧；窗口被遮挡 / 最小化时 `CompositionTarget.Rendering` 本身不出帧，节流不改变该行为。
- 超过 150 ms 没有新样本（暂停 / 停止 / 输出被释放）按静音喂给弹道，柱体会自然落下去。

### 6.2 FFT 与幅度标定

- 周期 Hann 窗（分母 N，不用 N−1）：相干增益 `CG = Σw / N = 0.5`，标定最简单。
- 实测 NAudio 的 `FastFourierTransform.FFT(true, m, …)` **已经把输出除以 N**
  （1.0 直流 + Hann → bin0 = 0.5 = Σw/N；0.5 幅度正弦 → |X| = A·Σw/(2N)），
  因此幅度只需 `A_k = 2·|X_k| / CG = 4·|X_k|`，再 `dBFS = 20·log10(A_k)`。
- 该标定对 1024 / 2048 / 4096 / 8192 四种点数都成立（单测逐个断言 −6.02 dBFS ±0.9 dB）。

### 6.3 频段划分与聚合

- `f_i = f_min · (f_max/f_min)^(i/N)`，边界→bin 用 `round(f·Nfft/fs)`；
  跳过直流；强制 bin 单调递增、每段至少 1 个 bin、上限 `Nfft/2`。
- `Rms`（默认）：`sqrt(mean(A²))` —— 每 bin 的功率密度，宽带噪声下各段持平（实测跨度 < 7 dB）；
  `Peak`：段内最强 bin —— 单音更准，但宽带段会随 bin 数抬高（实测跨度 > 8 dB，单测对比锁定这两种语义）。

### 6.4 斜率补偿（FFT Scale）

`gain(f) = slope · log2(f / 1000)`：1 kHz 不增不减，+3 dB/oct 时 8 kHz 抬 +9 dB，
用来抵消音乐 / 粉噪的能量密度倾斜；按频段中心（几何中值）预算，不在渲染时逐帧计算。

### 6.5 弹道

- 电平在 dB 域推进：`level = max(measured, level − release·dt)`，即 attack 瞬时、release 线性下落；
- 峰值：`peak = max(level, peak)` 并重置保持计时；保持期（默认 800 ms）结束后按 `PeakFallDbPerSecond` 下落；
- 输出统一归一化 `Normalize(db, floor, ceiling)` → 0..1。
- 所有速度用 **dB/s × dt**，与帧率无关（单测：60 帧 @1/60 s 与一次 1 s 的衰减结果一致）。
- 帧率上限节流不改变上述口径：分析帧变稀疏，但 `dt` 仍取真实 `RenderingTime` 差（**不是** `1 / 上限`），
  所以释放速度、峰值保持时长与“不限制”时一致；`SpectrumAnalyzer.NextFrame` 内部把 `dt` 钳制在
  `[0.001, 0.5]` 秒，极端卡顿恢复时也不会把弹道推进到异常状态（并入原 ADR-023-A1 §2.3）。

### 6.6 参数表（集中在此，便于调参与单测）

| 参数 | 默认 | 范围 | 说明 |
| --- | --- | --- | --- |
| 分段数 | 32 | 8…96 | owner 选定；参考图约 40 段，窄面板可降到 24 |
| FFT 点数 | 4096 | 1024 / 2048 / 4096 / 8192 | 越大低频越准、柱体越「迟钝」 |
| 频率范围 | 40–18000 Hz | 20…200 / 500…24000 | 覆盖人耳主要内容 |
| 聚合 | Rms | Rms / Peak | Rms 不虚抬高频 |
| 显示下限 | −50 dBFS | −100…−20 | 上限固定 0 dBFS |
| 斜率 | +3 dB/oct | −12…12 | FFT Scale |
| 释放 | 45 dB/s | 3…120 | 越小越「黏」 |
| 峰值保持 | 800 ms | 0…3000 | 掉落速度 30 dB/s（3…240） |

## 7. 分层与接口

- **Core**（不引用 WPF / NAudio）
  - 模型：`VisualizationSettings` / `SpectrumSettings` / `SpectrumOptions` / `SpectrumFrame`
  - 纯计算：`SpectrumMath`（边界、dB、斜率、标签格式）、`SpectrumBallistics`（弹道）
  - 接口（追加到 `Abstractions/Interfaces.cs`）：
    - `IAudioSampleSink { Configure(sampleRate, channels); Write(float[], offset, count); Reset(); }`
      —— 音频线程回调，实现方不得阻塞 / 分配；
    - `ISpectrumSource { Apply(VisualizationSettings); Reset(); NextFrame(dt); Stats; }`
      —— `NextFrame` 在 UI 渲染线程调用；
    - `SpectrumStats`（帧数、平均 / 最大耗时、采样率、段数）。
- **Infrastructure**
  - `SampleTapProvider`：`ISampleProvider` 旁路，`Read` 后把样本转交 sink；
  - `SpectrumProcessor`：窗 / FFT / 频段 / 标定（public，可单测）；
  - `SpectrumAnalyzer`：环形缓冲 + 弹道 + 拉取式 `NextFrame`（`IAudioSampleSink + ISpectrumSource + IDisposable`）；
  - `WasapiAudioPlayer`：构造函数接受可选 `IAudioSampleSink`，`StartOutput` 在音量前插入抽头，
    `TeardownOutput` / `Seek` 时复位缓冲。
- **App**
  - `SpectrumView`：自定义 `FrameworkElement`（`OnRender` + `CompositionTarget.Rendering`）；
  - `MainWindow` 右栏重写为 `ScrollViewer` + `Grid(MinHeight = ViewportHeight)` + 三行 `Auto/Auto/*`；
  - `MainViewModel`：暴露 `Spectrum` / `SpectrumOptions` / `ShowVisualizer` / `RefreshVisualizer()`；
  - `SettingsWindow`：新增「播放可视化」段落（改即存，与其它设置一致）；
  - `AppHost`：唯一装配点，创建 `SpectrumAnalyzer` 并注入播放器，退出时一并 Dispose。

## 8. 渲染细节

- 所有柱体合成**一个 `StreamGeometry`**，一次 `DrawGeometry`；渐变按**绘图区矩形**铺（不是按柱体包围盒），
  因此短柱只露渐变下端的深色、高柱才爬到顶部亮色 —— 与参考图的观感一致。
- 峰值线是 1 设备像素高的矩形；NaN 跳过。
- 网格线与刻度文字是**静态层**，按（宽 / 高 / DPI / 参数 / 主题刷引用）缓存为 `DrawingGroup` + `FormattedText`，
  参数或尺寸变化才重建；不每帧格式化字符串。
- 频率标签从低频往高频贪心放置，左对齐到柱体左边界，按实测文字宽度跳字，末尾夹紧到控件内。
- 宽度切换（240…520 列宽）时按 DPI 重新吸附设备像素；`RenderOptions.EdgeMode = Aliased` 保证柱边锐利。
- 颜色全部走主题令牌：`SpectrumBarBottomBrush` / `SpectrumBarTopBrush` / `SpectrumPeakBrush` /
  `SpectrumGridBrush` / `SpectrumLabelBrush`，ADR-024 的五套主题字典各定义一次，控件通过 `Controls.xaml`
  的样式绑定到依赖属性（`DynamicResource`），切换主题即时生效。

## 9. 性能实测

测试环境：1682×961 窗口、右栏 360 px、48 kHz 立体声、**Release 发布构建**、DWM compositing。
（下表为 6 秒采样；「渲染 tick」= `CompositionTarget.Rendering` 事件数，「实际绘制」= `OnRender` 次数。）

| 配置 | 渲染 tick | 实际绘制 | 每帧 OnRender | 单帧最大 | 分析 NextFrame 平均 |
| --- | --- | --- | --- | --- | --- |
| 深色 / 默认 32 段 / FFT 4096 | 140.2 FPS | 140.2 FPS | 0.17 ms | 8.1 ms（首帧 JIT） | 0.17 ms |
| 浅色 / 默认 32 段 / FFT 4096 | 139.5 FPS | 139.5 FPS | 0.18 ms | 8.0 ms（首帧 JIT） | 0.15 ms |
| 最宽 96 段 / FFT 8192 | 142.0 FPS | 142.0 FPS | 0.21 ms | 4.8 ms | 0.28 ms |
| 关闭可视化（Kind=None） | 0 | 0 | — | — | — |

- 「绘制帧数 == 渲染 tick 数」说明没有丢帧；稳定期帧率 139–142 FPS，距 60 FPS 红线有 2 倍以上余量。
- 分析在 UI 线程每帧只花 0.15–0.28 ms（4096 点复数 FFT 约 55 µs），不需要后台线程。
- 画笔 / 渐变刷按主题令牌与 DPI 缓存，渲染路径里没有每帧 new（几何本身每帧重建，量级可忽略）。
- 窗口被遮挡 / 最小化时 WPF 停止出帧：分析随之停摆，CPU 占用回落到空闲水平。

### 9.1 帧率上限档位（2026-11-16，并入原 ADR-023-A1）

默认 `Unlimited` 时上表的 139–142 FPS 即现状基线。设上限后，实际帧率由“能否在合成 tick 上出帧”决定：
间隔判定为 `0.95 / L` 秒，于是每 `m = max(1, ceil(0.95 · R / L))` 个 tick 分析一次、实际帧率 `R / m`
（`R` = 显示器刷新率、`L` = 设置的上限）。

| 显示器 | 30 档 | 60 档 | 120 档 | 144 档 | 180 档 |
| --- | --- | --- | --- | --- | --- |
| 60 Hz | 30 | 60 | 60 | 60 | 60 |
| 120 Hz | 30 | 60 | 120 | 120 | 120 |
| 144 Hz | 28.8 | 48 | 72 | 144 | 144 |
| 165 Hz | 27.5 | 55 | 82.5 | 82.5 | 165 |
| 180 Hz | 30 | 60 | 90 | 90 | 180 |
| 240 Hz | 30 | 60 | 120 | 120 | 120 |

上限是**天花板**而不是精确帧率（144 Hz 屏选 60 → 约 48 FPS）；`0.95` 容差让 `L` 为 `R` 的整数分频
（含 `L = R`）时能整除，避免 144 Hz 选 144 因 `RenderingTime` 抖动掉到 72。设置页提示文案明确
“实际帧率还受显示器刷新率限制”。代价：30 FPS 时约 33 ms 的瞬态峰值可能落在两次分析之间，默认不限制时不存在。

## 10. 面板布局

```
Border（右栏，1px 左边框，PanelBackgroundBrush）
└─ ScrollViewer  x:Name=NowPlayingScroll   (Auto 竖向滚动条，Padding 18,16,18,18)
   └─ Grid  MinHeight={Binding ViewportHeight, ElementName=NowPlayingScroll}
      ├─ Row 0 Auto：封面 Height=300（含加载 spinner / 无封面占位）+ 曲名 + 艺术家 + 专辑
      ├─ Row 1 Auto：「未在播放」
      └─ Row 2 * ：SpectrumView（MinHeight 96，Margin 0,14,0,0，Visibility=ShowVisualizer）
```

- 删除「正在播放」标题、分隔线、格式行、路径行；`UpdateNowPlayingMetadata` 不再拼年份。
- 用 `MinHeight = ViewportHeight` 解决 `ScrollViewer` 无限高度测量的问题：内容不足一屏时铺满，
  内容超出（小窗口）时出现滚动条，可视化最少保留 96 px。

## 11. 设置页

- 段落「播放可视化」插在「音频输出」与「快捷键」之间，沿用既有段落 / 行模板（90 px 标签列、ComboBox + Tag）。
- 行：可视化方式、帧率上限、分段数、FFT 大小、频率范围（两个 ComboBox）、频段聚合、显示下限、频谱斜率、
  释放速度、峰值保持（开关 + ComboBox）、刻度（网格 / 频率标签两个开关）、「恢复默认参数」。
- 与其它设置一致：改动即存（`SaveSettings()` → 800 ms 防抖写盘），并立即 `RefreshVisualizer()`
  让引擎与控件参数生效；非法值由 `Normalize` 回填。
- 「帧率上限」ComboBox 以枚举名作 Tag，链路为
  `ApplyVisualizer → Normalize → PopulateVisualizer → RefreshVisualizer → SaveSettings`；
  `MainViewModel.VisualizerFrameRateLimit` 把枚举换算成整数绑定到 `SpectrumView.FrameRateLimit`，
  `OnFrameRateLimitChanged` 重置计时、下一 tick 按新上限出帧。「恢复默认参数」把上限恢复为不限制
  （2026-11-16，并入原 ADR-023-A1）。

## 12. 诊断与验收

新增内置诊断开关（与 `--screenshot` / `--play-file` / `--waveform` 同族，AGENTS.md §8 已记录）：

```powershell
EchoGarage.exe --visualizer <音频文件> <截图.png> [秒数]
```

流程：播放指定文件 → 等 600 ms → 清零统计 → 采样 N 秒（默认 3）→ 写一行 INFO 日志 → 截图：

```
Visualizer diagnostic: file=… sampled=6.0s measured=5.99s renderFrames=838 fps=139.8
ticks=838 tickFps=139.8 renderAvg=0.213 ms renderMax=2.049 ms analyzerFrames=944
analyzerAvg=0.184 ms analyzerMax=5.502 ms rate=48000Hz bands=40
```

- `renderFrames` / `fps` 是实际绘制帧率，`ticks` / `tickFps` 是 WPF 渲染 tick 速率；
  不设上限时两者相等即无丢帧（用于区分「渲染慢」与「tick 变少」）。设了帧率上限后 `fps` 只统计真实
  绘制帧、`tickFps` 仍含被跳过的 tick，因此 **`tickFps > fps` 就是“帧率上限生效”的直接证据**；
  `analyzerFrames` 节流后等于实际分析帧数（2026-11-16，并入原 ADR-023-A1 §2.4）。
- 诊断行目前未输出设置值：`src/EchoGarage.App/App.xaml.cs` 的 `Visualizer diagnostic:` 字符串拼接不在
  2026-11-16 的改动范围内；后续若在其后追加 `frameRateLimit=<值>` 字段，在此登记。
- 退出时 `ShutdownAsync` 还会记一行 `Spectrum stats:`（帧数、平均 / 最大分析耗时、采样率、段数）。
- 自动化验收时按 ADR-012 把 `settings.json` 的 `Volume` 设为 0（抽头在音量前，画面不受影响）。
- 注意：诊断开关的文件参数**不能**加进 `FileArgumentParser` 的 skipNext 名单 ——
  音频文件要靠「启动文件」路径进入播放列表，否则播放列表里没有行可播。

## 13. 测试

新增 4 个测试文件（Core + Infrastructure，不依赖 WPF）：

- `SpectrumMathTests`：几何级数边界、归一化、斜率补偿、频率标签、dB 换算、dt 衰减；
- `SpectrumBallisticsTests`：attack 瞬时、release 按 dB/s、帧率无关、上限钳制、峰值保持与下落、关闭峰值、Resize 复位；
- `SpectrumProcessorTests`：四种 FFT 点数的幅度标定（−6.02 dBFS）、斜率增益逐段比对、Rms 白噪平坦性 vs Peak 倾斜、
  静音下限与 `LastFrameSilent`、直流不抬高最低段、超长输入取最新样本；
- `VisualizationSettingsTests`：默认值、逐字段钳制、未知枚举回退、深拷贝、快照不可变、经 `settings.json` 往返、
  越界 JSON 在 Load 时被修复；2026-11-16 扩充（并入原 ADR-023-A1 §3.1）：默认 `FrameRateLimit == Unlimited`
  且 `ToFps() == 0`、未知枚举 `(VisualizerFrameRate)42 → Unlimited`、六个合法档位逐一映射 0/30/60/120/144/180、
  `Fps144` 深拷贝、`settings.json` 往返、坏 JSON `"frameRateLimit": 42` 在 Load 时修复。
- WPF 渲染层节流不做自动化单测（需要真实合成时钟），手工验收见 §9.1 与 §12 的诊断口径。

全量测试：2026-11-15 为 472 项通过（含既有 439 项）、0 失败；2026-11-16 为 584 项通过、0 失败。

## 14. 分阶段路线

- **阶段一（本 ADR，已完成）**：面板精简 + 分段频谱图 + 参数设置 + 诊断开关 + 单测；
  2026-11-16 追加渲染帧率上限（30 / 60 / 120 / 144 / 180 / 不限制，默认不限制，见 §9.1）。
- 阶段二（未排期）：更多可视化形态（LED 分段柱、波形 / 示波器），在 `VisualizerKind` 上扩展枚举与渲染分支；
  若大窗口下出现布局抖动，把 `SpectrumView` 的 `OnRender` 换成 `DrawingVisual` 宿主（只触发渲染失效）。
- 阶段三（未排期）：可视化区域高度可拖拽、全屏可视化窗口、配色预设。

## 15. 风险与取舍

| 风险 | 处理 |
| --- | --- |
| `InvalidateVisual` 会连带 `InvalidateArrange`，大窗口下每帧可能跑布局 | 实测 144 FPS 无丢帧；已留 `DrawingVisual` 升级路径，且诊断开关能区分 tick 与绘制帧率 |
| 每帧在 UI 线程做 FFT | 4096 点约 55 µs、8192 点约 110 µs，占 16.7 ms 帧预算 < 1%；实测 0.19–0.38 ms |
| `Frame` 每帧克隆两个数组 | 每帧约 640 B，40 帧/秒量级可忽略；如成为问题可改成双缓冲复用 |
| 窄面板下分段偏细 | 分段数可调（8…96），默认 32 段 |
| 静音 / 音量 0 时抽头仍在工作 | 抽头在音量之前，属于设计选择（也是 ADR-012 验收所需） |
| 高刷屏档位取整，实际帧率低于所选值（144 Hz 选 60 → 约 48） | 上限语义 = 天花板；设置页提示 + §9.1 表格说明，避免验收误判 |
| 30 FPS 下瞬态峰值可能被跳过 | 视觉可接受；默认不限制不受影响，需要更灵敏可提高分析频率（另立决策） |
| 节流后诊断 `fps` 变小被误读为性能问题 | `tickFps` 仍在，两者之差即节流；后续可在诊断行加 `frameRateLimit` 字段 |
| 远程桌面 / 驱动刷新率低于档位时实际帧率更低 | 同“取整”语义：上限是天花板，不是保证值 |
| 错误 / 未知枚举值 | 三层兜底：`Normalize` 回退、`ToFps()` 回退 0、DP 默认 0 |
| 新增档位遗漏设置项 | ComboBox 以枚举名 Tag 驱动（`ReadEnumTag`），新增档位需同时加枚举与选项 |

**回滚**（2026-11-16，并入原 ADR-023-A1）：删除 `VisualizerFrameRate` / `FrameRateLimit` / `ToFps()` /
`FrameRateTolerance` / DP 与绑定 / 设置行与读取代码及对应测试即可；`settings.json` 中的 `frameRateLimit`
会被旧代码当作未知属性忽略，不需要迁移或清理配置。

## 16. 备选方案（已否决）

- **独立分析线程 + 发布最新帧**：需要跨线程同步与生命周期管理，且渲染慢时仍会算出多余帧（浪费），
  拉取式只在真正出帧时计算，故否决。
- **WASAPI loopback 采集**：多一条链路、独占模式不可用、有漂移，否决。
- **`WriteableBitmap` 逐像素绘制**：仅 LED 像素风格才需要，实心柱状用几何更快更清晰，否决。
- **每段一个 `Rectangle` + 数据绑定**：128 个矩形 @30 FPS 就吃约 30% CPU，否决。
- **只提供“性能模式”布尔（固定 30 FPS）**：无法满足 60 / 120 / 144 档位需求，否决。
- **用 `DispatcherTimer` 独立驱动分析 / 绘制**：计时器与合成时钟不同步，帧间抖动、额外延迟，
  且仍需 `InvalidateVisual`，否决。
- **在分析器内部“每 N 帧才 FFT”**：绘制仍每 tick 发生，省不了绘制开销，还会让诊断口径混乱，否决。
- **把 dt 固定成 `1 / 上限`**：破坏 §6.5 的“速度 = dB/s × dt、与帧率无关”，释放与峰值保持变慢，否决。
- **用 `RenderOptions.ProcessRenderMode` 降级软件渲染**：画面质量受损且不解决持续渲染，否决。
- **依赖“关闭可视化”（`VisualizerKind.None`）**：已有选项，但用户想要“开着但省资源”，否决。

## 17. 修订记录

- 2026-11-15：初稿（阶段一实现完成，附实测帧率与测试结果）。
- 2026-11-15：按 owner 反馈调整默认参数（32 段 / −50 dBFS 下限 / 45 dB/s 释放），移除设置页底部的「当前参数摘要」行；性能表按新默认值重测。
- 2026-11-15：owner 批准（Accepted），归档至 `docs/adr/` 并更新 `ADR-index.md`。
- 2026-11-16：**原 ADR-023-A1（已并入本文）**——新增渲染帧率上限 30 / 60 / 120 / 144 / 180 / 不限制，默认不限制；
  节流放在真实分析帧之前整帧跳过、`dt` 保持真实经过时间；新增 `VisualizerFrameRate` 枚举、`ToFps()`、
  `SpectrumView.FrameRateLimit` 依赖属性、设置页「帧率上限」行、§9.1 取整表与诊断口径（§0 / §5 / §6.1 / §6.5 /
  §9.1 / §11 / §12 / §13 / §15）。
- 2026-11-22：**并入独占模式缓冲区、格式协商修订（原提案 `ADR-023-amendment-exclusive-buffer-latency.md`）**——
  独占缓冲改为 10 / 20 / 50 / 100 ms 逐级回退；更正 §4.3 关于独占回调周期的结论；独占格式协商改为
  源采样率优先、按设备能力逐候选 `IsFormatSupported` 预检、支持 PCM 24-in-32；输出信息以
  `WasapiOut.OutputWaveFormat` 为准并区分有效位深 / 容器位深（§0 / §4.3 / §18）。

> 原 ADR-023-A1 与 2026-11-22 独占模式修订的正文均已并入本文，对应文件已从 `docs/adr/` 移除，编号作为追溯信息保留。

## 18. 2026-11-22 修订：独占模式输出缓冲区、格式协商与可视化回调节奏

### 18.1 背景

1. 用户反馈「独占模式下频谱动态效果帧数明显低很多」。隔离诊断显示两种模式的 WPF 渲染帧率一致
   （共享约 153.3 fps、独占约 152.6–153.6 fps），差异实际来自**采样抽头的数据更新粒度**：
   共享模式约 100 次/秒写入，独占模式旧实现为 100 ms 缓冲、约 7.5 次/秒，柱体每约 100 ms 跳一次。
2. 用户随后反馈 Demo 96 kHz / 24-bit FLAC 在独占模式下被输出为 96 kHz / **16-bit**。
   设备能力探测显示 Demo DAC 在独占下接受 PCM 16-bit 与 **PCM 24-in-32**
   （`WAVEFORMATEXTENSIBLE`，32-bit 容器、`wValidBitsPerSample = 24`），拒绝 float32、
   24-bit packed 与 32-bit int；旧候选表缺少 24-in-32，因此只能退到 16-bit。
   NAudio 2.2.1 的 `WaveFormatExtensible(rate, bits, channels)` 无法表达 24-in-32
   （固定 `wValidBitsPerSample = bits`），需按 WAVEFORMATEXTENSIBLE 内存布局自行构造。
3. 96 kHz 实际未被重采样（客户端流仍是 96 kHz）；界面显示的 48 kHz 是设备端点
   （Windows 声音设置里的默认格式），只在共享模式下参与混音，不是独占模式的强制约束。

### 18.2 决策

1. 独占模式按 `[10, 20, 50, 100] ms` 逐级尝试缓冲区：小缓冲区初始化失败时自动回退，
   保留对缓冲区不友好设备的兼容路径；每种协商格式（采样率 × 位深）各自尝试。
2. 共享模式保持请求 100 ms（实际回调节奏由 Windows 音频引擎决定，约 10 ms）。
3. §4.3 更正为“独占模式回调周期由缓冲区时长决定”，删除“所有缓冲区大小都按约 10 ms 回调”的表述。
4. 独占模式格式协商改为“优先源格式、尽量少转换”：
   - 采样率顺序：源采样率 → 源采样率 / 2 → 源采样率 × 2 → 48k / 96k / 192k /
     44.1k / 88.2k / 176.4k / 352.8k / 32k（去重，限 32k–384k）；
   - 每个采样率按质量从高到低尝试：float32（plain / extensible）→ PCM 32-bit →
     **PCM 24-in-32** → PCM 24-bit packed（plain / extensible）→ PCM 16-bit（plain / extensible）；
   - 每个候选先经 `IsFormatSupported(Exclusive, …)` 精确预检，避免 NAudio 静默选择
     “最接近格式”并做内部 DMO 重采样；
   - 用自建的 WAVEFORMATEXTENSIBLE 工厂表达 24-in-32，并新增 float → 32-bit 整数容器
     的转换 provider（按有效位左对齐）。
5. 输出信息以 `WasapiOut.OutputWaveFormat`（真正注册给 WASAPI 的流格式）为准；
   位深区分“有效位深”与“容器位深”（例如 `24-bit（32-bit 容器）`）。
   音频信息窗口额外展示设备端点默认格式，但不把它当作独占模式的输出格式：
   独占模式下该值不参与音频管线，不显示该段落，只在窗口底部保留一句暗色提示
   “Windows 设备默认格式仅由共享模式使用，已省略”；共享模式下它才是引擎最终转换的目标格式，
   作为“设备端点（最终输出）”显示。
   独占模式设备实际运行在协商出的客户端采样率（例如 44.1 kHz 源即 44.1 kHz 流），
   设备端点默认的 48 kHz 只由共享模式使用；设备/驱动若在内部再处理，WASAPI 层面不可观测。

### 18.3 实测（Demo 数据）

回调节奏（48 kHz 立体声）：

| 模式 | 缓冲区 | 每次 Read 样本 | 写入频率 | 最大写入间隔 |
| --- | --- | --- | --- | --- |
| 共享 | 100 ms | 960 | 约 100 次/秒 | 约 15 ms |
| 独占（旧） | 100 ms | 9600 | 约 7.5 次/秒 | 约 104 ms |
| 独占（新） | 10 ms | 960 | 约 100 次/秒 | < 20 ms |

24-in-32 协商（Demo 设备，96 kHz 源）：

| 候选格式 | 独占模式 |
| --- | --- |
| float32（plain / extensible） | 拒绝 |
| PCM 24-bit packed（plain / extensible） | 拒绝 |
| PCM 16-bit | 接受 |
| PCM 24-in-32 | 接受 |

修复后日志：`client=96 kHz · 24-bit（32-bit 容器） · 立体声`，转换仅剩
`32-bit float → 24-bit`，无重采样；设备端点默认格式仅作参考。修复前同一文件：
`client=96 kHz · 16-bit`，转换出现 `32-bit float → 16-bit`。

### 18.4 界面

- 正在播放页「输出」徽标：独占模式显示协商出的客户端流（最终送往设备的格式），
  共享模式显示设备端点（引擎最终转换后的格式）；tooltip 列出设备、输出流、混音格式与转换项。
- 「音频信息」窗口（输出徽标可点击打开）：音轨 → 解码器 → 转换 → 信号处理 → 缓冲区 →
  输出 →（共享）引擎混音 →（共享）设备端点，逐级展示各阶段实际格式；
  独占模式不显示设备端点段落，仅保留底部暗色提示。

### 18.5 后果

- 独占模式延迟更低（10 ms 档），频谱 / 波形数据更新节奏与共享模式一致；
- 音频回调频率升高（10 ms 档约 100 次/秒）；回调本身很轻（下混 + 环形缓冲写入），
  实测 CPU 开销可忽略；
- 设备拒绝小缓冲区时会多几次初始化尝试（仅 Debug 日志），首次播放可能多几毫秒；
- 独占候选格式变多（最多 8 种 × 采样率），首次播放的协商时间增加（每候选一次
  `IsFormatSupported`，实测总耗时仍远低于一次封面解码）；
- 支持 24-in-32 的设备可在独占模式下输出 24-bit 有效位深（不再被迫 16-bit）；
  不支持 24-in-32 的设备仍按 16-bit 输出，行为与旧版一致；
- 共享模式行为不变（仍由 Windows 音频引擎混音并转换到设备端点格式）。

### 18.6 备选方案（已否决）

- 保持 100 ms 独占缓冲，改在采样分析侧做时间轴插值 / 平滑以掩盖数据阶梯：治标不治本，
  且引入额外抖动算法与延迟，否决。
- 独占强制固定 10 ms、不提供回退：设备兼容性风险，否决。
- 直接修改 Windows 设备默认格式（`PKEY_AudioEngine_DeviceFormat`）来“匹配最佳输出”：
  会改动系统全局设置、影响其它应用，且本地用户环境不可写 / 不应写，否决；
  独占模式本身已绕过 Windows 混音引擎，无需修改系统设置即可按设备能力协商。
