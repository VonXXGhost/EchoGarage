# ADR-028：响度计算与响度调整（LUFS / LRA / True Peak）

- 状态：Accepted（owner 批准：2026-11-19）
- 日期：2026-11-18
- 相关：ADR-017（音频转换流水线，本 ADR 修订其 `ConversionRequest`）、ADR-024（五主题与主题令牌）、ADR-025（标签编辑辅助：规范化规则与候选值，含原 ADR-027 的候选值决策与右键批量编辑模式）、KB-019（响度测量算法与验证笔记）

## 1. 背景与问题

本轮 owner 提出三项需求：

1. **深色主题下播放列表歌曲名不可读**：分组视图里歌曲标题继承到 `ListViewItem` 默认前景色（系统黑色），深色主题下几乎看不见。
2. **响度计算**：歌曲右键菜单新增「响度计算」，弹出独立窗口（不阻塞主界面）并开始分析选中歌曲的 LUFS 状况；支持多文件选中、计算可取消；不做结果缓存、不写文件 tag、不写数据库。实现前调研同类软件（如 foobar2000 的 True Peak Scanner），采用最主流的 LUFS 算法。
3. **响度调整**：右键菜单新增「响度调整」，仅支持单文件；打开后自动测量 LUFS；降低或提升到目标 LUFS；提升时必须自动算出「无失真前提下的最大可增幅」并自动填入；结果保存为新文件、绝不修改原文件；输出复用「转换」路径，用户可选格式与新文件名，默认 FLAC。实现前调研 Audacity / DAW 的做法。

约束：

- 只做**线性增益**，不做限幅/压缩（不做有失真处理）；目标是「目标响度不可达时，退到无失真可达到的最大值并明确提示」。
- 不新增第三方 NuGet 依赖；编解码继续走现有 NAudio + MediaFoundation + 外部编码器（flac/lame/nero）。
- 不缓存扫描结果：每次「响度计算」都重新解码分析，窗口关闭即丢弃；**不写任何 tag / 数据库**。
- 所有 UI 颜色必须使用主题令牌；新窗口走 `ThemedWindow` 模式。
- 分层约束不变：`App → Infrastructure → Core`，跨层接口放 `Core/Abstractions`，`AppHost` 唯一装配点。

## 2. 调研结论

### 2.1 算法标准（详见 KB-019）

采用 **ITU-R BS.1770-5（2023-11）+ EBU R 128 / Tech 3341 / Tech 3342**，这是当前流媒体、广播与主流工具（foobar2000 True Peak Scanner、ffmpeg、libebur128、Audacity、REAPER、Audition）的共同基础：

- **K 加权**：48 kHz 官方两组 biquad 系数（BS.1770-5 Table 1/2）；其它采样率用 libebur128/ffmpeg 的解析设计公式现场生成（代入 48 kHz 与官方表逐位一致）。每声道独立、状态跨块连续、换文件重置。
- **积分响度 I**：400 ms 块、100 ms 步进（75% 重叠），`L = -0.691 + 10·log10(Σ G_i z_i)`；绝对门限 -70 LUFS，相对门限「绝对门限后响度 -10 LU」；能量域聚合；末尾不完整块丢弃。环绕声道能量权重 1.41，LFE 不计入。
- **LRA**：3 s 短时响度窗、每 100 ms 取一个 S 值（EBU Tech 3342 2023 要求相邻窗重叠 ≥2.9 s；libebur128/ffmpeg 历史实现为 1 s，实测差异 <0.2 LU，见 KB-019 §5）；绝对门限 -70 LUFS、相对门限 -20 LU；P10/P95 最近秩（不插值），`LRA = P95 - P10`。
- **True Peak**：BS.1770-5 Annex 2 的 4× 过采样 48-tap（4 相 × 12）官方表；浮点实现不需要 ±12.04 dB 衰减/补偿；每声道取绝对峰值，多声道取最大，**LFE 参与**。
- **M / S**：Momentary = 400 ms 矩形窗、Short-term = 3 s 矩形窗、均不门限；本 ADR v1 只计算 I / LRA / TP / sample peak，M/S 最大值留作后续可选列。

原型已用 Python + numpy/scipy 在真实文件上验证：I 与 ffmpeg 差 0.02 LU，TP 差 0.01 dB（详见 KB-019 §7）；ffmpeg 的 EBU 3341/3342 合规测试信号也全部复现。

### 2.2 同类工具做法

| 工具 | 做法 | 对本项目的意义 |
| --- | --- | --- |
| foobar2000 True Peak Scanner（foo_truepeak） | 多选扫描结果表（Track Peak / Gain / LRA / 可选 LUFS-I/M/S、DR、clip 数、峰值时间戳）；可中止且**中止后保留已完成结果**；扫描失败行内标注错误；底部显示耗时与实时倍速；结果可写 ReplayGain tag | 扫描窗口的交互基线；本项目按其表格 + 可取消 + 保留结果，但**不写 tag、不缓存** |
| Audacity Loudness Normalization | 默认目标 -23 LUFS；**只乘常数增益，没有峰值上限/削波检查**（官方论坛明确说明）；仅降位深时 dither | 证明「只做线性增益」是可行主流做法；其缺失的「最大安全增益」正是本项目的差异点 |
| Adobe Audition Match Loudness | Maximum True Peak Limiting，用限幅达标（有失真） | 本项目明确不采用限幅路线 |
| REAPER Render Normalization | 线性归一化 + 可选 Brickwall limit；**对 MP3/OGG 等有损输出按编码后的信号重算统计** | 有损输出要留过冲余量 / 需要复测 |
| ffmpeg `loudnorm` | `linear=true` 时只做常数缩放，增益被 `TP` 上限截断、不做限幅；动态模式才走 limiter | 与本项目口径一致：增益 = min(目标增益, 峰值上限 - 实测 TP)；但 ffmpeg 不把「不可达」明确告诉用户 |
| ReplayGain 2.0（loudgain 等） | 参考 -18 LUFS，peak 用 True Peak；播放器防削波 = 把增益限制到 `ceiling/peak` | 印证「无失真最大增益」= `ceiling - TP`；foobar 作者亦指出 TP 值依赖重采样器，属估算 |
| Spotify / Apple Podcasts / EBU | -14 LUFS / -16 LKFS / -23 LUFS；生产真峰上限分别 -1 / -1 / -1 dBTP；有损转码建议再留 1 dB（-2 dBTP） | 目标值预设与天花板策略的依据 |

未发现任何主流工具提供「自动填入最大无失真增益」的按钮；本项目的做法是「目标 + 峰值上限 + 自动截断并提示」，具有区分度且符合直觉。

### 2.3 结论

- 算法：ITU-R BS.1770-5 / EBU R 128 / Tech 3342，自研纯 C# 流式实现（不 P/Invoke libebur128、不调用 ffmpeg 子进程），理由：无新依赖、跨格式统一、结果可单测、与播放器现有 NAudio 解码链一致。
- 响度调整：**线性增益 + 真峰值天花板**，不做限幅；目标不可达时截断增益并提示；输出复用 ADR-017 转换流水线，新增可选的 `GainDb` 参数。

## 3. 决策

### 3.1 深色主题歌曲名修复（缺陷修复）

- 只改 `src/EchoGarage.App/Views/MainWindow.xaml` 的 `Cell.Title` DataTemplate：
  在 Title 文本的 `TextBlock.Style` 内增加默认 Setter
  `<Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}" />`；
  保留原有 `IsPlaying` DataTrigger（触发器优先级高于 Setter，播放高亮仍生效）。
- 不修改 `GroupedTrackItem` 的字体/颜色继承链，不新增主题令牌，不改主题字典。
- 用 MidnightVinyl、SlateStudio 两套深色主题的分组视图截图验证；同时确认 DataGrid 视图、播放中标题、其它主题无回归。

### 3.2 Core：响度测量模型、算法与接口

新增文件（Core 不引用 NAudio/TagLib）：

```csharp
// src/EchoGarage.Core/Models/Loudness.cs
public sealed record LoudnessMeasurement(
    double? IntegratedLufs,      // 无可测块（<400ms/全静音）时为 null
    double? LoudnessRange,       // <3s 或 datas 不足时为 null
    double TruePeakDb,
    double SamplePeakDb,
    double? MaxMomentaryLufs,
    double? MaxShortTermLufs,
    int SampleRate,
    int Channels,
    TimeSpan Duration,
    string? Note);               // 例如「静音」「时长为 0」等提示

public sealed record LoudnessAnalysisProgress(
    double Fraction,             // 0..1，无法获知总时长时为 -1
    TimeSpan Processed,
    TimeSpan? Duration,
    string Stage);               // 「解码分析」

public sealed record LoudnessAdjustmentPlan(
    double RequestedGainDb,      // target - measured
    double AppliedGainDb,        // min(target, maxSafe)
    double MaxSafeGainDb,        // ceiling - measured TP
    double ResultingLufs,        // measured + applied
    double ResultingTruePeakDb,  // measured TP + applied
    bool TargetReachable);

// src/EchoGarage.Core/Abstractions/ILoudnessAnalyzer.cs
public interface ILoudnessAnalyzer
{
    Task<LoudnessMeasurement> AnalyzeAsync(
        string path,
        IProgress<LoudnessAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

// src/EchoGarage.Core/Services/LoudnessMeter.cs（流式、纯 C#）
public sealed class LoudnessMeter
{
    public LoudnessMeter(int sampleRate, IReadOnlyList<double> channelWeights, bool enableTruePeak = true);
    public void Process(ReadOnlySpan<float> interleavedSamples, int frameCount);
    public LoudnessMeasurement Complete(TimeSpan duration);
}

// src/EchoGarage.Core/Services/LoudnessGain.cs（纯函数，易单测）
public static class LoudnessGain
{
    public const double LosslessCeilingDbTp = -1.0;
    public const double LossyCeilingDbTp = -2.0;
    public static double CeilingDbTp(bool lossyFormat);
    public static double MaxSafeGainDb(double measuredTruePeakDb, double ceilingDbTp);
    public static LoudnessAdjustmentPlan Plan(
        double measuredLufs, double measuredTruePeakDb, double targetLufs, double ceilingDbTp);
}
```

算法实现要点（完整公式/系数/验证见 KB-019）：

- K 加权：每声道两级 biquad（pre-filter + RLB），双精度、Direct Form I；48 kHz 用官方系数，其它采样率用设计公式；换文件重置状态。
- 积分响度：`hop=(fs+5)/10`、`block=4*hop`；每个 hop 累加每声道平方和；400 ms 块内每声道 `z_ij = Σ hop 平方和 / block`；存 `z_ij` 以便两遍门限；`l_j = -0.691 + 10log10(Σ G_i z_ij)`。
- 通道权重：由上层传入（Infrastructure 按 `WaveFormat`/`ChannelMask` 解析），L/R/C=1.0、Ls/Rs/Back/Side=1.41、LFE=0；单声道默认 1.0（不启用 dual-mono）。
- LRA：3 s 窗（30 个 hop 滚动和），每 100 ms 一个 S（3342 最低重叠要求；libebur128/ffmpeg 的 1 s 历史实现差异 <0.2 LU）；绝对门限 -70、相对门限 `mean(10^(S/10))` 的 -20 LU；P10/P95 最近秩；n 太小（<3 s）返回 null。
- True Peak：每声道保留最近 12 个输入样本；对 4 相各做 12 抽头 FIR，取 `max(|y|)`；与 sample peak 取最大；多声道取最大；LFE 参与。固定 4×（44.1/48/88.2/96/192 kHz 统一），不因采样率分支。
- 边界：<400 ms 无 I；全静音返回 `IntegratedLufs = null` + note；末尾不完整块丢弃；文件开头/结尾不补静音（与 ffmpeg 对齐）。
- 性能：单文件顺序流式处理，无整曲 PCM 缓存；I 只需每块每声道能量（10 块/s），LRA 只存 S 序列；TP 为约 48 MAC/样本/声道；估算 >60× 实时（详见 KB-019 §6）。

### 3.3 Infrastructure：文件级分析器

新增 `src/EchoGarage.Infrastructure/Audio/LoudnessAnalyzer.cs`：

- 依赖 `MediaDecoder`（现有，`internal`）解码为 float32；`AnalyzeAsync` 内部 `await Task.Run(...)`，避免阻塞 UI；每读一块检查取消。
- 扩展 `MediaDecoder.DecodedAudio`：增加 `TimeSpan? Duration`（WAV 取 `WaveFileReader.TotalTime`，MediaFoundation 取 `MediaFoundationReader.TotalTime`）。这是对 ADR-017 内部实现的兼容扩展，不改变公开接口。
- 解析声道布局：
  - `WaveFormatExtensible.ChannelMask` 可用时按声道标志映射（L/R/C=1.0，LFE=0，Side/Back/Surround=1.41，其余=1.0）；
  - 无 mask 时按声道数回退：1→[1]，2→[1,1]，4→[1,1,1.41,1.41]，5→[1,1,1,1.41,1.41]，6→[1,1,1,0,1.41,1.41]，8→[1,1,1,0,1.41,1.41,1.41,1.41]；其它声道数全部 1.0 并写 `[WARN]`。
- 读取失败、文件不存在、解码异常：抛 `LoudnessAnalysisException`（FriendlyMessage），由 UI 逐行显示错误，不中断整批扫描。
- 不写任何文件/DB；不缓存。

### 3.4 App：「响度计算」窗口

- 右键菜单（`MainWindow.xaml` 的 `TrackMenu`）在「转换」之后插入顶层项：
  `「响度计算…」`、`「响度调整…」`，各带 Click 处理；`OnTrackMenuOpening` 里按选中行数设置 enabled：
  - 响度计算：`_vm.SelectedRows.Count > 0`；
  - 响度调整：`_vm.SelectedRows.Count == 1`（多选时禁用；语义上也可用单行右键）。
- 选中行顺序按 `_vm.Tracks.Where(_vm.SelectedRows.Contains)`，同一路径去重（避免重复分析）。
- 新窗口 `LoudnessScanWindow`（`ThemedWindow`，约 980×600，可调整大小，`ShowInTaskbar=False`，`Owner=MainWindow`，**非模态**）：
  - 打开即自动开始分析；行模型 `LoudnessScanRowViewModel`（`ObservableObject`）：文件名、路径、时长、格式（Codec/SampleRate/Channels）、积分响度、LRA、真峰值、采样峰值、状态/错误。
  - 表格只读、数值右对齐、单位写列头（LUFS / LU / dBTP / dBFS）、可排序；状态列显示 等待中 / 分析中（n/N）/ 完成 / 失败:原因 / 已取消。
  - 底部：总进度条 + 「正在分析 3/10：Demo Track 01.flac」+ 取消按钮；结束后显示「完成 N 个，失败 M 个，用时 mm:ss，平均 x 倍实时」；「取消」后保留已完成行并显示「已取消（完成 N/M）」；可「重新计算」和「关闭」。
  - 分析期间窗口关闭 = 取消（`CancellationTokenSource`）；再次点击菜单若窗口已存在则激活现有窗口，不重复打开。
  - **不写 tag、不写库、不缓存**；窗口关闭后结果丢弃。
- 扫描窗口不新增设置项。

### 3.5 App：「响度调整」窗口

- 单文件、模态（`ShowDialog`，`Owner=MainWindow`；`ThemedWindow`，约 640×600，`SizeToContent=Height`，可调整高度）。
- 打开时自动分析源文件（显示进度与取消）；分析完成后显示：
  - 源文件信息（文件名/路径、时长、格式、采样率、声道）；
  - 测量结果：积分响度、真峰值、采样峰值、LRA（不可测显示「—」）；
  - 目标响度：**仅一个可编辑数值框**（范围 -60.0 … 0.0 LUFS，步进 0.1），默认 **-10.0 LUFS**（可在设置页「响度调整 → 默认目标响度」配置，见 §3.7）；不提供任何预设下拉（owner 2026-11-19 指定）；
  - 调整方向：「降低」/「提升」两个 RadioButton；
  - 「无失真上限」说明：当前真峰值 X.X dBTP，本格式峰值上限 -1.0 dBTP（无损）或 -2.0 dBTP（MP3/AAC），最多可提升 +Y.Y dB / 可达到 Z.Z LUFS；不可提升时显示原因；
  - 「实际增益」只读显示（如 `+3.2 dB`）与「预计输出：X.X LUFS / Y.Y dBTP」；
  - 输出：格式 ComboBox（**FLAC（默认）** / WAV / MP3 / AAC），输出路径 TextBox + 「浏览…」（复用 `IDialogService.PickSaveFile`，filter 随格式变化）；默认路径 = 源目录 + `源文件名 (响度调整)<扩展名>`；格式切换时若用户未手改路径则同步扩展名；
  - 底部：开始调整 / 取消 / 关闭。
- 行为：
  - 切到「提升」时，目标响度自动填为「当前可达上限」`measuredLufs + MaxSafeGainDb`（保留 1 位小数）；用户可再往下改；若手动输入超过上限，失焦时钳制到上限并提示。这样满足「选择增幅时自动填写最大可增幅」的要求。
  - 切到「降低」时，目标保持当前输入（默认取设置项，缺省 -10.0；**若当前响度已低于默认目标，则自动填当前实测值**，避免「降低」模式因目标高于实测值而实际产生提升；同时提示「当前已低于目标，无需降低」）。这一点在实现中按「降低模式绝不产生正增益」的语义落地。
  - 增益按 `LoudnessGain.Plan` 计算；只有**提升**受峰值天花板限制（`AppliedGainDb = min(target_gain, MaxSafeGainDb)`）；**降低**直接取目标增益（≤0 dB），不会因天花板额外多降。提升目标不可达时黄色提示「目标 -10.0 LUFS 不可达，已按 -1.0 dBTP 上限给到 +2.3 dB」。
  - 输出格式为 MP3/AAC 且源采样率 > 48 kHz 时，提示并自动按 `ConversionResamplePolicy.WithDownsample` 降采样到 48 kHz（复用现有策略，不再弹二次确认）。
  - 输出路径校验：非空、目录可创建、扩展名与格式一致、**绝不等于源文件路径**（大小写/相对路径归一后比较）；已存在时用 `IDialogService.Confirm` 确认覆盖。
  - 开始调整：构造 `ConversionPreset`（FLAC：Quality 5、BitDepth Source；WAV：BitDepth Source；MP3：Cbr 320、Pcm16；AAC：Vbr Quality 100、Pcm16；CopyTags/CopyCover=true），通过 `AudioConverter.ConvertAsync(new ConversionRequest(source, output, preset, encoders, gainDb, enableDither: true), progress, token)` 执行（`EnableDither` 仅 16-bit 无损输出实际生效）；窗口内嵌进度条/阶段文字与取消按钮；后台 `Task.Run` 避免 UI 冻结。
  - 完成后：显示成功与输出路径，提供「在资源管理器中显示」；**有损格式**再用 `ILoudnessAnalyzer` 对输出文件复测一次 I/TP，显示「输出实测」，若 TP > -1.0 dBTP 给出警告（编码过冲），提示降低目标；无损格式跳过复测（线性增益下 TP 严格同比例）。
  - 失败/取消：清理不完整输出（`AudioConverter` 已负责），状态提示；取消后保留窗口可重试。
- 始终不修改源文件；不写源文件 tag；输出文件的标签/封面从源复制（复用现有 `CopyTags`）。

### 3.6 修订 ADR-017：转换管线支持可选增益

- `ConversionRequest` 末尾增加可选参数：

  ```csharp
  public sealed record ConversionRequest(
      string SourcePath,
      string OutputPath,
      ConversionPreset Preset,
      ConversionEncoderPaths Encoders,
      double GainDb = 0,
      bool EnableDither = false);
  ```

  现有调用方（含测试）不传后两个参数即可编译，行为不变；`EnableDither` 只有「响度调整」路径显式传 `true`，普通「转换」菜单保持 `false`，因此既有转换输出逐位不变（见 §3.6.1）。
- `AudioConverter.ConvertAsync` 在 `MediaDecoder.Open` 之后、重采样/写中间 WAV 之前插入 `GainSampleProvider`（新增 `Infrastructure/Conversion/GainSampleProvider.cs`，`ISampleProvider` 包装，`factor = 10^(GainDb/20)`，读时乘、不裁剪；GainDb=0 时不包装）。
- `ConversionProgress` / `ConversionStage` 枚举不变；响度调整的进度窗口复用现有阶段文案（准备中/解码中/重采样中/编码中/写入标签/完成）。
- 峰值安全由调用方（`LoudnessGain`）保证，`AudioConverter` 不做隐式限幅。
- 不改 `ConversionPreset` / 编码器命令；位深/采样率/量化噪声策略见 §3.6.1（含已确认的 16-bit TPDF dither）。

#### 3.6.1 输出位深、采样率与「无损」口径

- **FLAC / WAV 输出**：采样率保持源值，**不重采样**（`Resample=false`，不走 SSRC/Wdl）；位深按 `ConversionPreset.BitDepth=Source` 规则：16-bit 源→16-bit，24-bit 源→24-bit，32-bit float 源→24-bit（FLAC 不支持 32f）；声道数保持、不混音、不合并；有 ChannelMask 的多声道布局元数据在 WAV/FLAC 中可能无法完整保留（v1 以立体声/单声道为验收范围，多声道只保证音频内容不混音）。
- **MP3 / AAC 输出**：有损编码，不是无损；源采样率 >48 kHz 时按 `ConversionResamplePolicy.WithDownsample` 降到 48 kHz；位深概念不适用（内部 16-bit）。
- **「无损」口径**：FLAC/WAV 路径不重采样、不引入有损编码、不削波（TP 上限保证），但线性增益是浮点处理后再量化，**不是与源文件逐位相同**；`GainDb=0` 时也不会走纯拷贝（仍会重新编码）。
- **量化噪声与 dither**：16-bit 输出在增益后重新量化，若不加 dither 会引入与信号相关的截断失真（噪声底约 -93 dBFS）；**响度调整的 16-bit 输出启用 TPDF dither（owner 2026-11-18 确认）**：`PcmWavWriter` 增加可选开关，仅目标位深 16-bit 时生效，24-bit / 32f / MP3/AAC 不启用，使降位深路径达到透明。开关由 `ConversionRequest.EnableDither` 控制，**仅响度调整路径传 true**；普通「转换」菜单不启用 dither，保持既有输出逐位不变。
- 测试与 UI 都要显示实际输出位深/采样率（如「输出：FLAC · 24-bit / 96 kHz（与源相同）」），避免用户误以为所有格式都无损。

#### 3.6.2 附带修复：SSRC 重采样前导延迟补偿

- **问题**：既有转换的重采样路径先用 `PcmWavWriter` 写中间 WAV，再调用 `tools/ssrc.exe --profile high`；SSRC 是线性相位 FIR，输出**前部包含滤波器群延迟填充**、尾部包含 flush 尾巴。旧代码只按 `expectedFrames` 裁尾，没有跳过前导填充，导致重采样输出开头多出静音、结尾丢失等长的音频内容（例如 96 kHz→48 kHz 开头多约 16 355 帧 ≈ 341 ms；44.1 kHz→48 kHz 约 10 909 帧 ≈ 248 ms；48 kHz→96 kHz 约 32 711 帧 ≈ 341 ms）。该问题在响度功能之前就存在，只影响发生重采样的转换输出。
- **决策（运行时探测 + 缓存，不写死常数）**：不同源/目标采样率组合与 profile 的延迟不一致，且没有简单的比例公式（实测 44.1k→48k=10 909、96k→48k=16 355、48k→96k=32 711），因此：
  1. `SsrcLatencyProbe` 为每个 `(sourceRate, targetRate, profile)` 生成 1 秒 16-bit 单声道脉冲探测 WAV（脉冲位于第 5 000 帧），用与正式转换完全相同的 SSRC 参数转换；
  2. 在输出中定位峰值帧 `peakFrame`，延迟 `D = peakFrame − 5000 × target / source`；
  3. 用 `PcmWavWriter.TrimToFrames(path, expectedFrames, skipFrames: D)` 跳过前导并保留 `expectedFrames`；
  4. 结果按组合缓存（`ConcurrentDictionary<LatencyKey, Lazy<Task<int>>>`，失败不缓存）；探测临时目录位于转换临时目录下，完成后删除。
- **失败回退**：若探测或裁剪失败（SSRC 版本变化/输出异常/帧数不足），记录 `[WARN]` 并从中间 WAV 用 NAudio `WdlResamplingSampleProvider` 重新生成，warning「SSRC 延迟补偿失败，已回退到内置重采样器。」；若 SSRC 进程本身失败，同样回退，warning「SSRC 重采样失败，已回退到内置重采样器。」。回退路径不改变输出格式、位深与取消语义。
- **测试**：新增集成测试 `Convert_WavToWav_ResamplesWithSsrc_PreservesAudioAtStart`（96 kHz 1 秒 440 Hz → 48 kHz WAV）：断言输出恰为 48 000 帧、前 4 800 帧 RMS 在 0.12–0.22（修复前该区间是填充静音）、与 440 Hz 正弦相关系数 >0.9；既有 `Convert_WavToWav_ResamplesWithSsrc` 时长断言继续通过。
- **影响**：仅影响重采样输出（长度与源一致、首帧对齐），不改变非重采样路径、编码参数与标签复制；同参数下输出相对旧版内容不同（修复了偏移）。

### 3.7 装配与菜单位置

- `AppHost` 增加只读属性 `public ILoudnessAnalyzer Loudness { get; }`，构造函数中 `Loudness = new LoudnessAnalyzer(Logger);`（位置在 `Converter` 之后）。
- `MainWindow` 增加 `private readonly LoudnessCoordinator _loudness;`，在 `_conversion` 之后构造；
  `src/EchoGarage.App/Services/LoudnessCoordinator.cs` 负责：
  - `Task ScanSelectedAsync()`：取选中 tracks、去重、创建/激活 `LoudnessScanWindow`、顺序分析、每文件更新行与总进度、处理取消；
  - `Task AdjustSingleAsync(Track track)`：打开模态调整窗口并驱动分析/转换（窗口 code-behind 也可承担，但分析/转换编排统一放 coordinator，便于测试与复用）。
- `IDialogService` 不新增方法：输出路径用现有 `PickSaveFile`，覆盖确认用现有 `Confirm`。
- 新增一个设置项（owner 2026-11-19 要求）：`AppSettings.LoudnessDefaultTargetLufs`（默认 `-10.0`，范围 -60.0…0.0，步进 0.1，`JsonSettingsService.Normalize` 中做 NaN/越界归一与 1 位小数取整）；设置窗口「文件与转换」页新增「响度调整 → 默认目标响度」数值框，失焦/回车时保存。该值仅作为 `LoudnessAdjustWindow` 打开时的初始目标，不记忆每首歌上次输入；峰值天花板仍写死为常量（-1.0/-2.0 dBTP）。

### 3.8 错误处理与日志

- 新增日志（`IAppLogger`）：
  - `[INFO]` 一次扫描开始/结束（文件数、成功/失败数、用时、平均倍速）；
  - `[WARN]` 单个文件分析失败（路径 + 异常消息）、输出文件复测异常、编码过冲；
  - `[ERROR]` 仅用于 UI 无法继续的错误（窗口创建失败等）。
- UI 可见错误都要 `SetStatus` 或窗口内错误文本 + `Logger.Error`；逐文件失败不弹窗打断整批。
- 取消不记 `ERROR`，按 `[INFO]`/`[DEBUG]` 处理。

### 3.9 性能与取消

- 扫描串行（解码 IO + CPU 混合，串行简单可预测；不做多文件并行，避免磁盘/内存抖动）。
- 分析在后台线程；每 8K–32K 帧检查一次取消并回报进度（进度更新 ≥5 Hz）。
- 不缓存、不预读全曲；内存与文件时长无关（除每块能量表，约 10 块/s/声道）。
- 为避免主界面卡顿：扫描/调整的分析与转换均不在 UI 线程执行；UI 更新经 `Dispatcher`。

## 4. 测试计划

### 4.1 Core 单元测试（新增 `tests/EchoGarage.Tests/LoudnessMeterTests.cs`、`LoudnessGainTests.cs`）

用合成 float 样本直接喂 `LoudnessMeter`（不依赖文件/解码）：

1. K 加权系数：`fs=48000` 代入设计公式 → 与官方表 15 位小数一致；`fs=44100` 生成系数有限且 DC 增益 0 dB。
2. 积分响度：1 kHz 立体声、每声道 -23 dBFS、20 s → `-23.0 ± 0.1 LUFS`；44.1 kHz 同信号同值；仅左声道 → `-26.0 ± 0.1`；100 Hz → `-24.8 ± 0.2`；4 kHz → `-19.7 ± 0.2`（覆盖采样率重设计、通道求和、K 频响）。
3. 门限：Tech 3341 #3（10s@-36 + 60s@-23 + 10s@-36）与 #4（含 -72 段）→ `-23.0 ± 0.1`。
4. 5.1 权重：6 声道信号（L/R -28、C -24、Ls/Rs -30 dBFS、LFE 静音）→ `-23.0 ± 0.2`，并断言 LFE 不参与响度。
5. LRA：20s@-20 + 20s@-30 → `10.0 ± 0.5 LU`；全静音 → null；2 s 文件 → null。
6. True Peak：0.5 幅度 fs/4 正弦（相位 45°）→ `-6.0 +0.2/-0.4 dBTP`；1 kHz 0.5 幅度 → `-6.02 ± 0.1`；负峰值被取绝对值；LFE 参与 TP。
7. 边界：<400 ms → `IntegratedLufs=null`；静音 → null + note；分块处理（一次喂 100 帧 vs 一次喂全部）结果一致。
8. `LoudnessGain.Plan`：TP=-3、ceiling=-1 → max=+2；目标 +6 → applied +2、不可达；目标 +1 → applied +1；衰减目标 -3 → 全量；TP 已高于 ceiling → max ≤ 0 且不可提升。
9. 增益计划与格式上限：MP3/AAC 使用 -2.0 dBTP，FLAC/WAV 使用 -1.0 dBTP。

### 4.2 Infrastructure 集成测试（新增 `LoudnessAnalyzerTests.cs`；转换增益测试加入 `ConversionPipelineTests.cs`）

10. `TestAudio.CreateWav` 生成 WAV → `LoudnessAnalyzer.AnalyzeAsync`；与 ffmpeg 结果对照（测试内可跳过 ffmpeg：断言已知合成信号的期望值；若 `ffmpeg.exe` 可用则额外 Assert±0.2 LU / ±0.3 dBTP）。
11. 44.1/48/96 kHz、16/24-bit、单/双声道 WAV 均可分析；96 kHz 正确用 96k 系数。
12. 缺失文件/损坏文件 → `LoudnessAnalysisException`，消息可读。
13. `AudioConverter` + `ConversionRequest.GainDb=+6`：WAV→WAV 后重新分析，I 提升约 6.0 LU，输出峰值不超过 -1 dBTP；**输出采样率/位深与源一致**（44.1/16→44.1/16，96/24→96/24）；源文件字节/时间戳不变；标签保留。
14. 增益 + FLAC 输出（`flac.exe` 可用时）：输出可解码、I 提升、标签/封面复制；**用 TagLib/NAudio 读回断言采样率与位深保持源值**（24/96 源→24/96；16 源→16）。
15. 取消：分析/转换中途取消 → `OperationCanceledException`/取消结果，无残留输出文件。
16. 现有 `ConversionPipelineTests` 全部保持通过（验证 `ConversionRequest` 可选参数不破坏调用方）；新增 SSRC 前导补偿回归测试 `Convert_WavToWav_ResamplesWithSsrc_PreservesAudioAtStart`（§3.6.2）。
17. 16-bit TPDF dither（已确认启用）：对低电平 1 kHz 信号断言 dither 版无截断谐波（或与无 dither 版的噪声统计对比）；24-bit / 有损路径不经过 dither。

### 4.3 手动 / UI 验证

- 构建 `dotnet build EchoGarage.sln -c Release`（受限环境加 `-nodeReuse:false -p:UseSharedCompilation=false`），跑 xUnit v3 进程内测试。
- 深色主题：UAT 数据副本 + MidnightVinyl / SlateStudio，切换分组视图/DataGrid 视图截图，检查歌名、播放中高亮、其它列。截图放 `.scratch/` 或 `artifacts/`（不提交）。
- 右键菜单：0 选、1 选、多选、分组视图、DataGrid 视图下两项 enabled 状态。
- 响度计算：3 个混合格式文件（FLAC 44.1/16、WAV 96/24、MP3）+ 1 个缺失文件；自动开始、中途取消、完成后「重新计算」；核对不写 tag（文件 mtime 不变）、窗口关闭后无缓存。
- 响度调整：单文件 FLAC（默认输出）；降低目标；提升目标（自动填入最大可达值、不可达提示）；输出路径等于源文件被拒绝；已存在文件确认覆盖；MP3/AAC 输出（有损上限 -2 dBTP + 输出复测警告）；取消后无残留；完成后源文件 mtime/标签不变、输出可播放且标签/封面正确。
- 主题：新窗口在 5 套主题下颜色全部走主题令牌，无硬编码色。

## 5. 备选方案（未采用）

| 方案 | 未采用原因 |
| --- | --- |
| P/Invoke libebur128 或调用 ffmpeg `ebur128` 子进程 | 增加部署依赖/进程管理复杂度；播放器已有 NAudio 解码链；自研纯 C# 可单测且无新依赖 |
| 用 ffmpeg `loudnorm` 做调整 | 需要外部进程与两遍文件处理，且无法复用现有转换流水线的标签/封面/命名逻辑 |
| 限幅/压缩达标（Audition 路线） | 违背「无失真」要求；owner 明确要求最大无失真增幅 |
| 扫描结果缓存到 DB / 写 ReplayGain tag | owner 明确要求不缓存、不写 tag；且解析度依赖算法版本，维护成本高 |
| 全格式 dither / 噪声整形 | 只在 16-bit 输出启用 TPDF（已确认）；24-bit/32f 与 MP3/AAC 不加；不引入噪声整形，避免改变听感 |
| 扫描窗口显示 M/S 最大值、DR、clip 数 | 首版聚焦 I/LRA/TP/sample peak；列可后续扩展（模型已预留 MaxMomentary/MaxShortTerm） |

## 6. 风险与缓解

1. **True Peak 是估算值**：不同过采样器可差 0.1–0.5 dB。固定 ITU 48-tap 4× 表并在 UI 标注「真峰值按 BS.1770-5 4× 过采样估算」；调整上限留 1 dB 余量（无损 -1.0、有损 -2.0）。
2. **MediaFoundation 解码差异**：部分 MP3/AAC 解码后的采样可能与其它播放器不同，进而影响 I/TP。以文件实际解码结果为准，测试覆盖 FLAC/WAV/MP3；文档说明估算性质。
3. **有损编码过冲**：MP3/AAC 解码后 TP 可高于源 0.5–2 dB；用 -2 dBTP 上限 + 输出复测警告；不自动重编码（v1）。
4. **超长文件扫描时间**：TP 计算约 48 MAC/样本/声道；UI 有进度/取消/倍速显示；不做并行以免抢占播放。
5. **输出覆盖/源文件保护**：路径归一比较 + 已存在确认 + 转换层 `File.Move(overwrite)` 仅在确认后发生。
6. **主题回归**：只加一个 Setter，影响面小；5 主题截图验证。
7. **不写 tag/不缓存** 与「跨会话无结果」的取舍：owner 已明确要求；UI 文案说明结果仅本次有效。

## 7. 决策落定（2026-11-19 更新）

> 本节原为“待 owner 决策”；下列各项已在 2026-11-18 / 11-19 的实施与验收中定稿，现记录最终口径
> （原提问已删除，仅保留结论）。

1. **默认目标响度**：-10.0 LUFS，且在设置页可配置（`AppSettings.LoudnessDefaultTargetLufs`，默认 -10.0，范围 -60…0）；
   不提供预设，用户直接输入。目标很响、动态范围大的素材在 -1 dBTP 上限下无法无失真达到时，程序截到最大安全增益并提示可达到值。
2. **真峰值上限**：无损（FLAC/WAV）**-1.0 dBTP**；有损（MP3/AAC）**-2.0 dBTP**
   （`LoudnessGain.LosslessCeilingDbTp` / `LossyCeilingDbTp`）；两者不统一为单值。
3. **扫描窗口指标**：v1 显示 I / LRA / True Peak / Sample Peak 四项；M/S 最大值、DR、clip 数不在 v1 显示。
4. **响度调整窗口**：**模态**（单文件流程）；输出格式 v1 提供 FLAC / WAV / MP3 / AAC 四项。
5. **dither（已启用）**：16-bit 输出加 TPDF，24-bit / 32f / 有损输出不加；实现为 `PcmWavWriter` 可选开关，
   响度调整路径对 16-bit 输出启用。
6. **记忆上次目标/格式**：v1 不记忆每首歌上次选择（默认目标值可在设置页配置），可后续加。

## 8. 实施顺序（已实施）

1. Core：`Loudness.cs`、`ILoudnessAnalyzer`、`LoudnessMeter`、`LoudnessGain` + 单元测试。
2. Infrastructure：`LoudnessAnalyzer`、声道权重解析、`MediaDecoder.Duration` + 集成测试。
3. ADR-017 修订：`ConversionRequest.GainDb`、`GainSampleProvider`、`AudioConverter` 插入 + 转换增益测试。
4. App：`LoudnessCoordinator`、扫描窗口、调整窗口、AppHost 装配、右键菜单/enabled 逻辑。
5. 深色主题 `Cell.Title` 修复 + 5 主题截图验证。
6. SSRC 重采样前导延迟修复（§3.6.2）：`SsrcLatencyProbe`、`TrimToFrames(skip)`、WDL 回退、回归测试。
7. Release 构建 + 全量测试 + 手动验收清单；交付说明中列出手动验证结果与截图位置（不提交二进制）。
