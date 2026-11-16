using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

/// <summary>
/// ADR-015 §6 / ADR-015-A2 的纯函数测试：分位拉伸、静音、包络、份额量化、设备像素列聚合。
/// </summary>
public sealed class WaveformMathTests
{
    // ---- 分位拉伸 / 高度（§6.6、ADR-015-A2）----

    [Fact]
    public void StretchWindow_ConstantSignal_ExtendsFloorToMinimumRange()
    {
        // 恒定电平素材：p5 == p99.5。窗口必须向下扩展（而不是抬高上限），否则高度全为 0。
        var (floor, ceiling) = WaveformMath.StretchWindow([-70.0, -70.0, -70.0]);

        Assert.Equal(-70.0, ceiling, 6);
        Assert.Equal(WaveformMath.MinimumStretchDb, ceiling - floor, 6);
        Assert.Equal(WaveformMath.SilenceDb, floor, 6);
        // 所有列落在窗口上限 → 满高，波形可见（旧实现为 0）。
        Assert.Equal(255, WaveformMath.StretchHeight(RmsForDecibel(-70.0), floor, ceiling));
    }

    [Fact]
    public void StretchWindow_EmptyInput_ReturnsSilenceWindow()
    {
        var (floor, ceiling) = WaveformMath.StretchWindow([]);

        Assert.Equal(WaveformMath.SilenceDb, floor, 6);
        Assert.True(ceiling - floor >= WaveformMath.MinimumStretchDb);
    }

    [Fact]
    public void StretchWindow_UsesPercentilesNotExtremes()
    {
        // 100 列：1 列很轻、98 列在 -30 dB 附近、1 列很响。p5 / p99.5 应忽略极端值。
        var decibels = Enumerable.Repeat(-30.0, 100).ToArray();
        decibels[0] = -90.0;
        decibels[99] = -3.0;

        var (floor, ceiling) = WaveformMath.StretchWindow(decibels);

        Assert.True(ceiling < -3.0, $"p99.5 不应取到单个最响列 {ceiling}");
        // 实测 p5 = -30、p99.5 ≈ -16.365（范围 13.6 dB < 30）→ 下界向下扩展到 ceiling - 30。
        Assert.True(floor < -31.0, $"窄动态素材的下界应向下扩展，实际 {floor}");
        Assert.Equal(WaveformMath.MinimumStretchDb, ceiling - floor, 6);
    }

    [Fact]
    public void StretchWindow_NarrowDynamicRange_ProducesVisibleHeights()
    {
        // 窄动态素材（±0.5 dB）：旧实现归一化后高度≈0；新窗口下应接近满高且仍保留层次。
        var decibels = Enumerable.Range(0, 100)
            .Select(index => -70.0 + (index % 2 == 0 ? 0.0 : 0.5))
            .ToArray();

        var (floor, ceiling) = WaveformMath.StretchWindow(decibels);
        var heights = WaveformMath.BuildHeights(
            [RmsForDecibel(-70.0), RmsForDecibel(-69.5)], floor, ceiling);

        Assert.Equal(WaveformMath.MinimumStretchDb, ceiling - floor, 6);
        Assert.All(heights, height => Assert.True(height > 200, $"窄动态素材应显示为高柱，实际 {height}"));
        Assert.True(heights[0] < heights[1], "窄动态素材内部仍应保留可见的高度差");
    }

    [Fact]
    public void StretchWindow_WideDynamicRange_KeepsPercentileWindow()
    {
        // 101 列，−95…−5 dB 线性分布：p5 = −90.5、p99.5 = −5.45，范围 85.05 dB ≥ 30 dB，
        // 窗口必须与旧实现一致（不被放宽调整）。
        var decibels = Enumerable.Range(0, 101).Select(index => -95.0 + 0.9 * index).ToArray();

        var (floor, ceiling) = WaveformMath.StretchWindow(decibels);

        Assert.Equal(-90.5, floor, 6);
        Assert.Equal(-5.45, ceiling, 6);
        Assert.Equal(85.05, ceiling - floor, 6);
    }

    [Fact]
    public void StretchWindow_VeryQuietNarrowSignal_ClampsFloorToSilenceDb()
    {
        // 极端安静且窄动态：理论下界 −129 dB 会被钳制到 SilenceDb，窗口仍非退化且可见。
        var (floor, ceiling) = WaveformMath.StretchWindow([-99.0, -99.0, -99.0]);

        Assert.Equal(WaveformMath.SilenceDb, floor, 6);
        Assert.Equal(-99.0, ceiling, 6);
        Assert.True(ceiling > floor);
        Assert.Equal(255, WaveformMath.StretchHeight(RmsForDecibel(-99.0), floor, ceiling));
    }

    [Fact]
    public void CollectNonSilentDecibels_ExcludesSilentColumns()
    {
        double[] columnRms = [0.0, 0.1, 0.0, 0.2, 1e-7];

        var decibels = WaveformMath.CollectNonSilentDecibels(columnRms);

        Assert.Equal(2, decibels.Length);
        Assert.All(decibels, value => Assert.True(value > -50.0, $"静音列不应参与分位：{value}"));
    }

    [Fact]
    public void StretchHeight_SilentColumnIsZero()
    {
        Assert.Equal(0, WaveformMath.StretchHeight(0.0, -40.0, -20.0));
        Assert.Equal(0, WaveformMath.StretchHeight(WaveformMath.SilenceThreshold * 0.5, -40.0, -20.0));
    }

    [Fact]
    public void StretchHeight_ClampsToWindow()
    {
        Assert.Equal(0, WaveformMath.StretchHeight(1e-4, -40.0, -20.0));   // -80 dB，低于下限
        Assert.Equal(255, WaveformMath.StretchHeight(1.0, -40.0, -20.0));  // 0 dB，高于上限
        Assert.Equal(128, WaveformMath.StretchHeight(0.0316227766016838, -40.0, -20.0)); // -30 dB，窗口中点
    }

    [Fact]
    public void StretchHeight_NonZeroButRoundsToZeroBecomesOne()
    {
        // dB 只比下限高 0.01 dB → 归一化后四舍五入为 0，但不应丢掉这一列（ADR-015 §6.6）。
        var rms = Math.Pow(10.0, (WaveformMath.SilenceDb + 0.01) / 20.0);

        var height = WaveformMath.StretchHeight(
            rms, WaveformMath.SilenceDb, WaveformMath.SilenceDb + WaveformMath.MinimumStretchDb);

        Assert.Equal(1, height);
    }

    [Fact]
    public void BuildHeights_ConstantSignal_DoesNotDivideByZero()
    {
        // 防御性场景：窗口上下限退化（floor == ceiling）时不能产生 NaN，全部当 0 处理。
        var heights = WaveformMath.BuildHeights([0.1, 0.1, 0.1], -30.0, -30.0);

        Assert.All(heights, height => Assert.Equal(0, height));
    }

    [Fact]
    public void Decibel_SilenceIsMinusHundred()
    {
        Assert.Equal(-100.0, WaveformMath.Decibel(0.0), 6);
        Assert.Equal(-100.0, WaveformMath.Decibel(1e-6), 6);
        Assert.Equal(0.0, WaveformMath.Decibel(1.0), 6);
    }

    [Fact]
    public void StreamingWindow_IsFixedAtZeroDbfsCeiling()
    {
        // 分析过程中的归一化窗口锁在 −48…0 dB：上限是 0 dBFS（电平最大值），不会随内容变化。
        var (floor, ceiling) = WaveformMath.StreamingWindow();

        Assert.Equal(-48.0, floor);
        Assert.Equal(0.0, ceiling);
    }

    // ---- 份额量化（§6.7）----

    [Fact]
    public void QuantizeShares_EqualPostExponentWeights_AreEvenlySplit()
    {
        // RMS 都为 1 时 q = 1^幂次 = 1，三项等权 → 精确三等分。
        var shares = WaveformMath.QuantizeShares(1.0, 1.0, 1.0);

        Assert.Equal(85, shares.Low);
        Assert.Equal(85, shares.Mid);
        Assert.Equal(85, shares.High);
    }

    [Fact]
    public void QuantizeShares_EqualRms_LowerExponentGetsLargerShare()
    {
        // ADR §6.7 的幂次（0.8/1.1/1.4）会让“三频段 RMS 相同”不再三等分：
        // 指数越小 q 越大，所以 low > mid > high。§12.1 的“三等分”只在等 q（等权）时成立。
        var shares = WaveformMath.QuantizeShares(0.5, 0.5, 0.5);

        Assert.True(shares.Low > shares.Mid);
        Assert.True(shares.Mid > shares.High);
        Assert.Equal(255, shares.Low + shares.Mid + shares.High);
    }

    [Fact]
    public void QuantizeShares_AllBandsZero_ReturnsNeutralShares()
    {
        var shares = WaveformMath.QuantizeShares(0.0, 0.0, 0.0);

        Assert.Equal((WaveformMath.SilentShare, WaveformMath.SilentShare, WaveformMath.SilentShare), shares);
    }

    [Fact]
    public void QuantizeShares_ZeroBand_NeverProducesNegativeShare()
    {
        var shares = WaveformMath.QuantizeShares(0.0, 0.8, 0.1);

        Assert.Equal(0, shares.Low);
        Assert.Equal(WaveformMath.MaxHeight, shares.Low + shares.Mid + shares.High);
        Assert.True(shares.Mid > 0);
    }

    [Theory]
    [InlineData(1.0, 0.0, 0.0)]
    [InlineData(0.0, 1.0, 0.0)]
    [InlineData(0.0, 0.0, 1.0)]
    [InlineData(1e-9, 1e-9, 1e-9)]
    [InlineData(1e9, 1.0, 1.0)]
    public void QuantizeShares_AlwaysSumsTo255(double low, double mid, double high)
    {
        var shares = WaveformMath.QuantizeShares(low, mid, high);

        Assert.Equal(WaveformMath.MaxHeight, shares.Low + shares.Mid + shares.High);
    }

    [Fact]
    public void QuantizeShares_RandomInputs_AlwaysSumTo255()
    {
        var random = new Random(20261114);
        for (var i = 0; i < 500; i++)
        {
            var low = random.NextDouble() * random.Next(0, 1000);
            var mid = random.NextDouble() * random.Next(0, 1000);
            var high = random.NextDouble() * random.Next(0, 1000);

            var shares = WaveformMath.QuantizeShares(low, mid, high);

            Assert.Equal(WaveformMath.MaxHeight, shares.Low + shares.Mid + shares.High);
            Assert.InRange(shares.Low, 0, 255);
            Assert.InRange(shares.Mid, 0, 255);
            Assert.InRange(shares.High, 0, 255);
        }
    }

    [Fact]
    public void QuantizeShares_HighExponent_SuppressesQuietHighBand()
    {
        // 同样的 RMS 下，high 的幂次（1.4）比 low（0.8）大，份额更集中。
        var shares = WaveformMath.QuantizeShares(0.1, 0.1, 0.1);
        Assert.True(shares.Low > shares.Mid);
        Assert.True(shares.Mid > shares.High);
        Assert.Equal(255, shares.Low + shares.Mid + shares.High);

        var uneven = WaveformMath.QuantizeShares(0.5, 0.1, 0.001);
        Assert.True(uneven.Low > uneven.Mid);
        Assert.True(uneven.Mid > uneven.High);
    }

    // ---- 包络（§6.5）----

    [Fact]
    public void Envelope_AttackIsFasterThanRelease()
    {
        double rising = 0.0;
        double falling = 1.0;
        for (var i = 0; i < 5; i++)
        {
            rising = WaveformMath.UpdateEnvelope(rising, 1.0);
            falling = WaveformMath.UpdateEnvelope(falling, 0.0);
        }

        Assert.True(rising > 1.0 - falling, $"attack 应比 release 快：rising={rising} falling={falling}");
    }

    [Fact]
    public void Envelope_ReachesSteadyStateWithinOnePercent()
    {
        var envelope = 0.0;
        for (var i = 0; i < 500; i++)
        {
            envelope = WaveformMath.UpdateEnvelope(envelope, 1.0);
        }

        Assert.True(Math.Abs(1.0 - envelope) < 0.01, $"稳态误差过大：{envelope}");
    }

    [Fact]
    public void Envelope_MatchesTimeConstants()
    {
        // attack 60 ms 后应到达约 1 - 1/e = 63.2%；10 ms 帧率下 6 帧。
        var attack = 0.0;
        for (var i = 0; i < 6; i++)
        {
            attack = WaveformMath.UpdateEnvelope(attack, 1.0);
        }

        Assert.InRange(attack, 0.58, 0.68);

        // release 250 ms 后剩余约 1/e = 36.8%；25 帧。
        var release = 1.0;
        for (var i = 0; i < 25; i++)
        {
            release = WaveformMath.UpdateEnvelope(release, 0.0);
        }

        Assert.InRange(release, 0.30, 0.44);
    }

    // ---- 设备像素列聚合（§9.4）----

    [Fact]
    public void AggregateHeight_UsesP80OfSegment()
    {
        byte[] heights = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        Assert.Equal(8, WaveformMath.AggregateHeight(heights, 0, 10));
        Assert.Equal(4, WaveformMath.AggregateHeight(heights, 0, 5)); // 1..5 的 p80 = 4
    }

    [Fact]
    public void AggregateHeight_SingleColumnSegment_ReturnsThatColumn()
    {
        byte[] heights = [10, 200, 30];

        Assert.Equal(200, WaveformMath.AggregateHeight(heights, 1, 2));
    }

    [Fact]
    public void AggregateHeight_EmptySegment_FallsBackToNearestColumn()
    {
        byte[] heights = [10, 20, 30];

        Assert.Equal(30, WaveformMath.AggregateHeight(heights, 2, 2));
        Assert.Equal(10, WaveformMath.AggregateHeight(heights, 0, 0));
        Assert.Equal(30, WaveformMath.AggregateHeight(heights, heights.Length + 5, heights.Length + 9));
    }

    [Fact]
    public void AggregateHeight_EmptyInput_ReturnsZero()
    {
        Assert.Equal(0, WaveformMath.AggregateHeight([], 0, 4));
    }

    [Fact]
    public void AggregateShares_WeightsByHeightPower()
    {
        // 只有第一列有高度，份额应完全来自它（255/255 归一到 255）。
        byte[] heights = [255, 0];
        byte[] low = [10, 255];
        byte[] mid = [200, 0];
        byte[] high = [45, 0];

        var shares = WaveformMath.AggregateShares(heights, low, mid, high, 0, 2);

        Assert.Equal(10, shares.Low);
        Assert.Equal(200, shares.Mid);
        Assert.Equal(45, shares.High);
    }

    [Fact]
    public void AggregateShares_NormalizesTo255()
    {
        // 两列等高但份额相反：加权平均后 low 与 high 接近 50/50，且严格相加为 255。
        byte[] heights = [255, 255];
        byte[] low = [255, 0];
        byte[] mid = [0, 0];
        byte[] high = [0, 255];

        var shares = WaveformMath.AggregateShares(heights, low, mid, high, 0, 2);

        Assert.Equal(WaveformMath.MaxHeight, shares.Low + shares.Mid + shares.High);
        Assert.InRange(shares.Low, 120, 135);
        Assert.InRange(shares.High, 120, 135);
        Assert.Equal(0, shares.Mid);
    }

    [Fact]
    public void AggregateShares_AllZeroHeights_ReturnsNeutralShares()
    {
        byte[] heights = [0, 0, 0];
        byte[] shares = [1, 2, 3];

        var result = WaveformMath.AggregateShares(heights, shares, shares, shares, 0, 3);

        Assert.Equal(
            (WaveformMath.SilentShare, WaveformMath.SilentShare, WaveformMath.SilentShare),
            result);
    }

    [Fact]
    public void AggregateShares_EmptySegment_FallsBackToNearestColumn()
    {
        byte[] heights = [255];
        byte[] low = [10];
        byte[] mid = [200];
        byte[] high = [45];

        var shares = WaveformMath.AggregateShares(heights, low, mid, high, 1, 1);

        Assert.Equal((10, 200, 45), shares);
    }

    [Fact]
    public void AggregateShares_ShortArrays_UseCommonLength()
    {
        byte[] heights = [255, 255, 255];
        byte[] low = [30];
        byte[] mid = [200];
        byte[] high = [25];

        var shares = WaveformMath.AggregateShares(heights, low, mid, high, 0, 3);

        Assert.Equal(30, shares.Low);
        Assert.Equal(200, shares.Mid);
        Assert.Equal(25, shares.High);
    }

    // ---- 参数表 / 派生量（§6.8）----

    [Fact]
    public void HopSize_IsTenMilliseconds()
    {
        Assert.Equal(441, WaveformMath.HopSize(44100));
        Assert.Equal(480, WaveformMath.HopSize(48000));
        Assert.Equal(80, WaveformMath.HopSize(8000)); // 8000 Hz × 10 ms = 80
    }

    [Fact]
    public void ColumnCountForFrames_PacksTwoFramesPerColumn()
    {
        Assert.Equal(0, WaveformMath.ColumnCountForFrames(0));
        Assert.Equal(1, WaveformMath.ColumnCountForFrames(1));
        Assert.Equal(1, WaveformMath.ColumnCountForFrames(2));
        Assert.Equal(2, WaveformMath.ColumnCountForFrames(3));
        Assert.Equal(50, WaveformMath.ColumnCountForFrames(100));
    }

    [Fact]
    public void HannWindow_HasExactMeanSquareOfThreeEighths()
    {
        var window = WaveformMath.CreateHannWindow();

        Assert.Equal(WaveformMath.FftSize, window.Length);
        var meanSquare = window.Sum(value => value * value) / window.Length;
        Assert.Equal(WaveformMath.HannMeanSquare, meanSquare, 6);
    }

    /// <summary>把 dB 值换算回线性 RMS，便于直接验证 StretchHeight 的映射。</summary>
    private static double RmsForDecibel(double decibels) => Math.Pow(10.0, decibels / 20.0);
}
