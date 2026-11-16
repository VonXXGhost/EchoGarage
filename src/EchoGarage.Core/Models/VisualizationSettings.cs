namespace EchoGarage.Core.Models;

/// <summary>右栏「正在播放」面板下方的可视化形态（ADR-023）。第一阶段只有分段频谱图。</summary>
public enum VisualizerKind
{
    /// <summary>关闭：面板下方不显示可视化。</summary>
    None,

    /// <summary>分段频谱图（Spectrum Analyzer）。</summary>
    Spectrum
}

/// <summary>
/// 播放可视化渲染的帧率上限（ADR-023 A1）。数值即节流后的目标 FPS，<see cref="Unlimited"/> 为 0。
/// 上限只是天花板：实际帧率还受显示器刷新率与 WPF 渲染 tick 的取整限制。
/// </summary>
public enum VisualizerFrameRate
{
    /// <summary>不限制：每个 WPF 渲染 tick 都分析并绘制（ADR-023 原有行为，默认）。</summary>
    Unlimited = 0,

    /// <summary>30 FPS。</summary>
    Fps30 = 30,

    /// <summary>60 FPS。</summary>
    Fps60 = 60,

    /// <summary>120 FPS。</summary>
    Fps120 = 120,

    /// <summary>144 FPS。</summary>
    Fps144 = 144,

    /// <summary>180 FPS。</summary>
    Fps180 = 180
}

/// <summary><see cref="VisualizerFrameRate"/> 与绑定层使用的整数帧率上限之间的换算。</summary>
public static class VisualizerFrameRateExtensions
{
    /// <summary>帧率上限值（FPS）；0 表示不限制。未定义的枚举值按不限制处理。</summary>
    public static int ToFps(this VisualizerFrameRate frameRate) =>
        Enum.IsDefined(frameRate) ? (int)frameRate : 0;
}

/// <summary>频段内多个 FFT bin 的聚合方式（ADR-023 §6.4）。</summary>
public enum SpectrumSummation
{
    /// <summary>频段内各 bin 的均方根（默认）。宽带段不会被过多的 bin 抬高底噪。</summary>
    Rms,

    /// <summary>频段内最强 bin。对单音 / 瞬态更灵敏，但宽带段整体偏高。</summary>
    Peak
}

/// <summary>
/// 分段频谱图的持久化参数（ADR-023 §5）。所有数值都由 <see cref="Normalize"/> 钳制到可用范围，
/// 越界的设置文件不会让分析器拒绝工作。
/// </summary>
public sealed class SpectrumSettings
{
    /// <summary>频段（柱）数量。</summary>
    public int BandCount { get; set; } = 32;

    /// <summary>FFT 点数，只允许 1024 / 2048 / 4096 / 8192。</summary>
    public int FftSize { get; set; } = 4096;

    /// <summary>最低频率（Hz）：第一段的左边界。</summary>
    public double MinFrequency { get; set; } = 40;

    /// <summary>最高频率（Hz）：最后一段的右边界。</summary>
    public double MaxFrequency { get; set; } = 18000;

    /// <summary>频段内 bin 的聚合方式。</summary>
    public SpectrumSummation Summation { get; set; } = SpectrumSummation.Rms;

    /// <summary>显示下限（dBFS）：低于它的频段贴地，上限固定 0 dBFS。</summary>
    public double FloorDb { get; set; } = -50;

    /// <summary>
    /// 频率斜率补偿（dB/oct，等价于 foobar2000 的 “FFT Scale”）：正值按倍频程抬高高频。
    /// 音乐与粉噪的能量密度随频率约按 −3 dB/oct 下降，所以 +3 能让画面看起来平坦。
    /// </summary>
    public double SlopeDbPerOctave { get; set; } = 3;

    /// <summary>柱体回落速度（dB/s）：越小柱体越“黏”。</summary>
    public double ReleaseDbPerSecond { get; set; } = 45;

    /// <summary>是否显示峰值保持线。</summary>
    public bool ShowPeaks { get; set; } = true;

    /// <summary>峰值保持时间（毫秒）。</summary>
    public double PeakHoldMs { get; set; } = 800;

    /// <summary>峰值保持结束后的下落速度（dB/s）。</summary>
    public double PeakFallDbPerSecond { get; set; } = 30;

    /// <summary>是否绘制 dB 网格线与刻度。</summary>
    public bool ShowGraticule { get; set; } = true;

    /// <summary>是否在底部绘制频率标签。</summary>
    public bool ShowFrequencyLabels { get; set; } = true;

    /// <summary>支持的 FFT 点数。</summary>
    public static IReadOnlyList<int> SupportedFftSizes { get; } = [1024, 2048, 4096, 8192];

    public SpectrumSettings Clone() => (SpectrumSettings)MemberwiseClone();

    public static bool IsSupportedFftSize(int fftSize) => SupportedFftSizes.Contains(fftSize);

    /// <summary>钳制到可用范围；非法枚举 / FFT 点数回退为默认值。就地修改并返回同一个实例。</summary>
    public static SpectrumSettings Normalize(SpectrumSettings? settings)
    {
        var result = settings ?? new SpectrumSettings();
        result.BandCount = Math.Clamp(result.BandCount, 8, 96);
        if (!IsSupportedFftSize(result.FftSize))
        {
            result.FftSize = 4096;
        }

        result.MinFrequency = Math.Clamp(result.MinFrequency, 20, 200);
        result.MaxFrequency = Math.Clamp(result.MaxFrequency, 500, 24000);
        if (result.MaxFrequency < result.MinFrequency * 4)
        {
            result.MaxFrequency = Math.Min(24000, result.MinFrequency * 4);
        }

        if (!Enum.IsDefined(result.Summation))
        {
            result.Summation = SpectrumSummation.Rms;
        }

        result.FloorDb = Math.Clamp(result.FloorDb, -100, -20);
        result.SlopeDbPerOctave = Math.Clamp(result.SlopeDbPerOctave, -12, 12);
        result.ReleaseDbPerSecond = Math.Clamp(result.ReleaseDbPerSecond, 3, 120);
        result.PeakHoldMs = Math.Clamp(result.PeakHoldMs, 0, 3000);
        result.PeakFallDbPerSecond = Math.Clamp(result.PeakFallDbPerSecond, 3, 240);
        return result;
    }
}

/// <summary>播放可视化设置（ADR-023），挂在 <see cref="AppSettings.Visualization"/> 下。</summary>
public sealed class VisualizationSettings
{
    /// <summary>当前可视化形态；<see cref="VisualizerKind.None"/> 时面板下方留空。</summary>
    public VisualizerKind Kind { get; set; } = VisualizerKind.Spectrum;

    /// <summary>
    /// 渲染帧率上限（ADR-023 A1）。默认不限制，保持加入该选项前的行为；
    /// 挂在可视化级别而不是 <see cref="SpectrumSettings"/> 上，因为限制的是渲染循环而不是频谱分析参数，
    /// 后续其它可视化形态可以复用同一个上限。
    /// </summary>
    public VisualizerFrameRate FrameRateLimit { get; set; } = VisualizerFrameRate.Unlimited;

    /// <summary>分段频谱图参数（切换形态时保留，切回来不丢）。</summary>
    public SpectrumSettings Spectrum { get; set; } = new();

    public VisualizationSettings Clone() => new()
    {
        Kind = Kind,
        FrameRateLimit = FrameRateLimit,
        Spectrum = Spectrum.Clone()
    };

    /// <summary>校验并规范化；未知形态回退为频谱图，未知帧率上限回退为不限制，频谱参数交给 <see cref="SpectrumSettings.Normalize"/>。</summary>
    public static VisualizationSettings Normalize(VisualizationSettings? settings)
    {
        var result = settings ?? new VisualizationSettings();
        if (!Enum.IsDefined(result.Kind))
        {
            result.Kind = VisualizerKind.Spectrum;
        }

        if (!Enum.IsDefined(result.FrameRateLimit))
        {
            result.FrameRateLimit = VisualizerFrameRate.Unlimited;
        }

        result.Spectrum = SpectrumSettings.Normalize(result.Spectrum);
        return result;
    }
}
