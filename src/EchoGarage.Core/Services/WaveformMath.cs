namespace EchoGarage.Core.Services;

/// <summary>
/// 波形分析的全部常数与纯函数（ADR-015 §6.8 的参数表集中在这里，便于调参与单测）。
/// 只做数学，不碰文件/解码/UI；Infrastructure 的分析器按这里的口径产出 EGWV 列数据。
/// </summary>
public static class WaveformMath
{
    // ---- ADR-015 §6.8 参数表 ----

    /// <summary>STFT 帧长（44.1 kHz 下 46 ms）。</summary>
    public const int FftSize = 2048;

    /// <summary>帧移（秒）：10 ms。</summary>
    public const double HopSeconds = 0.010;

    /// <summary>列率（列/秒）：两帧一列。</summary>
    public const int ColumnsPerSecond = 50;

    /// <summary>一列包含的 10 ms 帧数。</summary>
    public const int FramesPerColumn = 2;

    /// <summary>低频段下界（Hz）。</summary>
    public const double LowBandMinHz = 20.0;

    /// <summary>低/中分频点（Hz）。</summary>
    public const double LowCrossoverHz = 200.0;

    /// <summary>中/高分频点（Hz）。</summary>
    public const double HighCrossoverHz = 4000.0;

    /// <summary>高频段上界（Hz）：更高的部分对视觉无意义，直接忽略。</summary>
    public const double HighBandMaxHz = 16000.0;

    /// <summary>包络 attack 时间常数（秒）。</summary>
    public const double EnvelopeAttackSeconds = 0.060;

    /// <summary>包络 release 时间常数（秒）。</summary>
    public const double EnvelopeReleaseSeconds = 0.250;

    /// <summary>高度拉伸的下分位。</summary>
    public const double StretchLowPercentile = 5.0;

    /// <summary>高度拉伸的上分位（不取 100，避免单个爆音拉高上限）。</summary>
    public const double StretchHighPercentile = 99.5;

    /// <summary>
    /// 最终快照的最小显示动态范围（dB）：素材自身的 p5–p99.5 不足该值时，把下界向下扩展
    /// （而不是抬高上界，见 <see cref="StretchWindow"/>）。取 30 dB 让窄动态 / 恒定电平素材的
    /// 非静音列仍有明显高度；下界不会低于 <see cref="SilenceDb"/>（ADR-015-A2）。
    /// </summary>
    public const double MinimumStretchDb = 30.0;

    /// <summary>低频份额幂次（&lt; 1，保证基底始终存在）。</summary>
    public const double LowShareExponent = 0.8;

    /// <summary>中频份额幂次。</summary>
    public const double MidShareExponent = 1.1;

    /// <summary>高频份额幂次（&gt; 1，压掉常态能量、只留瞬态）。</summary>
    public const double HighShareExponent = 1.4;

    /// <summary>渲染聚合时份额的加权幂次（ADR-015 §9.4）。</summary>
    public const double ShareWeightExponent = 1.5;

    /// <summary>高度最大值（每列 1 字节）。</summary>
    public const int MaxHeight = 255;

    /// <summary>静音阈值：RMS 低于它视为数字静音。</summary>
    public const double SilenceThreshold = 1e-5;

    /// <summary>静音对应的 dB（20·log10(1e-5) = −100）。</summary>
    public const double SilenceDb = -100.0;

    /// <summary>静音列的份额（ADR 原文写 (1,1,1)，但会破坏 low+mid+high==255 的校验，取均分值）。</summary>
    public const byte SilentShare = 85;

    /// <summary>Hann 窗均方值；周期 Hann 窗精确为 0.375，Parseval 折算用它（ADR-015 §6.3）。</summary>
    public const double HannMeanSquare = 0.375;

    // ---- ADR-015 §6.10 渐进渲染 ----

    /// <summary>渐进发布步长：每 5 s 音频（250 列）一次。</summary>
    public const int PublishColumns = 250;

    /// <summary>取消检查间隔（帧）。</summary>
    public const int CancelCheckFrames = 64;

    /// <summary>
    /// 渐进分析期间固定的归一化窗口下限（dB）。分析过程中一直用 48 dB 的总量程，保证已经画出的
    /// 列在后续快照里高度不变；完成时才切到全曲 p5 / p99.5（ADR-015 §6.10 的“整体重排”）。
    /// </summary>
    public const double StreamingWindowFloorDb = -48.0;

    /// <summary>
    /// 渐进分析期间固定的归一化窗口上限（dB）：取 0 dBFS（电平最大值），不随已分析内容的上限变化，
    /// 这样波形在生成过程中不会“长高 / 缩矮”（owner 2026-11-14 反馈）。
    /// </summary>
    public const double StreamingWindowCeilingDb = 0.0;

    /// <summary>包络 attack 系数：a = 1 − exp(−0.010 / 0.060) ≈ 0.1535。</summary>
    public static readonly double EnvelopeAttackCoefficient = 1.0 - Math.Exp(-HopSeconds / EnvelopeAttackSeconds);

    /// <summary>包络 release 系数：r = 1 − exp(−0.010 / 0.250) ≈ 0.0392。</summary>
    public static readonly double EnvelopeReleaseCoefficient = 1.0 - Math.Exp(-HopSeconds / EnvelopeReleaseSeconds);

    /// <summary>hop = round(sr/100)（44.1 kHz 下为 441，即 10 ms）。</summary>
    public static int HopSize(int sampleRate) => Math.Max(1, (int)Math.Round(sampleRate * HopSeconds));

    /// <summary>由 10 ms 帧数算列数：两帧一列，末列允许只有一帧。</summary>
    public static int ColumnCountForFrames(int frameCount) =>
        frameCount <= 0 ? 0 : (frameCount + 1) / FramesPerColumn;

    /// <summary>
    /// 周期 Hann 窗 w[n] = 0.5·(1 − cos(2πn/N))。周期形式（分母 N 而非 N−1）的均方值恰好是
    /// 0.375，与 ADR-015 §6.3 的 Parseval 折算一致；对称形式会得到 0.375·N/(N−1) 的偏差。
    /// </summary>
    public static double[] CreateHannWindow()
    {
        var window = new double[FftSize];
        for (var i = 0; i < FftSize; i++)
        {
            window[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / FftSize));
        }

        return window;
    }

    /// <summary>RMS 是否算作静音（RMS &lt; 1e-5；NaN 也按静音处理）。</summary>
    public static bool IsSilent(double rms) => !(rms >= SilenceThreshold);

    /// <summary>RMS 转 dB：20·log10(max(rms, 1e-5))，静音为 −100 dB。</summary>
    public static double Decibel(double rms)
    {
        if (IsSilent(rms))
        {
            return SilenceDb;
        }

        return rms <= SilenceThreshold ? SilenceDb : 20.0 * Math.Log10(rms);
    }

    /// <summary>一阶 attack/release 包络（10 ms 帧率，ADR-015 §6.5）：上升用 attack，下降用 release。</summary>
    public static double UpdateEnvelope(double previous, double current)
    {
        var coefficient = current > previous ? EnvelopeAttackCoefficient : EnvelopeReleaseCoefficient;
        return previous + (current - previous) * coefficient;
    }

    /// <summary>升序样本的分位值（线性插值，<paramref name="percentile"/> 取 0..100）。</summary>
    public static double PercentileFromSorted(ReadOnlySpan<double> sorted, double percentile)
    {
        if (sorted.IsEmpty)
        {
            return double.NaN;
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var position = Math.Clamp(percentile, 0.0, 100.0) / 100.0 * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        var fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    /// <summary>
    /// 收集参与分位拉伸的列 dB（未排序）。静音列（RMS &lt; 1e-5）不参与：它们的 dB 恒为 −100，
    /// 若计入，长静音前奏/尾奏会把 p5 拉到 −100 dB，整曲动态被压缩成一条平带；
    /// 静音列本身的高度固定为 0，不需要窗口。
    /// </summary>
    public static double[] CollectNonSilentDecibels(ReadOnlySpan<double> columnRms)
    {
        var result = new List<double>(columnRms.Length);
        foreach (var rms in columnRms)
        {
            if (!IsSilent(rms))
            {
                result.Add(Decibel(rms));
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// 由非静音列的 dB 集合计算逐曲拉伸窗口：lo = p5、hi = p99.5；若 hi − lo 不足
    /// <see cref="MinimumStretchDb"/>，把 lo 向下扩展到 hi − MinimumStretchDb（且不低于
    /// <see cref="SilenceDb"/>），hi 保持不变。旧实现改为抬高 hi，会让恒定电平素材（p5 ≈ p99.5）
    /// 的所有列都落在窗口下界、高度归零，波形看起来是空的（ADR-015-A2）。
    /// 集合为空（整曲静音）时返回 [−100, −70]，高度全为 0，窗口只用于占位。
    /// </summary>
    public static (double FloorDb, double CeilingDb) StretchWindow(ReadOnlySpan<double> nonSilentDecibels)
    {
        if (nonSilentDecibels.IsEmpty)
        {
            return (SilenceDb, SilenceDb + MinimumStretchDb);
        }

        var sorted = nonSilentDecibels.ToArray();
        Array.Sort(sorted);
        var floor = PercentileFromSorted(sorted, StretchLowPercentile);
        var ceiling = PercentileFromSorted(sorted, StretchHighPercentile);

        // 只向下放宽：ceiling 保持贴近素材的响度上限（宽动态素材完全不变），floor 钳制在 SilenceDb。
        floor = Math.Max(SilenceDb, Math.Min(floor, ceiling - MinimumStretchDb));
        return (floor, ceiling);
    }

    /// <summary>
    /// 渐进分析期间使用的固定窗口（−48…0 dB）。上限锁在 0 dBFS，避免分析进度推进时高度整体重排；
    /// 完成后的最终窗口由 <see cref="StretchWindow"/> 给出，控件只重建一次。
    /// </summary>
    public static (double FloorDb, double CeilingDb) StreamingWindow() =>
        (StreamingWindowFloorDb, StreamingWindowCeilingDb);

    /// <summary>
    /// 把一列 RMS 按 dB 窗口映射为 0..255：静音列 0；v &gt; 0 但四舍五入为 0 时取 1（ADR-015 §6.6）。
    /// </summary>
    public static byte StretchHeight(double rms, double floorDb, double ceilingDb)
    {
        if (IsSilent(rms))
        {
            return 0;
        }

        // 正常路径下 StretchWindow 保证 ceiling > floor（最小显示范围 MinimumStretchDb）；这里只做防御。
        if (!(ceilingDb > floorDb))
        {
            return 0;
        }

        var value = Math.Clamp((Decibel(rms) - floorDb) / (ceilingDb - floorDb), 0.0, 1.0);
        if (value <= 0.0)
        {
            return 0;
        }

        var height = (int)Math.Round(MaxHeight * value, MidpointRounding.AwayFromZero);
        if (height <= 0)
        {
            return 1;
        }

        return (byte)Math.Clamp(height, 0, MaxHeight);
    }

    /// <summary>按给定窗口批量计算高度（列 RMS 为 0 的静音列自然得到 0）。</summary>
    public static byte[] BuildHeights(ReadOnlySpan<double> columnRms, double floorDb, double ceilingDb)
    {
        var heights = new byte[columnRms.Length];
        for (var i = 0; i < columnRms.Length; i++)
        {
            heights[i] = StretchHeight(columnRms[i], floorDb, ceilingDb);
        }

        return heights;
    }

    /// <summary>
    /// 三频段份额量化（ADR-015 §6.7）：q = RMS^幂次，份额归一化后 low = round(255·share_low)、
    /// mid = round(255·share_mid)、high = 255 − low − mid。这样三项严格相加为 255 且不会出现负值；
    /// 三个频段全为 0（静音）时写 85/85/85（高度为 0，不会绘制）。
    /// </summary>
    public static (byte Low, byte Mid, byte High) QuantizeShares(double lowRms, double midRms, double highRms)
    {
        var low = Math.Pow(Math.Max(lowRms, 0.0), LowShareExponent);
        var mid = Math.Pow(Math.Max(midRms, 0.0), MidShareExponent);
        var high = Math.Pow(Math.Max(highRms, 0.0), HighShareExponent);
        var sum = low + mid + high;
        if (!(sum > 0.0) || double.IsNaN(sum))
        {
            return (SilentShare, SilentShare, SilentShare);
        }

        var lowValue = (int)Math.Round(MaxHeight * low / sum, MidpointRounding.AwayFromZero);
        lowValue = Math.Clamp(lowValue, 0, MaxHeight);
        var midValue = (int)Math.Round(MaxHeight * mid / sum, MidpointRounding.AwayFromZero);
        midValue = Math.Clamp(midValue, 0, MaxHeight - lowValue);
        return ((byte)lowValue, (byte)midValue, (byte)(MaxHeight - lowValue - midValue));
    }

    /// <summary>
    /// 设备像素列的高度：段内 <paramref name="heights"/> 字节的 p80（256 桶直方图，O(段长)）。
    /// 段为空（像素比缓存列还宽）时取最近列。
    /// </summary>
    public static byte AggregateHeight(ReadOnlySpan<byte> heights, int startColumn, int endColumn)
    {
        if (heights.IsEmpty)
        {
            return 0;
        }

        var (from, to) = NormalizeRange(heights.Length, startColumn, endColumn);
        if (to <= from)
        {
            return 0;
        }

        var histogram = new int[MaxHeight + 1];
        for (var i = from; i < to; i++)
        {
            histogram[heights[i]]++;
        }

        // 最近秩定义：升序第 ceil(0.8·n) 个（1 基）。
        var count = to - from;
        var rank = Math.Max(1, (int)Math.Ceiling(0.8 * count));
        var accumulated = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            accumulated += histogram[value];
            if (accumulated >= rank)
            {
                return (byte)value;
            }
        }

        return heights[to - 1];
    }

    /// <summary>
    /// 设备像素列的份额：段内按 <c>height^1.5</c> 加权平均三段份额，再归一化到 255
    /// （三项严格相加 255，任一项不为负）。整段高度全为 0 时返回 85/85/85；段为空时取最近列。
    /// </summary>
    public static (byte Low, byte Mid, byte High) AggregateShares(
        ReadOnlySpan<byte> heights,
        ReadOnlySpan<byte> low,
        ReadOnlySpan<byte> mid,
        ReadOnlySpan<byte> high,
        int startColumn,
        int endColumn)
    {
        var length = Math.Min(heights.Length, Math.Min(low.Length, Math.Min(mid.Length, high.Length)));
        if (length == 0)
        {
            return (SilentShare, SilentShare, SilentShare);
        }

        var (from, to) = NormalizeRange(length, startColumn, endColumn);
        if (to <= from)
        {
            return (SilentShare, SilentShare, SilentShare);
        }

        double weightSum = 0.0;
        double lowSum = 0.0;
        double midSum = 0.0;
        double highSum = 0.0;
        for (var i = from; i < to; i++)
        {
            var weight = Math.Pow(heights[i], ShareWeightExponent);
            weightSum += weight;
            lowSum += weight * low[i];
            midSum += weight * mid[i];
            highSum += weight * high[i];
        }

        if (!(weightSum > 0.0) || double.IsNaN(weightSum))
        {
            return (SilentShare, SilentShare, SilentShare);
        }

        // 每列的份额本来和为 255，加权平均后仍约等于 255；这里再按总和归一化，消掉舍入误差。
        var shareSum = lowSum + midSum + highSum;
        if (!(shareSum > 0.0) || double.IsNaN(shareSum))
        {
            return (SilentShare, SilentShare, SilentShare);
        }

        var lowValue = (int)Math.Round(MaxHeight * lowSum / shareSum, MidpointRounding.AwayFromZero);
        lowValue = Math.Clamp(lowValue, 0, MaxHeight);
        var midValue = (int)Math.Round(MaxHeight * midSum / shareSum, MidpointRounding.AwayFromZero);
        midValue = Math.Clamp(midValue, 0, MaxHeight - lowValue);
        return ((byte)lowValue, (byte)midValue, (byte)(MaxHeight - lowValue - midValue));
    }

    /// <summary>
    /// 把缓存列区间收敛到合法范围；段为空（<paramref name="endColumn"/> &lt;= <paramref name="startColumn"/>）
    /// 时退化为“最近的一列”，保证渲染时不会出现空洞。
    /// </summary>
    private static (int From, int To) NormalizeRange(int length, int startColumn, int endColumn)
    {
        if (length <= 0)
        {
            return (0, 0);
        }

        var from = Math.Clamp(startColumn, 0, length);
        var to = Math.Clamp(endColumn, 0, length);
        if (to > from)
        {
            return (from, to);
        }

        var nearest = Math.Clamp(startColumn, 0, length - 1);
        return (nearest, nearest + 1);
    }
}
