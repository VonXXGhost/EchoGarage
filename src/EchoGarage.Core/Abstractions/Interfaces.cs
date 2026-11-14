using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Core.Abstractions;

public interface ILibraryRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryFolder>> GetFoldersAsync(CancellationToken cancellationToken = default);
    Task<LibraryFolder> AddFolderAsync(string path, CancellationToken cancellationToken = default);
    Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetLibraryTracksAsync(long? folderId = null, CancellationToken cancellationToken = default);
    Task<int> GetLibraryTrackCountAsync(CancellationToken cancellationToken = default);
    /// <summary>Number of library tracks whose cached metadata is unusable (currently zero duration).</summary>
    Task<int> GetIncompleteTrackCountAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetTracksInFolderAsync(string folderPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetTracksInDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrackState>> GetTrackStatesAsync(long? folderId = null, CancellationToken cancellationToken = default);
    Task<Track?> GetTrackByPathAsync(string path, CancellationToken cancellationToken = default);
    Task UpsertTrackAsync(Track track, CancellationToken cancellationToken = default);
    Task UpsertTracksAsync(IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default);
    Task MarkTrackMissingAsync(string path, CancellationToken cancellationToken = default);
    Task MarkTracksMissingAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    /// <summary>
    /// 文件被移动后同步数据库路径（同时更新 playlist_items.TrackPath）。<paramref name="folderId"/> 为
    /// null 表示新路径不在任何媒体库文件夹下（IsInLibrary = 0）。ADR-021。
    /// </summary>
    Task UpdateTrackPathAsync(string oldPath, string newPath, long? folderId,
        CancellationToken cancellationToken = default);
    Task MarkTracksMissingUnderFolderAsync(string folderPath, CancellationToken cancellationToken = default);
    Task UpdateFolderScanTimeAsync(long folderId, DateTime scannedAtUtc, CancellationToken cancellationToken = default);
    Task RecordPlaybackAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<long, int>> GetFolderTrackCountsAsync(CancellationToken cancellationToken = default);
}

public interface IPlaylistRepository
{
    Task EnsureDefaultsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken cancellationToken = default);
    Task<Playlist?> GetDefaultPlaylistAsync(CancellationToken cancellationToken = default);
    Task<Playlist> CreatePlaylistAsync(string name, PlaylistKind kind, SmartPlaylistDefinition? definition,
        CancellationToken cancellationToken = default);
    Task RenamePlaylistAsync(long playlistId, string name, CancellationToken cancellationToken = default);
    Task DeletePlaylistAsync(long playlistId, CancellationToken cancellationToken = default);
    Task UpdatePlaylistLayoutAsync(long playlistId, string layoutJson, CancellationToken cancellationToken = default);
    Task UpdatePlaylistRulesAsync(long playlistId, SmartPlaylistDefinition definition,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(Playlist playlist, CancellationToken cancellationToken = default);
    Task AddPlaylistItemsAsync(long playlistId, IReadOnlyList<Track> tracks,
        CancellationToken cancellationToken = default);
    Task RemovePlaylistItemsAsync(IReadOnlyList<long> playlistItemIds, CancellationToken cancellationToken = default);
    Task ClearPlaylistAsync(long playlistId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<long, int>> GetPlaylistCountsAsync(IReadOnlyList<Playlist> playlists,
        CancellationToken cancellationToken = default);
}

public interface ITagService
{
    /// <summary>
    /// Reads tags and audio properties. Set <paramref name="includeCover"/> to false while
    /// scanning the library: loading embedded pictures for every file is the dominant scan cost.
    /// </summary>
    AudioTagData Read(string path, bool includeCover = true);
    void Write(string path, TagEditModel edit);
}

public interface ICoverCache
{
    string CoversDirectory { get; }
    string? Resolve(string? coverKey);
    Task<string?> EnsureAsync(CoverRequest request, CancellationToken cancellationToken = default);
    void Invalidate(string? coverKey);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task RelocateAsync(string newRoot, CancellationToken cancellationToken = default);
}

public interface ILibraryScanner
{
    Task<ScanResult> ScanFolderAsync(LibraryFolder folder, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<Track?> IndexFileAsync(string path, bool inLibrary, long? folderId = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> IndexFilesAsync(IEnumerable<string> paths, bool inLibrary,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
}

public interface IFileWatcherService : IDisposable
{
    event EventHandler<LibraryTrackChangedEventArgs>? Changed;
    bool IsRunning { get; }
    void Start(IReadOnlyList<LibraryFolder> folders);
    void Stop();
}

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? Changed;
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    Task ExportAsync(string path, CancellationToken cancellationToken = default);
    Task ImportAsync(string path, CancellationToken cancellationToken = default);
}

public interface IAudioPlayer : IDisposable
{
    event EventHandler<AudioPlaybackStateChangedEventArgs>? StateChanged;
    event EventHandler? PlaybackEnded;
    AudioPlaybackState State { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    string? CurrentTrackPath { get; }
    double Volume { get; set; }
    string OutputDescription { get; }
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
    void Warmup();
    Task LoadAsync(string path, bool playWhenReady, CancellationToken cancellationToken = default);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);

    void ConfigureOutput(string? deviceId, OutputMode mode);
}

public interface IAppLogger
{
    string LogDirectory { get; }
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
    void Debug(string message);
}

/// <summary>
/// 波形分析器（ADR-015 §8.1）：解码 → 单声道 → STFT → 频段 RMS → 包络 → 列。
/// 实现本身不得调用 <see cref="Task.Run(Action)"/>：由 <see cref="IWaveformService"/> 包在后台线程里，
/// 这样测试可以直接同步调用它。
/// </summary>
public interface IWaveformAnalyzer
{
    /// <summary>算法版本；写进缓存头，版本变化即失效重算。</summary>
    int Version { get; }

    /// <summary>
    /// 分析整个文件。渐进快照（<see cref="WaveformPeaks.ValidCount"/> &lt; ColumnCount）通过
    /// <paramref name="progress"/> 在分析线程上发布；返回的快照一定是完整（IsComplete）的。
    /// </summary>
    Task<WaveformPeaks> AnalyzeAsync(
        string filePath,
        IProgress<WaveformPeaks>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>EGWV 波形缓存（ADR-015 §7）：只读写派生数据，损坏/过期一律返回 null 并删除重建。</summary>
public interface IWaveformCache
{
    /// <summary>波形缓存目录（<c>&lt;CacheDirectory&gt;\Waveforms</c>）。</summary>
    string Directory { get; }

    /// <summary>命中返回完整快照；未命中、损坏、过期或 I/O 失败都返回 null（不抛）。</summary>
    Task<WaveformPeaks?> TryLoadAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>原子写入（.tmp + Move）。只应收到完整快照。</summary>
    Task SaveAsync(string filePath, WaveformPeaks peaks, CancellationToken cancellationToken = default);

    /// <summary>删除单个文件的缓存（文件被替换、重新打标签后调用）。</summary>
    void Invalidate(string filePath);

    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>缓存目录整体迁移（设置里改缓存位置时调用）。</summary>
    Task RelocateAsync(string newRoot, CancellationToken cancellationToken = default);

    /// <summary>按当前上限立即裁剪缓存（设置页改上限后调用）。返回删除的文件数。</summary>
    Task<int> TrimAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 波形服务（ADR-015 §7.4 / §8.1）：缓存优先 → 单飞分析 → 写缓存；失败记 Warn + 会话内负缓存。
/// App 层只依赖这个接口；缓存命中时 <c>progress</c> 不触发，一次返回完整快照。
/// </summary>
public interface IWaveformService
{
    /// <summary>取某文件的波形；分析失败返回 null（同一路径本次运行不再重试）。</summary>
    Task<WaveformPeaks?> GetAsync(
        string filePath,
        IProgress<WaveformPeaks>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>失效内存与磁盘缓存（会清掉负缓存标记，允许重新尝试）。</summary>
    void Invalidate(string filePath);

    Task ClearAsync(CancellationToken cancellationToken = default);

    Task RelocateAsync(string newRoot, CancellationToken cancellationToken = default);
}
