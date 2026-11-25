using System.Text.Json;
using Dapper;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Infrastructure.Data;

/// <summary>
/// SQLite <c>track_bpm_cache</c> 表仓储（ADR-034 §5）：
/// 保存自动测量结果与未写文件的手动修正；「清空自动测量缓存」严格保护所有含 <c>ManualBpm</c> 的行。
/// </summary>
public sealed class SqliteBpmCache : IBpmCache
{
    private readonly SqliteDatabase _database;
    private readonly IAppLogger _logger;

    public SqliteBpmCache(SqliteDatabase database, IAppLogger logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BpmCacheEntry?> TryGetAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!PathUtil.TryNormalize(filePath, out var normalized))
        {
            return null;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<BpmCacheRow>(new CommandDefinition("""
            SELECT Path, FileSize, ModifiedAtUtc, DurationMs, MeasuredBpm, Confidence,
                   IsVariableTempo, CandidatesJson, SegmentsJson, DetectedSegmentCount,
                   AnalyzerId, AnalyzerVersion, ManualBpm, UpdatedAtUtc
            FROM track_bpm_cache
            WHERE Path = @normalized;
            """, new { normalized }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ToEntry();
    }

    public async Task SaveMeasuredAsync(BpmCacheEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var normalized = PathUtil.Normalize(entry.Path);
        var candidatesJson = entry.Candidates.Count > 0
            ? JsonSerializer.Serialize(entry.Candidates, JsonDefaults.Options)
            : null;
        var segmentsJson = entry.Segments.Count is >= 2 and <= BpmFormatter.MaxDisplaySegments
            ? JsonSerializer.Serialize(entry.Segments, JsonDefaults.Options)
            : null;
        var updatedAt = entry.UpdatedAtUtc == default ? DateTime.UtcNow : entry.UpdatedAtUtc;

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO track_bpm_cache (
                Path, FileSize, ModifiedAtUtc, DurationMs, MeasuredBpm, Confidence,
                IsVariableTempo, CandidatesJson, SegmentsJson, DetectedSegmentCount,
                AnalyzerId, AnalyzerVersion, ManualBpm, UpdatedAtUtc)
            VALUES (
                @Path, @FileSize, @ModifiedAtUtc, @DurationMs, @MeasuredBpm, @Confidence,
                @IsVariableTempo, @CandidatesJson, @SegmentsJson, @DetectedSegmentCount,
                @AnalyzerId, @AnalyzerVersion, @ManualBpm, @UpdatedAtUtc)
            ON CONFLICT(Path) DO UPDATE SET
                FileSize = excluded.FileSize,
                ModifiedAtUtc = excluded.ModifiedAtUtc,
                DurationMs = excluded.DurationMs,
                MeasuredBpm = excluded.MeasuredBpm,
                Confidence = excluded.Confidence,
                IsVariableTempo = excluded.IsVariableTempo,
                CandidatesJson = excluded.CandidatesJson,
                SegmentsJson = excluded.SegmentsJson,
                DetectedSegmentCount = excluded.DetectedSegmentCount,
                AnalyzerId = excluded.AnalyzerId,
                AnalyzerVersion = excluded.AnalyzerVersion,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """, new
        {
            Path = normalized,
            entry.FileSize,
            entry.ModifiedAtUtc,
            entry.DurationMs,
            entry.MeasuredBpm,
            entry.Confidence,
            IsVariableTempo = entry.IsVariableTempo ? 1 : 0,
            CandidatesJson = candidatesJson,
            SegmentsJson = segmentsJson,
            entry.DetectedSegmentCount,
            entry.AnalyzerId,
            entry.AnalyzerVersion,
            entry.ManualBpm,
            UpdatedAtUtc = updatedAt
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task SaveManualAsync(
        string filePath,
        long fileSize,
        DateTime modifiedAtUtc,
        long durationMs,
        double manualBpm,
        CancellationToken cancellationToken = default)
    {
        var normalized = PathUtil.Normalize(filePath);
        var now = DateTime.UtcNow;

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO track_bpm_cache (
                Path, FileSize, ModifiedAtUtc, DurationMs, ManualBpm, UpdatedAtUtc)
            VALUES (
                @Path, @FileSize, @ModifiedAtUtc, @DurationMs, @ManualBpm, @UpdatedAtUtc)
            ON CONFLICT(Path) DO UPDATE SET
                FileSize = excluded.FileSize,
                ModifiedAtUtc = excluded.ModifiedAtUtc,
                DurationMs = excluded.DurationMs,
                ManualBpm = excluded.ManualBpm,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """, new
        {
            Path = normalized,
            FileSize = fileSize,
            ModifiedAtUtc = modifiedAtUtc,
            DurationMs = durationMs,
            ManualBpm = BpmFormatter.RoundForStorage(manualBpm),
            UpdatedAtUtc = now
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task ClearManualAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!PathUtil.TryNormalize(filePath, out var normalized))
        {
            return;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE track_bpm_cache
            SET ManualBpm = NULL, UpdatedAtUtc = @now
            WHERE Path = @normalized;
            DELETE FROM track_bpm_cache
            WHERE Path = @normalized AND MeasuredBpm IS NULL AND ManualBpm IS NULL;
            """, new { normalized, now = DateTime.UtcNow }, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateFileStampAsync(
        string filePath,
        long fileSize,
        DateTime modifiedAtUtc,
        long durationMs,
        CancellationToken cancellationToken = default)
    {
        if (!PathUtil.TryNormalize(filePath, out var normalized))
        {
            return;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE track_bpm_cache
            SET FileSize = @fileSize,
                ModifiedAtUtc = @modifiedAtUtc,
                DurationMs = CASE WHEN @durationMs > 0 THEN @durationMs ELSE DurationMs END,
                UpdatedAtUtc = @now
            WHERE Path = @normalized;
            """, new { normalized, fileSize, modifiedAtUtc, durationMs, now = DateTime.UtcNow },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task MovePathAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        if (!PathUtil.TryNormalize(oldPath, out var oldNormalized) ||
            !PathUtil.TryNormalize(newPath, out var newNormalized) ||
            string.Equals(oldNormalized, newNormalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM track_bpm_cache
            WHERE Path = @newNormalized AND Path <> @oldNormalized
              AND EXISTS (SELECT 1 FROM track_bpm_cache WHERE Path = @oldNormalized);
            UPDATE track_bpm_cache SET Path = @newNormalized WHERE Path = @oldNormalized;
            """, new { oldNormalized, newNormalized }, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeletePathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var normalized = paths
            .Select(p => PathUtil.TryNormalize(p, out var n) ? n : null)
            .Where(n => n is not null)
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            return;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var batch in normalized.Chunk(200))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM track_bpm_cache WHERE Path IN @batch;",
                new { batch }, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ClearMeasuredAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM track_bpm_cache WHERE MeasuredBpm IS NOT NULL;",
            transaction: tx, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                DELETE FROM track_bpm_cache WHERE ManualBpm IS NULL;
                UPDATE track_bpm_cache
                SET MeasuredBpm = NULL,
                    Confidence = NULL,
                    IsVariableTempo = 0,
                    CandidatesJson = NULL,
                    SegmentsJson = NULL,
                    DetectedSegmentCount = 0,
                    AnalyzerId = NULL,
                    AnalyzerVersion = 0,
                    UpdatedAtUtc = @now
                WHERE ManualBpm IS NOT NULL AND MeasuredBpm IS NOT NULL;
                """, new { now = DateTime.UtcNow }, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        _logger.Info($"Cleared measured BPM cache: cleared={count} (manual overrides preserved).");
        return count;
    }

    public async Task<BpmCacheStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var stats = await connection.QuerySingleAsync<(int Total, int Measured, int Manual)>(new CommandDefinition("""
            SELECT
                COUNT(*) AS Total,
                SUM(CASE WHEN MeasuredBpm IS NOT NULL THEN 1 ELSE 0 END) AS Measured,
                SUM(CASE WHEN ManualBpm IS NOT NULL THEN 1 ELSE 0 END) AS Manual
            FROM track_bpm_cache;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new BpmCacheStats(stats.Total, stats.Measured, stats.Manual);
    }

    private sealed class BpmCacheRow
    {
        public string Path { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime ModifiedAtUtc { get; set; }
        public long DurationMs { get; set; }
        public double? MeasuredBpm { get; set; }
        public double? Confidence { get; set; }
        public int IsVariableTempo { get; set; }
        public string? CandidatesJson { get; set; }
        public string? SegmentsJson { get; set; }
        public int DetectedSegmentCount { get; set; }
        public string? AnalyzerId { get; set; }
        public int AnalyzerVersion { get; set; }
        public double? ManualBpm { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        public BpmCacheEntry ToEntry()
        {
            IReadOnlyList<BpmCandidate> candidates = [];
            if (!string.IsNullOrWhiteSpace(CandidatesJson))
            {
                try
                {
                    candidates = JsonSerializer.Deserialize<List<BpmCandidate>>(CandidatesJson, JsonDefaults.Options) ?? [];
                }
                catch
                {
                    candidates = [];
                }
            }

            IReadOnlyList<BpmSegment> segments = [];
            if (!string.IsNullOrWhiteSpace(SegmentsJson))
            {
                try
                {
                    segments = JsonSerializer.Deserialize<List<BpmSegment>>(SegmentsJson, JsonDefaults.Options) ?? [];
                }
                catch
                {
                    segments = [];
                }
            }

            return new BpmCacheEntry
            {
                Path = Path,
                FileSize = FileSize,
                ModifiedAtUtc = ModifiedAtUtc,
                DurationMs = DurationMs,
                MeasuredBpm = MeasuredBpm,
                Confidence = Confidence,
                IsVariableTempo = IsVariableTempo != 0,
                Candidates = candidates,
                Segments = segments,
                DetectedSegmentCount = DetectedSegmentCount,
                AnalyzerId = AnalyzerId,
                AnalyzerVersion = AnalyzerVersion,
                ManualBpm = ManualBpm,
                UpdatedAtUtc = UpdatedAtUtc
            };
        }
    }
}
