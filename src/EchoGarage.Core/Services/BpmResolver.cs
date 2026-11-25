using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// 合并文件级 BPM 标签与本地 <c>track_bpm_cache</c> 缓存的纯逻辑裁决器（ADR-034 §5.2）。
/// <para>
/// 智能失效规则：改封面/改标题会改变文件 <c>FileSize</c> 与 <c>ModifiedAtUtc</c>，但音频时长不变；
/// 因此只要 <c>|ΔDuration| ≤ 1000 ms</c>，自动测量与手动修正缓存均不误失效。
/// </para>
/// </summary>
public static class BpmResolver
{
    public const long DurationChangeThresholdMs = 1000;

    public sealed record Resolution(
        BpmDisplayState DisplayState,
        bool ShouldRefreshStamp);

    public static Resolution Resolve(
        double? fileTagBpm,
        BpmCacheEntry? cache,
        long currentFileSize,
        DateTime currentModifiedAtUtc,
        long currentDurationMs,
        string? executionProvider = null)
    {
        var validFileBpm = fileTagBpm is { } fb && BpmFormatter.IsValid(fb)
            ? BpmFormatter.RoundForStorage(fb)
            : (double?)null;

        if (cache is null)
        {
            return new Resolution(
                new BpmDisplayState(
                    EffectiveBpm: validFileBpm,
                    Source: validFileBpm.HasValue ? BpmSource.FileTag : BpmSource.None,
                    FileTagBpm: validFileBpm,
                    MeasuredBpm: null,
                    ManualBpm: null,
                    Confidence: null,
                    IsVariableTempoSuspected: false,
                    DurationChangedWarning: false,
                    ExternalFileTagChangedHint: false,
                    Candidates: [],
                    AnalyzerId: null,
                    ExecutionProvider: executionProvider),
                ShouldRefreshStamp: false);
        }

        var stampMatches = cache.FileSize == currentFileSize &&
                           cache.ModifiedAtUtc == currentModifiedAtUtc;
        var durationChanged = !stampMatches &&
                              currentDurationMs > 0 &&
                              cache.DurationMs > 0 &&
                              Math.Abs(currentDurationMs - cache.DurationMs) > DurationChangeThresholdMs;

        // 仅音频时长显著变化时作废自动测量值；手动修正值始终保留，但附带时长变化提醒。
        var effectiveMeasured = !durationChanged && cache.MeasuredBpm is { } mb && BpmFormatter.IsValid(mb)
            ? BpmFormatter.RoundForStorage(mb)
            : (double?)null;
        var effectiveManual = cache.ManualBpm is { } man && BpmFormatter.IsValid(man)
            ? BpmFormatter.RoundForStorage(man)
            : (double?)null;

        var candidates = durationChanged ? [] : cache.Candidates;
        var confidence = durationChanged ? null : cache.Confidence;
        var isVariable = !durationChanged && cache.IsVariableTempo;
        var detectedSegmentCount = durationChanged ? 0 : cache.DetectedSegmentCount;
        var tooManyTempoChanges = !durationChanged && detectedSegmentCount > BpmFormatter.MaxDisplaySegments;
        IReadOnlyList<BpmSegment> segments = durationChanged || tooManyTempoChanges ||
                                             cache.Segments.Count < 2 ||
                                             cache.Segments.Count > BpmFormatter.MaxDisplaySegments
            ? []
            : cache.Segments;

        // 外部工具后来修改了文件并写入了与旧 ManualBpm 不同的文件级 BPM Tag：文件真相优先并提示。
        var externalFileTagChanged = !stampMatches &&
                                     validFileBpm.HasValue &&
                                     effectiveManual.HasValue &&
                                     Math.Abs(validFileBpm.Value - effectiveManual.Value) >= 0.05;

        double? effective;
        BpmSource source;
        if (effectiveManual.HasValue && !externalFileTagChanged)
        {
            effective = effectiveManual;
            source = BpmSource.CachedManual;
        }
        else if (validFileBpm.HasValue)
        {
            effective = validFileBpm;
            source = BpmSource.FileTag;
        }
        else if (effectiveMeasured.HasValue)
        {
            effective = effectiveMeasured;
            source = BpmSource.CachedMeasured;
        }
        else
        {
            effective = null;
            source = BpmSource.None;
        }

        // 文件元信息变更但音频时长未变且没有外部 BPM 冲突 → 可顺手刷新缓存戳记
        var shouldRefreshStamp = !stampMatches &&
                                 !durationChanged &&
                                 !externalFileTagChanged &&
                                 currentFileSize > 0 &&
                                 currentModifiedAtUtc != default &&
                                 (effectiveMeasured.HasValue || effectiveManual.HasValue);

        var state = new BpmDisplayState(
            EffectiveBpm: effective,
            Source: source,
            FileTagBpm: validFileBpm,
            MeasuredBpm: effectiveMeasured,
            ManualBpm: effectiveManual,
            Confidence: confidence,
            IsVariableTempoSuspected: isVariable,
            DurationChangedWarning: durationChanged && effectiveManual.HasValue,
            ExternalFileTagChangedHint: externalFileTagChanged,
            Candidates: candidates,
            AnalyzerId: durationChanged ? null : cache.AnalyzerId,
            ExecutionProvider: executionProvider)
        {
            Segments = segments,
            DetectedSegmentCount = detectedSegmentCount,
            TooManyTempoChanges = tooManyTempoChanges
        };

        return new Resolution(state, shouldRefreshStamp);
    }
}
