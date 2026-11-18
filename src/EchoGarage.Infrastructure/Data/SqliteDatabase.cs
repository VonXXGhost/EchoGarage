using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;

namespace EchoGarage.Infrastructure.Data;

public sealed class SqliteDatabase
{
    private readonly string _connectionString;
    private readonly IAppLogger _logger;

    /// <summary>
    /// 已编译的智能列表正则（key 为原始模式，null 表示模式非法）。查询时 SQLite 会对
    /// 每一行回调 regexp()，缓存避免每行重复编译；模式数量与用户配置的条件数同阶。
    /// </summary>
    private readonly ConcurrentDictionary<string, Regex?> _regexCache = new(StringComparer.Ordinal);

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
            version = 2;
        }

        if (version < 3)
        {
            await MigrateToVersion3Async(connection, cancellationToken);
            version = 3;
        }

        if (version < 4)
        {
            await MigrateToVersion4Async(connection, cancellationToken);
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

    private async Task MigrateToVersion3Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Version 2 added the Composer column without invalidating existing cache rows, so
        // libraries created before that migration keep an empty Composer until a file changes.
        // Resetting DurationMs marks every library row as stale (ADR-010: DurationMs <= 0 means
        // the cache row must be rebuilt), and the next startup scan re-reads the file tags.
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE tracks SET DurationMs = 0 WHERE FolderId IS NOT NULL;
            PRAGMA user_version = 3;
            """, cancellationToken: cancellationToken));
        _logger.Info("SQLite schema migrated to version 3 (metadata repair for Composer).");
    }

    private async Task MigrateToVersion4Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Older versions only read track/disc numbers from the standard MP4 `trkn`/`disk`
        // atoms. Files tagged by Nero AAC or other legacy Apple tools store them as freeform
        // `----:com.apple.iTunes:track` / `disc` boxes instead, so their cached rows kept
        // TrackNumber = 0 forever: the incremental scan compares only size + mtime and skips
        // these unchanged files. The tag reader now understands the legacy boxes, but the
        // existing cache rows never get re-read. Resetting DurationMs marks exactly those
        // Apple-container rows as stale (ADR-010: DurationMs <= 0 means the row must be
        // rebuilt), so the startup repair scan re-reads their tags once. Rows that genuinely
        // have no track number are re-read once too and simply stay at 0.
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE tracks SET DurationMs = 0
            WHERE TrackNumber = 0
              AND (Path LIKE '%.m4a' OR Path LIKE '%.mp4' OR Path LIKE '%.m4b');
            PRAGMA user_version = 4;
            """, cancellationToken: cancellationToken));
        _logger.Info("SQLite schema migrated to version 4 (metadata repair for legacy Apple track numbers).");
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        // 连接池里每次 Open 都是一次新的 SQLite 连接，用户函数属于连接级状态，
        // 必须在 Open 之后重新注册（否则智能列表的 dirname/regexp 会报 no such function）。
        RegisterQueryFunctions(connection);
        return connection;
    }

    /// <summary>
    /// 注册智能播放列表查询用到的标量函数：
    /// <list type="bullet">
    /// <item><c>dirname(path)</c>：文件父目录，末尾不带分隔符；null / 空 / 根路径返回空串。</item>
    /// <item><c>regexp(pattern, input)</c>：SQLite 的 <c>X REGEXP Y</c> 语法会转成
    /// <c>regexp(Y, X)</c>，即模式在右操作数；忽略大小写、250ms 超时，非法模式与超时
    /// 都返回 false，绝不向 SQLite 抛异常中断整条查询。</item>
    /// </list>
    /// </summary>
    private void RegisterQueryFunctions(SqliteConnection connection)
    {
        connection.CreateFunction<string?, string?>("dirname", GetDirectoryName, isDeterministic: true);
        connection.CreateFunction<string?, string?, bool>("regexp", IsRegexMatch, isDeterministic: true);
    }

    private string GetDirectoryName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                return string.Empty;
            }

            // 文件直接位于盘符根目录（如 D:\song.wav）时父目录是根路径：按规格返回空串。
            var root = Path.GetPathRoot(directory);
            if (!string.IsNullOrEmpty(root) &&
                string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            _logger.Debug($"Smart playlist dirname: ignored invalid path \"{path}\".");
            return string.Empty;
        }
    }

    private bool IsRegexMatch(string? pattern, string? input)
    {
        var regex = ResolveRegex(pattern);
        if (regex is null)
        {
            return false;
        }

        try
        {
            return regex.IsMatch(input ?? string.Empty);
        }
        catch (RegexMatchTimeoutException)
        {
            // 模式本身合法但灾难性回溯超过上限：这一行视为不匹配，查询继续。
            _logger.Debug(
                $"Smart playlist regexp timed out after {SmartRegex.MatchTimeout.TotalMilliseconds:0} ms: \"{pattern}\".");
            return false;
        }
    }

    private Regex? ResolveRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        if (_regexCache.TryGetValue(pattern, out var cached))
        {
            return cached;
        }

        Regex? compiled = null;
        if (SmartRegex.TryCompile(pattern, out var regex))
        {
            compiled = regex;
        }
        else
        {
            // 正常路径下 BuildRule 已经跳过非法模式；这里兜底（旧配置 / 手工改过的 JSON）。
            _logger.Warn($"Smart playlist regexp ignored invalid pattern: \"{pattern}\".");
        }

        _regexCache.TryAdd(pattern, compiled);
        return compiled;
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
