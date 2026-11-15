using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

/// <summary>ADR-023 §6 的纯函数口径：频段边界、dB 换算、斜率补偿、频率标签。</summary>
public sealed class SpectrumMathTests
{
    [Fact]
    public void CreateBandEdges_UsesGeometricSequence()
    {
        var edges = SpectrumMath.CreateBandEdges(50, 23000, 40);

        Assert.Equal(41, edges.Length);
        Assert.Equal(50, edges[0], 6);
        Assert.Equal(23000, edges[^1], 6);

        var ratio = Math.Pow(23000.0 / 50.0, 1.0 / 40.0);
        Assert.Equal(ratio, edges[1] / edges[0], 6);
        Assert.Equal(ratio, edges[20] / edges[19], 6);
        for (var i = 1; i < edges.Length; i++)
        {
            Assert.True(edges[i] > edges[i - 1], $"边界必须单调递增：index={i}");
        }
    }

    [Fact]
    public void CreateBandEdges_ClampsDegenerateRangeAndCount()
    {
        var edges = SpectrumMath.CreateBandEdges(0, 0, 1);

        Assert.Equal(2, edges.Length);
        Assert.Equal(1.0, edges[0], 6);
        Assert.True(edges[1] > edges[0]);
    }

    [Theory]
    [InlineData(-70, -70, 0, 0.0)]
    [InlineData(-70, 0, 0, 1.0)]
    [InlineData(-70, -35, 0, 0.5)]
    [InlineData(-70, -1000, 0, 0.0)]
    [InlineData(-70, 12, 0, 1.0)]
    public void Normalize_MapsDbIntoColumnHeight(double floorDb, double db, double ceilingDb, double expected)
    {
        Assert.Equal(expected, SpectrumMath.Normalize(db, floorDb, ceilingDb), 6);
    }

    [Fact]
    public void Normalize_ReturnsZeroForInvertedRange()
    {
        Assert.Equal(0.0, SpectrumMath.Normalize(-20, 0, -70), 6);
    }

    [Theory]
    [InlineData(1000, 3, 0)]
    [InlineData(2000, 3, 3)]
    [InlineData(4000, 3, 6)]
    [InlineData(500, 3, -3)]
    [InlineData(8000, 1.5, 4.5)]
    public void SlopeGainDb_FollowsOctaves(double frequency, double slope, double expected)
    {
        Assert.Equal(expected, SpectrumMath.SlopeGainDb(frequency, slope), 6);
    }

    [Fact]
    public void SlopeGainDb_IsZeroWhenDisabledOrFrequencyInvalid()
    {
        Assert.Equal(0.0, SpectrumMath.SlopeGainDb(18000, 0), 6);
        Assert.Equal(0.0, SpectrumMath.SlopeGainDb(0, 3), 6);
    }

    [Theory]
    [InlineData(50, "50")]
    [InlineData(999.4, "999")]
    [InlineData(1000, "1k")]
    [InlineData(1200, "1.2k")]
    [InlineData(18000, "18k")]
    [InlineData(0, "0")]
    public void FormatFrequencyLabel_MatchesReferenceStyle(double frequency, string expected)
    {
        Assert.Equal(expected, SpectrumMath.FormatFrequencyLabel(frequency));
    }

    [Fact]
    public void AmplitudeToDb_ConvertsAndFloorsSilence()
    {
        Assert.Equal(-6.0206, SpectrumMath.AmplitudeToDb(0.5), 3);
        Assert.Equal(0.0, SpectrumMath.AmplitudeToDb(1.0), 6);
        Assert.Equal(SpectrumMath.MinimumAmplitudeDb, SpectrumMath.AmplitudeToDb(0), 6);
        Assert.Equal(SpectrumMath.MinimumAmplitudeDb, SpectrumMath.AmplitudeToDb(-1), 6);
    }

    [Fact]
    public void BandCenter_IsGeometricMidpoint()
    {
        Assert.Equal(200.0, SpectrumMath.BandCenter(100, 400), 6);
        Assert.Equal(0.0, SpectrumMath.BandCenter(0, 400), 6);
    }

    [Fact]
    public void DecayDbPerFrame_IsRateTimesElapsed()
    {
        Assert.Equal(15.0, SpectrumMath.DecayDbPerFrame(30, 0.5), 6);
        Assert.Equal(30.0, SpectrumMath.DecayDbPerFrame(30, 5), 6); // dt 上限 1 秒
        Assert.Equal(0.0, SpectrumMath.DecayDbPerFrame(-30, 0.5), 6);
    }
}
