using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// BPM 测量、本地缓存合并与可选文件级标签写入的协调服务（ADR-034）。
/// </summary>
public sealed class BpmService : IBpmService
{
    private readonly IBpmAnalyzer _analyzer;
    private readonly IBpmCache _cache;
    private readonly ITagService _tagService;
    private readonly Func<BpmSettings> _settingsProvider;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _analysisGate = new(1, 1);

    public BpmService(
        IBpmAnalyzer analyzer,
        IBpmCache cache,
        ITagService tagService,
        Func<BpmSettings> settingsProvider,
        IAppLogger logger)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _tagService = tagService ?? throw new ArgumentNullException(nameof(tagService));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BpmDisplayState> GetDisplayStateAsync(
        string filePath,
        long knownFileSize = 0,
        DateTime? knownModifiedAtUtc = null,
        long knownDurationMs = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return BpmDisplayState.Empty;
        }

        var normalized = PathUtil.Normalize(filePath);
        var (fileSize, modifiedAtUtc) = ReadFileStamp(normalized, knownFileSize, knownModifiedAtUtc);
        var (fileTagBpm, tagDurationMs) = await Task.Run(() => TryReadFileBpmAndDuration(normalized), cancellationToken)
            .ConfigureAwait(false);
        var durationMs = tagDurationMs > 0 ? tagDurationMs : knownDurationMs;

        var cacheEntry = await _cache.TryGetAsync(normalized, cancellationToken).ConfigureAwait(false);
        var resolution = BpmResolver.Resolve(fileTagBpm, cacheEntry, fileSize, modifiedAtUtc, durationMs);
        if (resolution.ShouldRefreshStamp)
        {
            try
            {
                await _cache.UpdateFileStampAsync(normalized, fileSize, modifiedAtUtc, durationMs, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Debug($"BPM cache stamp refresh skipped for {normalized}: {ex.Message}");
            }
        }

        return resolution.DisplayState;
    }

    public async Task<(BpmAnalysisResult Result, BpmDisplayState DisplayState)> MeasureAsync(
        string filePath,
        IProgress<BpmAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var normalized = PathUtil.Normalize(filePath);

        await _analysisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        BpmAnalysisResult result;
        try
        {
            result = await _analyzer.AnalyzeAsync(normalized, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _analysisGate.Release();
        }

        var (fileSize, modifiedAtUtc) = ReadFileStamp(normalized, 0, null);
        var (fileTagBpm, durationMs) = await Task.Run(() => TryReadFileBpmAndDuration(normalized), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsReliable && BpmFormatter.IsValid(result.Bpm))
        {
            var entry = new BpmCacheEntry
            {
                Path = normalized,
                FileSize = fileSize,
                ModifiedAtUtc = modifiedAtUtc,
                DurationMs = durationMs,
                MeasuredBpm = BpmFormatter.RoundForStorage(result.Bpm),
                Confidence = result.Confidence,
                IsVariableTempo = result.IsVariableTempoSuspected,
                Candidates = result.Candidates,
                Segments = result.Segments,
                DetectedSegmentCount = result.DetectedSegmentCount,
                AnalyzerId = result.AlgorithmId,
                AnalyzerVersion = result.AlgorithmVersion,
                UpdatedAtUtc = DateTime.UtcNow
            };
            await _cache.SaveMeasuredAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        var updatedCache = await _cache.TryGetAsync(normalized, cancellationToken).ConfigureAwait(false);
        var state = BpmResolver.Resolve(
            fileTagBpm, updatedCache, fileSize, modifiedAtUtc, durationMs, result.ExecutionProvider).DisplayState;

        return (result, state);
    }

    public async Task<BpmDisplayState> SaveManualAsync(
        string filePath,
        double? bpm,
        bool writeToFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var normalized = PathUtil.Normalize(filePath);

        if (bpm is { } val && !BpmFormatter.IsValid(val))
        {
            throw new ArgumentOutOfRangeException(
                nameof(bpm),
                $"BPM 必须在 {BpmFormatter.MinBpm:0}–{BpmFormatter.MaxBpm:0} 之间。");
        }

        if (writeToFile)
        {
            await WriteBpmTagOnlyAsync(normalized, bpm, cancellationToken).ConfigureAwait(false);
            // 已落盘成为文件级标签真相：清除未落盘 ManualBpm，并刷新自动测量缓存戳记防止自失效。
            await _cache.ClearManualAsync(normalized, cancellationToken).ConfigureAwait(false);
            var (newSize, newMtime) = ReadFileStamp(normalized, 0, null);
            var (_, durationMs) = await Task.Run(() => TryReadFileBpmAndDuration(normalized), cancellationToken)
                .ConfigureAwait(false);
            await _cache.UpdateFileStampAsync(normalized, newSize, newMtime, durationMs, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (bpm is { } manualValue)
        {
            var (fileSize, modifiedAtUtc) = ReadFileStamp(normalized, 0, null);
            var (_, durationMs) = await Task.Run(() => TryReadFileBpmAndDuration(normalized), cancellationToken)
                .ConfigureAwait(false);
            await _cache.SaveManualAsync(normalized, fileSize, modifiedAtUtc, durationMs, manualValue, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _cache.ClearManualAsync(normalized, cancellationToken).ConfigureAwait(false);
        }

        return await GetDisplayStateAsync(normalized, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task<BpmDisplayState> WriteEffectiveToFileAsync(
        string filePath,
        double bpm,
        CancellationToken cancellationToken = default) =>
        SaveManualAsync(filePath, bpm, writeToFile: true, cancellationToken);

    public Task<int> ClearMeasuredCacheAsync(CancellationToken cancellationToken = default) =>
        _cache.ClearMeasuredAsync(cancellationToken);

    public Task<BpmCacheStats> GetCacheStatsAsync(CancellationToken cancellationToken = default) =>
        _cache.GetStatsAsync(cancellationToken);

    public BpmModelStatus GetModelStatus() => _analyzer.GetModelStatus();

    public void UnloadRuntime() => _analyzer.UnloadRuntime();

    public void Dispose()
    {
        _analyzer.Dispose();
        _analysisGate.Dispose();
    }

    private async Task WriteBpmTagOnlyAsync(string path, double? bpm, CancellationToken cancellationToken)
    {
        var roundId3 = BpmSettings.Normalize(_settingsProvider()).RoundId3BpmToInteger;
        await Task.Run(() =>
        {
            // includeCover: false 读取其它标准字段；RemoveCover=false 且 NewCoverBytes=null 保持原封面不动；
            // YearChanged=false 保持文件原始日期文本不动；只修改 BPM 标签（走 ADR-020 共享句柄原子替换）。
            var current = _tagService.Read(path, includeCover: false);
            var model = new TagEditModel
            {
                Title = current.Title,
                Artist = current.Artist,
                Album = current.Album,
                AlbumArtist = current.AlbumArtist,
                Composer = current.Composer,
                Genre = current.Genre,
                Comment = current.Comment,
                Year = current.Year ?? 0,
                YearText = current.YearText,
                YearChanged = false,
                TrackNumber = current.TrackNumber,
                DiscNumber = current.DiscNumber,
                RemoveCover = false,
                NewCoverBytes = null,
                NewCoverMime = null,
                Bpm = bpm,
                BpmChanged = true,
                RoundId3BpmToInteger = roundId3
            };
            _tagService.Write(path, model);
        }, cancellationToken).ConfigureAwait(false);
    }

    private (double? Bpm, long DurationMs) TryReadFileBpmAndDuration(string path)
    {
        try
        {
            var tag = _tagService.Read(path, includeCover: false);
            return (tag.Bpm, tag.DurationMs);
        }
        catch (Exception ex)
        {
            _logger.Debug($"Reading file BPM failed for {path}: {ex.Message}");
            return (null, 0);
        }
    }

    private static (long FileSize, DateTime ModifiedAtUtc) ReadFileStamp(
        string path,
        long fallbackSize,
        DateTime? fallbackModifiedAtUtc)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                return (info.Length, info.LastWriteTimeUtc);
            }
        }
        catch
        {
            // Fall back to known values.
        }

        return (fallbackSize, fallbackModifiedAtUtc ?? DateTime.MinValue);
    }
}
