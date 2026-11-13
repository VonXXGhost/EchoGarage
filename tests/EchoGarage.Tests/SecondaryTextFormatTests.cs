using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class SecondaryTextFormatTests
{
    private static readonly Dictionary<string, string?> SampleValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = "Song",
        ["artist"] = "Artist",
        ["genre"] = "Rock",
        ["composer"] = "Bach",
        ["bitrate"] = "402",
        ["sampleRate"] = "48000"
    };

    private static string Render(string format, IReadOnlyDictionary<string, string?>? values = null) =>
        SecondaryTextFormat.Format(format, values ?? SampleValues);

    [Fact]
    public void Format_RendersSingleToken()
    {
        Assert.Equal("Rock", Render("{genre}"));
    }

    [Fact]
    public void Format_CombinesTokensWithSeparator()
    {
        Assert.Equal("402K/48Khz", Render("{bitrate}/{sampleRate}", new Dictionary<string, string?>
        {
            ["bitrate"] = "402K",
            ["sampleRate"] = "48Khz"
        }));
    }

    [Fact]
    public void Format_DropsEmptyTokenTogetherWithItsSeparator()
    {
        var noBitrate = new Dictionary<string, string?> { ["sampleRate"] = "48K" };
        var noSampleRate = new Dictionary<string, string?> { ["bitrate"] = "402K" };

        Assert.Equal("48K", Render("{bitrate}/{sampleRate}", noBitrate));
        Assert.Equal("402K", Render("{bitrate}/{sampleRate}", noSampleRate));
    }

    [Fact]
    public void Format_ReturnsEmptyWhenEveryTokenIsEmpty()
    {
        var values = new Dictionary<string, string?>();

        Assert.Equal(string.Empty, Render("{bitrate}/{sampleRate}", values));
        Assert.Equal(string.Empty, Render("{genre} - {composer}", values));
        Assert.Equal(string.Empty, SecondaryTextFormat.Format(null, values));
        Assert.Equal(string.Empty, SecondaryTextFormat.Format("   ", values));
    }

    [Fact]
    public void Format_DropsSeparatorWhenTokenInTheMiddleIsEmpty()
    {
        var values = new Dictionary<string, string?> { ["genre"] = "Rock", ["composer"] = "Bach" };

        Assert.Equal("Rock - Bach", Render("{genre} - {composer}", values));
        Assert.Equal("Bach", Render("{genre} - {composer}", new Dictionary<string, string?> { ["composer"] = "Bach" }));
        Assert.Equal("Rock", Render("{genre} - {composer}", new Dictionary<string, string?> { ["genre"] = "Rock" }));
    }

    [Fact]
    public void Format_DropsUnknownTokens()
    {
        Assert.Equal("Artist", Render("{notAToken}/{artist}"));
    }

    [Fact]
    public void Format_SupportsEscapedBraces()
    {
        Assert.Equal("{genre} Rock", Render("{{genre}} {genre}"));
    }

    [Fact]
    public void Format_TrimsTokenValues()
    {
        Assert.Equal("Rock", Render(" {genre} ", new Dictionary<string, string?> { ["genre"] = "  Rock  " }));
    }

    [Fact]
    public void Normalize_TrimsBoundsAndNullsBlankFormats()
    {
        Assert.Null(SecondaryTextFormat.Normalize(null));
        Assert.Null(SecondaryTextFormat.Normalize("   "));
        Assert.Equal("{genre}", SecondaryTextFormat.Normalize("  {genre}  "));
        Assert.Equal("{a} {b}", SecondaryTextFormat.Normalize("{a}\r\n{b}"));

        var longFormat = new string('x', SecondaryTextFormat.MaxFormatLength + 50);
        Assert.Equal(SecondaryTextFormat.MaxFormatLength, SecondaryTextFormat.Normalize(longFormat)!.Length);
    }

    [Fact]
    public void DescribeTokens_ListsComposerAndBitrate()
    {
        var described = SecondaryTextFormat.DescribeTokens();

        Assert.Contains("{composer}", described);
        Assert.Contains("{bitrate}", described);
        Assert.Contains("{sampleRate}", described);
    }
}
