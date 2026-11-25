using System.Diagnostics;

namespace EchoGarage.Core.Services;

/// <summary>一次轻点测速后的实时状态快照。</summary>
public readonly record struct TapTempoSnapshot(
    double? Bpm,
    int TapCount,
    bool IsStable,
    bool WasReset);

/// <summary>
/// Tap 轻点测速计算器（ADR-034 / KB-021 §5）：
/// 使用普通最小二乘线性回归（OLS）拟合 <c>t_k = α + k·T</c>，克服“相邻间隔算术平均等价于首尾差”的抖动缺陷。
/// 内置 180 ms 防误触消抖、2.5 s 停顿自动重置、16 次滚动窗口与 ±35% 漏拍重启保护。
/// </summary>
public sealed class TapTempoCalculator
{
    public const double MinIntervalSeconds = 0.18;  // > 333 BPM 视为误触双击
    public const double ResetTimeoutSeconds = 2.50; // < 24 BPM 自动开始新一轮
    public const int MaxWindowSize = 16;
    public const int StableTapThreshold = 5;
    private const double OutlierDeviationRatio = 0.35;

    private readonly List<double> _timestamps = new(MaxWindowSize + 1);

    public int TapCount => _timestamps.Count;

    public void Reset() => _timestamps.Clear();

    /// <summary>使用高精度单调时钟记录一次敲击。</summary>
    public TapTempoSnapshot TapNow() => Tap(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    /// <summary>记录指定时间戳（秒，单调递增）的一次敲击并返回最新 BPM 估计。</summary>
    public TapTempoSnapshot Tap(double timestampSeconds)
    {
        if (!double.IsFinite(timestampSeconds))
        {
            return CurrentSnapshot(wasReset: false);
        }

        if (_timestamps.Count == 0)
        {
            _timestamps.Add(timestampSeconds);
            return new TapTempoSnapshot(null, 1, false, WasReset: false);
        }

        var last = _timestamps[^1];
        var delta = timestampSeconds - last;

        // 防抖：过快双击直接忽略，保留现有状态
        if (delta < MinIntervalSeconds)
        {
            return CurrentSnapshot(wasReset: false);
        }

        // 超时停顿：自动开启新一轮
        if (delta > ResetTimeoutSeconds)
        {
            _timestamps.Clear();
            _timestamps.Add(timestampSeconds);
            return new TapTempoSnapshot(null, 1, false, WasReset: true);
        }

        // 漏拍/节奏突变保护：已有 ≥ 4 次敲击且本次间隔偏离中位间隔超过 ±35% 时，
        // 从上一拍 + 本拍重新建立窗口，避免一次手滑污染整个线性回归。
        var wasReset = false;
        if (_timestamps.Count >= 4)
        {
            var median = ComputeMedianInterval(_timestamps);
            if (median > 0 && Math.Abs(delta - median) / median > OutlierDeviationRatio)
            {
                _timestamps.Clear();
                _timestamps.Add(last);
                _timestamps.Add(timestampSeconds);
                wasReset = true;
            }
        }

        if (!wasReset)
        {
            _timestamps.Add(timestampSeconds);
            if (_timestamps.Count > MaxWindowSize)
            {
                _timestamps.RemoveAt(0);
            }
        }

        return CurrentSnapshot(wasReset);
    }

    private TapTempoSnapshot CurrentSnapshot(bool wasReset)
    {
        var n = _timestamps.Count;
        if (n < 2)
        {
            return new TapTempoSnapshot(null, n, false, wasReset);
        }

        var period = ComputeOlsPeriod(_timestamps);
        if (period <= 0)
        {
            return new TapTempoSnapshot(null, n, false, wasReset);
        }

        var rawBpm = 60.0 / period;
        double? bpm = BpmFormatter.IsValid(rawBpm)
            ? BpmFormatter.RoundForDisplay(rawBpm, 1)
            : null;
        return new TapTempoSnapshot(bpm, n, n >= StableTapThreshold && bpm.HasValue, wasReset);
    }

    private static double ComputeOlsPeriod(List<double> ts)
    {
        var n = ts.Count;
        if (n == 2)
        {
            return ts[1] - ts[0];
        }

        var meanK = (n - 1) / 2.0;
        double sumT = 0;
        for (var i = 0; i < n; i++)
        {
            sumT += ts[i];
        }

        var meanT = sumT / n;
        double num = 0;
        double den = 0;
        for (var i = 0; i < n; i++)
        {
            var dk = i - meanK;
            num += dk * (ts[i] - meanT);
            den += dk * dk;
        }

        return den > 0 ? num / den : 0;
    }

    private static double ComputeMedianInterval(List<double> ts)
    {
        var intervals = new double[ts.Count - 1];
        for (var i = 1; i < ts.Count; i++)
        {
            intervals[i - 1] = ts[i] - ts[i - 1];
        }

        Array.Sort(intervals);
        var mid = intervals.Length / 2;
        return intervals.Length % 2 == 1
            ? intervals[mid]
            : (intervals[mid - 1] + intervals[mid]) * 0.5;
    }
}
