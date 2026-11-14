using EchoGarage.Core.Models;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Data;
using EchoGarage.Infrastructure.Library;
using EchoGarage.Infrastructure.Logging;
using EchoGarage.Infrastructure.Tagging;

namespace EchoGarage.Tests;

/// <summary>
/// External files (outside every library folder) are only indexed when they are added to a
/// playlist. Nothing else re-reads their tags, so the app must be able to refresh them itself.
/// These tests cover the data path used by <c>MainViewModel.RefreshStaleTracksAsync</c>.
/// </summary>
public sealed class PlaylistMetadataRefreshTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly AppPaths _paths;
    private readonly SqliteLibraryRepository _library;
    private readonly SqlitePlaylistRepository _playlists;
    private readonly TagLibTagService _tags;
    private readonly LibraryScanner _scanner;

    public PlaylistMetadataRefreshTests()
    {
        _paths = new AppPaths(_workspace.Combine("state"));
        _paths.EnsureCreated();
        var logger = new FileLogger(_paths);
        var database = new SqliteDatabase(_paths, logger);
        _library = new SqliteLibraryRepository(database, logger);
        _playlists = new SqlitePlaylistRepository(database, logger);
        _tags = new TagLibTagService(logger);
        _scanner = new LibraryScanner(_library, _tags, logger);
        _library.InitializeAsync().GetAwaiter().GetResult();
        _playlists.EnsureDefaultsAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task ExternalPlaylistTrack_PicksUpEditedTags_AfterReindex()
    {
        var path = _workspace.Combine("outside", "song.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TestAudio.CreateWav(path);

        var indexed = await _scanner.IndexFileAsync(path, inLibrary: false);
        Assert.NotNull(indexed);
        Assert.False(indexed!.IsInLibrary);

        var playlist = await _playlists.CreatePlaylistAsync("外部歌曲", PlaylistKind.Normal, null);
        await _playlists.AddPlaylistItemsAsync(playlist.Id, [indexed]);

        // The row was written when the file had no tags: the cached title is the file name.
        var before = Assert.Single(await _playlists.GetPlaylistTracksAsync(playlist));
        Assert.Equal("song", before.DisplayTitle);

        // Edit the tags on disk the way an external tag editor would.
        _tags.Write(path, new TagEditModel
        {
            Title = "新しいタイトル",
            Artist = "新しいアーティスト",
            Album = "新しいアルバム"
        });

        var info = new FileInfo(path);
        Assert.True(Core.Services.TrackRefresh.NeedsRefresh(before, info.Length, info.LastWriteTimeUtc));

        // This is exactly what RefreshStaleTracksAsync does for stale rows.
        await _scanner.IndexFileAsync(path, inLibrary: false);

        var after = Assert.Single(await _playlists.GetPlaylistTracksAsync(playlist));
        Assert.Equal("新しいタイトル", after.DisplayTitle);
        Assert.Equal("新しいアーティスト", after.DisplayArtist);
        Assert.Equal("新しいアルバム", after.DisplayAlbum);
    }

    public void Dispose() => _workspace.Dispose();
}
