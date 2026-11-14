using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class TrackSearchTests
{
    private static Track MakeTrack(
        string title = "Track",
        string artist = "",
        string album = "",
        string albumArtist = "",
        string composer = "",
        string genre = "",
        string comment = "",
        int? year = null,
        string path = @"D:\Music\Track.mp3")
    {
        return new Track
        {
            Path = path,
            Title = title,
            Artist = artist,
            Album = album,
            AlbumArtist = albumArtist,
            Composer = composer,
            Genre = genre,
            Comment = comment,
            Year = year,
            Codec = "MP3"
        };
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyQuery_MatchesEverything(string? query)
    {
        Assert.True(TrackSearch.Matches(MakeTrack(), query));
    }

    [Fact]
    public void InlineSearch_MatchesTitleArtistAlbumAndAlbumArtist()
    {
        var track = MakeTrack(
            title: "MUSIC of DREAM!!!",
            artist: "Kizuna AI",
            album: "future bass",
            albumArtist: "AIKATSU☆STARS!");

        Assert.True(TrackSearch.Matches(track, "music"));
        Assert.True(TrackSearch.Matches(track, "kizuna"));
        Assert.True(TrackSearch.Matches(track, "future bass"));
        Assert.True(TrackSearch.Matches(track, "aikatsu"));
    }

    [Fact]
    public void InlineSearch_IgnoresComposerGenreCommentAndPath()
    {
        var track = MakeTrack(
            title: "Summer Tears Diary",
            composer: "成田ハネダ",
            genre: "Soundtrack",
            comment: "live version",
            path: @"D:\Music\Anime\Summer Tears Diary.mp3");

        // These fields belong to the advanced search, not to the inline search box.
        Assert.False(TrackSearch.Matches(track, "成田"));
        Assert.False(TrackSearch.Matches(track, "soundtrack"));
        Assert.False(TrackSearch.Matches(track, "live version"));
        Assert.False(TrackSearch.Matches(track, "anime"));
        Assert.False(TrackSearch.Matches(track, "path:\\music\\"));
    }

    [Fact]
    public void InlineSearch_IsSubstringOnly()
    {
        var track = MakeTrack(title: "Bunkasai");

        Assert.True(TrackSearch.Matches(track, "bunk"));
        Assert.False(TrackSearch.Matches(track, "bnk"));
    }

    [Fact]
    public void InlineSearch_AllTermsMustMatch()
    {
        var track = MakeTrack(title: "future bass", artist: "Kizuna AI");

        Assert.True(TrackSearch.Matches(track, "kizuna future"));
        Assert.False(TrackSearch.Matches(track, "kizuna jazz"));
    }

    [Fact]
    public void InlineSearch_QuotedPhrase_KeepsSpaces()
    {
        var track = MakeTrack(artist: "Kizuna AI");

        Assert.True(TrackSearch.Matches(track, "\"kizuna ai\""));
        Assert.False(TrackSearch.Matches(track, "\"kizuna aii\""));
    }

    [Fact]
    public void AdvancedSearch_SingleField_RestrictsMatching()
    {
        var track = MakeTrack(title: "Kizuna", artist: "Other", composer: "成田ハネダ");

        Assert.True(TrackSearch.Matches(track, [SearchField.Composer], SearchFieldMode.Any, "成田"));
        Assert.False(TrackSearch.Matches(track, [SearchField.Composer], SearchFieldMode.Any, "kizuna"));
        Assert.True(TrackSearch.Matches(track, [SearchField.Title], SearchFieldMode.Any, "kizuna"));
    }

    [Fact]
    public void AdvancedSearch_AnyMode_MatchesAnySelectedField()
    {
        var track = MakeTrack(artist: "Kizuna AI", composer: "成田ハネダ");

        // The same keyword may be looked up in composer and artist at the same time.
        Assert.True(TrackSearch.Matches(
            track, [SearchField.Composer, SearchField.Artist], SearchFieldMode.Any, "kizuna"));
        Assert.True(TrackSearch.Matches(
            track, [SearchField.Composer, SearchField.Artist], SearchFieldMode.Any, "成田"));
        Assert.False(TrackSearch.Matches(
            track, [SearchField.Composer, SearchField.Artist], SearchFieldMode.Any, "album"));
    }

    [Fact]
    public void AdvancedSearch_AllMode_RequiresEverySelectedField()
    {
        var track = MakeTrack(title: "Kizuna AI", artist: "Kizuna AI feat. 成田");

        Assert.True(TrackSearch.Matches(
            track, [SearchField.Title, SearchField.Artist], SearchFieldMode.All, "kizuna"));
        Assert.False(TrackSearch.Matches(
            track, [SearchField.Title, SearchField.Artist], SearchFieldMode.All, "feat"));
    }

    [Fact]
    public void AdvancedSearch_CanMatchFileNameAndPath()
    {
        var track = MakeTrack(title: "", path: @"D:\Music\KizunaAI - まさる.m4a");

        Assert.True(TrackSearch.Matches(track, [SearchField.FileName], SearchFieldMode.Any, "まさる"));
        Assert.True(TrackSearch.Matches(track, [SearchField.Path], SearchFieldMode.Any, "\\music\\"));
    }

    [Fact]
    public void AdvancedSearch_CanMatchYearAndCodec()
    {
        var track = MakeTrack(year: 2019);

        Assert.True(TrackSearch.Matches(track, [SearchField.Year], SearchFieldMode.Any, "2019"));
        Assert.True(TrackSearch.Matches(track, [SearchField.Codec], SearchFieldMode.Any, "mp3"));
    }

    [Fact]
    public void AdvancedSearch_NoFields_MatchesNothing()
    {
        var track = MakeTrack(title: "Kizuna");

        Assert.False(TrackSearch.Matches(track, [], SearchFieldMode.Any, "kizuna"));
        Assert.False(TrackSearch.Matches(track, [], SearchFieldMode.All, "kizuna"));
    }

    [Fact]
    public void Tokenize_SplitsOnWhitespace_AndHonoursQuotes()
    {
        var tokens = TrackSearch.Tokenize("  kizuna   \"future bass\" composer:foo ");

        Assert.Equal(["kizuna", "future bass", "composer:foo"], tokens);
    }
}
