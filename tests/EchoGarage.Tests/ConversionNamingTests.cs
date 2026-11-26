using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class ConversionNamingTests
{
    [Fact]
    public void ExpandTemplate_ReplacesKnownTokens()
    {
        var track = MakeTrack();

        var result = ConversionNaming.ExpandTemplate("%artist% - %title% (%year%)", track);

        Assert.Equal("Artist - Song (2024)", result);
    }

    [Fact]
    public void ExpandTemplate_EmptyTokenRemovesSurroundingSeparator()
    {
        var track = MakeTrack();
        track.Album = string.Empty;

        var result = ConversionNaming.ExpandTemplate("%artist% - %album% - %title%", track);

        Assert.Equal("Artist - Song", result);
    }

    [Fact]
    public void ExpandTemplate_UnknownTokenIsKeptAsLiteral()
    {
        var track = MakeTrack();

        var result = ConversionNaming.ExpandTemplate("%artist% - %typo%", track);

        Assert.Equal("Artist - %typo%", result);
    }

    [Fact]
    public void BuildRelativePath_UsesFormatExtension()
    {
        var preset = new ConversionPreset { Format = ConversionFormat.Aac, FileNameTemplate = "%title%" };
        var track = MakeTrack();

        var result = ConversionNaming.BuildRelativePath(preset, track);

        Assert.Equal("Song.m4a", result);
    }

    [Fact]
    public void BuildRelativePath_SanitizesInvalidCharactersAndReservedNames()
    {
        var preset = new ConversionPreset { Format = ConversionFormat.Mp3, FileNameTemplate = "%title%" };
        var track = MakeTrack();
        track.Title = "a:b?c";

        Assert.Equal("a_b_c.mp3", ConversionNaming.BuildRelativePath(preset, track));

        track.Title = "CON";
        Assert.Equal("_CON.mp3", ConversionNaming.BuildRelativePath(preset, track));
    }

    [Fact]
    public void BuildRelativePath_MapsAsteriskToXInsteadOfUnderscore()
    {
        var preset = new ConversionPreset { Format = ConversionFormat.Mp3, FileNameTemplate = "%title%" };
        var track = MakeTrack();
        track.Title = "a*b";

        Assert.Equal("axb.mp3", ConversionNaming.BuildRelativePath(preset, track));
    }

    [Fact]
    public void BuildRelativePath_SupportsSubdirectories()
    {
        var preset = new ConversionPreset { Format = ConversionFormat.Mp3, FileNameTemplate = "%artist%/%title%" };
        var track = MakeTrack();

        var result = ConversionNaming.BuildRelativePath(preset, track);

        Assert.Equal(Path.Combine("Artist", "Song.mp3"), result);
    }

    [Fact]
    public void BuildRelativePath_FallsBackToSourceFileNameWhenTemplateEmpty()
    {
        var preset = new ConversionPreset { Format = ConversionFormat.Mp3, FileNameTemplate = "%album%" };
        var track = MakeTrack();
        track.Album = string.Empty;

        Assert.Equal("source.mp3", ConversionNaming.BuildRelativePath(preset, track));
    }

    [Fact]
    public void TryResolveOutputPath_SourceFolderUsesTrackDirectory()
    {
        var preset = new ConversionPreset { Format = ConversionFormat.Mp3, FileNameTemplate = "%artist% - %title%" };
        var track = MakeTrack();

        var result = ConversionNaming.TryResolveOutputPath(preset, track);

        Assert.Equal(Path.Combine("C:\\music", "Artist - Song.mp3"), result);
    }

    [Fact]
    public void TryResolveOutputPath_CustomFolderWithoutPathReturnsNull()
    {
        var preset = new ConversionPreset
        {
            Format = ConversionFormat.Mp3,
            OutputMode = ConversionOutputMode.CustomFolder,
            OutputFolder = "  "
        };

        Assert.Null(ConversionNaming.TryResolveOutputPath(preset, MakeTrack()));
    }

    [Fact]
    public void MakeUniquePath_AppendsCounterUntilFree()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "C:\\music\\Song.mp3", "C:\\music\\Song (2).mp3" };

        var result = ConversionNaming.MakeUniquePath("C:\\music\\Song.mp3", existing.Contains);

        Assert.Equal("C:\\music\\Song (3).mp3", result);
    }

    [Fact]
    public void PathsEqual_IsCaseInsensitive()
    {
        Assert.True(ConversionNaming.PathsEqual("C:\\Music\\A.mp3", "c:\\music\\a.mp3"));
        Assert.False(ConversionNaming.PathsEqual("C:\\music\\a.mp3", "C:\\music\\b.mp3"));
    }

    private static Track MakeTrack() => new()
    {
        Path = "C:\\music\\source.mp3",
        Title = "Song",
        Artist = "Artist",
        Album = "Album",
        AlbumArtist = "Album Artist",
        Composer = "Composer",
        Genre = "Rock",
        Year = 2024,
        TrackNumber = 3,
        DiscNumber = 1
    };
}
