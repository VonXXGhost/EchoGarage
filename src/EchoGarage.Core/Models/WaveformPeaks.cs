using EchoGarage.Core.Services;

namespace EchoGarage.Core.Models;

/// <summary>
/// 波形峰值快照（ADR-015 §6 / §8.1）。列数据与元数据全部只读：渐进分析每次发布一个新的、
/// 数组按 <see cref="ValidCount"/> 截断的快照；完成时给出与 <see cref="ColumnCount"/> 等长的全长快照。
/// 快照之间不共享可变数组，渲染侧可以安全地跨线程持有。
/// </summary>
public sealed class WaveformPeaks
{
    /// <summary>列率（列/秒）。ADR-015 固定 50（20 ms 一列）。</summary>
    public required int ColumnsPerSecond { get; init; }

    /// <summary>整曲列数。渲染时用 <c>[x·N/W, (x+1)·N/W)</c> 把设备像素映射到列。</summary>
    public required int ColumnCount { get; init; }

    /// <summary>已分析列数。渐进快照里四个数组的长度就是它；完成时等于 <see cref="ColumnCount"/>。</summary>
    public required int ValidCount { get; init; }

    /// <summary>音频时长（来自解码器）。</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>解码后的采样率（44.1k / 48k 均可，频段按 Hz 计算与采样率无关）。</summary>
    public required int SampleRate { get; init; }

    /// <summary>逐曲拉伸窗口的下限（p5 dB）。</summary>
    public required double FloorDb { get; init; }

    /// <summary>逐曲拉伸窗口的上限（p99.5 dB，且至少比下限高 8 dB）。</summary>
    public required double CeilingDb { get; init; }

    /// <summary>每列高度 0..255；静音列为 0。</summary>
    public required byte[] Height { get; init; }

    /// <summary>低频（20–200 Hz）份额。与 <see cref="Mid"/>、<see cref="High"/> 严格相加为 255。</summary>
    public required byte[] Low { get; init; }

    /// <summary>中频（200–4000 Hz）份额。</summary>
    public required byte[] Mid { get; init; }

    /// <summary>高频（4000–16000 Hz）份额。</summary>
    public required byte[] High { get; init; }

    /// <summary>分析是否已经覆盖整曲。</summary>
    public bool IsComplete => ValidCount >= ColumnCount;

    /// <summary>
    /// 返回只保留前 <paramref name="validCount"/> 列的新快照。数组截断复制（不共享引用），
    /// 因此调用方拿到的两个快照互不影响。请求的列数超过已有数据时按可用列数收敛。
    /// </summary>
    public WaveformPeaks WithValidCount(int validCount)
    {
        var available = Math.Min(
            Math.Min(ColumnCount, Height.Length),
            Math.Min(Low.Length, Math.Min(Mid.Length, High.Length)));
        var count = Math.Clamp(validCount, 0, Math.Max(0, available));
        return new WaveformPeaks
        {
            ColumnsPerSecond = ColumnsPerSecond,
            ColumnCount = ColumnCount,
            ValidCount = count,
            Duration = Duration,
            SampleRate = SampleRate,
            FloorDb = FloorDb,
            CeilingDb = CeilingDb,
            Height = Truncate(Height, count),
            Low = Truncate(Low, count),
            Mid = Truncate(Mid, count),
            High = Truncate(High, count),
        };
    }

    /// <summary>
    /// 空快照（0 列、已完成）。用于 0 字节 / 短于一个 FFT 窗口的文件，
    /// 让界面走“有 Peaks 但没有列”的空态，而不是一直转圈。
    /// </summary>
    public static WaveformPeaks Empty(TimeSpan duration, int sampleRate) => new()
    {
        ColumnsPerSecond = WaveformMath.ColumnsPerSecond,
        ColumnCount = 0,
        ValidCount = 0,
        Duration = duration,
        SampleRate = sampleRate,
        FloorDb = WaveformMath.SilenceDb,
        CeilingDb = WaveformMath.SilenceDb + WaveformMath.MinimumStretchDb,
        Height = [],
        Low = [],
        Mid = [],
        High = [],
    };

    private static byte[] Truncate(byte[] source, int count)
    {
        // 即使 count == source.Length 也复制：快照之间不共享数组，避免一方误改影响另一方。
        var result = new byte[count];
        Array.Copy(source, result, count);
        return result;
    }
}
