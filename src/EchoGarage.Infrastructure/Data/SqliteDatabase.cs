using System.Data;
using System.Globalization;
using Dapper;
using EchoGarage.Core.Abstractions;
using EchoGarage.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;

namespace EchoGarage.Infrastructure.Data;

public sealed class SqliteDatabase
{
    private readonly string _connectionString;
    private readonly IAppLogger _logger;

    static SqliteDatabase()
    {
        SqlMapper.AddTypeHandler(new DateTimeTypeHandler());
    }

    public SqliteDatabase(AppPaths paths, IAppLogger logger)
    {
        _logger = logger;
        Directory.CreateDirectory(paths.DataDirectory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 8
        };
        _connectionString = builder.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA journal_mode=WAL;", cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA synchronous=NORMAL;", cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys=ON;", cancellationToken: cancellationToken));

        var version = await connection.ExecuteScalarAsync<long>("PRAGMA user_version;");
        if (version < 1)
        {
            await InitializeVersion1Async(connection, cancellationToken);
            version = 1;
        }

        if (version < 2)
        {
            await MigrateToVersion2Async(connection, cancellationToken);
        }
    }

    private async Task InitializeVersion1Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE TABLE IF NOT EXISTS library_folders (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                AddedAtUtc TEXT NOT NULL,
                LastScanAtUtc TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS tracks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                FolderId INTEGER NULL,
                IsInLibrary INTEGER NOT NULL DEFAULT 0,
                IsMissing INTEGER NOT NULL DEFAULT 0,
                Title TEXT NOT NULL DEFAULT '',
                Artist TEXT NOT NULL DEFAULT '',
                Album TEXT NOT NULL DEFAULT '',
                AlbumArtist TEXT NOT NULL DEFAULT '',
                Genre TEXT NOT NULL DEFAULT '',
                Comment TEXT NOT NULL DEFAULT '',
                Year INTEGER NULL,
                TrackNumber INTEGER NOT NULL DEFAULT 0,
                DiscNumber INTEGER NOT NULL DEFAULT 0,
                Rating INTEGER NOT NULL DEFAULT 0,
                DurationMs INTEGER NOT NULL DEFAULT 0,
                Bitrate INTEGER NOT NULL DEFAULT 0,
                SampleRate INTEGER NOT NULL DEFAULT 0,
                Channels INTEGER NOT NULL DEFAULT 0,
                Codec TEXT NOT NULL DEFAULT '',
                FileSize INTEGER NOT NULL DEFAULT 0,
                ModifiedAtUtc TEXT NOT NULL,
                AddedAtUtc TEXT NOT NULL,
                LastPlayedAtUtc TEXT NULL,
                PlayCount INTEGER NOT NULL DEFAULT 0,
                CoverKey TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS playlists (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Kind TEXT NOT NULL DEFAULT 'Normal',
                IsDefault INTEGER NOT NULL DEFAULT 0,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                SmartRulesJson TEXT NULL,
                ViewLayoutJson TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS playlist_items (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlaylistId INTEGER NOT NULL,
                TrackPath TEXT NOT NULL COLLATE NOCASE,
                Position INTEGER NOT NULL,
                TitleSnapshot TEXT NOT NULL DEFAULT '',
                ArtistSnapshot TEXT NOT NULL DEFAULT '',
                AlbumSnapshot TEXT NOT NULL DEFAULT '',
                DurationMsSnapshot INTEGER NOT NULL DEFAULT 0,
                CoverKeySnapshot TEXT NULL,
                AddedAtUtc TEXT NOT NULL,
                FOREIGN KEY (PlaylistId) REFERENCES playlists(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_tracks_Library ON tracks(IsInLibrary, IsMissing);
            CREATE INDEX IF NOT EXISTS IX_tracks_Folder ON tracks(FolderId);
            CREATE INDEX IF NOT EXISTS IX_playlist_items_Playlist ON playlist_items(PlaylistId, Position);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_playlists_Default ON playlists(IsDefault) WHERE IsDefault = 1;
            PRAGMA user_version = 1;
            """, transaction: transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        _logger.Info("SQLite schema initialized (version 1).");
    }

    private async Task MigrateToVersion2Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Composer is a cached tag value derived from the file (see ADR-003); existing rows
        // start empty and are repaired by the next scan that touches the file.
        await connection.ExecuteAsync(new CommandDefinition("""
            ALTER TABLE tracks ADD COLUMN Composer TEXT NOT NULL DEFAULT '';
            PRAGMA user_version = 2;
            """, cancellationToken: cancellationToken));
        _logger.Info("SQLite schema migrated to version 2 (Composer).");
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private sealed class DateTimeTypeHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override void SetValue(IDbDataParameter parameter, DateTime value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        public override DateTime Parse(object value) => value switch
        {
            DateTime dt => dt,
            string text when DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed) => parsed,
            _ => Convert.ToDateTime(value, CultureInfo.InvariantCulture)
        };
    }
}
