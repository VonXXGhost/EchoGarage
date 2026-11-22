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

    /// <summary>
    /// 读取标签编辑补全候选：媒体库中已解析歌曲出现过的艺术家 / 专辑艺术家 / 作曲家 / 流派，
    /// 按字段内出现频率降序、每字段最多 <paramref name="limitPerField"/> 个。
    /// 仅统计 <c>IsInLibrary = 1 AND IsMissing = 0</c> 的缓存行，不访问音频文件。
    /// </summary>
    Task<TagSuggestionValues> GetTagSuggestionValuesAsync(
        int limitPerField = TagSuggestionValues.DefaultLimit,
        CancellationToken cancellationToken = default);
}

public interface IPlaylistRepository
{
    Task EnsureDefaultsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken cancellationToken = default);
    Task<Playlist?> GetDefaultPlaylistAsync(CancellationToken cancellationToken = default);
    Task<Playlist> CreatePlaylistAsync(string name, PlaylistKind kind, SmartPlaylistDefinition? definition,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// 批量更新播放列表的显示顺序：按 <paramref name="orderedPlaylistIds"/> 的先后依次为非默认播放列表写入
    /// <c>SortOrder = index + 1</c>；默认播放列表固定排在最前，<c>SortOrder</c> 保持 0，即使出现在列表中也会被跳过。
    /// 整个更新在一个事务里完成；空列表直接返回，不访问数据库。
    /// </summary>
    Task UpdatePlaylistOrderAsync(IReadOnlyList<long> orderedPlaylistIds,
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
    /// <summary>
    /// 按歌曲路径从所有普通播放列表删除条目（智能播放列表没有 <c>playlist_items</c> 行，天然不受影响）。
    /// 用于文件删除到回收站后同步播放列表（ADR-021（A1 修订））。路径先经 <see cref="PathUtil.TryNormalize(string, out string)"/>
    /// 规范化并按 Windows 语义忽略大小写匹配；空列表直接返回 0，不访问数据库。返回实际删除的行数。
    /// </summary>
    Task<int> RemovePlaylistItemsByPathAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
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

/// <summary>
/// 标签编辑候选项的读取/缓存入口。底层从媒体库缓存（library.db）查询，
/// UI 只在需要时异步取一次快照；媒体库数据变化后由应用层调用 <see cref="Invalidate"/>。
/// </summary>
public interface ITagSuggestionService
{
    /// <summary>取候选值快照；首次调用会查询数据库，之后命中内存缓存。</summary>
    Task<TagSuggestionValues> GetAsync(CancellationToken cancellationToken = default);
    /// <summary>让缓存失效；下次 <see cref="GetAsync"/> 会重新查询媒体库。</summary>
    void Invalidate();
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

    /// <summary>
    /// 无阻塞播放快照（UI 线程用）。加载 / 切换输出期间播放器内部锁可能被后台线程持有数百
    /// 毫秒；返回 false 表示本次拿不到快照，调用方应跳过本帧而不是阻塞 UI 线程。
    /// </summary>
    bool TryReadPlaybackSnapshot(out AudioPlaybackState state, out TimeSpan position, out TimeSpan duration);
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

/// <summary>
/// 输出设备热插拔监视（Windows：IMMNotificationClient + 周期兜底重扫）。
/// <see cref="DevicesChanged"/> 在后台线程触发，UI 订阅方必须自行切回 Dispatcher。
/// </summary>
public interface IAudioDeviceMonitor : IDisposable
{
    event EventHandler<AudioDevicesChangedEventArgs>? DevicesChanged;

    /// <summary>启动后台监视线程；重复调用无副作用。</summary>
    void Start();

    /// <summary>停止后台线程并注销系统通知回调；可重复调用，之后仍可再次 Start。</summary>
    void Stop();
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

/// <summary>
/// 音频样本抽头（ADR-023 §6.2）：播放器在重采样 / 声道转换之后、音量调节之前把样本交给可视化引擎，
/// 因此静音时仍能出图（符合 ADR-012 的静音验收要求）。实现运行在音频线程上，必须无锁、无阻塞、不分配。
/// </summary>
public interface IAudioSampleSink
{
    /// <summary>输出链建立时调用，告知实际的输出采样率与声道数。</summary>
    void Configure(int sampleRate, int channels);

    /// <summary>
    /// 音频线程回调。<paramref name="samples"/> 为交错样本（帧数 = count / channels）。
    /// 只在该调用内有效，不得保存引用。
    /// </summary>
    void Write(float[] samples, int offset, int count);

    /// <summary>跳转 / 停止 / 换曲：丢弃已缓冲的旧样本与弹道状态。</summary>
    void Reset();
}

/// <summary>
/// 频谱数据源（ADR-023 §6.3）：由 UI 渲染线程每帧调用 <see cref="NextFrame"/> 拉取一帧，分析在调用内
/// 同步完成（4096 点 FFT 约 0.06 ms，远小于一帧预算），因此不需要后台线程与跨线程发布。
/// 参数通过 <see cref="Apply"/> 整体替换，可在任意线程调用。
/// </summary>
public interface ISpectrumSource
{
    /// <summary>应用新的可视化设置；下一帧生效。</summary>
    void Apply(VisualizationSettings settings);

    /// <summary>重置弹道与已缓冲样本（跳转 / 换曲）。</summary>
    void Reset();

    /// <summary>
    /// 计算并返回一帧。<paramref name="dtSeconds"/> 是距上一帧的秒数，用于弹道推进。
    /// 必须在渲染线程（UI 线程）调用，不要在工作线程调用。
    /// </summary>
    SpectrumFrame NextFrame(double dtSeconds);

    /// <summary>诊断统计（日志 / 性能验收用）。</summary>
    SpectrumStats Stats { get; }
}

/// <summary>频谱分析的运行统计（ADR-023 §11）：帧数、累计与单帧最大分析耗时、采样率。</summary>
public readonly record struct SpectrumStats(
    long Frames,
    double TotalMilliseconds,
    double MaxMilliseconds,
    int SampleRate,
    int BandCount)
{
    /// <summary>平均每帧分析耗时（毫秒）；尚无数据时为 0。</summary>
    public double AverageMilliseconds => Frames > 0 ? TotalMilliseconds / Frames : 0.0;
}
