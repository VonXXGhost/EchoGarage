using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class WebTagTests
{
    private static Track Local(string title, int track = 0, int disc = 0, long durationMs = 0) => new()
    {
        Path = $"C:\\demo\\{title}.flac",
        Title = title,
        TrackNumber = track,
        DiscNumber = disc,
        DurationMs = durationMs
    };

    private static WebTagTrack Remote(string title, int track = 0, int disc = 0,
        string? composer = null, long? durationMs = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [WebTagFieldKeys.Title] = title,
            [WebTagFieldKeys.Artist] = "Demo Artist",
            [WebTagFieldKeys.Track] = track.ToString(),
            [WebTagFieldKeys.Disc] = disc.ToString()
        };
        if (composer is not null)
        {
            fields[WebTagFieldKeys.Composer] = composer;
        }

        return new WebTagTrack
        {
            TrackNumber = track,
            DiscNumber = disc,
            DurationMs = durationMs,
            Fields = fields
        };
    }

    private static WebTagRelease Release(params WebTagTrack[] tracks) => new()
    {
        SourceId = WebTagSourceIds.AppleMusicJp,
        ReleaseId = "0000000001",
        Title = "Demo Album A",
        Artist = "Demo Artist",
        AlbumFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [WebTagFieldKeys.Album] = "Demo Album A",
            [WebTagFieldKeys.AlbumArtist] = "Demo Artist",
            [WebTagFieldKeys.Year] = "2024",
            [WebTagFieldKeys.Genre] = "Demo Genre",
            [WebTagFieldKeys.Cover] = "https://example.invalid/{w}x{h}bb.{f}"
        },
        Tracks = tracks
    };

    [Fact]
    public void Matcher_MatchesPartialAlbumByDiscAndTrackNumber()
    {
        var remoteTracks = Enumerable.Range(1, 10)
            .Select(number => Remote($"Demo Track {number:00}", number))
            .ToArray();
        var locals = new[] { Local("Demo Track 01", 1), Local("Demo Track 03", 3), Local("Demo Track 07", 7) };

        var matches = WebTagMatcher.Match(locals, Release(remoteTracks));

        Assert.Equal(3, matches.Count);
        Assert.All(matches, match => Assert.Equal(WebTagMatchKind.TrackNumber, match.Kind));
        Assert.Equal([1, 3, 7], matches.Select(match => match.RemoteTrack!.TrackNumber));
    }

    [Fact]
    public void Matcher_DoesNotMatchByOrderOrCount()
    {
        var locals = new[] { Local("Local A"), Local("Local B") };
        var remoteTracks = Enumerable.Range(1, 10)
            .Select(number => Remote($"Demo Track {number:00}", number))
            .ToArray();

        var matches = WebTagMatcher.Match(locals, Release(remoteTracks));

        Assert.All(matches, match => Assert.False(match.IsMatched));
        Assert.All(matches, match => Assert.Equal(WebTagMatchKind.None, match.Kind));
    }

    [Fact]
    public void Matcher_FallsBackToUniqueNormalizedTitle()
    {
        var remote = Remote("Demo Track 02", 2);
        var local = Local("  DEMO   track 02 ", track: 99);

        var matches = WebTagMatcher.Match([local], Release(remote));

        var match = Assert.Single(matches);
        Assert.Equal(WebTagMatchKind.Title, match.Kind);
        Assert.Same(remote, match.RemoteTrack);
    }

    [Fact]
    public void Matcher_UsesDiscNumberToDisambiguateDuplicateTrackNumbers()
    {
        var remoteDisc1 = Remote("Demo Track 01", 1, 1);
        var remoteDisc2 = Remote("Demo Track 01", 1, 2);
        var local = Local("Demo Track 01", 1, 2);

        var matches = WebTagMatcher.Match([local], Release(remoteDisc1, remoteDisc2));

        var match = Assert.Single(matches);
        Assert.Same(remoteDisc2, match.RemoteTrack);
    }

    [Fact]
    public void Planner_WritesAlbumFieldsToAllAndTrackFieldsOnlyToMatched()
    {
        var local1 = Local("Local 1", 1);
        var local2 = Local("Local 2", 2);
        var remote1 = Remote("Remote 1", 1, composer: "Demo Composer A");
        var remote2 = Remote("Remote 2", 2);
        var matches = new[]
        {
            new WebTagMatch(local1, remote1, WebTagMatchKind.TrackNumber),
            new WebTagMatch(local2, null, WebTagMatchKind.None)
        };
        var album = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { WebTagFieldKeys.Album, WebTagFieldKeys.AlbumArtist, WebTagFieldKeys.Year, WebTagFieldKeys.Genre };
        var track = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { WebTagFieldKeys.Title, WebTagFieldKeys.Artist, WebTagFieldKeys.Composer, WebTagFieldKeys.Track };

        var plan = WebTagApplyPlanner.BuildPlan(matches, Release(remote1, remote2), album, track,
            includeCover: false, cover: null);

        Assert.Equal(2, plan.Edits.Count);
        var first = plan.Edits.First(edit => ReferenceEquals(edit.Track, local1)).Edit;
        Assert.Equal("Demo Album A", first.Fields[TagFieldKeys.Album]);
        Assert.Equal("2024", first.Fields[TagFieldKeys.Year]);
        Assert.Equal("Remote 1", first.Fields[TagFieldKeys.Title]);
        Assert.Equal("Demo Composer A", first.Fields[TagFieldKeys.Composer]);
        Assert.Equal("1", first.Fields[TagFieldKeys.TrackNumber]);

        var second = plan.Edits.First(edit => ReferenceEquals(edit.Track, local2)).Edit;
        Assert.Equal("Demo Album A", second.Fields[TagFieldKeys.Album]);
        Assert.False(second.Fields.ContainsKey(TagFieldKeys.Title));
        Assert.False(second.Fields.ContainsKey(TagFieldKeys.Composer));
    }

    [Fact]
    public void Planner_SkipsEmptyAndInvalidValuesAndWritesCoverToAll()
    {
        var local1 = Local("Local 1", 1);
        var local2 = Local("Local 2", 2);
        var remote = Remote("Remote 1", 1);
        remote.Fields[WebTagFieldKeys.Composer] = "   ";
        var release = Release(remote);
        release.AlbumFields[WebTagFieldKeys.Year] = "not-a-year";
        release.AlbumFields[WebTagFieldKeys.Genre] = "  ";
        var matches = new[]
        {
            new WebTagMatch(local1, remote, WebTagMatchKind.TrackNumber),
            new WebTagMatch(local2, null, WebTagMatchKind.None)
        };
        var album = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { WebTagFieldKeys.Album, WebTagFieldKeys.Year, WebTagFieldKeys.Genre };
        var track = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { WebTagFieldKeys.Title, WebTagFieldKeys.Composer };
        var cover = new WebTagCover([1, 2, 3], "image/jpeg");

        var plan = WebTagApplyPlanner.BuildPlan(matches, release, album, track, includeCover: true, cover);

        Assert.Equal(2, plan.Edits.Count);
        Assert.All(plan.Edits, edit =>
        {
            Assert.True(edit.Edit.CoverChanged);
            Assert.Equal("image/jpeg", edit.Edit.NewCoverMime);
            Assert.Equal([1, 2, 3], edit.Edit.NewCoverBytes);
            Assert.False(edit.Edit.Fields.ContainsKey(TagFieldKeys.Year));
            Assert.False(edit.Edit.Fields.ContainsKey(TagFieldKeys.Genre));
        });
        Assert.False(plan.Edits[0].Edit.Fields.ContainsKey(TagFieldKeys.Composer));
    }

    [Fact]
    public void Planner_OmitsFilesWithNoChanges()
    {
        var local = Local("Local", 1);
        var matches = new[] { new WebTagMatch(local, null, WebTagMatchKind.None) };
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var plan = WebTagApplyPlanner.BuildPlan(matches, Release(), empty, empty, includeCover: false, cover: null);

        Assert.Empty(plan.Edits);
    }

    [Fact]
    public void Settings_NormalizesOldAndInvalidValues()
    {
        var settings = new WebTagSettings
        {
            EnabledSourceIds = ["unknown-provider"],
            DefaultSourceId = "unknown-provider",
            SelectedFields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [WebTagSourceIds.AppleMusicJp] = ["album", "not-a-field", "album"],
                ["unknown-provider"] = ["album"]
            }
        };

        var normalized = WebTagSettings.Normalize(settings);

        Assert.Equal([WebTagSourceIds.AppleMusicJp], normalized.EnabledSourceIds);
        Assert.Equal(WebTagSourceIds.AppleMusicJp, normalized.DefaultSourceId);
        Assert.Equal([WebTagFieldKeys.Album], normalized.SelectedFields[WebTagSourceIds.AppleMusicJp]);
        Assert.False(normalized.SelectedFields.ContainsKey("unknown-provider"));
    }

    [Fact]
    public void Settings_KeepsExplicitEmptyFieldSelection()
    {
        var settings = new WebTagSettings();
        settings.SetSelectedFields(WebTagSourceIds.AppleMusicJp, []);

        var normalized = WebTagSettings.Normalize(settings);

        Assert.Empty(normalized.GetSelectedFields(WebTagSourceIds.AppleMusicJp));
    }

    [Fact]
    public void NetworkSettings_InvalidCustomProxyFallsBackToSystem()
    {
        var settings = new NetworkSettings
        {
            ProxyMode = WebProxyMode.Custom,
            CustomProxyUrl = "socks5://127.0.0.1:7890"
        };

        var normalized = NetworkSettings.Normalize(settings);

        Assert.Equal(WebProxyMode.System, normalized.ProxyMode);
        Assert.Equal(string.Empty, normalized.CustomProxyUrl);
    }

    [Fact]
    public void NetworkSettings_AcceptsHttpProxy()
    {
        var settings = new NetworkSettings
        {
            ProxyMode = WebProxyMode.Custom,
            CustomProxyUrl = "http://127.0.0.1:7890/"
        };

        var normalized = NetworkSettings.Normalize(settings);

        Assert.Equal(WebProxyMode.Custom, normalized.ProxyMode);
        Assert.Equal("http://127.0.0.1:7890", normalized.CustomProxyUrl);
        Assert.Equal(new Uri("http://127.0.0.1:7890"), normalized.GetProxyUri());
    }
}
