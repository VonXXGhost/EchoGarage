using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// Beat This! 无 DBN 拍点拾取与全局 BPM 稳健聚合（ADR-034 / KB-021 §3.4）。
/// <para>
/// 输入 50 fps 的逐帧 <c>beat_logits</c>：先以 7 帧局部极大值 + 二次抛物线插值获得亚帧拍点时间戳，
/// 再经 0.1 BPM 高斯核密度众数与截尾均值消除 20 ms 帧量化阶梯误差，并给出置信度、变速标记与候选倍频。
/// </para>
/// </summary>
public static class BeatThisPostProcessor
{
    public const double FramesPerSecond = 50.0;
    private const int PeakWindowRadius = 3; // 7 帧 = 140 ms
    private const double MinPeakSpacingFrames = 3.0;
    private const double MinIoiSeconds = 0.20; // 300 BPM
    private const double MaxIoiSeconds = 2.00; // 30 BPM
    private const int MinValidIntervals = 4;

    private const double KdeMinBpm = 30.0;
    private const double KdeMaxBpm = 300.0;
    private const double KdeStep = 0.1;
    private const double KdeSigma = 0.8;

    public sealed record Estimation(
        double Bpm,
        double Confidence,
        IReadOnlyList<BpmCandidate> Candidates,
        bool IsVariableTempoSuspected,
        bool IsReliable,
        string? Note,
        int PeakCount)
    {
        public IReadOnlyList<BpmSegment> Segments { get; init; } = [];
        public int DetectedSegmentCount { get; init; }
        public bool TooManyTempoChanges { get; init; }
    }

    public static Estimation EstimateGlobalBpm(ReadOnlySpan<float> beatLogits)
    {
        if (beatLogits.Length < 10)
        {
            return Unreliable("音频过短，无法可靠测量 BPM。");
        }

        // 主阈值 logit > 0（sigmoid > 0.5）；若拍点稀疏，尝试对弱瞬态曲目做一次温和回退。
        var (peaks, peakStrengths) = PickPeaks(beatLogits, threshold: 0.0f);
        if (peaks.Count < MinValidIntervals + 1)
        {
            var (fallbackPeaks, fallbackStrengths) = PickPeaks(beatLogits, threshold: -0.85f);
            if (fallbackPeaks.Count > peaks.Count)
            {
                peaks = fallbackPeaks;
                peakStrengths = fallbackStrengths;
            }
        }

        if (peaks.Count < MinValidIntervals + 1)
        {
            return Unreliable("未检测到明显节拍，可点击 BPM 手动输入或轻点测速。");
        }

        var validIois = new List<double>(peaks.Count - 1);
        for (var i = 1; i < peaks.Count; i++)
        {
            var ioi = (peaks[i] - peaks[i - 1]) / FramesPerSecond;
            if (ioi >= MinIoiSeconds && ioi <= MaxIoiSeconds)
            {
                validIois.Add(ioi);
            }
        }

        if (validIois.Count < MinValidIntervals)
        {
            return Unreliable("有效节拍间隔不足，可点击 BPM 手动输入或轻点测速。");
        }

        var (estimatedBpm, inlierRatio, iqr, density, bestDensity) = EstimateFromIoisDetailed(validIois);
        var meanStrength = peakStrengths.Count > 0 ? peakStrengths.Average() : 0.0;
        var confidence = Math.Clamp(0.65 * inlierRatio + 0.35 * meanStrength, 0.0, 1.0);

        // 分段变速分析：基于时间轴拍点时间戳识别 2–5 个稳定段落，超过 5 段时自动隐藏明细并提示
        var totalDurationSec = beatLogits.Length / FramesPerSecond;
        var segmentation = TempoSegmentAnalyzer.Analyze(peaks, totalDurationSec, iqr, inlierRatio);
        var isVariable = segmentation.IsVariableTempo;

        var candidates = BuildCandidates(estimatedBpm, density, bestDensity);
        string? note = null;
        if (segmentation.TooManyTempoChanges)
        {
            note = $"检测到曲目变速过于频繁（超过 {BpmFormatter.MaxDisplaySegments} 个段落），已隐藏分段明细，仅显示主段落速度。";
        }
        else if (segmentation.Segments.Count >= 2)
        {
            note = $"检测到 {segmentation.Segments.Count} 个速度段落，显示为主段落速度。";
        }
        else if (isVariable)
        {
            note = "检测到曲目存在速度变化，显示为主段落速度。";
        }

        return new Estimation(
            estimatedBpm,
            Math.Round(confidence, 3),
            candidates,
            isVariable,
            IsReliable: true,
            Note: note,
            PeakCount: peaks.Count)
        {
            Segments = segmentation.Segments,
            DetectedSegmentCount = segmentation.DetectedSegmentCount,
            TooManyTempoChanges = segmentation.TooManyTempoChanges
        };
    }

    internal static (double Bpm, double InlierRatio, double Iqr, double BestDensity) EstimateFromIois(
        IReadOnlyList<double> validIois)
    {
        var (bpm, inlierRatio, iqr, _, bestDensity) = EstimateFromIoisDetailed(validIois);
        return (bpm, inlierRatio, iqr, bestDensity);
    }

    private static (double Bpm, double InlierRatio, double Iqr, double[] Density, double BestDensity)
        EstimateFromIoisDetailed(IReadOnlyList<double> validIois)
    {
        var validBpms = new double[validIois.Count];
        for (var i = 0; i < validIois.Count; i++)
        {
            validBpms[i] = 60.0 / validIois[i];
        }

        // 0.1 BPM 网格高斯 KDE 找主峰
        var gridCount = (int)Math.Round((KdeMaxBpm - KdeMinBpm) / KdeStep) + 1;
        var density = new double[gridCount];
        var invTwoSigmaSq = 1.0 / (2.0 * KdeSigma * KdeSigma);

        for (var i = 0; i < validBpms.Length; i++)
        {
            var bpm = validBpms[i];
            var center = (int)Math.Round((bpm - KdeMinBpm) / KdeStep);
            var span = (int)Math.Ceiling(4.0 * KdeSigma / KdeStep); // ±3.2 BPM 内贡献 > 99.99%
            var lo = Math.Max(0, center - span);
            var hi = Math.Min(gridCount - 1, center + span);
            for (var g = lo; g <= hi; g++)
            {
                var d = (KdeMinBpm + g * KdeStep) - bpm;
                density[g] += Math.Exp(-d * d * invTwoSigmaSq);
            }
        }

        var bestGridIndex = 0;
        var bestDensity = -1.0;
        for (var g = 0; g < gridCount; g++)
        {
            if (density[g] > bestDensity)
            {
                bestDensity = density[g];
                bestGridIndex = g;
            }
        }

        var modeBpm = KdeMinBpm + bestGridIndex * KdeStep;
        var tolerance = Math.Max(2.5, 0.03 * modeBpm);

        var inlierIois = new List<double>(validIois.Count);
        for (var i = 0; i < validIois.Count; i++)
        {
            if (Math.Abs(validBpms[i] - modeBpm) <= tolerance)
            {
                inlierIois.Add(validIois[i]);
            }
        }

        if (inlierIois.Count == 0)
        {
            inlierIois.AddRange(validIois);
        }

        // 对主峰邻域内的亚帧拍间隔取 10% 截尾均值，兼顾抗离群与消除 20 ms 量化阶梯。
        inlierIois.Sort();
        var trim = inlierIois.Count >= 10 ? inlierIois.Count / 10 : 0;
        var start = trim;
        var end = inlierIois.Count - trim;
        double sumIoi = 0;
        for (var i = start; i < end; i++)
        {
            sumIoi += inlierIois[i];
        }

        var meanIoi = sumIoi / Math.Max(1, end - start);
        var estimatedBpm = BpmFormatter.RoundForStorage(60.0 / meanIoi);
        var inlierRatio = (double)inlierIois.Count / validIois.Count;

        Array.Sort(validBpms);
        var q1 = Percentile(validBpms, 0.25);
        var q3 = Percentile(validBpms, 0.75);
        var iqr = q3 - q1;

        return (estimatedBpm, inlierRatio, iqr, density, bestDensity);
    }

    private static (List<double> Peaks, List<double> Strengths) PickPeaks(ReadOnlySpan<float> logits, float threshold)
    {
        var peaks = new List<double>();
        var strengths = new List<double>();
        var n = logits.Length;

        for (var i = 0; i < n; i++)
        {
            var value = logits[i];
            if (value <= threshold)
            {
                continue;
            }

            var lo = Math.Max(0, i - PeakWindowRadius);
            var hi = Math.Min(n - 1, i + PeakWindowRadius);
            var isMax = true;
            for (var j = lo; j <= hi; j++)
            {
                if (logits[j] > value)
                {
                    isMax = false;
                    break;
                }
            }

            if (!isMax)
            {
                continue;
            }

            // 抛物线插值精修至亚帧位置
            double pos = i;
            if (i > 0 && i < n - 1)
            {
                double y0 = logits[i - 1];
                double y1 = value;
                double y2 = logits[i + 1];
                var denom = y0 - 2.0 * y1 + y2;
                if (denom < -1e-6)
                {
                    var delta = Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5);
                    pos += delta;
                }
            }

            if (peaks.Count > 0 && pos - peaks[^1] <= MinPeakSpacingFrames)
            {
                continue;
            }

            peaks.Add(pos);
            strengths.Add(1.0 / (1.0 + Math.Exp(-value)));
        }

        return (peaks, strengths);
    }

    private static List<BpmCandidate> BuildCandidates(double primaryBpm, double[] density, double bestDensity)
    {
        var list = new List<BpmCandidate>
        {
            new(BpmFormatter.RoundForDisplay(primaryBpm, 1), 1.0)
        };

        void TryAdd(double bpm, double score)
        {
            var rounded = BpmFormatter.RoundForDisplay(bpm, 1);
            if (!BpmFormatter.IsValid(rounded))
            {
                return;
            }

            if (list.Any(c => Math.Abs(c.Bpm - rounded) <= Math.Max(1.5, 0.03 * rounded)))
            {
                return;
            }

            list.Add(new BpmCandidate(rounded, Math.Round(Math.Clamp(score, 0.0, 1.0), 3)));
        }

        // 查找 KDE 中显著次峰（例如双速段落或次级节拍）
        if (bestDensity > 0)
        {
            var secondaryDensity = 0.0;
            var secondaryBpm = 0.0;
            for (var g = 1; g < density.Length - 1; g++)
            {
                if (density[g] < density[g - 1] || density[g] < density[g + 1])
                {
                    continue;
                }

                var bpm = KdeMinBpm + g * KdeStep;
                if (Math.Abs(bpm - primaryBpm) <= Math.Max(4.0, 0.06 * primaryBpm))
                {
                    continue;
                }

                if (density[g] > secondaryDensity)
                {
                    secondaryDensity = density[g];
                    secondaryBpm = bpm;
                }
            }

            if (secondaryDensity >= bestDensity * 0.25)
            {
                TryAdd(secondaryBpm, secondaryDensity / bestDensity);
            }
        }

        // 附加经典半频 / 倍频参考，方便人工快速比对
        if (BpmFormatter.TryHalve(primaryBpm, out var halved))
        {
            TryAdd(halved, 0.5);
        }

        if (BpmFormatter.TryDouble(primaryBpm, out var doubled))
        {
            TryAdd(doubled, 0.5);
        }

        return list.Take(4).ToList();
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var idx = Math.Clamp(p * (sorted.Length - 1), 0, sorted.Length - 1);
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }

    private static Estimation Unreliable(string note) =>
        new(0, 0, [], false, IsReliable: false, Note: note, PeakCount: 0);
}
