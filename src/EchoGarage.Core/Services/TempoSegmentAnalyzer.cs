using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// 基于 Beat This! 亚帧拍点时间戳的纯 C# 分段 BPM 识别器：
/// <list type="number">
/// <item>按静音/无鼓空洞切分连续拍流，并在 <c>ln(BPM)</c> 空间执行 5 拍滑动中值滤波消除单拍漏拍与加花毛刺；</item>
/// <item>利用带自适应惩罚项的前缀和精确动态规划（分段常数拟合）检出真正速度跃变点（耗时 &lt; 1 ms）；</item>
/// <item>对每段原始拍间隔复用 KDE + 10% 截尾均值精算精确 BPM，合并过短过渡段与相近速度段（消除间奏伪变速）；</item>
/// <item>当检出段落数 &gt; <see cref="BpmFormatter.MaxDisplaySegments"/>（5）时，返回空明细并标记 <c>TooManyTempoChanges</c>。</item>
/// </list>
/// </summary>
public static class TempoSegmentAnalyzer
{
    private const double MinIoiSeconds = 0.20;         // 300 BPM
    private const double MaxIoiSeconds = 2.00;         // 30 BPM；大于此值视为无鼓空洞/休止
    private const int MinRunIntervals = 4;             // 连续拍流最少 4 间隔，过滤静音间奏中的孤立杂拍
    private const int MinSegmentBeats = 6;             // 动态规划单段最少间隔数
    private const double MinSegmentDurationSec = 6.0;  // 最终展示段落最短时长（秒）
    private const double MinSegmentConfidence = 0.40;  // 段内主速拍点一致率低于 40% 视为非稳定节拍过渡
    private const double MergeMinDeltaBpm = 3.0;       // 相邻段 BPM 绝对差小于 3.0 且相对差 < 3% 时合并
    private const double MergeMinRelDelta = 0.03;
    private const double EdgeSnapThresholdSec = 8.0;   // 首尾段落距离起止 <= 8s 时贴齐曲目边界
    private const double GapBridgeThresholdSec = 6.0;  // 相邻段间隙 <= 6s 时在中点无缝衔接
    private const int MaxDpBins = 600;                 // 超长曲目/DJ Mix 自动微装箱保持 O(B²) < 1 ms
    private const double BaseChangePenalty = 0.030;    // ln(BPM) 空间分段基础惩罚（显著压制 ±1.5% 正常抖动）

    public sealed record SegmentationResult(
        IReadOnlyList<BpmSegment> Segments,
        int DetectedSegmentCount,
        bool TooManyTempoChanges,
        bool IsVariableTempo);

    private readonly record struct BeatInterval(
        double StartSec,
        double EndSec,
        double Ioi,
        double LogBpm,
        int RunId);

    private sealed class MutableSegment
    {
        public double StartSec { get; set; }
        public double EndSec { get; set; }
        public List<double> Iois { get; } = [];
        public double Bpm { get; set; }
        public double Confidence { get; set; }
        public double DurationSec => Math.Max(0, EndSec - StartSec);
    }

    public static SegmentationResult Analyze(
        IReadOnlyList<double> peakFrames,
        double totalDurationSeconds,
        double globalIqr,
        double globalInlierRatio)
    {
        if (peakFrames.Count < MinSegmentBeats * 2 + 1)
        {
            var fallbackVariable = globalIqr > 4.5 && globalInlierRatio < 0.55;
            return new SegmentationResult(
                Segments: [],
                DetectedSegmentCount: fallbackVariable ? BpmFormatter.MaxDisplaySegments + 1 : 1,
                TooManyTempoChanges: fallbackVariable,
                IsVariableTempo: fallbackVariable);
        }

        // 1. 提取连续拍间隔，并标记无鼓空洞（> 2 s）
        var rawRuns = BuildContiguousRuns(peakFrames);
        var observations = new List<BeatInterval>();
        var runId = 0;
        foreach (var run in rawRuns)
        {
            if (run.Count < MinRunIntervals)
            {
                continue;
            }

            foreach (var item in run)
            {
                observations.Add(item with { RunId = runId });
            }

            runId++;
        }

        if (observations.Count < MinSegmentBeats * 2)
        {
            var fallbackVariable = globalIqr > 4.5 && globalInlierRatio < 0.55;
            return new SegmentationResult(
                Segments: [],
                DetectedSegmentCount: fallbackVariable ? BpmFormatter.MaxDisplaySegments + 1 : 1,
                TooManyTempoChanges: fallbackVariable,
                IsVariableTempo: fallbackVariable);
        }

        // 2. 在各连续拍流内部做 5 拍滑动中值平滑，消除单拍漏拍/切分音尖峰
        var smoothLog = ApplyContiguousMedianFilter(observations, halfWindow: 2);

        // 3. 估算局域基线抖动方差，计算自适应惩罚项并执行前缀和精确动态规划分段
        var penalty = ComputeAdaptivePenalty(smoothLog, observations);
        var rawSplitBounds = RunPenalizedSegmentationDp(smoothLog, observations.Count, penalty);

        // 4. 将分段映射回原始 IOI 列表并用 KDE + 截尾均值精算每段 BPM
        var segments = new List<MutableSegment>(rawSplitBounds.Count - 1);
        for (var s = 0; s < rawSplitBounds.Count - 1; s++)
        {
            var lo = rawSplitBounds[s];
            var hi = rawSplitBounds[s + 1];
            if (hi <= lo)
            {
                continue;
            }

            var seg = new MutableSegment
            {
                StartSec = observations[lo].StartSec,
                EndSec = observations[hi - 1].EndSec
            };
            for (var i = lo; i < hi; i++)
            {
                seg.Iois.Add(observations[i].Ioi);
            }

            RecomputeSegmentStats(seg);
            if (BpmFormatter.IsValid(seg.Bpm))
            {
                segments.Add(seg);
            }
        }

        // 5. 合并过短过渡段（< 6 s 或 < 6 拍）以及相邻速度接近的段落（消除无鼓间奏造成的伪切段）
        MergeShortSegments(segments);
        MergeSimilarAdjacentSegments(segments);

        // 5.5 过滤非稳定节拍的噪声过渡段（段内一致率 < 40%，如无鼓独奏或切分过门）；
        // 若绝大多数段落都不稳定且总段数多，说明整曲属于自由拍/频繁变速。
        var unstableCount = segments.Count(s => s.Confidence < MinSegmentConfidence);
        if (unstableCount > 0 && segments.Count > 1)
        {
            var stable = segments.Where(s => s.Confidence >= MinSegmentConfidence).ToList();
            if (stable.Count >= 2)
            {
                segments = stable;
                MergeSimilarAdjacentSegments(segments);
            }
            else if (unstableCount >= 2)
            {
                return new SegmentationResult(
                    Segments: [],
                    DetectedSegmentCount: Math.Max(segments.Count, BpmFormatter.MaxDisplaySegments + 1),
                    TooManyTempoChanges: true,
                    IsVariableTempo: true);
            }
        }

        // 若单段但整体高度发散（极端连续自由拍/非阶梯混沌变速），标记为变速过多
        if (segments.Count <= 1)
        {
            var chaoticRubato = globalIqr > 4.5 && globalInlierRatio < 0.55;
            return new SegmentationResult(
                Segments: [],
                DetectedSegmentCount: chaoticRubato ? BpmFormatter.MaxDisplaySegments + 1 : 1,
                TooManyTempoChanges: chaoticRubato,
                IsVariableTempo: chaoticRubato);
        }

        // 6. 超过展示上限（> 5 段）时按要求隐藏段落明细并给出变速过多提示
        if (segments.Count > BpmFormatter.MaxDisplaySegments)
        {
            return new SegmentationResult(
                Segments: [],
                DetectedSegmentCount: segments.Count,
                TooManyTempoChanges: true,
                IsVariableTempo: true);
        }

        // 7. 规整边界时间戳与标记主段落
        var effectiveTotalDuration = Math.Max(totalDurationSeconds, segments[^1].EndSec);
        SnapSegmentBoundaries(segments, effectiveTotalDuration);

        var primaryIndex = 0;
        var bestScore = -1.0;
        for (var i = 0; i < segments.Count; i++)
        {
            // 按时长与拍数综合选取主段落
            var score = segments[i].DurationSec * (0.5 + 0.5 * segments[i].Confidence);
            if (score > bestScore)
            {
                bestScore = score;
                primaryIndex = i;
            }
        }

        var finalSegments = new List<BpmSegment>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            finalSegments.Add(new BpmSegment(
                StartSeconds: Math.Round(Math.Max(0, seg.StartSec), 2),
                EndSeconds: Math.Round(Math.Min(effectiveTotalDuration, Math.Max(seg.StartSec, seg.EndSec)), 2),
                Bpm: BpmFormatter.RoundForStorage(seg.Bpm),
                Confidence: Math.Round(Math.Clamp(seg.Confidence, 0.0, 1.0), 3),
                IsPrimary: i == primaryIndex));
        }

        return new SegmentationResult(
            Segments: finalSegments,
            DetectedSegmentCount: finalSegments.Count,
            TooManyTempoChanges: false,
            IsVariableTempo: true);
    }

    private static List<List<BeatInterval>> BuildContiguousRuns(IReadOnlyList<double> peakFrames)
    {
        var runs = new List<List<BeatInterval>>();
        var current = new List<BeatInterval>();

        for (var i = 1; i < peakFrames.Count; i++)
        {
            var t0 = peakFrames[i - 1] / BeatThisPostProcessor.FramesPerSecond;
            var t1 = peakFrames[i] / BeatThisPostProcessor.FramesPerSecond;
            var ioi = t1 - t0;

            if (ioi > MaxIoiSeconds)
            {
                if (current.Count > 0)
                {
                    runs.Add(current);
                    current = [];
                }

                continue;
            }

            if (ioi < MinIoiSeconds)
            {
                continue;
            }

            var bpm = 60.0 / ioi;
            current.Add(new BeatInterval(t0, t1, ioi, Math.Log(bpm), 0));
        }

        if (current.Count > 0)
        {
            runs.Add(current);
        }

        return runs;
    }

    private static double[] ApplyContiguousMedianFilter(List<BeatInterval> observations, int halfWindow)
    {
        var n = observations.Count;
        var result = new double[n];
        var scratch = new double[halfWindow * 2 + 1];

        for (var i = 0; i < n; i++)
        {
            var run = observations[i].RunId;
            var count = 0;
            var lo = Math.Max(0, i - halfWindow);
            var hi = Math.Min(n - 1, i + halfWindow);
            for (var j = lo; j <= hi; j++)
            {
                if (observations[j].RunId == run)
                {
                    scratch[count++] = observations[j].LogBpm;
                }
            }

            Array.Sort(scratch, 0, count);
            var mid = count / 2;
            result[i] = count % 2 == 1
                ? scratch[mid]
                : (scratch[mid - 1] + scratch[mid]) * 0.5;
        }

        return result;
    }

    /// <summary>
    /// 使用相邻一阶差分的 MAD 估算正常演奏微抖动方差，避免把轻微真人波动误切为多段，
    /// 同时限定上限防止真正的大幅度变速被淹没。
    /// </summary>
    private static double ComputeAdaptivePenalty(double[] smoothLog, List<BeatInterval> observations)
    {
        var diffs = new List<double>(smoothLog.Length);
        for (var i = 1; i < smoothLog.Length; i++)
        {
            if (observations[i].RunId == observations[i - 1].RunId)
            {
                diffs.Add(Math.Abs(smoothLog[i] - smoothLog[i - 1]));
            }
        }

        if (diffs.Count < 4)
        {
            return BaseChangePenalty;
        }

        diffs.Sort();
        var mad = diffs[diffs.Count / 2];
        // 对高斯噪声，一阶差分的 MAD / 0.6745 / sqrt(2) ≈ σ
        var sigma = Math.Clamp(mad / (0.6745 * Math.Sqrt(2.0)), 0.004, 0.025);
        var variance = sigma * sigma;
        var adaptive = 14.0 * variance * Math.Log(Math.Max(16, smoothLog.Length));
        return Math.Max(BaseChangePenalty, adaptive);
    }

    /// <summary>
    /// 基于前缀和的精确分段常数动态规划（超过 600 拍时微装箱保证 O(B²) 恒定亚毫秒）。
    /// 返回在原始 <c>observations</c> 坐标系中的分段边界列表 <c>[0, b1, ..., n]</c>。
    /// </summary>
    private static List<int> RunPenalizedSegmentationDp(double[] smoothLog, int n, double penalty)
    {
        var binSize = n > MaxDpBins ? (int)Math.Ceiling(n / (double)MaxDpBins) : 1;
        var binCount = (int)Math.Ceiling(n / (double)binSize);
        var minBins = Math.Max(1, (int)Math.Ceiling(MinSegmentBeats / (double)binSize));

        // 构造每箱均值与权重，并用加权前缀和在 O(1) 计算任意区间 SSE
        var prefixW = new double[binCount + 1];
        var prefixSum = new double[binCount + 1];
        var prefixSq = new double[binCount + 1];

        for (var b = 0; b < binCount; b++)
        {
            var lo = b * binSize;
            var hi = Math.Min(n, lo + binSize);
            double w = hi - lo;
            double s = 0;
            double sq = 0;
            for (var i = lo; i < hi; i++)
            {
                var v = smoothLog[i];
                s += v;
                sq += v * v;
            }

            prefixW[b + 1] = prefixW[b] + w;
            prefixSum[b + 1] = prefixSum[b] + s;
            prefixSq[b + 1] = prefixSq[b] + sq;
        }

        double SegmentSse(int bStart, int bEnd)
        {
            var w = prefixW[bEnd] - prefixW[bStart];
            if (w <= 0)
            {
                return 0;
            }

            var s = prefixSum[bEnd] - prefixSum[bStart];
            var sq = prefixSq[bEnd] - prefixSq[bStart];
            return Math.Max(0.0, sq - (s * s) / w);
        }

        var dp = new double[binCount + 1];
        var prev = new int[binCount + 1];
        Array.Fill(dp, double.PositiveInfinity);
        dp[0] = -penalty; // 第一段不计切分惩罚

        for (var r = minBins; r <= binCount; r++)
        {
            // 候选 1：从 0 直接作为一个整体段
            var bestCost = SegmentSse(0, r);
            var bestSplit = 0;

            // 候选 2：从任意合法左边界 l >= minBins 切分
            var maxL = r - minBins;
            for (var l = minBins; l <= maxL; l++)
            {
                var candidate = dp[l] + SegmentSse(l, r) + penalty;
                if (candidate < bestCost)
                {
                    bestCost = candidate;
                    bestSplit = l;
                }
            }

            dp[r] = bestCost;
            prev[r] = bestSplit;
        }

        var binBounds = new List<int> { binCount };
        var cursor = binCount;
        while (cursor > 0)
        {
            cursor = prev[cursor];
            binBounds.Add(cursor);
        }

        binBounds.Reverse();

        var obsBounds = new List<int>(binBounds.Count);
        foreach (var b in binBounds)
        {
            obsBounds.Add(Math.Min(n, b * binSize));
        }

        return obsBounds;
    }

    private static void RecomputeSegmentStats(MutableSegment segment)
    {
        if (segment.Iois.Count == 0)
        {
            segment.Bpm = 0;
            segment.Confidence = 0;
            return;
        }

        var (bpm, inlierRatio, _, _) = BeatThisPostProcessor.EstimateFromIois(segment.Iois);
        segment.Bpm = bpm;
        segment.Confidence = Math.Clamp(inlierRatio, 0.0, 1.0);
    }

    private static void MergeShortSegments(List<MutableSegment> segments)
    {
        var changed = true;
        while (changed && segments.Count > 1)
        {
            changed = false;
            for (var i = 0; i < segments.Count; i++)
            {
                if (segments[i].DurationSec >= MinSegmentDurationSec &&
                    segments[i].Iois.Count >= MinSegmentBeats)
                {
                    continue;
                }

                // 选择相邻段中 BPM 更接近的一侧并入
                int targetIndex;
                if (i == 0)
                {
                    targetIndex = 1;
                }
                else if (i == segments.Count - 1)
                {
                    targetIndex = i - 1;
                }
                else
                {
                    var leftDiff = Math.Abs(segments[i].Bpm - segments[i - 1].Bpm);
                    var rightDiff = Math.Abs(segments[i].Bpm - segments[i + 1].Bpm);
                    targetIndex = leftDiff <= rightDiff ? i - 1 : i + 1;
                }

                MergePair(segments, Math.Min(i, targetIndex), Math.Max(i, targetIndex));
                changed = true;
                break;
            }
        }
    }

    private static void MergeSimilarAdjacentSegments(List<MutableSegment> segments)
    {
        var changed = true;
        while (changed && segments.Count > 1)
        {
            changed = false;
            for (var i = 0; i < segments.Count - 1; i++)
            {
                var a = segments[i].Bpm;
                var b = segments[i + 1].Bpm;
                var minBpm = Math.Min(a, b);
                var absDiff = Math.Abs(a - b);
                var relDiff = minBpm > 0 ? absDiff / minBpm : 0;

                if (absDiff < MergeMinDeltaBpm || relDiff < MergeMinRelDelta)
                {
                    MergePair(segments, i, i + 1);
                    changed = true;
                    break;
                }
            }
        }
    }

    private static void MergePair(List<MutableSegment> segments, int leftIndex, int rightIndex)
    {
        var left = segments[leftIndex];
        var right = segments[rightIndex];
        left.StartSec = Math.Min(left.StartSec, right.StartSec);
        left.EndSec = Math.Max(left.EndSec, right.EndSec);
        left.Iois.AddRange(right.Iois);
        RecomputeSegmentStats(left);
        segments.RemoveAt(rightIndex);
    }

    private static void SnapSegmentBoundaries(List<MutableSegment> segments, double totalDurationSeconds)
    {
        if (segments.Count == 0)
        {
            return;
        }

        // 首尾贴齐：仅在离曲目起止 <= 8s 时贴齐，超过 8s 的真实长静音前奏/尾奏保留空段
        if (segments[0].StartSec <= EdgeSnapThresholdSec)
        {
            segments[0].StartSec = 0;
        }

        if (totalDurationSeconds - segments[^1].EndSec <= EdgeSnapThresholdSec)
        {
            segments[^1].EndSec = totalDurationSeconds;
        }

        // 连续相邻段在中点无缝衔接；超过 6s 的长无鼓间奏保留在进度条中作为中性空档
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var gap = segments[i + 1].StartSec - segments[i].EndSec;
            if (gap is >= -0.5 and <= GapBridgeThresholdSec)
            {
                var mid = (segments[i].EndSec + segments[i + 1].StartSec) * 0.5;
                segments[i].EndSec = mid;
                segments[i + 1].StartSec = mid;
            }
        }
    }
}
