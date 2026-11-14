using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class CoverImageTests
{
    [Theory]
    [InlineData(2000, 1800, 1000, 1000, 900)]
    [InlineData(1800, 2000, 1000, 900, 1000)]
    [InlineData(3000, 2000, 1000, 1000, 667)]
    [InlineData(1000, 1000, 1000, 1000, 1000)]
    [InlineData(800, 600, 1000, 800, 600)]
    [InlineData(1001, 1001, 1000, 1000, 1000)]
    public void ScaleDown_PreservesAspectAndNeverUpscales(int width, int height, int maxEdge, int expectedWidth, int expectedHeight)
    {
        var (targetWidth, targetHeight) = CoverImageSizing.ScaleDown(width, height, maxEdge);

        Assert.Equal(expectedWidth, targetWidth);
        Assert.Equal(expectedHeight, targetHeight);
        Assert.True(Math.Max(targetWidth, targetHeight) <= maxEdge);
    }

    [Fact]
    public void ScaleDown_KeepsAspectRatioWithinOnePixel()
    {
        var (width, height) = CoverImageSizing.ScaleDown(1234, 987, 600);

        Assert.True(Math.Max(width, height) <= 600);
        var sourceRatio = 1234.0 / 987.0;
        var targetRatio = (double)width / height;
        Assert.True(Math.Abs(sourceRatio - targetRatio) < 0.01);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void ScaleDown_InvalidInput_ReturnsOriginal(int width, int height)
    {
        Assert.Equal((width, height), CoverImageSizing.ScaleDown(width, height, 1000));
    }

    [Fact]
    public void ScaleDown_NoLimit_ReturnsOriginal()
    {
        Assert.Equal((4000, 3000), CoverImageSizing.ScaleDown(4000, 3000, 0));
    }

    [Fact]
    public void Policy_Downscaling_AlwaysConverts()
    {
        Assert.True(CoverConversionPolicy.ShouldConvert(
            sourceIsJpeg: true, sourceWidth: 1200, sourceHeight: 1200, targetMaxEdge: 1000,
            originalSize: 100_000, convertedSize: 200_000));
    }

    [Fact]
    public void Policy_JpegSmallerThanTarget_IsKept()
    {
        Assert.False(CoverConversionPolicy.ShouldConvert(
            sourceIsJpeg: true, sourceWidth: 800, sourceHeight: 600, targetMaxEdge: 1000,
            originalSize: 100_000, convertedSize: 50_000));
    }

    [Fact]
    public void Policy_NonJpegWithoutScaling_ConvertsOnlyWhenSmaller()
    {
        Assert.True(CoverConversionPolicy.ShouldConvert(
            sourceIsJpeg: false, sourceWidth: 800, sourceHeight: 600, targetMaxEdge: 1000,
            originalSize: 200_000, convertedSize: 90_000));
        Assert.False(CoverConversionPolicy.ShouldConvert(
            sourceIsJpeg: false, sourceWidth: 800, sourceHeight: 600, targetMaxEdge: 1000,
            originalSize: 100_000, convertedSize: 120_000));
    }

    [Fact]
    public void Policy_JpegAtTargetResolution_ConvertsOnlyWhenSmaller()
    {
        Assert.True(CoverConversionPolicy.ShouldConvert(
            sourceIsJpeg: true, sourceWidth: 1000, sourceHeight: 1000, targetMaxEdge: 1000,
            originalSize: 100_000, convertedSize: 90_000));
        Assert.False(CoverConversionPolicy.ShouldConvert(
            sourceIsJpeg: true, sourceWidth: 1000, sourceHeight: 1000, targetMaxEdge: 1000,
            originalSize: 100_000, convertedSize: 110_000));
    }

    [Fact]
    public void Policy_NoLimit_KeepsJpegButConvertsSmallerNonJpeg()
    {
        Assert.False(CoverConversionPolicy.ShouldConvert(
            sourceIsJpeg: true, sourceWidth: 2000, sourceHeight: 2000, targetMaxEdge: 0,
            originalSize: 100_000, convertedSize: 50_000));
        Assert.True(CoverConversionPolicy.ShouldConvert(
            sourceIsJpeg: false, sourceWidth: 2000, sourceHeight: 2000, targetMaxEdge: 0,
            originalSize: 300_000, convertedSize: 50_000));
    }

    [Fact]
    public void Policy_FailedEncoding_DoesNotConvert()
    {
        Assert.False(CoverConversionPolicy.ShouldConvert(
            sourceIsJpeg: false, sourceWidth: 800, sourceHeight: 600, targetMaxEdge: 1000,
            originalSize: 100_000, convertedSize: 0));
    }
}
