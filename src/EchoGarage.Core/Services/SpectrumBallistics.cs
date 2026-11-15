using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// 分段频谱的弹道（ADR-023 §6.6）：dB 域 attack 瞬时、release 按 dB/s 线性下落，峰值保持到期后
/// 按设定速度下落。全部用 dt 推进，所以 60 / 120 / 144 Hz 下的观感一致；输出为 0..1 的归一化柱高。
/// </summary>
public sealed class SpectrumBallistics
{
    private double[] _levels = [];
    private double[] _peaks = [];
    private double[] _holdRemaining = [];
    private double _floorDb = -70;
    private double _ceilingDb;
    private double _releaseDbPerSecond = 30;
    private double _peakHoldSeconds = 0.8;
    private double _peakFallDbPerSecond = 30;
    private bool _showPeaks = true;

    /// <summary>当前频段数。</summary>
    public int BandCount => _levels.Length;

    /// <summary>应用参数；频段数变化时重排并重新贴地。</summary>
    public void Configure(SpectrumOptions options)
    {
        _floorDb = options.FloorDb;
        _ceilingDb = options.CeilingDb;
        _releaseDbPerSecond = options.ReleaseDbPerSecond;
        _peakHoldSeconds = options.PeakHoldSeconds;
        _peakFallDbPerSecond = options.PeakFallDbPerSecond;
        _showPeaks = options.ShowPeaks;
        if (_levels.Length != options.BandCount)
        {
            Resize(options.BandCount);
        }

        Reset();
    }

    /// <summary>重排到指定频段数并重新贴地。</summary>
    public void Resize(int bandCount)
    {
        var count = Math.Max(0, bandCount);
        _levels = new double[count];
        _peaks = new double[count];
        _holdRemaining = new double[count];
        Reset();
    }

    /// <summary>丢弃当前弹道状态：电平回到下限、峰值清零（跳转 / 换曲 / 参数变化时调用）。</summary>
    public void Reset()
    {
        Array.Fill(_levels, _floorDb);
        Array.Fill(_holdRemaining, 0.0);
        Array.Fill(_peaks, _showPeaks ? _floorDb : double.NaN);
    }

    /// <summary>
    /// 用本帧测得的各频段 dB（已含斜率补偿）推进弹道，写出归一化柱高与峰值高度。
    /// <paramref name="peaks"/> 里 NaN 表示该段不显示峰值。
    /// </summary>
    public void Update(ReadOnlySpan<double> bandDb, double dtSeconds, Span<double> levels, Span<double> peaks)
    {
        var count = Math.Min(BandCount, Math.Min(bandDb.Length, Math.Min(levels.Length, peaks.Length)));
        var release = SpectrumMath.DecayDbPerFrame(_releaseDbPerSecond, dtSeconds);
        var peakFall = SpectrumMath.DecayDbPerFrame(_peakFallDbPerSecond, dtSeconds);
        var dt = Math.Clamp(dtSeconds, 0.0, 1.0);
        for (var i = 0; i < count; i++)
        {
            var measured = Math.Clamp(bandDb[i], _floorDb, _ceilingDb);
            var decayed = _levels[i] - release;
            var level = measured > decayed ? measured : decayed;
            level = Math.Clamp(level, _floorDb, _ceilingDb);
            _levels[i] = level;
            levels[i] = SpectrumMath.Normalize(level, _floorDb, _ceilingDb);

            if (!_showPeaks)
            {
                _peaks[i] = double.NaN;
                peaks[i] = double.NaN;
                continue;
            }

            var peak = _peaks[i];
            if (double.IsNaN(peak) || level >= peak)
            {
                peak = level;
                _holdRemaining[i] = _peakHoldSeconds;
            }
            else
            {
                _holdRemaining[i] -= dt;
                if (_holdRemaining[i] <= 0.0)
                {
                    peak = Math.Max(level, peak - peakFall);
                }
            }

            peak = Math.Clamp(peak, _floorDb, _ceilingDb);
            _peaks[i] = peak;
            peaks[i] = SpectrumMath.Normalize(peak, _floorDb, _ceilingDb);
        }
    }
}
