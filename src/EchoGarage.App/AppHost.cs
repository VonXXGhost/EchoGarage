using System.Diagnostics;
using EchoGarage.App.Services;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Infrastructure.Audio;
using EchoGarage.Infrastructure.Caching;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Data;
using EchoGarage.Infrastructure.Library;
using EchoGarage.Infrastructure.Logging;
using EchoGarage.Infrastructure.Tagging;
using EchoGarage.Infrastructure.Windows;

namespace EchoGarage.App;

/// <summary>
/// Manual composition root. Keeping wiring in one place makes it obvious what the
/// app depends on and keeps UI code free of infrastructure details.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly Stopwatch _lifetime = Stopwatch.StartNew();
    private bool _shutdown;

    public AppHost(AppPaths? paths = null)
    {
        Paths = paths ?? AppPaths.CreateForStartup();
        Paths.EnsureCreated();
        Logger = new FileLogger(Paths);
        Settings = new JsonSettingsService(Paths, Logger);
        Database = new SqliteDatabase(Paths, Logger);
        LibraryRepository = new SqliteLibraryRepository(Database, Logger);
        PlaylistRepository = new SqlitePlaylistRepository(Database, Logger);
        TagService = new TagLibTagService(Logger);
        Scanner = new LibraryScanner(LibraryRepository, TagService, Logger);
        Watcher = new LibraryFileWatcher(Logger);
        Player = new WasapiAudioPlayer(Logger);
    }

    public AppPaths Paths { get; }
    public IAppLogger Logger { get; }
    public ISettingsService Settings { get; }
    public SqliteDatabase Database { get; }
    public ILibraryRepository LibraryRepository { get; }
    public IPlaylistRepository PlaylistRepository { get; }
    public ITagService TagService { get; }
    public ILibraryScanner Scanner { get; }
    public IFileWatcherService Watcher { get; }
    public IAudioPlayer Player { get; }
    public ICoverCache? Covers { get; private set; }
    public CoverImageService CoverImages { get; private set; } = null!;
    public FileAssociationService Associations { get; } = new();

    public AppSettings SettingsSnapshot => Settings.Current;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Settings.LoadAsync(cancellationToken);
        App.Timeline.Mark("settings-loaded");

        await Database.InitializeAsync(cancellationToken);
        await PlaylistRepository.EnsureDefaultsAsync(cancellationToken);
        App.Timeline.Mark("database-ready");

        Covers = new FileCoverCache(Settings.Current.CacheDirectory, Logger);
        CoverImages = new CoverImageService(Covers, Logger);

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

            Logger.Info($"Warmup finished in {stopwatch.ElapsedMilliseconds} ms.");
        });
    }

    public async Task ApplyCacheDirectoryAsync(string newDirectory, CancellationToken cancellationToken = default)
    {
        var oldDirectory = Covers?.CoversDirectory;
        Settings.Current.CacheDirectory = newDirectory;
        await Settings.SaveAsync(cancellationToken);
        if (Covers is not null)
        {
            await Covers.RelocateAsync(newDirectory, cancellationToken);
            Settings.Current.CacheDirectory = Covers.CoversDirectory;
            CoverImages.ClearMemoryCache();
            await Settings.SaveAsync(cancellationToken);
        }

        Logger.Info($"Cover cache moved from {oldDirectory} to {newDirectory}");
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
            CoverImages.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Disposing services failed: {ex.Message}");
        }

        Logger.Info($"Application exiting after {_lifetime.Elapsed.TotalMinutes:F1} minutes.");
    }
}
