using System.Diagnostics;
using EchoGarage.App.Services;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Infrastructure.Audio;
using EchoGarage.Infrastructure.Caching;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Conversion;
using EchoGarage.Infrastructure.Data;
using EchoGarage.Infrastructure.Files;
using EchoGarage.Infrastructure.Library;
using EchoGarage.Infrastructure.Logging;
using EchoGarage.Infrastructure.Tagging;
using EchoGarage.Infrastructure.WebTags;
using EchoGarage.Infrastructure.Windows;

namespace EchoGarage.App;

/// <summary>
/// Manual composition root. Keeping wiring in one place makes it obvious what the
/// app depends on and keeps UI code free of infrastructure details.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    /// <summary>波形缓存目录名（相对 CacheDirectory，ADR-015 §7.1）。</summary>
    public const string WaveformCacheFolderName = "Waveforms";

    private readonly Stopwatch _lifetime = Stopwatch.StartNew();
    private readonly SpectrumAnalyzer _spectrumAnalyzer;
    private bool _shutdown;
    private FileWaveformCache? _waveformCacheImpl;
    private FileCoverCache? _coverCacheImpl;

    public AppHost(AppPaths? paths = null)
    {
        Paths = paths ?? new AppPaths();
        Paths.EnsureCreated();
        Logger = new FileLogger(Paths);
        NowPlayingGlow = new NowPlayingGlowService(Logger);
        Settings = new JsonSettingsService(Paths, Logger);
        Database = new SqliteDatabase(Paths, Logger);
        LibraryRepository = new SqliteLibraryRepository(Database, Logger);
        TagSuggestions = new TagSuggestionService(LibraryRepository, Logger);
        PlaylistRepository = new SqlitePlaylistRepository(Database, Logger);
        TagService = new TagLibTagService(Logger);
        Converter = new AudioConverter(TagService, Logger, Paths);
        Loudness = new LoudnessAnalyzer(Logger);
        Scanner = new LibraryScanner(LibraryRepository, TagService, Logger);
        Watcher = new LibraryFileWatcher(Logger);
        // 播放可视化数据源（ADR-023）：播放器把解码后的样本喂给它，右栏控件按渲染帧拉取。
        _spectrumAnalyzer = new SpectrumAnalyzer(Logger);
        Player = new WasapiAudioPlayer(Logger, _spectrumAnalyzer);
        Files = new FileOperationService(Logger);
        // 文件关联注册结果需要写日志（哪些格式因注册表项只读而只做了 Capabilities 注册）。
        Associations = new FileAssociationService(logger: Logger);

        var webTagHttp = new WebTagHttpClientFactory();
        WebTags = new WebTagService(
            [new AppleMusicWebTagSource(webTagHttp, Settings, Logger)],
            Settings,
            webTagHttp,
            Logger);
    }

    public AppPaths Paths { get; }
    public IAppLogger Logger { get; }
    public ISettingsService Settings { get; }
    public SqliteDatabase Database { get; }
    public ILibraryRepository LibraryRepository { get; }
    /// <summary>标签编辑候选项（艺术家/专辑艺术家/作曲家/流派）的缓存入口，媒体库变化后需 Invalidate。</summary>
    public ITagSuggestionService TagSuggestions { get; }
    public IPlaylistRepository PlaylistRepository { get; }
    public ITagService TagService { get; }
    public IAudioConverter Converter { get; }
    /// <summary>EBU R128 / BS.1770 响度测量（ADR-028）；计算与调整共用。</summary>
    public ILoudnessAnalyzer Loudness { get; }
    public ILibraryScanner Scanner { get; }
    public IFileWatcherService Watcher { get; }
    public IAudioPlayer Player { get; }
    /// <summary>右栏「播放可视化」的数据源（ADR-023）；由 MainViewModel 在启动与设置变更时 Apply。</summary>
    public ISpectrumSource Spectrum => _spectrumAnalyzer;
    /// <summary>复制 / 移动 / 删除到回收站（ADR-021）。</summary>
    public IFileOperationService Files { get; }
    /// <summary>Web 搜索标签源服务（ADR-022）；构造时不联网。</summary>
    public IWebTagService WebTags { get; }
    public ICoverCache? Covers { get; private set; }
    public CoverImageService CoverImages { get; private set; } = null!;
    /// <summary>「正在播放」页封面柔光取色（按封面缓存键记忆原始主/次色）。</summary>
    public NowPlayingGlowService NowPlayingGlow { get; }
    public IWaveformService Waveforms { get; private set; } = null!;
    public IWaveformCache WaveformCache { get; private set; } = null!;
    public FileAssociationService Associations { get; }

    public AppSettings SettingsSnapshot => Settings.Current;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Settings.LoadAsync(cancellationToken);
        App.Timeline.Mark("settings-loaded");

        await Database.InitializeAsync(cancellationToken);
        await PlaylistRepository.EnsureDefaultsAsync(cancellationToken);
        App.Timeline.Mark("database-ready");

        AudioConverter.CleanupStaleTempFolders(Paths, Logger);

        var coverCache = new FileCoverCache(Settings.Current.CacheDirectory, Logger)
        {
            LimitMb = Math.Max(0, Settings.Current.CoverCacheLimitMb),
        };
        _coverCacheImpl = coverCache;
        Covers = coverCache;
        CoverImages = new CoverImageService(Covers, Logger);

        var waveformCache = new FileWaveformCache(
            Path.Combine(Settings.Current.CacheDirectory, WaveformCacheFolderName),
            Logger)
        {
            LimitMb = Math.Max(0, Settings.Current.WaveformCacheLimitMb),
        };
        _waveformCacheImpl = waveformCache;
        WaveformCache = waveformCache;
        // 350 ms 启动延迟（ADR-015 §6.10 建议 300–500 ms）：切歌瞬间播放器正在解码 / 建缓冲。
        Waveforms = new WaveformService(
            new WaveformAnalyzer(),
            waveformCache,
            Logger,
            analysisStartDelay: TimeSpan.FromMilliseconds(350));

        Player.ConfigureOutput(Settings.Current.OutputDeviceId, Settings.Current.OutputMode);
        Player.Volume = Settings.Current.Volume;

        Logger.Info($"Host initialized in {_lifetime.ElapsedMilliseconds} ms. " +
                    $"Config={Paths.ConfigDirectory}, Data={Paths.DataDirectory}, Cache={Covers.CoversDirectory}");
    }

    /// <summary>
    /// Warms Media Foundation, WASAPI and the SQLite connection pool on a background
    /// thread so the first user action (play, seek, playlist switch) feels instant.
    /// </summary>
    public void StartWarmup()
    {
        _ = Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                Player.Warmup();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Audio warmup failed: {ex.Message}");
            }

            try
            {
                using var connection = Database.OpenConnectionAsync().GetAwaiter().GetResult();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM tracks;";
                command.ExecuteScalar();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Database warmup failed: {ex.Message}");
            }

            TrimWaveformCache();
            TrimCoverCache();

            Logger.Info($"Warmup finished in {stopwatch.ElapsedMilliseconds} ms.");
        });
    }

    /// <summary>
    /// Applies the configured waveform cache limit and trims the directory in the
    /// background. Called at startup and whenever the limit changes in settings.
    /// </summary>
    public void TrimWaveformCache()
    {
        if (_waveformCacheImpl is not { } cache)
        {
            return;
        }

        cache.LimitMb = Math.Max(0, Settings.Current.WaveformCacheLimitMb);
        _ = Task.Run(async () =>
        {
            try
            {
                var removed = await cache.TrimAsync();
                if (removed > 0)
                {
                    Logger.Info($"Waveform cache trimmed: removed {removed} file(s), limit {cache.LimitMb} MB.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Waveform cache trim failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 应用封面缓存上限并在后台裁剪。启动预热与设置里改动上限时各调一次。
    /// </summary>
    public void TrimCoverCache()
    {
        if (_coverCacheImpl is not { } cache)
        {
            return;
        }

        cache.LimitMb = Math.Max(0, Settings.Current.CoverCacheLimitMb);
        _ = Task.Run(async () =>
        {
            try
            {
                var removed = await cache.TrimAsync();
                if (removed > 0)
                {
                    Logger.Info($"Cover cache trimmed: removed {removed} file(s), limit {cache.LimitMb} MB.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Cover cache trim failed: {ex.Message}");
            }
        });
    }

    public async Task ApplyCacheDirectoryAsync(string newDirectory, CancellationToken cancellationToken = default)
    {
        var oldDirectory = Covers?.CoversDirectory;
        var oldWaveformDirectory = WaveformCache?.Directory;
        Settings.Current.CacheDirectory = newDirectory;
        await Settings.SaveAsync(cancellationToken);
        if (Covers is not null)
        {
            await Covers.RelocateAsync(newDirectory, cancellationToken);
            Settings.Current.CacheDirectory = Covers.CoversDirectory;
            CoverImages.ClearMemoryCache();
            await Settings.SaveAsync(cancellationToken);
        }

        if (WaveformCache is not null)
        {
            await WaveformCache.RelocateAsync(
                Path.Combine(newDirectory, WaveformCacheFolderName),
                cancellationToken);
        }

        Logger.Info($"Cover cache moved from {oldDirectory} to {newDirectory}; " +
                    $"waveform cache moved from {oldWaveformDirectory} to {WaveformCache?.Directory}");
    }

    public async ValueTask DisposeAsync()
    {
        Shutdown();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Synchronous shutdown path: settings are written without awaiting continuations,
    /// because the dispatcher is already shutting down when this runs.
    /// </summary>
    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        try
        {
            Settings.Current.Volume = Player.Volume;
            if (Settings is JsonSettingsService json)
            {
                json.SaveImmediately();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Saving settings on exit failed: {ex.Message}");
        }

        try
        {
            Watcher.Dispose();
            Player.Dispose();
            _spectrumAnalyzer.Dispose();
            CoverImages.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Disposing services failed: {ex.Message}");
        }

        Logger.Info($"Application exiting after {_lifetime.Elapsed.TotalMinutes:F1} minutes.");
    }
}
