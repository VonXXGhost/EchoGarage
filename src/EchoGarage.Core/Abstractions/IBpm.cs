using EchoGarage.Core.Models;

namespace EchoGarage.Core.Abstractions;

/// <summary>BPM 测量引擎抽象（ADR-034）：由 Infrastructure 实现（ONNX Runtime DirectML + CPU 回退）。</summary>
public interface IBpmAnalyzer : IDisposable
{
    string AlgorithmId { get; }
    int Version { get; }

    Task<BpmAnalysisResult> AnalyzeAsync(
        string filePath,
        IProgress<BpmAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>主动卸载底层推理会话，立即释放显存与原生内存。</summary>
    void UnloadRuntime();

    /// <summary>查询内置 small0 与可选 final0 模型的本地就绪状态。</summary>
    BpmModelStatus GetModelStatus();
}

/// <summary>SQLite <c>track_bpm_cache</c> 表仓储抽象（ADR-034 §5）。</summary>
public interface IBpmCache
{
    Task<BpmCacheEntry?> TryGetAsync(string filePath, CancellationToken cancellationToken = default);
    Task SaveMeasuredAsync(BpmCacheEntry entry, CancellationToken cancellationToken = default);
    Task SaveManualAsync(string filePath, long fileSize, DateTime modifiedAtUtc, long durationMs, double manualBpm,
        CancellationToken cancellationToken = default);
    Task ClearManualAsync(string filePath, CancellationToken cancellationToken = default);
    Task UpdateFileStampAsync(string filePath, long fileSize, DateTime modifiedAtUtc, long durationMs,
        CancellationToken cancellationToken = default);
    Task MovePathAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);
    Task DeletePathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>清空自动测量缓存，同时完整保留所有含 <c>ManualBpm</c> 的用户手动修正记录。</summary>
    Task<int> ClearMeasuredAsync(CancellationToken cancellationToken = default);

    Task<BpmCacheStats> GetStatsAsync(CancellationToken cancellationToken = default);
}

/// <summary>BPM 测量、缓存查询、手动修正与可选文件级 Tag 写入的统一服务入口（App 层唯一依赖）。</summary>
public interface IBpmService : IDisposable
{
    Task<BpmDisplayState> GetDisplayStateAsync(
        string filePath,
        long knownFileSize = 0,
        DateTime? knownModifiedAtUtc = null,
        long knownDurationMs = 0,
        CancellationToken cancellationToken = default);

    Task<(BpmAnalysisResult Result, BpmDisplayState DisplayState)> MeasureAsync(
        string filePath,
        IProgress<BpmAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<BpmDisplayState> SaveManualAsync(
        string filePath,
        double? bpm,
        bool writeToFile,
        CancellationToken cancellationToken = default);

    Task<BpmDisplayState> WriteEffectiveToFileAsync(
        string filePath,
        double bpm,
        CancellationToken cancellationToken = default);

    Task<int> ClearMeasuredCacheAsync(CancellationToken cancellationToken = default);
    Task<BpmCacheStats> GetCacheStatsAsync(CancellationToken cancellationToken = default);
    BpmModelStatus GetModelStatus();
    void UnloadRuntime();
}

/// <summary>BPM 分析过程中面向用户的可恢复异常（缺模型、解码失败、不可测等）。</summary>
public sealed class BpmAnalysisException : Exception
{
    public BpmAnalysisException(string filePath, string friendlyMessage, Exception? innerException = null)
        : base(friendlyMessage, innerException)
    {
        FilePath = filePath;
        FriendlyMessage = friendlyMessage;
    }

    public string FilePath { get; }
    public string FriendlyMessage { get; }
}
