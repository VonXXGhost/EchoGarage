namespace EchoGarage.Core.Models;

/// <summary>
/// 一帧频谱（ADR-023 §6.5）：<see cref="Levels"/> 与 <see cref="Peaks"/> 都是 0..1 的归一化柱高，
/// 渲染层直接按面板高度映射。数组由分析器创建后不再修改，读取方只读。
/// </summary>
public sealed class SpectrumFrame
{
    /// <summary>空帧：尚未取得音频或可视化已关闭。</summary>
    public static SpectrumFrame Empty { get; } = new([], [], 0, true);

    public SpectrumFrame(double[] levels, double[] peaks, long frameId, bool isSilent)
    {
        Levels = levels;
        Peaks = peaks;
        FrameId = frameId;
        IsSilent = isSilent;
    }

    /// <summary>各频段当前高度（0..1）。</summary>
    public double[] Levels { get; }

    /// <summary>各频段峰值高度（0..1）；NaN 表示该段没有峰值可画。</summary>
    public double[] Peaks { get; }

    /// <summary>递增帧号：渲染层用它判断是否换了新帧。</summary>
    public long FrameId { get; }

    /// <summary>本帧音频全部低于显示下限（静音 / 暂停 / 无输出）。</summary>
    public bool IsSilent { get; }

    public int BandCount => Levels.Length;
}
