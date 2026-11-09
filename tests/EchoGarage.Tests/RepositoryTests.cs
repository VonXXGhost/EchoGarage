using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Data;
using EchoGarage.Infrastructure.Logging;

namespace EchoGarage.Tests;

public sealed class RepositoryTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly AppPaths _paths;
    private readonly SqliteDatabase _database;
    private readonly SqliteLibraryRepository _library;
    private readonly SqlitePlaylistRepository _playlists;

    public RepositoryTests()
    {
        _paths = new AppPaths(_workspace.Combine("state"));
        _paths.EnsureCreated();
        var logger = new FileLogger(_paths);
        _database = new SqliteDatabase(_paths, logger);
        _library = new SqliteLibraryRepository(_database, logger);
        _playlists = new SqlitePlaylistRepository(_database, logger);
        _library.InitializeAsync().GetAwaiter().GetResult();
        _playlists.EnsureDefaultsAsync().GetAwaiter().GetResult();
    }

    private Track MakeTrack(string name, string artist = "Artist", string album = "Album", bool inLibrary = true,
        long folderId = 0, int trackNumber = 1)
    {
        var path = _workspace.Combine("media", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fake audio");
        return new Track
        {
            Path = path,
            FolderId = inLibrary ? folderId : null,
            IsInLibrary = inLibrary,
            Title = Path.GetFileNameWithoutExtension(path),
            Artist = artist,
            Album = album,
            AlbumArtist = artist,
            TrackNumber = trackNumber,
            DurationMs = 2000,
            FileSize = 10,
            ModifiedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            AddedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Codec = "WAV"
        };
    }

    [Fact]
    public async Task DefaultPlaylist_IsCreatedOnce()
    {
        await _playlists.EnsureDefaultsAsync();
        await _playlists.EnsureDefaultsAsync();

        var playlists = await _playlists.GetPlaylistsAsync();
        var defaults = playlists.Where(p => p.IsDefault).ToList();

        Assert.Single(defaults);
        Assert.Equal(PlaylistKind.Normal, defaults[0].Kind);
        Assert.Equal("默认播放列表", defaults[0].Name);
    }

    [Fact]
    public async Task LibraryFolder_AddIsIdempotent_AndCountsTracks()
    {
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var again = await _library.AddFolderAsync(_workspace.Combine("media") + "\\");

        Assert.Equal(folder.Id, again.Id);
        Assert.Single(await _library.GetFoldersAsync());

        await _library.UpsertTracksAsync([MakeTrack("a.wav", folderId: folder.Id), MakeTrack("b.wav", folderId: folder.Id)]);
        Assert.Equal(2, await _library.GetLibraryTrackCountAsync());
        var counts = await _library.GetFolderTrackCountsAsync();
        Assert.Equal(2, counts[folder.Id]);

        var tracks = await _library.GetLibraryTracksAsync();
        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, t => Assert.True(t.Id > 0));
    }

    [Fact]
    public async Task Upsert_UpdatesMetadata_AndPreservesAddedAt()
    {
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var track = MakeTrack("a.wav", folderId: folder.Id);
        await _library.UpsertTrackAsync(track);

        var updated = MakeTrack("a.wav", artist: "New Artist", folderId: folder.Id);
        updated.AddedAtUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _library.UpsertTrackAsync(updated);

        var stored = await _library.GetTrackByPathAsync(track.Path);
        Assert.NotNull(stored);
        Assert.Equal("New Artist", stored!.Artist);
        Assert.Equal(track.AddedAtUtc, stored.AddedAtUtc);
        Assert.Equal(1, await _library.GetLibraryTrackCountAsync());
    }

    [Fact]
    public async Task MissingTracks_AreExcludedFromLibraryQueries()
    {
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var track = MakeTrack("a.wav", folderId: folder.Id);
        await _library.UpsertTrackAsync(track);
        await _library.MarkTrackMissingAsync(track.Path);

        Assert.Equal(0, await _library.GetLibraryTrackCountAsync());
        var states = await _library.GetTrackStatesAsync(folder.Id);
        Assert.True(states.Single().IsMissing);

        await _library.UpsertTrackAsync(MakeTrack("a.wav", folderId: folder.Id));
        Assert.Equal(1, await _library.GetLibraryTrackCountAsync());
    }

    [Fact]
    public async Task Playlist_KeepsOrder_AndSupportsRemoval()
    {
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var a = MakeTrack("a.wav", folderId: folder.Id, trackNumber: 2);
        var b = MakeTrack("b.wav", folderId: folder.Id, trackNumber: 1);
        await _library.UpsertTracksAsync([a, b]);

        var playlist = await _playlists.CreatePlaylistAsync("My List", PlaylistKind.Normal, null);
        await _playlists.AddPlaylistItemsAsync(playlist.Id, [a, b]);

        var tracks = await _playlists.GetPlaylistTracksAsync(playlist);
        Assert.Equal([a.Path, b.Path], tracks.Select(t => t.Path));
        Assert.All(tracks, t => Assert.True(t.PlaylistItemId > 0));

        var counts = await _playlists.GetPlaylistCountsAsync([playlist]);
        Assert.Equal(2, counts[playlist.Id]);

        await _playlists.RemovePlaylistItemsAsync([tracks[0].PlaylistItemId]);
        tracks = await _playlists.GetPlaylistTracksAsync(playlist);
        Assert.Single(tracks);
        Assert.Equal(b.Path, tracks[0].Path);
    }

    [Fact]
    public async Task Playlist_ExternalFile_UsesSnapshotWhenTrackRowIsGone()
    {
        var playlist = await _playlists.CreatePlaylistAsync("External", PlaylistKind.Normal, null);
        var external = MakeTrack("outside.wav", inLibrary: false);
        external.Title = "外部歌曲";
        await _playlists.AddPlaylistItemsAsync(playlist.Id, [external]);

        var tracks = await _playlists.GetPlaylistTracksAsync(playlist);
        var track = Assert.Single(tracks);
        Assert.False(track.IsInLibrary);
        Assert.True(track.IsMissing);
        Assert.Equal("外部歌曲", track.Title);
        Assert.Equal(0, track.Id);
    }

    [Fact]
    public async Task SmartPlaylist_FiltersLibraryTracks()
    {
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var keep1 = MakeTrack("keep1.wav", artist: "Target Artist", folderId: folder.Id);
        var keep2 = MakeTrack("keep2.wav", artist: "target artist", folderId: folder.Id);
        var drop = MakeTrack("drop.wav", artist: "Other", folderId: folder.Id);
        await _library.UpsertTracksAsync([keep1, keep2, drop]);

        var smart = await _playlists.CreatePlaylistAsync("Smart", PlaylistKind.Smart, new SmartPlaylistDefinition
        {
            MatchMode = SmartMatchMode.All,
            Rules = [new SmartRule { Field = "artist", Operator = "contains", Value = "Target" }]
        });

        var tracks = await _playlists.GetPlaylistTracksAsync(smart);
        Assert.Equal(2, tracks.Count());
        Assert.DoesNotContain(tracks, t => t.Path == drop.Path);

        await _playlists.UpdatePlaylistRulesAsync(smart.Id, new SmartPlaylistDefinition
        {
            MatchMode = SmartMatchMode.Any,
            Rules = [new SmartRule { Field = "artist", Operator = "equals", Value = "Other" }]
        });
        var updated = (await _playlists.GetPlaylistsAsync()).Single(p => p.Id == smart.Id);
        var afterUpdate = await _playlists.GetPlaylistTracksAsync(updated);
        Assert.Single(afterUpdate);
        Assert.Equal(drop.Path, afterUpdate[0].Path);
    }

    [Fact]
    public async Task LayoutAndRules_PersistPerPlaylist()
    {
        var playlist = await _playlists.CreatePlaylistAsync("Persisted", PlaylistKind.Normal, null);
        var layout = ColumnCatalog.CreateLayout("自定义", ["cover", "title", "duration"]);
        layout.SortColumn = "Title";
        layout.SortDescending = true;
        layout.Columns.Single(c => c.Key == "title").Width = 333;

        await _playlists.UpdatePlaylistLayoutAsync(playlist.Id, ViewLayoutFactory.Serialize(layout));
        var reloaded = (await _playlists.GetPlaylistsAsync()).Single(p => p.Id == playlist.Id);
        var restored = ViewLayoutFactory.Deserialize(reloaded.ViewLayoutJson);

        Assert.Equal("Title", restored.SortColumn);
        Assert.Equal(333, restored.Columns.Single(c => c.Key == "title").Width, 3);
        Assert.False(restored.Columns.Single(c => c.Key == "artist").Visible);
    }

    [Fact]
    public async Task PlaybackCounter_IsRecorded()
    {
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var track = MakeTrack("counted.wav", folderId: folder.Id);
        await _library.UpsertTrackAsync(track);

        await _library.RecordPlaybackAsync(track.Path);
        await _library.RecordPlaybackAsync(track.Path);

        var stored = await _library.GetTrackByPathAsync(track.Path);
        Assert.Equal(2, stored!.PlayCount);
        Assert.NotNull(stored.LastPlayedAtUtc);
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }
}
