using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Caching;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Logging;
using EchoGarage.Infrastructure.Tagging;

namespace EchoGarage.Tests;

public sealed class TagAndCoverTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly TagLibTagService _tags;

    public TagAndCoverTests()
    {
        var paths = new AppPaths(_workspace.Combine("state"));
        paths.EnsureCreated();
        _tags = new TagLibTagService(new FileLogger(paths));
    }

    [Fact]
    public void WriteThenRead_RoundTripsAllEditableFields()
    {
        var path = TestAudio.CreateWav(_workspace.Combine("media", "tagged.wav"));
        var edit = new TagEditModel
        {
            Title = "标题 A",
            Artist = "艺术家 A",
            Album = "专辑 A",
            AlbumArtist = "专辑艺术家 A",
            Genre = "电子",
            Comment = "备注",
            Year = 2024,
            TrackNumber = 7,
            DiscNumber = 2,
            NewCoverBytes = TestAudio.CoverBytes(),
            NewCoverMime = "image/png"
        };

        _tags.Write(path, edit);
        var stored = _tags.Read(path);

        Assert.Equal("标题 A", stored.Title);
        Assert.Equal("艺术家 A", stored.Artist);
        Assert.Equal("专辑 A", stored.Album);
        Assert.Equal("专辑艺术家 A", stored.AlbumArtist);
        Assert.Equal("电子", stored.Genre);
        Assert.Equal("备注", stored.Comment);
        Assert.Equal(2024, stored.Year);
        Assert.Equal(7, stored.TrackNumber);
        Assert.Equal(2, stored.DiscNumber);
        Assert.NotNull(stored.CoverBytes);
        Assert.Equal("WAV", stored.Codec);
    }

    [Fact]
    public void ReadWithoutCover_StillReturnsTagsAndDuration()
    {
        var path = TestAudio.CreateWav(_workspace.Combine("media", "scan.wav"), seconds: 2);
        _tags.Write(path, new TagEditModel
        {
            Title = "扫描标题",
            Artist = "扫描艺术家",
            Album = "扫描专辑"
        });

        var tag = _tags.Read(path, includeCover: false);

        Assert.Equal("扫描标题", tag.Title);
        Assert.Equal("扫描艺术家", tag.Artist);
        Assert.Equal("扫描专辑", tag.Album);
        Assert.InRange(tag.DurationMs, 1800, 2200);
        Assert.Null(tag.CoverBytes);
    }

    [Fact]
    public void Write_CanReplaceAndRemoveCover()
    {
        var path = TestAudio.CreateWav(_workspace.Combine("media", "cover.wav"));
        _tags.Write(path, new TagEditModel { Title = "t", NewCoverBytes = TestAudio.CoverBytes(), NewCoverMime = "image/png" });
        Assert.NotNull(_tags.Read(path).CoverBytes);

        _tags.Write(path, new TagEditModel { Title = "t", RemoveCover = true });
        Assert.Null(_tags.Read(path).CoverBytes);
    }

    [Fact]
    public void EditingTwice_PreservesFilePlayability()
    {
        var path = TestAudio.CreateWav(_workspace.Combine("media", "twice.wav"), seconds: 1);
        _tags.Write(path, new TagEditModel { Title = "one" });
        _tags.Write(path, new TagEditModel { Title = "two", Artist = "artist" });

        var stored = _tags.Read(path);
        Assert.Equal("two", stored.Title);
        Assert.InRange(stored.DurationMs, 800, 1200);
    }

    [Fact]
    public async Task CoverCache_ExtractsCoverToDisk_AndCachesNegativeResults()
    {
        var paths = new AppPaths(_workspace.Combine("state"));
        paths.EnsureCreated();
        var logger = new FileLogger(paths);
        var cache = new FileCoverCache(paths.DefaultCacheDirectory, logger);

        var withCover = TestAudio.CreateWav(_workspace.Combine("media", "with.wav"));
        _tags.Write(withCover, new TagEditModel { Title = "x", NewCoverBytes = TestAudio.CoverBytes(), NewCoverMime = "image/png" });
        var info = new FileInfo(withCover);

        var key = CoverKeyFactory.ForTrack(withCover, "album", "artist");
        var resolved = await cache.EnsureAsync(new CoverRequest(withCover, key, info.LastWriteTimeUtc, info.Length));

        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved));
        Assert.Equal(resolved, cache.Resolve(key));

        var withoutCover = TestAudio.CreateWav(_workspace.Combine("media", "without.wav"));
        var info2 = new FileInfo(withoutCover);
        var key2 = CoverKeyFactory.ForTrack(withoutCover, "album2", "artist");
        var missing = await cache.EnsureAsync(new CoverRequest(withoutCover, key2, info2.LastWriteTimeUtc, info2.Length));

        Assert.Null(missing);
        Assert.True(File.Exists(Path.Combine(cache.CoversDirectory, key2 + ".none")));
    }

    [Fact]
    public async Task CoverCache_InvalidatesWhenSourceFileChanges()
    {
        var paths = new AppPaths(_workspace.Combine("state"));
        paths.EnsureCreated();
        var logger = new FileLogger(paths);
        var cache = new FileCoverCache(paths.DefaultCacheDirectory, logger);

        var track = TestAudio.CreateWav(_workspace.Combine("media", "changing.wav"));
        _tags.Write(track, new TagEditModel { Title = "a", NewCoverBytes = TestAudio.CoverBytes(), NewCoverMime = "image/png" });
        // Empty album => per-file cache key, which is stamped by that file's own mtime/size.
        var key = CoverKeyFactory.ForTrack(track, string.Empty, "artist");
        var info = new FileInfo(track);
        var first = await cache.EnsureAsync(new CoverRequest(track, key, info.LastWriteTimeUtc, info.Length));
        Assert.NotNull(first);

        // Removing the embedded cover must invalidate the previously cached image.
        _tags.Write(track, new TagEditModel { Title = "b", RemoveCover = true });
        var info2 = new FileInfo(track);
        var second = await cache.EnsureAsync(new CoverRequest(track, key, info2.LastWriteTimeUtc, info2.Length));

        Assert.Null(second);
        Assert.Null(cache.Resolve(key));
        Assert.True(File.Exists(Path.Combine(cache.CoversDirectory, key + ".none")));
    }

    [Fact]
    public async Task CoverCache_InvalidateRemovesAlbumCover()
    {
        var paths = new AppPaths(_workspace.Combine("state"));
        paths.EnsureCreated();
        var logger = new FileLogger(paths);
        var cache = new FileCoverCache(paths.DefaultCacheDirectory, logger);

        var track = TestAudio.CreateWav(_workspace.Combine("media", "invalidate.wav"));
        _tags.Write(track, new TagEditModel { Title = "x", NewCoverBytes = TestAudio.CoverBytes(), NewCoverMime = "image/png" });
        var info = new FileInfo(track);
        var key = CoverKeyFactory.ForTrack(track, "album", "artist");
        Assert.NotNull(await cache.EnsureAsync(new CoverRequest(track, key, info.LastWriteTimeUtc, info.Length)));

        cache.Invalidate(key);

        Assert.Null(cache.Resolve(key));
        Assert.False(File.Exists(Path.Combine(cache.CoversDirectory, key + ".stamp")));
    }

    [Fact]
    public async Task CoverCache_AlbumKey_SurvivesDifferentTrackStamps()
    {
        var paths = new AppPaths(_workspace.Combine("state"));
        paths.EnsureCreated();
        var logger = new FileLogger(paths);
        var cache = new FileCoverCache(paths.DefaultCacheDirectory, logger);

        var folder = _workspace.Combine("media", "album");
        var first = TestAudio.CreateWav(Path.Combine(folder, "01.wav"));
        var second = TestAudio.CreateWav(Path.Combine(folder, "02.wav"));
        _tags.Write(second, new TagEditModel { Title = "x", NewCoverBytes = TestAudio.CoverBytes(), NewCoverMime = "image/png" });

        var key = CoverKeyFactory.ForTrack(first, "Same Album", "Same Artist");
        Assert.Equal(key, CoverKeyFactory.ForTrack(second, "Same Album", "Same Artist"));

        var firstInfo = new FileInfo(first);
        var secondInfo = new FileInfo(second);

        // First track has no cover: a negative marker is cached.
        Assert.Null(await cache.EnsureAsync(new CoverRequest(first, key, firstInfo.LastWriteTimeUtc, firstInfo.Length)));

        // Second track of the same album has the cover: it must replace the negative marker.
        var image = await cache.EnsureAsync(new CoverRequest(second, key, secondInfo.LastWriteTimeUtc, secondInfo.Length));
        Assert.NotNull(image);

        // Asking again with the first track's stamp must not delete the shared album cover.
        var again = await cache.EnsureAsync(new CoverRequest(first, key, firstInfo.LastWriteTimeUtc, firstInfo.Length));
        Assert.NotNull(again);
        Assert.True(File.Exists(image));
    }

    [Fact]
    public async Task CoverCache_RelocateMovesFiles()
    {
        var paths = new AppPaths(_workspace.Combine("state"));
        paths.EnsureCreated();
        var logger = new FileLogger(paths);
        var cache = new FileCoverCache(_workspace.Combine("cache-a"), logger);
        var track = TestAudio.CreateWav(_workspace.Combine("media", "relocate.wav"));
        _tags.Write(track, new TagEditModel { Title = "x", NewCoverBytes = TestAudio.CoverBytes(), NewCoverMime = "image/png" });
        var info = new FileInfo(track);
        var key = CoverKeyFactory.ForTrack(track, "album", "artist");
        await cache.EnsureAsync(new CoverRequest(track, key, info.LastWriteTimeUtc, info.Length));

        await cache.RelocateAsync(_workspace.Combine("cache-b"));

        Assert.NotNull(cache.Resolve(key));
        Assert.StartsWith(_workspace.Combine("cache-b"), cache.CoversDirectory, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _workspace.Dispose();
}
