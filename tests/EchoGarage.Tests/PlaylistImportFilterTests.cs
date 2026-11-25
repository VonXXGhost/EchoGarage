using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class PlaylistImportFilterTests
{
    [Fact]
    public void FilterNewAudioPaths_SkipsExistingPaths_AcrossCaseSeparatorsAndRelativeSegments()
    {
        var existing = new[]
        {
            @"C:\Demo\AlbumA\Demo Track 01.flac"
        };

        var candidates = new[]
        {
            @"c:\demo\albuma\demo track 01.FLAC",
            @"C:/Demo/AlbumA/Demo Track 01.flac",
            @"C:\Demo\AlbumA\Sub\..\Demo Track 01.flac",
            @"C:\Demo\AlbumA\Demo Track 02.flac"
        };

        var result = PlaylistImportFilter.FilterNewAudioPaths(candidates, existing);

        Assert.Equal([@"C:\Demo\AlbumA\Demo Track 02.flac"], result.PathsToAdd);
        // 同一已存在文件的多种路径写法在本批次内去重后只计 1 次忽略。
        Assert.Equal(1, result.SkippedExistingCount);
    }

    [Fact]
    public void FilterNewAudioPaths_AllowsSameFileNameInDifferentDirectories()
    {
        var existing = new[]
        {
            @"C:\Demo\AlbumA\01.flac"
        };

        var candidates = new[]
        {
            @"C:\Demo\AlbumB\01.flac"
        };

        var result = PlaylistImportFilter.FilterNewAudioPaths(candidates, existing);

        Assert.Equal([@"C:\Demo\AlbumB\01.flac"], result.PathsToAdd);
        Assert.Equal(0, result.SkippedExistingCount);
    }

    [Fact]
    public void FilterNewAudioPaths_DeduplicatesWithinBatch_PreservingFirstOccurrenceOrder()
    {
        var candidates = new[]
        {
            @"C:\Demo\AlbumA\02.flac",
            @"C:\Demo\AlbumA\01.flac",
            @"c:/demo/albuma/02.FLAC",
            @"C:\Demo\AlbumA\03.mp3"
        };

        var result = PlaylistImportFilter.FilterNewAudioPaths(candidates, []);

        Assert.Equal(
            [
                @"C:\Demo\AlbumA\02.flac",
                @"C:\Demo\AlbumA\01.flac",
                @"C:\Demo\AlbumA\03.mp3"
            ],
            result.PathsToAdd);
        Assert.Equal(0, result.SkippedExistingCount);
    }

    [Fact]
    public void FilterNewAudioPaths_IgnoresUnsupportedBlankAndInvalidPaths_AndNormalizesQuotes()
    {
        var existing = new[]
        {
            @"C:\Demo\AlbumA\existing.wav"
        };

        var candidates = new[]
        {
            "",
            "   ",
            @"C:\Demo\AlbumA\cover.jpg",
            @"C:\Demo\AlbumA\notes.txt",
            "\"C:\\Demo\\AlbumA\\existing.wav\"",
            "\"C:\\Demo\\AlbumA\\fresh.wav\""
        };

        var result = PlaylistImportFilter.FilterNewAudioPaths(candidates, existing);

        Assert.Equal([@"C:\Demo\AlbumA\fresh.wav"], result.PathsToAdd);
        Assert.Equal(1, result.SkippedExistingCount);
    }

    [Fact]
    public void FilterNewAudioPaths_AllExisting_ReturnsEmptyPathsWithAccurateSkippedCount()
    {
        var existing = new[]
        {
            @"C:/Demo/AlbumA/01.flac",
            @"C:\Demo\AlbumA\01.flac",
            @"C:\Demo\AlbumA\02.flac"
        };

        var candidates = new[]
        {
            @"C:\Demo\AlbumA\01.flac",
            @"c:\demo\albuma\02.FLAC"
        };

        var result = PlaylistImportFilter.FilterNewAudioPaths(candidates, existing);

        Assert.Empty(result.PathsToAdd);
        Assert.Equal(2, result.SkippedExistingCount);
    }

    [Fact]
    public void FilterNewTracks_SkipsExistingAndDuplicateTracksByNormalizedAbsolutePath()
    {
        var existing = new[]
        {
            @"C:\Demo\AlbumA\01.flac"
        };

        var tracks = new[]
        {
            new Track { Path = @"c:/demo/albuma/01.FLAC", Title = "DupExisting" },
            new Track { Path = @"C:\Demo\AlbumA\02.flac", Title = "FirstNew" },
            new Track { Path = @"C:\Demo\AlbumA\Sub\..\02.flac", Title = "DupInBatch" },
            new Track { Path = @"C:\Demo\AlbumB\01.flac", Title = "SameNameOtherDir" }
        };

        var filtered = PlaylistImportFilter.FilterNewTracks(tracks, existing);

        Assert.Equal(["FirstNew", "SameNameOtherDir"], filtered.Select(t => t.Title));
    }
}
