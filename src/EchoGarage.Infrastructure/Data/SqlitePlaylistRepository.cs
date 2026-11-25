using Dapper;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Infrastructure.Data;

public sealed class SqlitePlaylistRepository : IPlaylistRepository
{
    private const string PlaylistColumns = """
        Id, Name, Kind, IsDefault, SortOrder, SmartRulesJson, ViewLayoutJson, CreatedAtUtc, UpdatedAtUtc
        """;

    private const string SnapshotTrackColumns = """
        i.Id AS PlaylistItemId, i.TrackPath AS Path,
        CASE WHEN t.Id IS NULL THEN 0 ELSE t.IsInLibrary END AS IsInLibrary,
        CASE WHEN t.Id IS NULL THEN 1 ELSE t.IsMissing END AS IsMissing,
        COALESCE(NULLIF(t.Title, ''), i.TitleSnapshot) AS Title,
        COALESCE(NULLIF(t.Artist, ''), i.ArtistSnapshot) AS Artist,
        COALESCE(NULLIF(t.Album, ''), i.AlbumSnapshot) AS Album,
        COALESCE(t.AlbumArtist, '') AS AlbumArtist,
        COALESCE(t.Composer, '') AS Composer,
        COALESCE(t.Genre, '') AS Genre,
        COALESCE(t.Comment, '') AS Comment,
        t.Year AS Year,
        COALESCE(t.TrackNumber, 0) AS TrackNumber,
        COALESCE(t.DiscNumber, 0) AS DiscNumber,
        COALESCE(t.Rating, 0) AS Rating,
        CASE WHEN t.DurationMs IS NULL OR t.DurationMs = 0 THEN i.DurationMsSnapshot ELSE t.DurationMs END AS DurationMs,
        COALESCE(t.Bitrate, 0) AS Bitrate,
        COALESCE(t.SampleRate, 0) AS SampleRate,
        COALESCE(t.Channels, 0) AS Channels,
        COALESCE(t.Codec, '') AS Codec,
        COALESCE(t.FileSize, 0) AS FileSize,
        COALESCE(t.ModifiedAtUtc, '0001-01-01T00:00:00.0000000Z') AS ModifiedAtUtc,
        i.AddedAtUtc AS AddedAtUtc,
        t.LastPlayedAtUtc AS LastPlayedAtUtc,
        COALESCE(t.PlayCount, 0) AS PlayCount,
        COALESCE(t.CoverKey, i.CoverKeySnapshot) AS CoverKey
        """;

    private const string LibraryTrackColumns = """
        Id, Path, FolderId, IsInLibrary, IsMissing, Title, Artist, Album, AlbumArtist, Composer, Genre, Comment,
        Year, TrackNumber, DiscNumber, Rating, DurationMs, Bitrate, SampleRate, Channels, Codec, FileSize,
        ModifiedAtUtc, AddedAtUtc, LastPlayedAtUtc, PlayCount, CoverKey
        """;

    private readonly SqliteDatabase _database;
    private readonly IAppLogger _logger;

    public SqlitePlaylistRepository(SqliteDatabase database, IAppLogger logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task EnsureDefaultsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await EnsureDefaultPlaylistAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Playlist>(
            $"SELECT {PlaylistColumns} FROM playlists ORDER BY IsDefault DESC, SortOrder, Name COLLATE NOCASE;");
        return rows.ToList();
    }

    public async Task<Playlist?> GetDefaultPlaylistAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Playlist>(
            $"SELECT {PlaylistColumns} FROM playlists WHERE IsDefault = 1 LIMIT 1;");
    }

    public async Task<Playlist> CreatePlaylistAsync(string name, PlaylistKind kind,
        SmartPlaylistDefinition? definition, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var nextOrder = await connection.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(SortOrder), 0) + 1 FROM playlists;");
        var id = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO playlists (Name, Kind, IsDefault, SortOrder, SmartRulesJson, ViewLayoutJson, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@name, @kind, 0, @nextOrder, @rules, NULL, @now, @now);
            SELECT last_insert_rowid();
            """, new
        {
            name = name.Trim(),
            kind = kind.ToString(),
            nextOrder,
            rules = kind == PlaylistKind.Smart ? ViewLayoutFactory.SerializeSmart(definition ?? new SmartPlaylistDefinition()) : null,
            now = DateTime.UtcNow
        });

        _logger.Info($"Playlist created: {name} ({kind})");
        return new Playlist
        {
            Id = id,
            Name = name.Trim(),
            Kind = kind,
            SortOrder = nextOrder,
            SmartRulesJson = kind == PlaylistKind.Smart ? ViewLayoutFactory.SerializeSmart(definition ?? new SmartPlaylistDefinition()) : null,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public async Task UpdatePlaylistOrderAsync(IReadOnlyList<long> orderedPlaylistIds,
        CancellationToken cancellationToken = default)
    {
        if (orderedPlaylistIds.Count == 0)
        {
            return;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;
        // 一条 UPDATE 只影响一行，循环放在同一个事务里：中途失败会整体回滚，不会留下半套顺序。
        // 默认播放列表固定排最前（GetPlaylistsAsync 的 IsDefault DESC），它的 SortOrder 保持 0 不变，
        // 因此序号只在真正更新了非默认行时递增，保证非默认列表的 SortOrder 始终是连续的 1..N。
        var sortOrder = 1;
        foreach (var playlistId in orderedPlaylistIds)
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE playlists SET SortOrder = @sortOrder, UpdatedAtUtc = @now WHERE Id = @playlistId AND IsDefault = 0;",
                new { playlistId, sortOrder, now },
                transaction, cancellationToken: cancellationToken));
            if (affected > 0)
            {
                sortOrder++;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        _logger.Debug($"Playlist order updated: {orderedPlaylistIds.Count} id(s).");
    }

    public async Task RenamePlaylistAsync(long playlistId, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE playlists SET Name = @name, UpdatedAtUtc = @now WHERE Id = @playlistId;",
            new { playlistId, name = name.Trim(), now = DateTime.UtcNow });
    }

    public async Task DeletePlaylistAsync(long playlistId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM playlists WHERE Id = @playlistId AND IsDefault = 0;", new { playlistId });
        _logger.Info($"Playlist deleted: {playlistId}");
    }

    public async Task UpdatePlaylistLayoutAsync(long playlistId, string layoutJson, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE playlists SET ViewLayoutJson = @layoutJson, UpdatedAtUtc = @now WHERE Id = @playlistId;",
            new { playlistId, layoutJson, now = DateTime.UtcNow });
    }

    public async Task UpdatePlaylistRulesAsync(long playlistId, SmartPlaylistDefinition definition,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE playlists SET SmartRulesJson = @rules, UpdatedAtUtc = @now WHERE Id = @playlistId;",
            new { playlistId, rules = ViewLayoutFactory.SerializeSmart(definition), now = DateTime.UtcNow });
    }

    public async Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(Playlist playlist, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        if (playlist.IsSmart)
        {
            var query = SmartQueryBuilder.Build(playlist.GetSmartDefinition());
            var sql = $"""
                SELECT {LibraryTrackColumns} FROM tracks t
                WHERE {query.WhereSql}
                ORDER BY t.AlbumArtist COLLATE NOCASE, t.Album COLLATE NOCASE, t.DiscNumber, t.TrackNumber, t.Title COLLATE NOCASE;
                """;
            var smartRows = await connection.QueryAsync<Track>(sql, new DynamicParameters(query.Parameters));
            return smartRows.ToList();
        }

        var rows = await connection.QueryAsync<Track>($"""
            SELECT {SnapshotTrackColumns}
            FROM playlist_items i
            LEFT JOIN tracks t ON t.Path = i.TrackPath
            WHERE i.PlaylistId = @playlistId
            ORDER BY i.Position;
            """, new { playlistId = playlist.Id });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<string>> GetPlaylistItemPathsAsync(long playlistId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT TrackPath FROM playlist_items WHERE PlaylistId = @playlistId;",
            new { playlistId }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<int> AddPlaylistItemsAsync(long playlistId, IReadOnlyList<Track> tracks,
        bool skipExisting = false, CancellationToken cancellationToken = default)
    {
        if (tracks.Count == 0)
        {
            return 0;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        IReadOnlyList<Track> toInsert = tracks;
        if (skipExisting)
        {
            var existingPaths = (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT TrackPath FROM playlist_items WHERE PlaylistId = @playlistId;",
                new { playlistId }, transaction, cancellationToken: cancellationToken))).ToList();
            toInsert = PlaylistImportFilter.FilterNewTracks(tracks, existingPaths);
            if (toInsert.Count == 0)
            {
                return 0;
            }
        }

        var position = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COALESCE(MAX(Position), 0) FROM playlist_items WHERE PlaylistId = @playlistId;",
            new { playlistId }, transaction, cancellationToken: cancellationToken));
        var now = DateTime.UtcNow;

        foreach (var track in toInsert)
        {
            position++;
            var path = PathUtil.TryNormalize(track.Path, out var normalized) ? normalized : track.Path;
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO playlist_items (
                    PlaylistId, TrackPath, Position, TitleSnapshot, ArtistSnapshot, AlbumSnapshot,
                    DurationMsSnapshot, CoverKeySnapshot, AddedAtUtc)
                VALUES (@playlistId, @path, @position, @title, @artist, @album, @duration, @coverKey, @now);
                """, new
            {
                playlistId,
                path,
                position,
                title = track.DisplayTitle,
                artist = track.Artist,
                album = track.Album,
                duration = track.DurationMs,
                coverKey = track.CoverKey,
                now
            }, transaction, cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE playlists SET UpdatedAtUtc = @now WHERE Id = @playlistId;",
            new { playlistId, now }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return toInsert.Count;
    }

    public async Task RemovePlaylistItemsAsync(IReadOnlyList<long> playlistItemIds, CancellationToken cancellationToken = default)
    {
        if (playlistItemIds.Count == 0)
        {
            return;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM playlist_items WHERE Id IN @playlistItemIds;",
            new { playlistItemIds });
    }

    public async Task<int> RemovePlaylistItemsByPathAsync(IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
        {
            return 0;
        }

        // 与 UpdateTrackPathAsync 一致：先规范化成完整路径（分隔符 / 相对路径 / 引号），
        // 单个坏路径不能让整批播放列表清理失败，因此用 TryNormalize 跳过而不是抛异常。
        var normalized = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (PathUtil.TryNormalize(path, out var candidate) &&
                !normalized.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(candidate);
            }
        }

        if (normalized.Count == 0)
        {
            return 0;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // playlist_items.TrackPath 在建表时就是 COLLATE NOCASE（SqliteDatabase v1），这里显式写出来，
        // 保证磁盘路径大小写不同也能命中；整个删除放在一个事务里，要么全删要么不动。
        var removed = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM playlist_items WHERE TrackPath COLLATE NOCASE IN @paths;",
            new { paths = normalized }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);

        if (removed > 0)
        {
            _logger.Debug($"Removed {removed} playlist item(s) for {normalized.Count} deleted path(s).");
        }

        return removed;
    }

    public async Task ClearPlaylistAsync(long playlistId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM playlist_items WHERE PlaylistId = @playlistId;", new { playlistId });
    }

    public async Task<IReadOnlyDictionary<long, int>> GetPlaylistCountsAsync(IReadOnlyList<Playlist> playlists,
        CancellationToken cancellationToken = default)
    {
        var counts = new Dictionary<long, int>();
        if (playlists.Count == 0)
        {
            return counts;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var normalIds = playlists.Where(p => !p.IsSmart).Select(p => p.Id).ToList();
        if (normalIds.Count > 0)
        {
            var rows = await connection.QueryAsync<(long PlaylistId, int Count)>("""
                SELECT PlaylistId, COUNT(*) FROM playlist_items
                WHERE PlaylistId IN @normalIds GROUP BY PlaylistId;
                """, new { normalIds });
            foreach (var row in rows)
            {
                counts[row.PlaylistId] = row.Count;
            }
        }

        foreach (var smart in playlists.Where(p => p.IsSmart))
        {
            var query = SmartQueryBuilder.Build(smart.GetSmartDefinition());
            var count = await connection.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM tracks t WHERE {query.WhereSql};",
                new DynamicParameters(query.Parameters));
            counts[smart.Id] = count;
        }

        return counts;
    }

    private static async Task<Playlist> EnsureDefaultPlaylistAsync(System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        var existing = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<Playlist>(connection,
            $"SELECT {PlaylistColumns} FROM playlists WHERE IsDefault = 1 LIMIT 1;");
        if (existing is not null)
        {
            return existing;
        }

        var now = DateTime.UtcNow;
        var id = await Dapper.SqlMapper.ExecuteScalarAsync<long>(connection, """
            INSERT INTO playlists (Name, Kind, IsDefault, SortOrder, SmartRulesJson, ViewLayoutJson, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('默认播放列表', 'Normal', 1, 0, NULL, NULL, @now, @now);
            SELECT last_insert_rowid();
            """, new { now });

        return new Playlist
        {
            Id = id,
            Name = "默认播放列表",
            Kind = PlaylistKind.Normal,
            IsDefault = true,
            SortOrder = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }
}
