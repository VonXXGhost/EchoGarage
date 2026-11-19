# ADR-015：播放进度条的波形视图（Waveform Seek Bar）

| 项目 | 内容 |
| --- | --- |
| 状态 | **Accepted（2026-11-14；2026-11-16 并入原 ADR-015-A1 / ADR-015-A2）** |
| 实现 | **P0 + P1 已实现（2026-11-14）**，提交 `af9ddf2` / `51107d6` / `d187305`；P2 / P3 未做 |
| 日期 | 2026-11-14 |
| 关联 | ADR-002（分层）、ADR-003（文件是真相）、ADR-006（配置与库分离）、ADR-010（缓存失效）、KB-001（解码与 Seek 输入） |
| 影响面 | 新增 Core 抽象 / 新增磁盘缓存格式 / 新增 App 控件 / 主题令牌 / AppSettings 字段（2026-11-16 起按 ADR-024 五套主题提供令牌） |
| 不改动 | SQLite schema、`IAudioPlayer`、扫描器、现有 `SeekSlider` 的行为 |

> 本文是已批准的方案（owner 于 2026-11-14 批准），按 §13 分阶段实施。P0 + P1 已实现并合入。
>
> **2026-11-16 两份修订案已并入本文（原 ADR-015-A1 / ADR-015-A2，owner 已批准）**：
> 已播放区不再用半透明遮罩覆盖，改为按区域 clip + `PushOpacity(0.26)` 直接绘制三色带
> （未播放区 100% 不透明度），`WaveformPlayedOverlayBrush` 令牌与依赖属性已删除（§5.3 / §5.4 / §9.1 / §9.5）；
> 高度窗口改为只向下放宽下界，最小显示动态范围 30 dB，`AnalyzerVersion` 升至 2、旧 `.wfm` 自动失效重算（§6.6 / §6.8 / §6.10 / §7.3 / §12.1）。
> 三色带配色随 ADR-024 五套主题重定（低饱和赤陶 / 麦金 / 蔚蓝体系）。
> 实施过程中 owner 又确认了三处调整（切换按钮形态、波形高度、归一化策略），本文已同步为**实际实现**的规格；修订点见 §17 修订记录。

---

## 0. 决策摘要

1. 进度条支持在**滑块**与**整曲波形**两种形态间切换；切换按钮放在进度区左端（30 px 见方、仅图标），观感与右侧顺序/随机按钮一致——只靠图标区分形态，不画“已启用”底色。
2. 波形是**整曲缩略**，不是 DJ 软件的滚动波形：平底、自下而上 **低频 / 中频 / 高频** 三层**不透明堆叠**，总高度表示整体能量；波形高度**填满传输条可用高度**（约 63 px，最小 28 px），不是固定 28 px。
3. 高度用**逐曲分位数拉伸的 RMS 包络**（p5–p99.5，最小显示动态范围 30 dB，只向下放宽下界），颜色用**频段份额的幂次加权**（p = 0.8 / 1.1 / 1.4），落盘量化到 0–255。
4. 分析在**后台线程分段**完成（每段约 5 s 音频），已分析部分**按时间顺序即时绘制**；完整结果写入**逐曲二进制缓存**（50 列/秒，约 40 KB/首），缓存带 **LRU 上限（可配置，默认 256 MB）**；失败只记日志，界面回退为轨道底。
5. 渲染用自定义 `FrameworkElement` + **按尺寸缓存、按段追加的三层几何**；播放位置变化只重绘已播放区（26% 不透明度）与播放头。
6. **不引入新 NuGet 依赖**：解码复用 NAudio 的 `AudioFileReader`，FFT 用 NAudio 2.2.1 自带的 `NAudio.Dsp.FastFourierTransform`。
7. 模式是**全局记忆**：首次默认滑块，用户切到波形后一直保持；持久化到 `AppSettings.SeekBarMode`。
8. 配色全部走**主题令牌**（ADR-024 的五套主题），控件内不硬编码颜色。
9. 渐进渲染期间归一化窗口**锁死**在 −48…0 dB（上限固定为 0 dBFS = 电平最大值），已画出的列在生成过程中不重排；分析完成时才用全曲 p5 / p99.5 重算一次最终快照（owner 2026-11-14 决定，以“生成过程零跳动”优先）。
10. 缓存清理改为**菜单**（可分别清封面/波形/全部）；新增 `--waveform <file> <png>` 无 UI 诊断开关。

---

## 1. 背景与问题

当前传输条只有一条 4 px 高的进度滑块（`MainWindow.xaml` 的进度区）。它能回答“播到哪了”，但不能回答“这一段是什么状态”：

- 前奏、主歌、副歌、间奏、尾奏在进度条上完全同质，用户想跳到“最炸的那段”只能盲拖；
- 无法判断某一段是低频主导（鼓/贝斯）还是高频主导（镲/空气感）；
- 参考图（用户提供）展示了 DJ 软件式的彩色波形：平底、低频在底、高频成尖峰，靠轮廓与色带就能一眼读出段落能量。

用户希望：**点击波形任意位置跳转**，并在 72 px 高的传输条里优雅地展示。

## 2. 目标与非目标

### 2.1 目标

| 编号 | 目标 | 验收方式 |
| --- | --- | --- |
| G1 | 一键在滑块/波形间切换，状态持久化 | 重启应用后仍是上次选择 |
| G2 | 波形可点击/拖动跳转，与滑块行为一致 | 点击处播放头与时间文本一致；拖动只提交一次 Seek |
| G3 | 传输条可用高度（约 63 px）内可读：轮廓有层次、三色分明 | 深/浅主题 + 100%/150% DPI 目测 |
| G4 | 快速判断能量：段落起伏可见，瞬态不糊 | 用 EDM/流行/摇滚三首对照 |
| G5 | 不阻塞 UI、不影响启动与切歌 | 分析在后台；播放开始 < 1 s 不退化 |

### 2.2 非目标

- N1 不做 DJ 式滚动/缩放波形、不做 beat grid 与 BPM 对齐；
- N2 不在歌曲列表每一行画迷你波形（未来可复用同一份缓存，但不在本次范围）；
- N3 不做频谱瀑布图、不做实时频谱动画；
- N4 不改变播放器 `Seek` 语义，不改 `IAudioPlayer`；
- N5 不做跨曲目的绝对响度比较（波形是逐曲相对的，见 §6.6）。

## 3. 现状（代码事实）

> 下列行号是 2026-11-14 实施完成后的快照；行号仍会随改动漂移，检索时以 `x:Name`（`SeekSlider`、`WaveformView`、`WaveformModeToggle`）和成员名（`HookSeekSlider`、`HookWaveformSeek`、`CommitSeek`）为锚点。

### 3.1 传输条布局

`src/EchoGarage.App/Views/MainWindow.xaml`（传输条 `Border Grid.Row=3 Height=72` 约在 :1060；行号以归档时为准，检索锚点为下列 `x:Name` / 成员名）：

- `Border Grid.Row=3 Height=72`，内部 `Grid Margin="16,0"`，三列 `Auto / * / Auto`；
- 列 0：上一首/播放/停止/下一首；
- 列 1：`Grid Margin="20,0"`，四列 `30 / 46 / * / 46` —— 左端形态切换按钮、`PositionText`、波形或滑块、`DurationText`；
- `SeekSlider`（约 :1136）：`Minimum=0`、`Maximum={Binding SeekMaximum}`、`Value={Binding PositionSeconds, Mode=OneWay}`、`IsEnabled={Binding HasCurrentTrack}`、`IsMoveToPointEnabled=False`；
- `WaveformSeekBar`（约 :1142，`x:Name="WaveformView"`）：`Margin="0,4"`，`Peaks / Position / Duration` 绑定，`IsEnabled={Binding HasCurrentTrack}`，`Visibility` 与滑块互斥；
- 列 2：音量标签 + 120 px 滑块 + 百分比 + 静音按钮。

滑块模板（`Resources/Controls.xaml`）：高 18 px、轨道 4 px、Thumb 11 px；波形形态下这 18 px 让位给填满高度的波形控件（§5.5）。

### 3.2 进度与 Seek 数据流

- `MainViewModel` 用 200 ms 的 `DispatcherTimer` 轮询 `Player.Position/Duration` 更新 `PositionSeconds`、`PositionText`（`MainViewModel.OnPositionTick`）；
- `SeekMaximum => DurationSeconds > 0 ? DurationSeconds : 1`（`MainViewModel.SeekMaximum`）；
- 滑块手势由 `MainWindow.xaml.cs` 的 `HookSeekSlider` / `OnSeekMouseDown` / `OnSeekMouseMove` / `OnSeekMouseUp` / `OnSeekLostCapture` 手工实现：`AddHandler(..., handledEventsToo: true)` + `CaptureMouse`，按 `x / ActualWidth` 计算目标，`BeginSeek()` 冻结 tick，松开时 `CommitSeek(target)`；
- 提交有 0.75 s 与 2 px 的防误触门限（`CommitSeekInteraction`），因为滑块有可抓的 Thumb；波形没有实体，不走这个门限（§10.2）；
- `CommitSeek` 内部 `Player.Seek` 后直接写 `PositionSeconds/PositionText`（`MainViewModel.CommitSeek`），Seek 本身是 `Pause → reader.CurrentTime → Play`（KB-001，实测 2–9 ms）。

### 3.3 缓存与主题基础设施

- `AppPaths.DefaultCacheDirectory = <root>\data\Cache`，`EnsureCreated()` 建目录；设置页可改目录并迁移（`AppHost.ApplyCacheDirectoryAsync`，`AppHost.cs:109-123`）；
- `FileCoverCache` 是现成的范式：目录 + 内容键 + sidecar `.stamp` + `.none` 负缓存 + `SemaphoreSlim` 串行写 + `RelocateAsync`（`Infrastructure/Caching/FileCoverCache.cs`）；
- 主题令牌模式：`Color x:Key` + `SolidColorBrush x:Key`（`StaticResource` 引用），视图一律 `DynamicResource`（ADR-024 起的五套主题字典：`PureStudio` / `WarmPaper` / `NordicIce` / `MidnightVinyl` / `SlateStudio`）；
- `AppSettings` 是 JSON 持久化的 sealed class，新增字段有默认值即向后兼容（`SchemaVersion=1` 不需要迁移）；
- 当前**没有任何波形相关代码**，`NAudio.Dsp` 也未被引用（但 NAudio 2.2.1 已包含 `FastFourierTransform` 与 `BiQuadFilter`）。

### 3.4 约束

- AGENTS.md §6：启动 ~0.9 s、切列表几十 ms，启动路径不得做全库扫描/解码；
- 界面颜色只能走主题令牌；
- 跨层能力必须经 `Core/Abstractions` 暴露，`AppHost` 唯一装配点；
- 引入新依赖需 ADR；本方案不引入；
- IO/解码不得阻塞 UI 线程；
- 波形分析不得与播放解码抢 IO：分析延迟 300–500 ms 启动、`BelowNormal` 优先级、每段让出（§6.10）。

## 4. 竞品调研

### 4.1 总览

| 软件 | 形态 | 频段/配色 | 交互 | 算法公开度 |
| --- | --- | --- | --- | --- |
| Serato DJ | 主波形（滚动） | 红=低、绿=中、蓝=高；Ctrl+点击切三频段视图 | 点击/按住 scrub、± 缩放 | 闭源，官方仅描述 |
| rekordbox / CDJ | 整曲概览（PWV）+ 滚动主波形 | 3Band：蓝=低、橙=中、白=高（硬件）；软件可 RGB | 点击跳转、Cue | ANLZ 格式已被逆向 |
| Traktor Pro 2 | 滚动 | 4 种 Color Mode（Ultraviolet / Infrared / X-Ray / Spectrum） | 点击跳转、Beat Jump | 闭源 |
| Mixxx | 滚动 + 概览 | RGB / Filtered / Stacked 三种；600 / 4000 Hz 分频 | 点击跳转 | 开源（GPL） |
| BBC audiowaveform | 静态图/CLI | 单色为主，可生成多分辨率 | 无 | 开源 |

### 4.2 Serato

官方说明主波形按频率着色：**红=低频、绿=中频、蓝=高频**；`Ctrl` + 点击可切换为只显示三个频段的频谱视图；按住点击可 scrub，`+/-` 缩放。它的颜色是“频段标签”，不是能量份额——低频永远红、高频永远蓝，用户靠颜色认频段，靠高度认能量。

更贴近本功能的是 Serato 的 **Track Overview Display**：整曲缩略、按频谱着色（同样是红/绿/蓝）、点击任意位置跳转，背后用灰线标出每分钟（细）与每 5 分钟（粗），未分析时载入后填充。这正是“进度条位置的整曲概览”形态。

### 4.3 rekordbox / CDJ（重点）

rekordbox 的分析结果存在 `ANLZ` 文件里，其中 `PWV7` 是 150 列/秒的三频段数据，`PWV5` 是带颜色的预览，`PWAV` 是 400 列的单色概览。开源项目 `baken-export` 4.0.2 的 `waveform.rs` 给出了完整的生成算法，并用 rekordbox 7.2 的 1070 首曲目对比，单色高度与 whiteness **99.5% 的列逐字节吻合**（颜色与三频段是拟合近似）。要点：

- 单声道 `(L+R)/2`，第 i 列覆盖 `[round(i·sr/150), round((i+1)·sr/150))`；
- 每列取 `peak = max|x|`；三个频段用二阶 Butterworth（RBJ cookbook，Q=1/√2）滤波后逐样本取峰值：
  - low = 300 Hz 低通；mid = 250 Hz 高通 → 1500 Hz 低通；high = 2500 Hz 高通；whiteness 用 150 Hz 低通；
- 频段包络有**逐段 release**：`env[i] = max(band[i], env[i-1] × RELEASE)`，`RELEASE = [0.97, 0.93, 0.90]`；
- **逐频段自动增益**：`gain = clamp(round(TARGET / max), FLOOR, CAP)`，target `[78,105,115]`、floor `[80,80,95]`、cap `[267,300,500]`；`pwv7 = min(127, round(SCALE × gain × env))`，scale `[1.25,1.15,0.6]`；
- 高度：`height = min(31, floor(31.5 × (peak / trackPeak)²))`（5 bit），即**峰值平方**映射；
- 颜色（PWV5）：每频段 `level = round(7 × sqrt(band / maxBand))`，是**相对最强频段的份额**，再配 5 bit 高度；
- 概览（PWAV）：400 列，`height = round(24 × rms / rmsMax)`（线性 RMS，不平方）。

结论：rekordbox 的“彩色整曲概览”本质是 **1200 列、逐频段自动增益的三层电平**；高度用峰值平方，颜色用相对份额。它不追求绝对响度，而是让每一段都“看起来有内容”。

CDJ-3000 / XDJ-RX3 的经销商资料把 3Band 配色写作 **Blue=LOW / Orange=MID / White=HIGH**，并说明“三种颜色分别表示各频段的音量”；AlphaTheta 支持文档只确认 3Band 的存在与部分硬件仅显示 Blue，以及 rekordbox 6 的 3Band 需要“Add New Analysis Data”、旧分析显示 `?`——后者说明分析结果**可以版本化失效**，与我们的缓存版本号设计一致。

### 4.4 Traktor

Traktor Pro 2 提供 4 种 Color Mode（Ultraviolet / Infrared / X-Ray / Spectrum），彩色波形用于区分低频与高频内容，用户可切换。它的“全彩”模式在矮高度下会退化成噪点，不适合我们。

### 4.5 Mixxx

开源实现，三种波形（RGB / Filtered / Stacked）；滤波用 4 阶 Bessel，分频 600 / 4000 Hz；分析按列 RMS 存 `.mixxx` 文件。社区 bug 1268299 批评其高频段被不成比例放大、绘制前又加了 gain——正好说明“逐段增益 + 份额映射”很容易失衡，需要像 §6.7 那样显式调参并用真实曲目验证。

### 4.6 BBC audiowaveform

`audiowaveform` 是“多分辨率峰值”方案的经典实现；issue #210 中有人用扫频实测 rekordbox，认为它更像 4 段（蓝低频高切 110 Hz、金低中 150–160 Hz、橙中频 180–800 Hz、白高频 2500–3000 Hz 与 19 kHz 之间有静音间隙）。这条证据与 `baken-export` 的 2500 Hz 高通互相印证，但也说明**厂商的分频点并不统一**，我们应按听感与像素可读性自行定。

### 4.7 可借鉴 / 不可借鉴

| 借鉴 | 理由 |
| --- | --- |
| 三频段 + 固定颜色语义 | 用户一眼认频段；比“按频率连续渐变”稳定 |
| 平底、不镜像 | 把全部像素用于能量，而不是对称的上下两半 |
| 逐曲归一 | 响度战争下固定 dB 窗口必然显平（实测 p50 与 p95 只差 3.1 dB） |
| 缓存 + 版本号失效 | 分析一次约 1 s，必须落盘；分析器升级要能自动重算 |

| 不借鉴 | 理由 |
| --- | --- |
| 逐频段自动增益到固定 target | 会把安静段落也“拉满”，破坏段落对比；我们用份额幂次达到类似目的但保留相对关系 |
| 峰值平方映射 | 矮高度下会把弱段压成 0，且对瞬态过度敏感 |
| 滚动/缩放 | 进度条必须显示整曲，缩放是另一种交互 |
| 全彩频谱 | 矮高度下不可读 |

## 5. 视觉设计

### 5.1 参考图拆解

用户附图（248×414，6 行波形）的像素统计（4× 放大后取样）：

| 元素 | 颜色 | 占整图面积 | 占波形像素 |
| --- | --- | --- | --- |
| 背景 | `#121212` | 48.8% | — |
| 奶油（低频） | `#F3E6D0` | 22.5% | ≈57% |
| 深棕（中频） | `#844B1F` | 12.8% | ≈32% |
| 蓝（高频） | `#1737B1` | 3.3% | ≈8% |
| 琥珀（过渡/高光） | `#C98335` | 1.1% | ≈3% |

逐列看，颜色是**自下而上的不透明堆叠**：奶油在最底、深棕居中、蓝色只出现在顶部尖峰；单列内三层比例可剧烈变化（实测某列 8 px 蓝 / 64 px 棕 / 20 px 奶油，4× 下），说明**总高度是整体包络，内部按频段份额切分**。没有渐变、没有镜像、没有透明叠加。

### 5.2 分层模型

```
y = 0   ┌──────────────────────────────┐
        │  高频（蓝）  ← 只出现在尖峰    │
        ├──────────────────────────────┤
        │  中频（琥珀/棕）← 人声、军鼓   │
        ├──────────────────────────────┤
        │  低频（奶油/沙）← 鼓、贝斯     │
y = H   └──────────────────────────────┘   ← 平底
```

- 三层**不透明**、边界像素对齐，不做 alpha 叠加（叠加会得到浑浊的第三色，参考图不是这种画法）；
- 低频永远在底、高频永远在顶，颜色语义固定，用户不需要图例；
- 某一层高度为 0 时直接不画，不保留空槽——这样弱段只剩低频色，强段才出现蓝尖。

### 5.3 配色（主题令牌）

深色主题直接取参考图色；浅色主题的面板是 `#FFFFFF`，奶油色几乎不可见，必须整体压深一档，并给波形一条浅灰轨道底色：

| 令牌 | 深色 | 浅色 | 用途 |
| --- | --- | --- | --- |
| `WaveformLowBrush` | `#F3E6D0` | `#C9A36B` | 低频层 |
| `WaveformMidBrush` | `#C98335` | `#B2691F` | 中频层 |
| `WaveformHighBrush` | `#1E43C8` | `#2F55C8` | 高频层 |
| `WaveformTrackBackgroundBrush` | `#1A1F27` | `#F0F2F6` | 波形轨道底 |
| `WaveformPlayheadBrush` | `#E7EAEF` | `#1B1F27` | 播放头（用文本主色，两种主题都够亮） |

**已播放区的画法（2026-11-16，并入原 ADR-015-A1）**：不引入覆盖色令牌——未播放区 `[playhead, width)` 以 100% 不透明度绘制三色带；已播放区 `[0, playhead)` 同样绘制三色带，但整体 `PushOpacity(0.26)`（控件内部常量 `PlayedOpacity = 0.26`，落在 owner 要求的 25–28% 区间中部；未来若需按主题微调再升级为令牌）。这样低/中/高三个频段在两侧都保持色相分离，不会像半透明覆盖层那样与面板底色混合、在浅色主题下发灰。播放头、悬停预览、聚焦环与未分析区提示线仍绘制在色带之上。

> 令牌必须同时写进主题字典（ADR-024 的 5 套），控件默认样式里用 `DynamicResource` 绑定；主题切换时 `DynamicResource` 会重新求值，控件需在画刷变化时使几何缓存失效（§9.2）。

### 5.4 状态

| 状态 | 画法 |
| --- | --- |
| 无歌曲 | 只画整条轨道底（30% 不透明度），无文字 |
| 分析中 | 已分析段正常绘制；未分析段轨道底 + 2 px 基线，前缘 1 px 竖线；不转圈、不闪烁（避免干扰播放） |
| 就绪 | 三层波形 + 已播放区（26% 不透明度）+ 播放头 |
| 悬停 | 鼠标处 1 px 竖线（`TextSecondary`，50%）+ 可选时间气泡（P2） |
| 键盘焦点 | 内侧 1 px `AccentBrush` 描边 |
| 分析失败 | 同“无歌曲”，日志 `Warn`，状态栏不报错（非关键路径） |

### 5.5 尺寸与布局

- 波形高度**填满传输条可用高度**：控件 `VerticalAlignment=Stretch`、`MinHeight=28`、`Margin="0,4"`，在 72 px 传输条里实测约 63 px（owner 2026-11-14 反馈：尽量填满上下空间）；
- 进度区列定义从 `46 / * / 46` 改为 `30 / 46 / * / 46`：30 px 给切换按钮，波形宽度只减少 30 px；
- 切换按钮 30×30、仅图标：波形形态=三根高低不同的竖条，滑杆形态=横线+圆点；与右侧顺序/随机按钮同一观感（灰阶、悬停 `RowHoverBrush`、无选中底色、无焦点框）；
- `ToolTip` 随形态在「切换为波形进度条 / 切换为滑杆进度条」之间切换；
- 典型窗口（1280 宽、侧栏 236、专辑面板 320）下波形宽约 600–700 px；无专辑面板时约 900 px。

```
┌ 传输条 72 px ──────────────────────────────────────────────────────────────┐
│ [⏮] [▶] [⏹] [⏭] [▂▄▆] 0:42  ▐███▓▓▓▒▒░░░░░░░░░▌  3:33   音量 ─●─ 75%      │
│                 形态切换   └─── 波形（填满高度，约 63 px）───┘              │
└────────────────────────────────────────────────────────────────────────────┘
```

## 6. 算法设计

### 6.1 流水线

```
文件 → 解码（AudioFileReader）→ 单声道 → STFT(2048, Hann, hop=10 ms)
     → 三频段 RMS + 总 RMS → 10 ms 包络（attack 60 / release 250 ms）
     → 20 ms 一列（2 帧）→ 逐曲 dB 分位拉伸 → 高度 0..255
     → 频段份额（幂次）→ low/mid/high 0..255（和为 255）
     → 写 .wfm 缓存
```

### 6.2 解码

- 用 `AudioFileReader`（与播放器同一解码路径，KB-001：wav/mp3/aiff 原生，flac/m4a/aac/wma 走 Media Foundation）；
- 读成 float 交错缓冲，按 `mean(channels)` 下混单声道；采样率保持原生（44.1k/48k 均可，频率分界按 Hz 计算，与采样率无关）；
- 在后台线程顺序读取，不 Seek、不并发；整曲只读一遍；
- 分析期间播放器可以同时播同一文件（两个只读句柄，Windows 允许）。

### 6.3 STFT 与频段

- 帧长 `N = 2048`（44.1k 下 46 ms），Hann 窗，hop = `round(sr / 100)`（44.1k 为 441，即 10 ms）；
- 单帧能量用 Parseval 折算：

```
P_b = Σ_{k∈band} (re² + im²) × (k==0 || k==N/2 ? 1 : 2)
MS_b = P_b / (N² × mean(w²))        // Hann: mean(w²)=0.375
RMS_b = sqrt(MS_b)
```

- 分频点：低 `20–200 Hz`、中 `200–4000 Hz`、高 `4000–16000 Hz`（上限取 16 kHz，超过的部分对视觉无意义）；
- 总能量 `RMS_total = sqrt(RMS_low² + RMS_mid² + RMS_high²)`（忽略 20 Hz 以下与 16 kHz 以上，误差可忽略）。

> 为什么高频分界取 4 kHz 而不是 rekordbox 的 2.5 kHz：2–4 kHz 是人声“临场感”最集中的频段，把它算进高频会让几乎每一句人声都顶出一条蓝边；4 kHz 以上主要是镲片与空气感，蓝色只在真正的瞬态出现，和参考图的细蓝尖一致（也与 Mixxx 的 4 kHz 一致）。

### 6.4 列聚合

- 两帧（20 ms）合成一列，即 **50 列/秒**；
- 列高度取两帧总 RMS 的算术平均；列频段取两帧频段功率的平均再开方（等价于 20 ms 窗口的 RMS）；
- 50 列/秒的理由：3 分钟曲目在 900 px 上约 5 px/s，50 列/秒 = 每像素 10 列，足够聚合出稳定轮廓；再高的列率只增加缓存体积。

### 6.5 包络

在 10 ms 帧率上对总 RMS 与三个频段分别做一阶包络：

```
a = 1 - exp(-0.010 / 0.060) ≈ 0.1535   // attack 60 ms
r = 1 - exp(-0.010 / 0.250) ≈ 0.0392   // release 250 ms
env += (x - env) * (x > env ? a : r)
```

attack 让鼓点“起得快”，release 让波形在鼓点之间不塌成梳齿。原型阶段实测：不做包络时，相邻像素高度差中位数 1 px、p90 5 px（10 ms 帧在节拍内剧烈波动），加包络后轮廓连续。

### 6.6 高度映射（逐曲对比度拉伸）

响度战争下固定 dB 窗口不可用：实测某 EDM 曲 p50 RMS=0.414、p95=0.587，只差 3.1 dB，映射到 0–255 后几乎所有列都挤在 180 以上，看起来是一条平带。

做法：

1. 对整曲所有列的 `RMS_total` 取 dB：`20·log10(max(rms, 1e-5))`；
2. 取 `lo = p5`、`hi = p99.5`（排序后取分位；99.5 而不是 100，避免单个爆音把上限拉高）；
3. `lo = max(SilenceDb, min(lo, hi - MinimumStretchDb))`，`MinimumStretchDb = 30.0`（最小**显示**动态范围：只向下放宽下界，**不抬高** `hi`）；
4. `v = clamp((dB - lo) / (hi - lo), 0, 1)`；`height = round(255·v)`，`v>0` 但四舍五入为 0 时取 1；
5. 静音列（`RMS < 1e-5`）高度为 0。

**为什么是“向下放宽”而不是“抬高上限”（2026-11-16，并入原 ADR-015-A2）**：抬高上限（旧做法 `hi = max(hi, lo + 8)`）对恒定电平 / 窄动态素材无效——这类素材 `p5 ≈ p99.5`，`lo ≈ hi` 被抬到 `lo + 8`，而所有列的 dB 都约等于 `lo`，高度归 0，整条波形显示为空白（渐进分析时固定 −48…0 dB 窗口反而正常，分析完成切到最终快照的瞬间才变空）。改为向下扩展下界后：动态范围 ≥ 30 dB 的素材窗口逐位不变（宽动态零回归）；不足 30 dB 时保证至少 30 dB 的显示量程，稳定列高度可达满高，2–3 dB 的内部起伏只占约 7% 高度，不会被放大成“假层次”；`lo` 钳制在 `SilenceDb`（−100 dB），极端安静素材的窗口也不会落到数字静音以下。

### 6.7 颜色份额（幂次加权）

直接按物理能量占比堆叠不可行：实测高频能量份额的中位数就有 18–30%（分频 2 kHz 时），线性堆叠会让蓝色铺满顶部，三层变成横条纹。

做法：

```
q_low  = low^0.8
q_mid  = mid^1.1
q_high = high^1.4
share_j = q_j / (q_low + q_mid + q_high)
```

- 指数 > 1 压掉常态能量、保留瞬态；低频指数 < 1 保证基底始终存在；
- 用**幅度**（RMS）而不是功率做幂次，指数才落在 0.8–1.4 这个可调区间；
- 份额落盘时量化：`low = round(255·share_low)`、`mid = round(255·share_mid)`、`high = 255 - low - mid`（保证三者严格相加为 255，渲染时不会出现 1 px 缝隙）；
- 静音列份额写 `(85,85,85)`：高度为 0 不绘制，但 `low+mid+high == 255` 的校验对所有列恒成立（原方案的 `(1,1,1)` 会破坏该校验）。

### 6.8 参数与实测

参数速查：

| 参数 | 值 | 说明 |
| --- | --- | --- |
| FFT / 窗 | 2048 / Hann | 46 ms @44.1k |
| hop | 10 ms | `round(sr/100)` |
| 列率 | 50 列/秒 | 2 帧一列 |
| 分频 | 200 / 4000 Hz | 低/中/高 |
| 包络 | attack 60 ms / release 250 ms | 10 ms 帧率 |
| 高度拉伸 | p5–p99.5，最小显示动态范围 30 dB（下界扩展） | 逐曲 |
| 份额幂次 | 0.8 / 1.1 / 1.4 | 低/中/高 |

三首真实曲目的份额中位数（分频 4 kHz，`p=(0.8,1.1,1.6)` 时）：

| 曲目 | 时长 | 低 / 中 / 高 |
| --- | --- | --- |
| EDM（mp3） | 173.1 s | 0.561 / 0.346 / 0.085 |
| 女声流行（flac） | 252.3 s | 0.605 / 0.352 / 0.033 |
| 东方摇滚（mp3） | 226.2 s | 0.461 / 0.481 / 0.047 |

`p_high` 扫描（低/中/高份额中位数）：

| `p_high` | EDM | 流行 | 摇滚 |
| --- | --- | --- | --- |
| 1.2 | 0.518 / 0.315 / 0.164 | 0.575 / 0.334 / 0.079 | 0.432 / 0.449 / 0.104 |
| 1.35 | 0.537 / 0.329 / 0.129 | 0.589 / 0.343 / 0.057 | 0.445 / 0.464 / 0.078 |
| 1.5 | 0.553 / 0.340 / 0.100 | 0.599 / 0.349 / 0.041 | 0.456 / 0.475 / 0.058 |

取 `p_high = 1.4`：蓝色中位 5–13%，与参考图蓝色占波形像素约 8% 同量级；`1.2` 蓝边过厚，`1.5` 又几乎看不见。

> 这些数值来自本地 Python 原型（numpy STFT），不是最终 C# 实现的测量值；实现完成后应以 C# 输出重新校准一次，并记录到 ADR 的“验证”一节。

### 6.9 与 rekordbox 的差异（为什么不用它的自动增益）

rekordbox 的 `gain = clamp(TARGET/max, FLOOR, CAP)` 是**逐频段把最强处拉到固定电平**，再配 `sqrt(band/maxBand)` 的颜色份额。这样做的副作用是：安静段落里的底噪也会被拉到可见高度，段落之间的能量对比被削弱。我们的目标是“一眼看出哪段最炸”，所以：

- 高度保留逐曲相对动态（分位拉伸，不做逐段增益）；
- 颜色用幂次份额（同样避免某一频段独占，但不改变总高度）；
- 代价是：不同曲目之间的波形高度不可直接比较——进度条只需要曲内比较，可接受。

### 6.10 分段分析与渐进渲染

整曲一次性分析在本地盘约 1 s，但网络盘/慢盘可能 3–5 s；启动即波形模式时，用户会先看到一段空白。因此分析按**时间顺序分段**产出，界面边分析边画。

**分段方式**：分段单位是**列**，不是文件块。解码按 4096 样本块读入，攒够 2048 样本窗口就推进一帧（hop 441），与一次性分析共用同一套状态（上一帧频段值、包络、累积分位）。这样**完成后的快照**与整曲一次性分析的输出应逐字节一致（§12 有对照测试），不会因为分段引入接缝。

**发布节奏**：每 **5 s 音频**（250 列）发布一次快照，UI 侧节流到 10 Hz；快照里的 `ValidCount` 之前的部分有效，之后不绘制。控件只**追加**新增列的几何（`DrawingGroup.Children.Add`），播放头与已播放区是独立 visual，每 tick 只改这两个。

**调度**：分析在 `LoadAsync` 完成、开始播放后 **300–500 ms** 才启动，避免与首帧解码抢 IO；线程 `BelowNormal`；每段结束 `await Task.Yield()` 让出，播放解码优先。切歌/停止时取消 CTS，已算部分丢弃（不写半截缓存）。

**渐进渲染的归一化窗口**（owner 2026-11-14 决定）：

| 时机 | 窗口 | 规则 |
| --- | --- | --- |
| 分析中（每次快照） | 固定 −48…0 dB，上限固定为 **0 dBFS**（电平最大值） | 全程不变：已画出的列高度不再变化，波形只向右延伸 |
| 完成 | 全曲 p5 / p99.5（最小显示动态范围 30 dB，下界扩展，§6.6） | 发布最终快照，控件整体重建一次 |

生成过程中的视觉代价：未完成时上限锁在 0 dBFS，而整曲峰值通常低于它，所以部分波形会显得比最终结果略“矮”，完成瞬间统一拉伸到 p5–p99.5。这是“生成过程零跳动、只收尾归一化”的取舍（owner 反馈：波形不能在渲染过程中变形）。不做逐段增益，理由同 §6.9。

**为什么不做两遍扫描**：预扫描估分位要把文件再读一遍（网络盘代价翻倍），而锁定窗口后过程中本来就零跳动，预扫描没有收益。

## 7. 缓存设计

### 7.1 位置与命名

```
<CacheDirectory>\Waveforms\wave_<sha1(path.ToUpperInvariant())[..20]>.wfm
```

- 与封面缓存同级、独立子目录；路径大小写不敏感（Windows），键用大写路径的 SHA-1 前 20 位，和 `CoverKeyFactory` 的风格一致；
- 一首 4 分钟曲目约 **40 KB**；1000 首约 **40 MB**，与封面缓存同一数量级；
- 缓存是**可丢弃的派生数据**，删掉只会触发重算，不影响媒体库。

### 7.2 二进制格式（EGWV v1，小端）

| 偏移 | 长度 | 字段 |
| --- | --- | --- |
| 0 | 4 | magic `"EGWV"` |
| 4 | 2 | format version = 1 |
| 6 | 2 | header size = 72 |
| 8 | 4 | analyzer version |
| 12 | 4 | sample rate |
| 16 | 2 | columns per second（50） |
| 18 | 2 | low crossover（200） |
| 20 | 2 | high crossover（4000） |
| 22 | 2 | reserved |
| 24 | 4 | column count |
| 28 | 8 | duration（TimeSpan ticks） |
| 36 | 8 | source file length |
| 44 | 8 | source mtime（UTC ticks） |
| 52 | 4 | floor dB（float） |
| 56 | 4 | ceiling dB（float） |
| 60 | 12 | reserved（置 0） |
| 72 | 4×N | 每列 `height, low, mid, high`（各 1 字节） |

- 头部长度固定 72，便于未来扩展；
- 载入时校验：magic、version、`stream.Length == 72 + 4×columnCount`、每列 `low+mid+high == 255`；任一不符则视为损坏，删除后重算；
- `source length + mtime` 用于失效判断（文件被替换/重新编码后必须重算）。

### 7.3 失效与原子写

- 载入前比较缓存的 `source length/mtime` 与当前文件；不一致 → 忽略并重算；
- `analyzer version` 变化 → 重算（rekordbox 的 “Add New Analysis Data” 是同一个思路）；当前 `AnalyzerVersion = 2`（2026-11-16 由 1 提升，对应“最小显示动态范围 30 dB”的高度窗口改动）：旧 v1 `.wfm` 读取时头部校验失败 → 删除文件 → 后台重新分析并按 v2 写回，无需迁移脚本、也无需用户手动清缓存；EGWV `FormatVersion` 仍为 1（72 字节头与每列 4 字节布局未变），按 72 字节头解析的外部工具不受影响；`FloorDb/CeilingDb` 的数值语义随分析器版本变化，缓存本身已用 analyzer version 区分；
- 写入：先写 `<final>.tmp`，`Flush` 后 `File.Move(tmp, final, overwrite: true)`；同卷内 Move 是原子的，进程崩溃不会留下半个正式文件；
- 目录懒创建（首次写入时），不在启动路径上做 IO。

### 7.4 并发与失败

- 同一路径的并发请求合并为一个 `Task`（`ConcurrentDictionary<string, Task<WaveformPeaks?>>`），避免快速切歌时重复分析；
- 全局同时只跑 **1 个** 分析任务（`SemaphoreSlim(1,1)`），避免切歌瞬间 CPU 峰值；
- 解码/解析失败：`Warn` 日志 + 会话内负缓存（同一路径本次运行不再重试），返回 `null`；
- 取消：切歌时取消上一个 `CancellationToken`；分析器每 64 帧检查一次，取消时不写缓存。

### 7.5 容量与清理

- **LRU 上限**：`AppSettings.WaveformCacheLimitMb`（默认 256；设置页可选 128 / 256 / 512 / 1024 / 0=不限）；
- 触发点：启动预热的后台任务里一次 + 每次写缓存后；枚举 `Waveforms\*.wfm` 的 `LastWriteTimeUtc`，超限时从最旧删到 **90%** 上限，给下一批留余量；单次枚举 1000 个文件约几毫秒，**不进入启动同步路径**；
- 读取时刷新 `LastWriteTimeUtc`（一次 `File.SetLastWriteTimeUtc`），让“最近用过”的排在后面；跳过当前曲目的文件；
- 清理入口改成**菜单**：设置页按钮 `清空缓存 ▾` 打开 `ContextMenu`（与传输条的预设菜单同一套写法，`ContextMenu` + `PlacementTarget`），菜单打开时异步统计并显示 `封面缓存 xx MB` / `波形缓存 xx MB` / `全部 xx MB`，分别可点、执行前二次确认，完成后回写 `CacheStatusText`；
- `AppHost.ApplyCacheDirectoryAsync` 迁移目录时一并迁移 `Waveforms` 子目录；
- 半截结果不落盘：取消的分析直接丢弃（重算约 1 s，换实现简单）；P2 如需“续算”，再给缓存头加 `validCount`。

## 8. 架构设计

### 8.1 分层与接口

```
Core
  Models/WaveformPeaks.cs          只读 DTO（4 个 byte[] + 元数据）
  Services/WaveformMath.cs         纯函数：分位拉伸、份额幂次、p80 聚合
  Models/AppSettings.cs            + SeekBarMode
  Abstractions/Interfaces.cs       + IWaveformAnalyzer / IWaveformCache / IWaveformService

Infrastructure
  Audio/WaveformAnalyzer.cs        STFT + 频段 + 包络 + 映射（纯计算，无 UI）
  Caching/FileWaveformCache.cs     EGWV 读写、原子写、失效、迁移、清理
  Audio/WaveformService.cs         缓存优先 + 单飞 + 负缓存 + 日志

App
  Views/Controls/WaveformSeekBar.cs  自绘控件（几何缓存 + 交互）
  ViewModels/MainViewModel.cs        模式、Peaks、加载/取消
  Views/MainWindow.xaml(.cs)         切换按钮、绑定、手势
  AppHost.cs                         装配 + 目录迁移 + 清理入口
```

接口草案：

```csharp
public sealed class WaveformPeaks
{
    public required int ColumnsPerSecond { get; init; }
    public required int ColumnCount { get; init; }
    public required int ValidCount { get; init; }   // 渐进：已分析列数，== ColumnCount 即完成
    public required TimeSpan Duration { get; init; }
    public required int SampleRate { get; init; }
    public required double FloorDb { get; init; }
    public required double CeilingDb { get; init; }
    public required byte[] Height { get; init; }   // 0..255；长度 = ValidCount（渐进快照）或 ColumnCount（完成）
    public required byte[] Low { get; init; }      // low+mid+high == 255
    public required byte[] Mid { get; init; }
    public required byte[] High { get; init; }
}

public interface IWaveformAnalyzer
{
    int Version { get; }
    Task<WaveformPeaks> AnalyzeAsync(
        string filePath,
        IProgress<WaveformPeaks>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IWaveformCache
{
    string Directory { get; }
    Task<WaveformPeaks?> TryLoadAsync(string filePath, CancellationToken cancellationToken = default);
    Task SaveAsync(string filePath, WaveformPeaks peaks, CancellationToken cancellationToken = default);
    void Invalidate(string filePath);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task RelocateAsync(string newRoot, CancellationToken cancellationToken = default);
}

public interface IWaveformService
{
    Task<WaveformPeaks?> GetAsync(
        string filePath,
        IProgress<WaveformPeaks>? progress = null,
        CancellationToken cancellationToken = default);
    void Invalidate(string filePath);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task RelocateAsync(string newRoot, CancellationToken cancellationToken = default);
}
```

- `IWaveformCache` 另有 `Task<int> TrimAsync(CancellationToken cancellationToken = default)`（实现补充）：供 `AppHost` 预热与设置页调整上限后立即按 LRU 裁剪，返回删除的文件数；
- `IWaveformService` 是 App 唯一依赖的接口；`GetAsync` 内部完成“查缓存 → 分析 → 写缓存”，缓存命中时 `progress` 不触发、一次返回完整快照；共享的进行中分析被前一个调用方取消时，后续调用方会重试（最多 3 次、间隔 20 ms），不会拿到已取消的任务；
- 渐进快照是**独立不可变对象**（数组按 `ValidCount` 截断，只追加），完成快照 `IsComplete == true` 且数组为全长；**只有完成快照写缓存**；
- `AnalyzeAsync` 必须在后台线程执行；`WaveformAnalyzer` 自己不 `Task.Run`，由 `WaveformService` 用 `Task.Run` 包住，便于测试直接同步调用；`progress` 回调在分析线程上触发，VM 负责 `Dispatcher` 切回 UI；
- 所有日志走 `IAppLogger`，分析耗时用 `[DEBUG] Waveform analyzed path=... columns=... ms=...`。

### 8.2 新增 / 修改文件

| 文件 | 动作 |
| --- | --- |
| `src/EchoGarage.Core/Models/WaveformPeaks.cs` | 新增 |
| `src/EchoGarage.Core/Services/WaveformMath.cs` | 新增（纯函数：分位拉伸、份额幂次、p80 聚合，便于单测） |
| `src/EchoGarage.Core/Models/AppSettings.cs` | 加 `SeekBarMode`（默认 `Slider`） |
| `src/EchoGarage.Core/Abstractions/Interfaces.cs` | 加 3 个接口 |
| `src/EchoGarage.Infrastructure/Audio/WaveformAnalyzer.cs` | 新增 |
| `src/EchoGarage.Infrastructure/Audio/WaveformService.cs` | 新增 |
| `src/EchoGarage.Infrastructure/Caching/FileWaveformCache.cs` | 新增 |
| `src/EchoGarage.App/Views/Controls/WaveformSeekBar.cs` | 新增 |
| `src/EchoGarage.App/Resources/Controls.xaml` | 加控件默认样式 + 切换按钮样式 |
| `src/EchoGarage.App/Resources/Themes/*Theme.xaml`（ADR-024 的五套主题） | 每套加 6 个令牌 |
| `src/EchoGarage.App/Views/MainWindow.xaml` | 进度区加切换按钮与波形控件 |
| `src/EchoGarage.App/Views/MainWindow.xaml.cs` | 波形手势（与滑块手势分开，互不影响） |
| `src/EchoGarage.App/ViewModels/MainViewModel.cs` | 模式、Peaks、加载、预览时间 |
| `src/EchoGarage.App/AppHost.cs` | 装配 + 迁移 + 清理 |
| `src/EchoGarage.App/Views/SettingsWindow.xaml(.cs)` | 清空缓存包含波形 |
| `tests/EchoGarage.Tests/*` | 见 §12 |

### 8.3 数据流

```
PlayRowAsync → CurrentRow 更新
             → VM.StartWaveformLoad(path)         （仅波形模式）
             → IWaveformService.GetAsync(path, progress)
                 ├─ FileWaveformCache.TryLoadAsync  ── 命中 → 一次返回（<10 ms）
                 └─ WaveformAnalyzer.AnalyzeAsync   ── 解码 + STFT + 映射（~1 s）
                     ├─ 每 5 s 音频：progress → 快照（ValidCount 前缀）
                     │                  → VM 切回 UI → 控件追加几何
                     └─ 完成：FileWaveformCache.SaveAsync → 返回完整快照
             → CurrentWaveform = peaks             （UI 线程）
             → WaveformSeekBar.Peaks 绑定 → 追加或重建几何
```

### 8.4 线程模型

| 阶段 | 线程 |
| --- | --- |
| 读缓存 | `Task.Run`（文件 IO，几十 KB） |
| 解码 + FFT | 线程池，`ThreadPriority.BelowNormal`，单飞 |
| 写缓存 | 同上（分析任务内） |
| 更新 `CurrentWaveform` | `await` 回到 UI 线程（VM 在 Dispatcher 上） |
| 绘制 | UI 线程，只画缓存好的几何 |

## 9. 渲染设计

### 9.1 控件

`WaveformSeekBar : FrameworkElement`，依赖属性：

```csharp
Peaks (WaveformPeaks?)
Position (double, seconds)
Duration (double, seconds)
LowBrush / MidBrush / HighBrush / TrackBrush / PlayheadBrush / HoverBrush / FocusBrush
```

- 已播放区不走依赖属性：`PlayedOpacity` 是控件内部常量 `0.26`（原覆盖色令牌 `PlayedOverlayBrush` 已在 2026-11-16 删除，见 §5.3）；
- 交互事件：`SeekStarted`、`SeekPreview`、`SeekCommitted`、`SeekCancelled`（均带 `WaveformSeekEventArgs(double Seconds)`）；
- 默认样式在 `Controls.xaml`，画刷全部 `DynamicResource`；
- `OnRender` 顺序：轨道底 → 三层波形（已播放区整体 26% 不透明度）→ 播放头 → 悬停线 → 焦点框；
- `Clip` 到 2 px 圆角矩形，避免首尾像素越界；
- 不继承 `Control`，避免模板开销；键盘焦点用 `Focusable=true` + `IsKeyboardFocusWithin`。

### 9.2 几何缓存

- 缓存的键：设备像素宽高、`dpiScale`、`Peaks.ColumnCount`、`Peaks.FloorDb/CeilingDb`（容差 0.01 dB）与已取用的 `ValidCount`；
- 每个频段一个 `GeometryGroup`，内部按设备像素列存放**冻结的 `RectangleGeometry`**；颜色由画刷在 `OnRender` 时给出，**画刷/主题变化不需要重建几何**；
- 位置每 200 ms 变化时**不重建几何**，只 `InvalidateVisual()` 重画已播放区、播放头与悬停线；
- 尺寸 / DPI / 列数 / 归一化窗口变化 → 清空重建（完成时窗口切换会触发一次，见 §9.7）。

### 9.3 像素对齐

- 列宽按**设备像素**计算：`W = ceil(ActualWidth × dpiScale)`，每列宽 `1/dpiScale` DIP，保证 150%/200% 缩放下不会出现 1–2 px 宽窄交替的摩尔纹；
- 每列的高度与三层分界都取整到设备像素；`RenderOptions.SetEdgeMode(this, EdgeMode.Aliased)` 让矩形边缘锐利；
- 高度 `hPx = round(h/255 × H)`；`lowPx = round(hPx × low/255)`、`midPx = round(hPx × mid/255)`、`highPx = hPx - lowPx - midPx`，并 clamp 防止舍入越界。

### 9.4 聚合

一个设备像素列对应缓存中的 `[x·N/W, (x+1)·N/W)` 段：

- 高度：该段内 `height` 字节的 **p80**（256 桶直方图，O(段长)）；
- 份额：按 `height^1.5` 加权平均，再归一化；
- 段为空（W > N）时取最近列。

为什么 p80 而不是 max/mean：max 会把每像素内的鼓点尖峰全部保留，顶边出现 1 px 梳齿；mean 会把瞬态抹平。p80 在“保留轮廓”和“稳定”之间（原型三首曲目对照后选定）。

### 9.5 播放头与已播放区

- 播放头 `x = Position/Duration × W`，1.5 设备像素宽，`WaveformPlayheadBrush`；
- 已播放区 `[0, x)` 以 26% 不透明度绘制三色带（`PushOpacity(PlayedOpacity)`，常量 `0.26`），未播放区 100%；不做颜色混合（2026-11-16 并入原 ADR-015-A1）；
- 播放头到达右端时钳制在 `W-1`，避免画到控件外；
- 位置更新频率沿用 200 ms（5 Hz）。在 3 分钟/900 px 下每 tick 移动 6 px，肉眼可见轻微跳动；P2 可选：波形模式下把定时器降到 100 ms，或在控件内做 200 ms 线性插值。

### 9.6 DPI 与空态

- 监听 `DpiChanged`（`OnDpiChanged` 重写）清缓存；
- 空态/加载态只画轨道底，不加文字，避免在传输条里出现第二套排版；分析失败也不显示错误文本（状态栏已足够）。

### 9.7 渐进渲染

- 控件比较新旧 `Peaks`：`ValidCount` 增大且归一化窗口未变 → 只对新增的尾部设备像素列建几何并 `Children.Add`（从上一段末列 -1 开始重叠；重叠处是同色不透明矩形，视觉无差别）；`ValidCount` 变小、窗口变化、尺寸 / DPI / 列数变化 → 整体重建；
- 未分析区域画轨道底 + 2 px 基线，前缘 1 px 竖线（`TextMuted` 45%），不用转圈/闪烁；
- 渐进快照的归一化窗口恒定（§6.10），因此分析过程**只追加、零重排**；唯一的整体重建发生在完成时（窗口切到全曲 p5 / p99.5），≤5 ms；
- 追加的几何 `Freeze()`；P1 直接在 UI 线程追加（250 列 <1 ms），P2 若单段变大再考虑后台构建。

## 10. 交互设计

### 10.1 模式切换

- `ToggleButton` 绑定 `IsWaveformMode`（TwoWay）；切换时写 `Settings.SeekBarMode` 并 `QueueSettingsSave()`；形态是**全局记忆**，下次启动沿用上次选择（首次默认滑块）；
- 切到波形且当前有歌 → 触发加载；切回滑块 → **取消进行中的分析并清空波形快照**（已完整落盘的缓存仍可被下次命中）；
- 滑块与波形放在同一个 Grid 单元格，用 `Visibility` 互斥（`BoolToVisibility` 在 `MainWindow.Resources` 注册；`InverseBooleanToVisibilityConverter` 以 key `InverseBoolToVisibility` 在 `App.xaml` 注册）；
- 波形模式下 `SeekSlider` 折叠，但**保留在可视树里**（`Collapsed` 不影响绑定），切回时位置仍然正确。

### 10.2 点击与拖动

复用滑块手势的经验（KB-001）：`AddHandler(PreviewMouseLeftButtonDown/Move/Up, handledEventsToo: true)` + `CaptureMouse`，但语义按波形调整：

| 事件 | 行为 |
| --- | --- |
| MouseDown | `Focus()`；`_vm.BeginSeek()`；捕获鼠标；计算并显示预览位置 |
| MouseMove | 更新预览线；调用 `_vm.PreviewSeek(target)` 只改时间文本（不 Seek） |
| MouseUp | `_vm.CommitSeek(target)`（**总是提交**）；释放捕获 |
| LostCapture | 仍在手势中 → `CommitSeek`（与滑块一致） |
| Escape | 置 `_cancelled` 标志 → `CancelSeek()` + 释放捕获；随后的 `LostCapture` 看到标志后**不再提交** |

与滑块的关键差异：滑块有 Thumb，点在没有位移的 Thumb 上视为误触（0.75 s 门限）；波形没有可抓的实体，**点击即跳转**，所以不做门限，但把提交放在 MouseUp（一次手势最多一次 Seek），避免拖动过程中反复 `Pause/Play` 造成卡顿。

### 10.3 键盘

控件获得焦点时：

| 键 | 行为 |
| --- | --- |
| ← / → | ∓5 s |
| Shift + ← / → | ∓1 s |
| PageUp / PageDown | ±30 s |
| Home / End | 0 / 时长 |

全部走 `CommitSeek`（立即生效），并 `e.Handled = true`。空格仍由窗口的全局处理器负责播放/暂停，控件不拦截。

窗口的键盘处理挂在 `PreviewKeyDown`（隧道，先于控件）：先跑用户热键 `_hotkeys.TryHandleLocal`，再处理内置快捷键（`MainWindow.OnPreviewKeyDown`）。因此若用户把 `←/→` 绑成热键，会在波形控件收到之前被消费。实现里在窗口处理器最前面加了例外：焦点在 `WaveformSeekBar` 且按键属于 `{←,→,PageUp,PageDown,Home,End}` 时，跳过热键与内置分支，交给控件处理；其余按键保持现状。

### 10.4 无障碍

- `AutomationProperties.Name="播放进度"`、`HelpText="左右方向键跳转 5 秒"`；
- P1 只保证键盘可用；`IRangeValueProvider`（让屏幕阅读器读写进度）放 P2；
- 波形模式下滑块折叠，屏幕阅读器会失去 RangeValue 语义——这是 P1 的已知缺口，P2 补。

### 10.5 边界情况

| 情况 | 处理 |
| --- | --- |
| 无当前歌曲 | 控件禁用（`IsEnabled` 绑定 `HasCurrentTrack`），点击无效果 |
| 时长为 0 | 不显示波形；`CommitSeek` 内部已 clamp |
| 拖动越界 | `x` clamp 到 `[0, W]`，目标时间 clamp 到 `[0, Duration]` |
| 切歌时分析未完成 | 取消旧 CTS；结果回来时比对路径，不一致则丢弃 |
| 快速连续切歌 | 全局单飞 + 取消，最多一个分析任务在跑 |
| 分析失败（损坏/不支持） | 本次运行负缓存；界面回退为轨道底；`Warn` 日志 |
| 文件被移动/改名 | 路径键失效 → 重新分析（可接受） |
| 标签编辑 | mtime 变化 → 重新分析（音频未变，约 1 s 成本；P2 可改为只比 size） |
| 播放到结尾 | 现有 `PlaybackEnded` 逻辑不变，波形随新曲目重载 |

## 11. 性能预算

| 项目 | 预算 | 说明 |
| --- | --- | --- |
| 首次分析（4 分钟 mp3） | ≤ 1.5 s | 解码 ~0.4 s + FFT ~0.5 s + 映射 ~0.1 s；numpy 原型 1.0–1.2 s |
| 首帧可见 | ≤ 200 ms | 第一段（5 s 音频 / 250 列）出图 |
| 分段追加 | ≤ 5 ms / 段 | 250 列几何 + 快照分配 |
| LRU 扫描 | ≤ 20 ms | 1000 个文件枚举，预热后台 |
| 缓存命中 | ≤ 10 ms | 40 KB 顺序读 |
| 几何构建 | ≤ 5 ms | 900 列 × 3 层，仅在尺寸/曲目变化时 |
| 每 tick 重绘 | ≤ 1 ms | 5 次 draw call |
| 内存 | ≤ 100 KB/曲 | 4 个 byte[] + 几何 |
| 启动影响 | 0 | 无启动路径改动 |
| 切歌影响 | 0（UI） | 分析在后台，播放不等它 |

日志里记录分析耗时；若超过 3 s，`Warn` 一次，便于发现慢盘/网络路径。

## 12. 测试计划

### 12.1 单元测试（xUnit v3）

| 目标 | 用例 |
| --- | --- |
| `WaveformMath` | dB 分位拉伸：最小显示动态范围 30 dB 生效（窄动态向下扩展下界、宽动态窗口不变、`lo` 钳制 `SilenceDb`）；常数信号不除零；静音列高度 0 |
| 份额 | `low+mid+high == 255` 恒成立；三频段 RMS 相等时按幂次得约 103/84/68（低频更宽，不是 1/3）；单频段为 0 时不出现负值；静音列为 85/85/85 |
| 包络 | attack 上升快于 release 下降；阶跃输入的稳态误差 < 1% |
| 聚合 | p80 正确；单列段；空段取最近列；加权份额归一化 |
| 缓存 | 往返一致；截断文件 → null；magic 错误 → null；`FormatVersion` 不符 → null；analyzer version 落后（v1 缓存）→ 拒绝并删除、随后重算并按当前版本写回；stamp 不符 → null；写入后无 `.tmp` 残留 |
| 分析器 | 生成 100 Hz / 1 kHz / 8 kHz 正弦 WAV，对应频段份额 > 0.9；幅度加倍 → 高度单调增；静音 → 全 0 |
| 分块一致性 | 同一文件分块分析与整曲分析，归一化后逐字节相同；渐进快照的窗口恒为 −48…0 dB 且已发布列高度逐列不变（`Analyze_ProgressSnapshots_KeepFixedWindowUntilComplete`）；完成时只做一次整曲归一化 |
| 进度 | `ValidCount` 单调不减且 ≤ `ColumnCount`；最终 `IsComplete`；取消后不再回调 |
| LRU | 超限删到 90%；读取刷新时间戳；跳过当前曲目；空目录/并发写不抛 |
| 清理 | 封面 / 波形 / 全部三个入口的字节统计与删除范围正确 |
| Seek 数学 | `x/W` 比例、clamp、0 与 Duration 边界 |

测试用的 WAV 在测试内用 30 行 RIFF 写入器生成到临时目录，不提交二进制素材。

### 12.2 手动验证

- 三首真实曲目（mp3 / flac / 高码率），对照参考图看轮廓与配色；
- 深/浅主题、100% / 150% / 200% DPI、窗口从 900 拉到 1600 px；
- 点击/拖动/键盘/空格/切歌/停止/播放到结尾；
- 分析中切歌、分析中关闭窗口、缓存目录为网络盘；
- 损坏文件、0 字节文件、超短（<2 s）文件；
- 连续快速切 20 首，观察 CPU 与日志。

### 12.3 性能观察

- 日志 `Waveform analyzed ... ms=`；
- 首次播放的“可听见开始”不晚于当前基线（不因分析延后）；
- 拖动时 UI 不掉帧（目测 + `--screenshot` 辅助）。

## 13. 分阶段实施

| 阶段 | 内容 | 交付 |
| --- | --- | --- |
| P0 | Core DTO/接口、分段分析器、缓存、LRU、单元测试 | 已完成（`dotnet build` + 测试全绿） |
| P1 | 控件 + 渐进渲染 + 主题令牌 + VM + 切换按钮 + 手势 + 诊断开关 | 已完成（默认滑块、记忆上次形态） |
| P2 | 悬停时间气泡、播放头平滑、清理菜单的占用统计、无障碍 RangeValue、seek 优先分析/续算 | 未做 |
| P3 | （可选）列表行迷你波形、按需预分析下一首 | 未做（视反馈） |

粗略工作量：P0 ≈ 1.5 天，P1 ≈ 2 天，P2 ≈ 1 天（不含评审与截图验收）。

**交付状态（2026-11-14）**：P0、P1 已完成（构建 0 警告 0 错误；测试 311 项全绿，其中 ADR-015 相关新增 92 项）；P2 / P3 未做。

## 14. 风险与备选

| 风险 | 影响 | 缓解 |
| --- | --- | --- |
| Media Foundation 在后台线程初始化 | flac/m4a 分析失败 | 复用播放器的 `Warmup()`；失败时 `Warn` 并回退；必要时在专用线程显式 `MediaFoundationApi.Startup()` |
| 网络盘/慢盘解码 | 分析 > 3 s | 单飞 + 取消；不阻塞播放；日志告警 |
| 逐段份额调参主观 | 某类曲风蓝色过厚/过薄 | 常量集中在 `WaveformMath`；用三首基准曲目回归 |
| 浅色主题下波形与轨道对比不足 | 可读性差 | 浅色专用沙色/琥珀色/蓝 + 轨道底色；已完成深浅主题截图对比 |
| 缓存增长 | 占磁盘 | 40 KB/曲；LRU 上限可配置（默认 256 MB）；设置页可清空 |
| 切换按钮挤压波形 | 窄窗口下波形变短 | 30 px 仅图标按钮，无文字；窄窗口下可接受 |
| 200 ms 播放头跳动 | 短曲目明显 | P2 降定时器到 100 ms 或插值 |
| 分析器版本升级 | 旧缓存全废 | 版本号在头部；升级时自然重算，不需要迁移脚本 |
| 用户热键占用 ←/→ | 波形控件收不到导航键 | 窗口 `PreviewKeyDown` 对波形焦点做按键白名单例外（§10.3） |

备选方案（已评估，不采用）：

| 备选 | 不采用的原因 |
| --- | --- |
| 扫描时预分析 | 违反扫描性能红线（AGENTS.md §6），1000 首会多出十几分钟 |
| 存进 SQLite | 需要 schema 迁移，与 ADR-014 的修复迁移冲突；缓存应可整目录删除 |
| 复用 rekordbox 的 ANLZ | 只有 rekordbox 分析过的曲目才有；格式专利/兼容风险 |
| 透明叠加三色 | 颜色浑浊，参考图是实色堆叠 |
| 镜像波形 | 高度有限时浪费一半垂直分辨率，且与“能量高度”目标不符 |
| 全彩频谱/瀑布 | 矮高度下不可读，且计算量更大 |
| 用 `MediaElement`/`SoundPlayer` 取样本 | 无法拿到 PCM，不能做频段分析 |

## 15. 决策与遗留问题

### 15.1 已确认（2026-11-14，owner）

1. **默认形态**：全局记忆——切到波形后一直是波形，下次启动沿用；首次默认滑块。
2. **缓存上限**：加 LRU，大小在设置页可调（128 / 256 / 512 / 1024 MB / 不限，默认 256）。
3. **清理入口**：`清空缓存 ▾` 弹菜单，分封面 / 波形 / 全部三项，二次确认。
4. **诊断开关**：加 `--waveform <file> <png>`，与 `--screenshot` 同一风格。
5. **渲染方式**：按时间顺序分段渲染（§6.10），已分析部分即时可见。
6. **切换按钮形态**（实施中追加）：进度区左端 30 px、仅图标，观感与顺序/随机按钮一致；只靠图标区分形态，不做“已启用”底色（§5.5）。
7. **波形高度**（实施中追加）：填满传输条可用高度（约 63 px，最小 28 px），不固定 28 px（§5.5）。
8. **归一化策略**（实施中追加）：渐进期间锁死 −48…0 dB（上限 0 dBFS），完成时做一次整曲 p5 / p99.5 归一化；替代原“最多两次窗口切换”方案（§6.10）。
9. **已播放区不透明度**（2026-11-16，并入原 ADR-015-A1）：未播放区 100%，已播放区整体 26% 不透明度；删除覆盖色令牌 `WaveformPlayedOverlayBrush`（§5.3 / §9.5）。
10. **高度窗口下界**（2026-11-16，并入原 ADR-015-A2）：最小**显示**动态范围 30 dB，只向下放宽 `lo`、不抬高 `hi`；`AnalyzerVersion` 升至 2、旧 `.wfm` 自动重算（§6.6 / §7.3）。

### 15.2 遗留问题（不阻塞验收）

1. **列表行迷你波形**：仍不做（原 P3；缓存格式已可复用）。
2. **P2 打磨项**：悬停时间气泡、播放头平滑（200 ms → 100 ms 或线性插值）、清理菜单的占用统计、无障碍 `IRangeValueProvider`、seek 优先分析/续算。
3. **分段长度**：已按推荐值实现为 5 s 音频（250 列），调整只需改 `WaveformMath.PublishColumns`。

（原 §15.2 的“切换按钮位置 / 配色 / 归一化策略”已随实现确定，见 §15.1 第 6–8 条与 §5.5。）

## 16. 参考资料

- Serato — Main Waveform Display：<https://support.serato.com/hc/en-us/articles/224969307-Main-Waveform-Display>
- Serato — Track Overview Display（整曲概览，按频谱着色、点击跳转）：<https://support.serato.com/hc/en-us/articles/224969227-Track-Overview-Display>
- baken-export 4.0.2 `waveform.rs`（rekordbox ANLZ 生成算法，含 1070 首对比结论）：<https://docs.rs/baken-export/4.0.2/src/baken_export/anlz/generate/waveform.rs.html>
- AlphaTheta 支持 — 3Band 波形与硬件显示：<https://support.alphatheta.com/en-us/articles/8113178546201>
- AlphaTheta 支持 — rekordbox 6 需要 “Add New Analysis Data”：<https://support.alphatheta.com/en-us/articles/8113210113177>
- Traktor Pro 2 官方手册（Color Mode，四种配色）：<https://www.native-instruments.com/fileadmin/ni_media/downloads/manuals/TRAKTOR_PRO_2_9_Manual_Englisch_2015_08.pdf>
- Resident Advisor — Traktor Pro 2 评测（彩色波形区分低频/高频）：<https://ra.co/reviews/8962>
- Mixxx 波形渲染源码目录：<https://github.com/mixxxdj/mixxx/tree/main/src/waveform>
- Mixxx bug 1268299（高频段被不成比例放大）：<https://bugs.launchpad.net/mixxx/+bug/1268299>
- BBC audiowaveform issue #210（扫频实测 rekordbox 分频）：<https://github.com/bbc/audiowaveform/issues/210>
- 仓库内相关文档：`docs/kb/KB-001-naudio-decode-matrix.md`、`docs/adr/ADR-010-metadata-cache-invalidation.md`

## 17. 修订记录

| 日期 | 原文件 | 主要变更 |
| --- | --- | --- |
| 2026-11-14 | ADR-015（本文） | 批准并实施 P0 / P1（`af9ddf2`） |
| 2026-11-14 | ADR-015（本文） | 切换按钮改为 30 px 仅图标、与顺序/随机按钮同一观感；波形高度改为填满传输条（§0、§5.5） |
| 2026-11-14 | ADR-015（本文） | 渐进归一化窗口锁死 −48…0 dB（上限 0 dBFS），完成时一次整曲归一化；删除前缀窗口切换（§0.9、§6.10、§9.7、§12.1） |
| 2026-11-16 | 原 ADR-015-A1（已并入本文） | 已播放区改为按区域 clip + `PushOpacity(0.26)` 直接绘制三色带，未播放区 100%；删除 `WaveformPlayedOverlayBrush` 令牌与 `PlayedOverlayBrush` 依赖属性；三色带配色随 ADR-024 五套主题重定（§5.3 / §5.4 / §9.1 / §9.5 / §15.1） |
| 2026-11-16 | 原 ADR-015-A2（已并入本文） | 高度窗口改为只向下放宽下界：`MinimumStretchDb` 8.0 → 30.0；`AnalyzerVersion` 1 → 2，旧 v1 `.wfm` 读取时失效并重算，EGWV `FormatVersion` 保持 1（§6.6 / §6.8 / §6.10 / §7.3 / §12.1 / §15.1） |

> 原 ADR-015-A1、ADR-015-A2 的正文已并入本文，对应文件已从 `docs/adr/` 移除，编号作为追溯信息保留。

