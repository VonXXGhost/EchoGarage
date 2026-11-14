using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class TrackIndexFormatterTests
{
    [Fact]
    public void Format_WithoutDiscNumber_UsesTwoDigitTrackNumber()
    {
        Assert.Equal("05", TrackIndexFormatter.Format(0, 5));
    }

    [Fact]
    public void Format_WithDiscNumber_PrefixesDiscNumber()
    {
        Assert.Equal("1.05", TrackIndexFormatter.Format(1, 5));
        Assert.Equal("2.12", TrackIndexFormatter.Format(2, 12));
    }

    [Fact]
    public void Format_WithoutTrackNumber_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TrackIndexFormatter.Format(0, 0));
        Assert.Equal(string.Empty, TrackIndexFormatter.Format(1, 0));
        Assert.Equal(string.Empty, TrackIndexFormatter.Format(0, -1));
    }

    [Fact]
    public void Format_ThreeDigitTrackNumber_IsNotTruncated()
    {
        Assert.Equal("100", TrackIndexFormatter.Format(0, 100));
        Assert.Equal("3.100", TrackIndexFormatter.Format(3, 100));
    }
}
