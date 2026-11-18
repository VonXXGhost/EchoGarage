using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Data;
using EchoGarage.Infrastructure.Logging;
using EchoGarage.Infrastructure.Tagging;
using Microsoft.Data.Sqlite;

namespace EchoGarage.Tests;

public sealed class RepositoryTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly SqliteDatabase _database;
    private readonly SqliteLibraryRepository _library;
    private readonly SqlitePlaylistRepository _playlists;

    public RepositoryTests()
    {
        _paths = new AppPaths(_workspace.Combine("state"));
        _paths.EnsureCreated();
        _logger = new FileLogger(_paths);
        _database = new SqliteDatabase(_paths, _logger);
        _library = new SqliteLibraryRepository(_database, _logger);
        _playlists = new SqlitePlaylistRepository(_database, _logger);
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
    public async Task Database_MigratesVersion1ToLatest_AddsComposerAndInvalidatesDuration()
    {
        // Build a database that looks like the old (v1) schema, then let InitializeAsync
        // upgrade it. Old rows must keep working with an empty Composer value, and the
        // duration cache must be invalidated so the next scan re-reads the file tags.
        var paths = new AppPaths(_workspace.Combine("migrate"));
        paths.EnsureCreated();
        var options = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        await using (var connection = new SqliteConnection(options.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE tracks (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    FolderId INTEGER NULL,
                    Title TEXT NOT NULL DEFAULT '',
                    Album TEXT NOT NULL DEFAULT '',
                    AlbumArtist TEXT NOT NULL DEFAULT '',
                    TrackNumber INTEGER NOT NULL DEFAULT 0,
                    DiscNumber INTEGER NOT NULL DEFAULT 0,
                    DurationMs INTEGER NOT NULL DEFAULT 0);
                INSERT INTO tracks (Path, FolderId, Title, Album, AlbumArtist, DurationMs)
                VALUES ('old/one.wav', 1, 'Old', 'Album', 'Artist', 2000);
                PRAGMA user_version = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var logger = new FileLogger(paths);
        var database = new SqliteDatabase(paths, logger);
        await database.InitializeAsync();

        await using var upgraded = new SqliteConnection(options.ToString());
        await upgraded.OpenAsync();
        await using var columns = upgraded.CreateCommand();
        columns.CommandText = "PRAGMA table_info(tracks);";
        var names = new List<string>();
        await using (var reader = await columns.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(1));
            }
        }

        Assert.Contains("Composer", names);
        await using var versionCommand = upgraded.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt64(await versionCommand.ExecuteScalarAsync());
        Assert.Equal(4, version);

        await using var legacy = upgraded.CreateCommand();
        legacy.CommandText = "SELECT Composer, DurationMs FROM tracks WHERE Path = 'old/one.wav';";
        await using var legacyReader = await legacy.ExecuteReaderAsync();
        Assert.True(await legacyReader.ReadAsync());
        Assert.Equal(string.Empty, legacyReader.GetString(0));
        Assert.Equal(0, legacyReader.GetInt64(1));
    }

    [Fact]
    public async Task Database_MigratesVersion2ToLatest_AndKeepsComposer()
    {
        // A v2 library already has the Composer column but its cached value is empty because
        // the v2 migration did not force a rescan. The v3 migration invalidates DurationMs.
        var paths = new AppPaths(_workspace.Combine("migrate-v2"));
        paths.EnsureCreated();
        var options = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        await using (var connection = new SqliteConnection(options.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE tracks (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    FolderId INTEGER NULL,
                    Title TEXT NOT NULL DEFAULT '',
                    Album TEXT NOT NULL DEFAULT '',
                    AlbumArtist TEXT NOT NULL DEFAULT '',
                    Composer TEXT NOT NULL DEFAULT '',
                    TrackNumber INTEGER NOT NULL DEFAULT 0,
                    DiscNumber INTEGER NOT NULL DEFAULT 0,
                    DurationMs INTEGER NOT NULL DEFAULT 0);
                INSERT INTO tracks (Path, FolderId, Title, Album, AlbumArtist, Composer, DurationMs)
                VALUES ('old/two.wav', 1, 'Old', 'Album', 'Artist', 'Some Composer', 5000);
                PRAGMA user_version = 2;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var logger = new FileLogger(paths);
        var database = new SqliteDatabase(paths, logger);
        await database.InitializeAsync();

        await using var upgraded = new SqliteConnection(options.ToString());
        await upgraded.OpenAsync();
        await using var versionCommand = upgraded.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(4, Convert.ToInt64(await versionCommand.ExecuteScalarAsync()));

        await using var legacy = upgraded.CreateCommand();
        legacy.CommandText = "SELECT Composer, DurationMs FROM tracks WHERE Path = 'old/two.wav';";
        await using var legacyReader = await legacy.ExecuteReaderAsync();
        Assert.True(await legacyReader.ReadAsync());
        Assert.Equal("Some Composer", legacyReader.GetString(0));
        Assert.Equal(0, legacyReader.GetInt64(1));
    }

    [Fact]
    public async Task Database_MigratesVersion3ToVersion4_InvalidatesAppleRowsWithoutTrackNumber()
    {
        // Legacy Apple taggers (for example Nero AAC) stored track/disc numbers as freeform
        // ----:com.apple.iTunes:track boxes. Version 3 and older cached those rows with
        // TrackNumber = 0 and the incremental scan skips unchanged files, so the v4 migration
        // marks exactly the affected Apple-container rows as stale for the startup repair scan.
        var paths = new AppPaths(_workspace.Combine("migrate-v3"));
        paths.EnsureCreated();
        var options = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        await using (var connection = new SqliteConnection(options.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE tracks (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    FolderId INTEGER NULL,
                    IsMissing INTEGER NOT NULL DEFAULT 0,
                    Title TEXT NOT NULL DEFAULT '',
                    Album TEXT NOT NULL DEFAULT '',
                    AlbumArtist TEXT NOT NULL DEFAULT '',
                    TrackNumber INTEGER NOT NULL DEFAULT 0,
                    DiscNumber INTEGER NOT NULL DEFAULT 0,
                    DurationMs INTEGER NOT NULL DEFAULT 0);
                INSERT INTO tracks (Path, FolderId, TrackNumber, DurationMs) VALUES
                    ('media/legacy-a.m4a', 1, 0, 2000),
                    ('media/LEGACY-B.M4A', 1, 0, 3000),
                    ('media/legacy-c.mp4', 1, 0, 4000),
                    ('media/has-track.m4a', 1, 5, 5000),
                    ('media/plain.flac', 1, 0, 6000),
                    ('external/outside.m4a', NULL, 0, 7000);
                PRAGMA user_version = 3;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var logger = new FileLogger(paths);
        var database = new SqliteDatabase(paths, logger);
        await database.InitializeAsync();

        await using var upgraded = new SqliteConnection(options.ToString());
        await upgraded.OpenAsync();
        await using var versionCommand = upgraded.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(4, Convert.ToInt64(await versionCommand.ExecuteScalarAsync()));

        await using var rows = upgraded.CreateCommand();
        rows.CommandText = "SELECT Path, DurationMs FROM tracks ORDER BY Path;";
        var durations = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await rows.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                durations[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        // Apple containers without a cached track number are invalidated (case-insensitive
        // extension match), including playlist-only rows so ADR-018 activation refresh fixes them.
        Assert.Equal(0, durations["media/legacy-a.m4a"]);
        Assert.Equal(0, durations["media/LEGACY-B.M4A"]);
        Assert.Equal(0, durations["media/legacy-c.mp4"]);
        Assert.Equal(0, durations["external/outside.m4a"]);
        // Rows that already have a track number, or are not MP4 containers, stay untouched.
        Assert.Equal(5000, durations["media/has-track.m4a"]);
        Assert.Equal(6000, durations["media/plain.flac"]);
    }

    [Fact]
    public async Task Upsert_RoundTripsComposer()
    {
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var track = MakeTrack("composer.wav", folderId: folder.Id);
        track.Composer = "Composer A";
        await _library.UpsertTrackAsync(track);

        var stored = await _library.GetTrackByPathAsync(track.Path);
        Assert.NotNull(stored);
        Assert.Equal("Composer A", stored!.Composer);
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
    public async Task UpdatePlaylistOrder_PersistsNewOrder_AndKeepsDefaultFirst()
    {
        var first = await _playlists.CreatePlaylistAsync("List A", PlaylistKind.Normal, null);
        var second = await _playlists.CreatePlaylistAsync("List B", PlaylistKind.Smart, new SmartPlaylistDefinition());
        var third = await _playlists.CreatePlaylistAsync("List C", PlaylistKind.Normal, null);

        Assert.Equal(["默认播放列表", "List A", "List B", "List C"],
            (await _playlists.GetPlaylistsAsync()).Select(p => p.Name));

        // 逆序：C、B、A。默认播放列表也一并传入（与拖拽时保持一致），它必须被跳过且保持首位。
        var before = await _playlists.GetPlaylistsAsync();
        var defaultPlaylist = before.Single(p => p.IsDefault);
        await _playlists.UpdatePlaylistOrderAsync([defaultPlaylist.Id, third.Id, second.Id, first.Id]);

        var after = await _playlists.GetPlaylistsAsync();
        Assert.Equal(["默认播放列表", "List C", "List B", "List A"], after.Select(p => p.Name));
        Assert.True(after[0].IsDefault);
        Assert.Equal(0, after[0].SortOrder);
        Assert.Equal([1, 2, 3], after.Skip(1).Select(p => p.SortOrder));
    }

    [Fact]
    public async Task UpdatePlaylistOrder_OnlyAffectsNormalLists_AndEmptyListIsSafe()
    {
        var normal = await _playlists.CreatePlaylistAsync("List A", PlaylistKind.Normal, null);
        var smart = await _playlists.CreatePlaylistAsync("Smart", PlaylistKind.Smart, new SmartPlaylistDefinition());
        var playlistsBefore = await _playlists.GetPlaylistsAsync();

        // 空列表不报错，也不改动现有顺序。
        await _playlists.UpdatePlaylistOrderAsync([]);
        Assert.Equal(playlistsBefore.Select(p => p.Name), (await _playlists.GetPlaylistsAsync()).Select(p => p.Name));

        await _playlists.UpdatePlaylistOrderAsync([smart.Id, normal.Id]);
        var reordered = (await _playlists.GetPlaylistsAsync()).Where(p => !p.IsDefault).ToList();
        Assert.Equal([smart.Id, normal.Id], reordered.Select(p => p.Id));
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
    public async Task SmartPlaylist_DirectoryField_MatchesParentDirectoryOnly()
    {
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var inDir1 = MakeTrack(Path.Combine("DemoFolder", "one.wav"), folderId: folder.Id);
        var inDir2 = MakeTrack(Path.Combine("DemoFolder", "two.wav"), folderId: folder.Id);
        var onlyNameMatches = MakeTrack(Path.Combine("OtherFolder", "DemoFolder.wav"), folderId: folder.Id);
        var outside = MakeTrack("plain.wav", folderId: folder.Id);
        await _library.UpsertTracksAsync([inDir1, inDir2, onlyNameMatches, outside]);

        var smart = await _playlists.CreatePlaylistAsync("Dir", PlaylistKind.Smart, new SmartPlaylistDefinition
        {
            MatchMode = SmartMatchMode.All,
            Rules = [new SmartRule { Field = "directory", Operator = "contains", Value = "DemoFolder" }]
        });

        var tracks = await _playlists.GetPlaylistTracksAsync(smart);
        // directory 是 dirname(t.Path)：只有目录部分包含关键字才算命中，文件名包含关键字不算。
        Assert.Equal(2, tracks.Count());
        Assert.DoesNotContain(tracks, t => t.Path == onlyNameMatches.Path);
        Assert.DoesNotContain(tracks, t => t.Path == outside.Path);

        var expectedDirectory = Path.GetDirectoryName(inDir1.Path)!;
        await _playlists.UpdatePlaylistRulesAsync(smart.Id, new SmartPlaylistDefinition
        {
            MatchMode = SmartMatchMode.All,
            Rules = [new SmartRule { Field = "directory", Operator = "equals", Value = expectedDirectory }]
        });
        var updated = (await _playlists.GetPlaylistsAsync()).Single(p => p.Id == smart.Id);
        var byDirectory = await _playlists.GetPlaylistTracksAsync(updated);
        Assert.Equal(2, byDirectory.Count());
        Assert.Contains(byDirectory, t => t.Path == inDir1.Path);
        Assert.Contains(byDirectory, t => t.Path == inDir2.Path);
    }

    [Fact]
    public async Task SmartPlaylist_RegexRules_ExecuteAgainstLibrary()
    {
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var demo1 = MakeTrack("Demo Track 01.wav", folderId: folder.Id);
        var demo2 = MakeTrack("Demo Track 02.wav", folderId: folder.Id);
        var live = MakeTrack("Live At Somewhere.wav", folderId: folder.Id);
        await _library.UpsertTracksAsync([demo1, demo2, live]);

        var smart = await _playlists.CreatePlaylistAsync("Regex", PlaylistKind.Smart, new SmartPlaylistDefinition
        {
            MatchMode = SmartMatchMode.All,
            Rules = [new SmartRule { Field = "title", Operator = "regex", Value = @"^demo track \d{2}$" }]
        });

        var tracks = await _playlists.GetPlaylistTracksAsync(smart);
        // 注册的 regexp 忽略大小写，小写模式应命中 Demo Track 曲目。
        Assert.Equal([demo1.Path, demo2.Path], tracks.Select(t => t.Path));

        await _playlists.UpdatePlaylistRulesAsync(smart.Id, new SmartPlaylistDefinition
        {
            MatchMode = SmartMatchMode.All,
            Rules = [new SmartRule { Field = "title", Operator = "notRegex", Value = @"^demo track" }]
        });
        var updated = (await _playlists.GetPlaylistsAsync()).Single(p => p.Id == smart.Id);
        var remaining = await _playlists.GetPlaylistTracksAsync(updated);
        var only = Assert.Single(remaining);
        Assert.Equal(live.Path, only.Path);
    }

    [Fact]
    public async Task SmartPlaylist_InvalidRegexRule_DoesNotCrashQuery()
    {
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var first = MakeTrack("first.wav", folderId: folder.Id);
        var second = MakeTrack("second.wav", folderId: folder.Id);
        await _library.UpsertTracksAsync([first, second]);

        var smart = await _playlists.CreatePlaylistAsync("Broken", PlaylistKind.Smart, new SmartPlaylistDefinition
        {
            MatchMode = SmartMatchMode.All,
            Rules = [new SmartRule { Field = "title", Operator = "regex", Value = "(" }]
        });

        // 无法编译的正则被 BuildRule 跳过，查询退化为“无额外条件”，不能抛异常。
        var tracks = await _playlists.GetPlaylistTracksAsync(smart);
        Assert.Equal(2, tracks.Count());
    }

    [Fact]
    public async Task SqliteConnection_RegistersSmartQueryFunctions()
    {
        await using var connection = await _database.OpenConnectionAsync();

        async Task<object?> ScalarAsync(string sql, string name, object value)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
            return await command.ExecuteScalarAsync();
        }

        Assert.Equal(@"D:\Demo\Album", Convert.ToString(await ScalarAsync(
            "SELECT dirname(@path);", "@path", @"D:\Demo\Album\Track.flac")));
        // 文件直接位于盘符根时没有目录部分。
        Assert.Equal(string.Empty, Convert.ToString(await ScalarAsync(
            "SELECT dirname(@path);", "@path", @"D:\Track.flac")));
        Assert.Equal(string.Empty, Convert.ToString(await ScalarAsync(
            "SELECT dirname(@path);", "@path", string.Empty)));

        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(
            "SELECT 'Demo Track' REGEXP @pattern;", "@pattern", "^demo")));
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(
            "SELECT 'Demo Track' REGEXP @pattern;", "@pattern", "live")));
        // 非法正则在 SQL 函数层也要返回 false，不能让整条查询抛异常。
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(
            "SELECT 'Demo Track' REGEXP @pattern;", "@pattern", "(")));
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

    [Fact]
    public async Task UpdateTrackPath_MovesTrackRowAndPlaylistItems()
    {
        var sourceFolder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var targetFolder = await _library.AddFolderAsync(_workspace.Combine("dest"));
        var track = MakeTrack("moved.wav", folderId: sourceFolder.Id);
        await _library.UpsertTrackAsync(track);

        var playlist = await _playlists.CreatePlaylistAsync("Moves", PlaylistKind.Normal, null);
        await _playlists.AddPlaylistItemsAsync(playlist.Id, [track]);

        var newPath = _workspace.Combine("dest", "moved.wav");
        await _library.UpdateTrackPathAsync(track.Path, newPath, targetFolder.Id);

        Assert.Null(await _library.GetTrackByPathAsync(track.Path));
        var moved = await _library.GetTrackByPathAsync(newPath);
        Assert.NotNull(moved);
        Assert.Equal(targetFolder.Id, moved!.FolderId);
        Assert.True(moved.IsInLibrary);
        Assert.False(moved.IsMissing);

        var playlistTracks = await _playlists.GetPlaylistTracksAsync(playlist);
        Assert.Equal(newPath, Assert.Single(playlistTracks).Path);
    }

    [Fact]
    public async Task UpdateTrackPath_DeletesStaleRowAtNewPath()
    {
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var track = MakeTrack("dup.wav", folderId: folder.Id);
        await _library.UpsertTrackAsync(track);

        // 目标路径恰好已有一行缓存（移动前被扫描过），更新时应删除它而不是撞唯一约束。
        var stale = MakeTrack("dup.wav", folderId: folder.Id);
        stale.Path = _workspace.Combine("dest", "dup.wav");
        stale.Title = "stale";
        await _library.UpsertTrackAsync(stale);

        await _library.UpdateTrackPathAsync(track.Path, stale.Path, null);

        var moved = await _library.GetTrackByPathAsync(stale.Path);
        Assert.NotNull(moved);
        Assert.False(moved!.IsInLibrary);
        Assert.Equal("dup", moved.Title);
    }

    [Fact]
    public async Task RemovePlaylistItemsByPath_RemovesFromEveryNormalPlaylist_AndKeepsOtherItems()
    {
        // ADR-021-A1：删除到回收站后，同一首歌在所有普通播放列表里的条目都应移除，
        // 其它歌曲的条目保留；返回实际删除的行数。
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var deletedTrack = MakeTrack("deleted.wav", folderId: folder.Id);
        var keptTrack = MakeTrack("kept.wav", folderId: folder.Id);
        await _library.UpsertTracksAsync([deletedTrack, keptTrack]);

        var first = await _playlists.CreatePlaylistAsync("List A", PlaylistKind.Normal, null);
        var second = await _playlists.CreatePlaylistAsync("List B", PlaylistKind.Normal, null);
        await _playlists.AddPlaylistItemsAsync(first.Id, [deletedTrack, keptTrack]);
        await _playlists.AddPlaylistItemsAsync(second.Id, [keptTrack, deletedTrack]);

        var removed = await _playlists.RemovePlaylistItemsByPathAsync([deletedTrack.Path]);

        Assert.Equal(2, removed);
        Assert.Equal([keptTrack.Path], (await _playlists.GetPlaylistTracksAsync(first)).Select(t => t.Path));
        Assert.Equal([keptTrack.Path], (await _playlists.GetPlaylistTracksAsync(second)).Select(t => t.Path));
        var counts = await _playlists.GetPlaylistCountsAsync([first, second]);
        Assert.Equal(1, counts[first.Id]);
        Assert.Equal(1, counts[second.Id]);
    }

    [Fact]
    public async Task RemovePlaylistItemsByPath_UnknownPathOrEmptyList_ReturnsZero()
    {
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var track = MakeTrack("kept.wav", folderId: folder.Id);
        await _library.UpsertTrackAsync(track);
        var playlist = await _playlists.CreatePlaylistAsync("List A", PlaylistKind.Normal, null);
        await _playlists.AddPlaylistItemsAsync(playlist.Id, [track]);

        var unknown = _workspace.Combine("media", "not-in-any-list.wav");
        Assert.Equal(0, await _playlists.RemovePlaylistItemsByPathAsync([unknown]));
        Assert.Equal(0, await _playlists.RemovePlaylistItemsByPathAsync([]));
        Assert.Single(await _playlists.GetPlaylistTracksAsync(playlist));
    }

    [Fact]
    public async Task RemovePlaylistItemsByPath_MatchesDifferentPathCaseAndSeparators()
    {
        // playlist_items.TrackPath 是 COLLATE NOCASE；这里再覆盖 Windows 分隔符规范化。
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var uppercase = MakeTrack("case.wav", folderId: folder.Id);
        await _library.UpsertTrackAsync(uppercase);
        var playlist = await _playlists.CreatePlaylistAsync("List A", PlaylistKind.Normal, null);

        await _playlists.AddPlaylistItemsAsync(playlist.Id, [uppercase]);
        Assert.Equal(1, await _playlists.RemovePlaylistItemsByPathAsync([uppercase.Path.ToUpperInvariant()]));
        Assert.Empty(await _playlists.GetPlaylistTracksAsync(playlist));

        var mixedSeparators = MakeTrack("case.wav", folderId: folder.Id);
        await _playlists.AddPlaylistItemsAsync(playlist.Id, [mixedSeparators]);
        var forwardSlashes = mixedSeparators.Path.Replace('\\', '/');
        Assert.Equal(1, await _playlists.RemovePlaylistItemsByPathAsync([forwardSlashes]));
        Assert.Empty(await _playlists.GetPlaylistTracksAsync(playlist));
    }

    [Fact]
    public async Task GetTagSuggestionValues_ReturnsFrequentLibraryValuesPerField()
    {
        // 候选项按媒体库出现频率降序；TRIM 把前后空格不同的缓存值合并。
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var a1 = MakeTrack("s1.wav", "A", folderId: folder.Id);
        var a2 = MakeTrack("s2.wav", "A", folderId: folder.Id);
        var a3 = MakeTrack("s3.wav", "A ", folderId: folder.Id);
        var b1 = MakeTrack("s4.wav", "B", folderId: folder.Id);
        var b2 = MakeTrack("s5.wav", "B", folderId: folder.Id);
        var c = MakeTrack("s6.wav", "C", folderId: folder.Id);
        a1.Composer = "Composer X";
        a2.Composer = "Composer X";
        a3.Composer = "Composer Y";
        b1.Composer = "Composer Z";
        b2.Composer = "Composer Z";
        c.Composer = "Composer W";
        a1.AlbumArtist = "Album Artist";
        a2.AlbumArtist = "Album Artist";
        a3.AlbumArtist = "Album Artist";
        b1.AlbumArtist = "Album Artist";
        b2.AlbumArtist = "Album Artist";
        c.AlbumArtist = "Album Artist";
        a1.Genre = "Rock";
        a2.Genre = "Rock";
        a3.Genre = "Jazz";
        b1.Genre = "Jazz";
        b2.Genre = "Pop";
        c.Genre = "Pop";
        await _library.UpsertTracksAsync([a1, a2, a3, b1, b2, c]);

        var values = await _library.GetTagSuggestionValuesAsync();

        Assert.Equal(["A", "B", "C"], values.Get(TagFieldKeys.Artist));
        Assert.Equal(["Album Artist"], values.Get(TagFieldKeys.AlbumArtist));
        Assert.Equal(["Composer X", "Composer Z", "Composer W", "Composer Y"], values.Get(TagFieldKeys.Composer));
        // 三者频率相同（各 2 首），同频时按名称 NOCASE 排序。
        Assert.Equal(["Jazz", "Pop", "Rock"], values.Get(TagFieldKeys.Genre));
    }

    [Fact]
    public async Task GetTagSuggestionValues_ExcludesUnavailableAndBlankRows_AndRespectsLimit()
    {
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var keep = MakeTrack("keep.wav", "Keep", folderId: folder.Id);
        keep.Genre = "Rock";
        var notInLibrary = MakeTrack("external.wav", "External", inLibrary: false);
        notInLibrary.Genre = "Rock";
        var missing = MakeTrack("missing.wav", "Missing", folderId: folder.Id);
        missing.Genre = "Rock";
        var blank = MakeTrack("blank.wav", "", folderId: folder.Id);
        blank.Artist = "";
        blank.Genre = "   ";
        await _library.UpsertTracksAsync([keep, notInLibrary, missing, blank]);
        await _library.MarkTrackMissingAsync(missing.Path);

        // limitPerField = 1：每字段只返回频率最高的一条，证明 LIMIT 生效。
        var values = await _library.GetTagSuggestionValuesAsync(1);

        Assert.Equal(["Keep"], values.Get(TagFieldKeys.Artist));
        Assert.Equal(["Rock"], values.Get(TagFieldKeys.Genre));
    }

    [Fact]
    public async Task TagSuggestionService_CachesSnapshotUntilInvalidated()
    {
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        await _library.UpsertTracksAsync([MakeTrack("first.wav", "First Artist", folderId: folder.Id)]);

        var service = new TagSuggestionService(_library, _logger);
        var initial = await service.GetAsync();
        Assert.Contains("First Artist", initial.Get(TagFieldKeys.Artist));

        // 媒体库新增歌曲后，未失效前仍返回旧快照（避免每次补全都查库）。
        await _library.UpsertTracksAsync([MakeTrack("second.wav", "Second Artist", folderId: folder.Id)]);
        var cached = await service.GetAsync();
        Assert.DoesNotContain("Second Artist", cached.Get(TagFieldKeys.Artist));

        // 扫描/标签保存等流程会调用 Invalidate，下一次读取应重新查询。
        service.Invalidate();
        var refreshed = await service.GetAsync();
        Assert.Contains("Second Artist", refreshed.Get(TagFieldKeys.Artist));
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }
}
