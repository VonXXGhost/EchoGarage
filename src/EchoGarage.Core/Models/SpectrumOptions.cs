using EchoGarage.Core.Services;

namespace EchoGarage.Core.Models;

/// <summary>
/// 一次分析使用的不可变参数快照（ADR-023 §6.1）：由 <see cref="SpectrumSettings"/> 规范化得到，
/// 频段边界（Hz）在这里一次算好，分析与渲染不再各自换算；参数变化时整体替换，不原地修改。
/// </summary>
public sealed class SpectrumOptions
{
    /// <summary>默认参数（40 段 / 4096 点 / 40 Hz–18 kHz）。</summary>
    public static SpectrumOptions Default { get; } = From(new SpectrumSettings());

    /// <summary>频段（柱）数量。</summary>
    public required int BandCount { get; init; }

    /// <summary>FFT 点数。</summary>
    public required int FftSize { get; init; }

    /// <summary>FFT 点数对应的 2 的幂（NAudio 的 FFT 只收指数）。</summary>
    public required int FftExponent { get; init; }

    /// <summary>最低频率（Hz）。</summary>
    public required double MinFrequency { get; init; }

    /// <summary>最高频率（Hz）。</summary>
    public required double MaxFrequency { get; init; }

    /// <summary>频段内 bin 的聚合方式。</summary>
    public required SpectrumSummation Summation { get; init; }

    /// <summary>显示下限（dBFS）。</summary>
    public required double FloorDb { get; init; }

    /// <summary>显示上限（dBFS），固定 0。</summary>
    public required double CeilingDb { get; init; }

    /// <summary>频率斜率补偿（dB/oct）。</summary>
    public required double SlopeDbPerOctave { get; init; }

    /// <summary>柱体回落速度（dB/s）。</summary>
    public required double ReleaseDbPerSecond { get; init; }

    /// <summary>是否显示峰值保持线。</summary>
    public required bool ShowPeaks { get; init; }

    /// <summary>峰值保持时间（秒）。</summary>
    public required double PeakHoldSeconds { get; init; }

    /// <summary>峰值保持结束后的下落速度（dB/s）。</summary>
    public required double PeakFallDbPerSecond { get; init; }

    /// <summary>是否绘制 dB 网格线与刻度。</summary>
    public required bool ShowGraticule { get; init; }

    /// <summary>是否绘制频率标签。</summary>
    public required bool ShowFrequencyLabels { get; init; }

    /// <summary>频段边界（Hz），长度 <see cref="BandCount"/> + 1，几何级数递增。</summary>
    public required IReadOnlyList<double> BandEdges { get; init; }

    /// <summary>由持久化设置生成快照（先复制再规范化，不改调用方的实例）。</summary>
    public static SpectrumOptions From(SpectrumSettings settings)
    {
        var normalized = SpectrumSettings.Normalize(settings?.Clone());
        return new SpectrumOptions
        {
            BandCount = normalized.BandCount,
            FftSize = normalized.FftSize,
            FftExponent = (int)Math.Round(Math.Log2(normalized.FftSize)),
            MinFrequency = normalized.MinFrequency,
            MaxFrequency = normalized.MaxFrequency,
            Summation = normalized.Summation,
            FloorDb = normalized.FloorDb,
            CeilingDb = SpectrumMath.DefaultCeilingDb,
            SlopeDbPerOctave = normalized.SlopeDbPerOctave,
            ReleaseDbPerSecond = normalized.ReleaseDbPerSecond,
            ShowPeaks = normalized.ShowPeaks,
            PeakHoldSeconds = normalized.PeakHoldMs / 1000.0,
            PeakFallDbPerSecond = normalized.PeakFallDbPerSecond,
            ShowGraticule = normalized.ShowGraticule,
            ShowFrequencyLabels = normalized.ShowFrequencyLabels,
            BandEdges = SpectrumMath.CreateBandEdges(normalized.MinFrequency, normalized.MaxFrequency, normalized.BandCount)
        };
    }
}
