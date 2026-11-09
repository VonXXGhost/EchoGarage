using Dapper;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Infrastructure.Data;

public sealed class SqliteLibraryRepository : ILibraryRepository
{
    private const string TrackColumns = """
        Id, Path, FolderId, IsInLibrary, IsMissing, Title, Artist, Album, AlbumArtist, Genre, Comment,
        Year, TrackNumber, DiscNumber, Rating, DurationMs, Bitrate, SampleRate, Channels, Codec, FileSize,
        ModifiedAtUtc, AddedAtUtc, LastPlayedAtUtc, PlayCount, CoverKey
        """;

    private const string UpsertSql = """
        INSERT INTO tracks (
            Path, FolderId, IsInLibrary, IsMissing, Title, Artist, Album, AlbumArtist, Genre, Comment,
            Year, TrackNumber, DiscNumber, Rating, DurationMs, Bitrate, SampleRate, Channels, Codec, FileSize,
            ModifiedAtUtc, AddedAtUtc, LastPlayedAtUtc, PlayCount, CoverKey)
        VALUES (
            @Path, @FolderId, @IsInLibrary, @IsMissing, @Title, @Artist, @Album, @AlbumArtist, @Genre, @Comment,
            @Year, @TrackNumber, @DiscNumber, @Rating, @DurationMs, @Bitrate, @SampleRate, @Channels, @Codec, @FileSize,
            @ModifiedAtUtc, @AddedAtUtc, @LastPlayedAtUtc, @PlayCount, @CoverKey)
        ON CONFLICT(Path) DO UPDATE SET
            FolderId = excluded.FolderId,
            IsInLibrary = excluded.IsInLibrary,
            IsMissing = excluded.IsMissing,
            Title = excluded.Title,
            Artist = excluded.Artist,
            Album = excluded.Album,
            AlbumArtist = excluded.AlbumArtist,
            Genre = excluded.Genre,
            Comment = excluded.Comment,
            Year = excluded.Year,
            TrackNumber = excluded.TrackNumber,
            DiscNumber = excluded.DiscNumber,
            Rating = excluded.Rating,
            DurationMs = excluded.DurationMs,
            Bitrate = excluded.Bitrate,
            SampleRate = excluded.SampleRate,
            Channels = excluded.Channels,
            Codec = excluded.Codec,
            FileSize = excluded.FileSize,
            ModifiedAtUtc = excluded.ModifiedAtUtc,
            CoverKey = excluded.CoverKey;
        """;

    private readonly SqliteDatabase _database;
    private readonly IAppLogger _logger;

    public SqliteLibraryRepository(SqliteDatabase database, IAppLogger logger)
    {
        _database = database;
        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _database.InitializeAsync(cancellationToken);

    public async Task<IReadOnlyList<LibraryFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<LibraryFolder>(
            "SELECT Id, Path, AddedAtUtc, LastScanAtUtc FROM library_folders ORDER BY Path;");
        return rows.ToList();
    }

    public async Task<LibraryFolder> AddFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = PathUtil.Normalize(path);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var existing = await connection.QuerySingleOrDefaultAsync<LibraryFolder>(
            "SELECT Id, Path, AddedAtUtc, LastScanAtUtc FROM library_folders WHERE Path = @normalized;",
            new { normalized });
        if (existing is not null)
        {
            return existing;
        }

        var addedAt = DateTime.UtcNow;
        var id = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO library_folders (Path, AddedAtUtc) VALUES (@normalized, @addedAt);
            SELECT last_insert_rowid();
            """, new { normalized, addedAt });

        _logger.Info($"Library folder added: {normalized}");
        return new LibraryFolder { Id = id, Path = normalized, AddedAtUtc = addedAt };
    }

    public async Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM library_folders WHERE Id = @folderId;", new { folderId });
        // Tracks stay in the DB as "not in library" so playlists referencing them keep working.
        await connection.ExecuteAsync(
            "UPDATE tracks SET IsInLibrary = 0, FolderId = NULL WHERE FolderId = @folderId;", new { folderId });
        _logger.Info($"Library folder removed: {folderId}");
    }

    public async Task<IReadOnlyList<Track>> GetLibraryTracksAsync(long? folderId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var sql = $"""
            SELECT {TrackColumns} FROM tracks
            WHERE IsInLibrary = 1 AND IsMissing = 0
            {(folderId.HasValue ? "AND FolderId = @folderId" : string.Empty)}
            ORDER BY AlbumArtist COLLATE NOCASE, Album COLLATE NOCASE, DiscNumber, TrackNumber, Title COLLATE NOCASE;
            """;
        var rows = await connection.QueryAsync<Track>(sql, new { folderId });
        return rows.ToList();
    }

    public async Task<int> GetLibraryTrackCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tracks WHERE IsInLibrary = 1 AND IsMissing = 0;");
    }

    public async Task<IReadOnlyList<Track>> GetTracksInFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var folderId = await GetFolderIdAsync(connection, PathUtil.Normalize(folderPath));
        var rows = await connection.QueryAsync<Track>(
            $"SELECT {TrackColumns} FROM tracks WHERE FolderId = @folderId;", new { folderId });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<Track>> GetTracksInDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var prefix = NormalizeDirectory(directoryPath) + "\\%";
        var rows = await connection.QueryAsync<Track>(
            $"SELECT {TrackColumns} FROM tracks WHERE FolderId IS NOT NULL AND Path LIKE @prefix ESCAPE '\\';",
            new { prefix = PathUtil.EscapeLike(prefix) });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<TrackState>> GetTrackStatesAsync(long? folderId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var sql = """
            SELECT Path, ModifiedAtUtc, FileSize, IsMissing FROM tracks
            WHERE FolderId IS NOT NULL
            """ + (folderId.HasValue ? " AND FolderId = @folderId" : string.Empty) + ";";
        var rows = await connection.QueryAsync<TrackState>(sql, new { folderId });
        return rows.ToList();
    }

    public async Task<Track?> GetTrackByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = PathUtil.Normalize(path);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Track>(
            $"SELECT {TrackColumns} FROM tracks WHERE Path = @normalized;", new { normalized });
    }

    public async Task UpsertTrackAsync(Track track, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(UpsertSql, track);
        track.Id = await connection.ExecuteScalarAsync<long>(
            "SELECT Id FROM tracks WHERE Path = @Path;", new { track.Path });
    }

    public async Task UpsertTracksAsync(IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var batch in tracks.Chunk(200))
        {
            await connection.ExecuteAsync(UpsertSql, batch, transaction);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkTrackMissingAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = PathUtil.Normalize(path);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE tracks SET IsMissing = 1 WHERE Path = @normalized;", new { normalized });
    }

    public async Task MarkTracksMissingAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
        {
            return;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE tracks SET IsMissing = 1 WHERE Path IN @paths;", new { paths });
    }

    public async Task MarkTracksMissingUnderFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        var prefix = PathUtil.EscapeLike(NormalizeDirectory(folderPath) + "\\%");
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE tracks SET IsMissing = 1 WHERE IsInLibrary = 1 AND Path LIKE @prefix ESCAPE '\\';",
            new { prefix });
    }

    public async Task UpdateFolderScanTimeAsync(long folderId, DateTime scannedAtUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE library_folders SET LastScanAtUtc = @scannedAtUtc WHERE Id = @folderId;",
            new { folderId, scannedAtUtc });
    }

    public async Task RecordPlaybackAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = PathUtil.Normalize(path);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE tracks SET PlayCount = PlayCount + 1, LastPlayedAtUtc = @now WHERE Path = @normalized;",
            new { normalized, now = DateTime.UtcNow });
    }

    public async Task<IReadOnlyDictionary<long, int>> GetFolderTrackCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<(long FolderId, int Count)>("""
            SELECT FolderId, COUNT(*) FROM tracks
            WHERE FolderId IS NOT NULL AND IsInLibrary = 1 AND IsMissing = 0
            GROUP BY FolderId;
            """);
        return rows.ToDictionary(r => r.FolderId, r => r.Count);
    }

    private static string NormalizeDirectory(string path) =>
        PathUtil.Normalize(path)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

    private static async Task<long> GetFolderIdAsync(System.Data.Common.DbConnection connection, string path)
    {
        return await SqlMapper.ExecuteScalarAsync<long?>(
            connection, "SELECT Id FROM library_folders WHERE Path = @path;", new { path }) ?? 0;
    }
}
