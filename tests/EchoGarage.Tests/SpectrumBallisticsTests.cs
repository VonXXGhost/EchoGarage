using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

/// <summary>ADR-023 §6.6 的弹道：attack 瞬时、release 按 dB/s、峰值保持后下落，全部由 dt 推进。</summary>
public sealed class SpectrumBallisticsTests
{
    private static SpectrumOptions CreateOptions(
        int bandCount = 4,
        double floorDb = -70,
        double release = 30,
        bool showPeaks = true,
        double peakHoldSeconds = 0.8,
        double peakFall = 30) =>
        SpectrumOptions.From(new SpectrumSettings
        {
            BandCount = bandCount,
            FftSize = 1024,
            MinFrequency = 40,
            MaxFrequency = 18000,
            FloorDb = floorDb,
            ReleaseDbPerSecond = release,
            ShowPeaks = showPeaks,
            PeakHoldMs = peakHoldSeconds * 1000,
            PeakFallDbPerSecond = peakFall,
        });

    [Fact]
    public void Update_AttackIsImmediate()
    {
        var ballistics = new SpectrumBallistics();
        ballistics.Configure(CreateOptions());
        var levels = new double[4];
        var peaks = new double[4];

        ballistics.Update([-20, -40, -60, -70], 1.0 / 60.0, levels, peaks);

        Assert.Equal(50.0 / 70.0, levels[0], 6);
        Assert.Equal(30.0 / 70.0, levels[1], 6);
        Assert.Equal(10.0 / 70.0, levels[2], 6);
        Assert.Equal(0.0, levels[3], 6);
    }

    [Fact]
    public void Update_ReleaseFallsByRateTimesDelta()
    {
        var ballistics = new SpectrumBallistics();
        ballistics.Configure(CreateOptions(release: 30, showPeaks: false));
        var levels = new double[4];
        var peaks = new double[4];

        ballistics.Update([-20, -20, -20, -20], 1.0 / 60.0, levels, peaks);
        ballistics.Update([-70, -70, -70, -70], 1.0, levels, peaks);

        // −20 dB 起、30 dB/s 落 1 秒 → −50 dB → (70 − 50) / 70。
        Assert.Equal(20.0 / 70.0, levels[0], 6);
    }

    [Fact]
    public void Update_ReleaseIsFrameRateIndependent()
    {
        var slow = new SpectrumBallistics();
        slow.Configure(CreateOptions(release: 30, showPeaks: false));
        var fast = new SpectrumBallistics();
        fast.Configure(CreateOptions(release: 30, showPeaks: false));
        var levelsSlow = new double[4];
        var levelsFast = new double[4];
        var peaks = new double[4];

        slow.Update([-20, -20, -20, -20], 0.1, levelsSlow, peaks);
        slow.Update([-70, -70, -70, -70], 1.0, levelsSlow, peaks);

        fast.Update([-20, -20, -20, -20], 0.1, levelsFast, peaks);
        for (var i = 0; i < 60; i++)
        {
            fast.Update([-70, -70, -70, -70], 1.0 / 60.0, levelsFast, peaks);
        }

        Assert.Equal(levelsSlow[0], levelsFast[0], 2);
    }

    [Fact]
    public void Update_MeasurementAboveCeilingIsClamped()
    {
        var ballistics = new SpectrumBallistics();
        ballistics.Configure(CreateOptions(showPeaks: false));
        var levels = new double[4];
        var peaks = new double[4];

        ballistics.Update([12, 12, 12, 12], 1.0 / 60.0, levels, peaks);

        Assert.Equal(1.0, levels[0], 6);
    }

    [Fact]
    public void Update_PeaksHoldThenFall()
    {
        var ballistics = new SpectrumBallistics();
        ballistics.Configure(CreateOptions(peakHoldSeconds: 0.5, peakFall: 30));
        var levels = new double[4];
        var peaks = new double[4];

        ballistics.Update([-20, -70, -70, -70], 1.0 / 60.0, levels, peaks);
        Assert.Equal(50.0 / 70.0, peaks[0], 6);

        // 保持期内保持在 −20 dB。
        ballistics.Update([-70, -70, -70, -70], 0.25, levels, peaks);
        Assert.Equal(50.0 / 70.0, peaks[0], 6);

        // 保持期过后按 30 dB/s 下落：−20 − 30×0.3 = −29 dB。
        ballistics.Update([-70, -70, -70, -70], 0.3, levels, peaks);
        Assert.Equal(41.0 / 70.0, peaks[0], 6);
    }

    [Fact]
    public void Update_PeaksAreNaNWhenHidden()
    {
        var ballistics = new SpectrumBallistics();
        ballistics.Configure(CreateOptions(showPeaks: false));
        var levels = new double[4];
        var peaks = new double[4];

        ballistics.Update([-20, -20, -20, -20], 1.0 / 60.0, levels, peaks);

        Assert.All(peaks, peak => Assert.True(double.IsNaN(peak)));
    }

    [Fact]
    public void Resize_DropsStateAndReturnsToFloor()
    {
        var ballistics = new SpectrumBallistics();
        ballistics.Configure(CreateOptions(bandCount: 4, showPeaks: false));
        var levels = new double[4];
        var peaks = new double[4];
        ballistics.Update([-20, -40, -60, -70], 1.0 / 60.0, levels, peaks);

        ballistics.Resize(8);
        Assert.Equal(8, ballistics.BandCount);

        var resizedLevels = new double[8];
        var resizedPeaks = new double[8];
        ballistics.Update([-200, -200, -200, -200, -200, -200, -200, -200], 1.0 / 60.0,
            resizedLevels, resizedPeaks);
        Assert.All(resizedLevels, level => Assert.Equal(0.0, level, 6));
    }
}
