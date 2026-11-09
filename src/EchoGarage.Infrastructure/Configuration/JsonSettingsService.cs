using System.Text.Json;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Infrastructure.Configuration;

public sealed class JsonSettingsService : ISettingsService
{
    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;
    private AppSettings _current = new();

    public JsonSettingsService(AppPaths paths, IAppLogger logger)
    {
        _paths = paths;
        _logger = logger;
        _paths.EnsureCreated();
    }

    public AppSettings Current => _current;
    public event EventHandler<AppSettings>? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        AppSettings settings;
        if (!File.Exists(_paths.SettingsFile))
        {
            settings = CreateDefaultSettings();
            _logger.Info($"No settings file found; creating defaults at {_paths.SettingsFile}");
        }
        else
        {
            try
            {
                await using var stream = File.OpenRead(_paths.SettingsFile);
                settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonDefaults.Options, cancellationToken)
                    ?? CreateDefaultSettings();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to read settings file {_paths.SettingsFile}; using defaults.", ex);
                TryBackupCorruptSettings();
                settings = CreateDefaultSettings();
            }
        }

        _current = Normalize(settings);
        await SaveInternalAsync(cancellationToken);
        Changed?.Invoke(this, _current);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        _current = Normalize(_current);
        await SaveInternalAsync(cancellationToken);
        Changed?.Invoke(this, _current);
    }

    /// <summary>
    /// Synchronous write used during process shutdown, when awaiting continuations is unsafe.
    /// </summary>
    public void SaveImmediately()
    {
        _current = Normalize(_current);
        Directory.CreateDirectory(_paths.ConfigDirectory);
        var temp = _paths.SettingsFile + ".tmp";
        var json = JsonSerializer.Serialize(_current, JsonDefaults.Options);
        File.WriteAllText(temp, json);
        File.Move(temp, _paths.SettingsFile, true);
    }

    public async Task ExportAsync(string path, CancellationToken cancellationToken = default)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(Normalize(_current), JsonDefaults.Options);
        await File.WriteAllTextAsync(path, json, cancellationToken);
        _logger.Info($"Settings exported to {path}");
    }

    public async Task ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var imported = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonDefaults.Options, cancellationToken)
            ?? throw new InvalidDataException("配置文件为空或格式不正确。");

        _current = Normalize(imported);
        await SaveInternalAsync(cancellationToken);
        _logger.Info($"Settings imported from {path}");
        Changed?.Invoke(this, _current);
    }

    private async Task SaveInternalAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        var temp = _paths.SettingsFile + ".tmp";
        var json = JsonSerializer.Serialize(_current, JsonDefaults.Options);
        await File.WriteAllTextAsync(temp, json, cancellationToken);
        File.Move(temp, _paths.SettingsFile, true);
    }

    private AppSettings CreateDefaultSettings()
    {
        var settings = new AppSettings
        {
            CacheDirectory = _paths.DefaultCacheDirectory,
            ColumnPresets = ViewLayoutFactory.CreateBuiltInPresets(),
            LibraryLayout = ViewLayoutFactory.CreateDefault()
        };
        Directory.CreateDirectory(settings.CacheDirectory);
        return settings;
    }

    private AppSettings Normalize(AppSettings settings)
    {
        settings.SchemaVersion = Math.Max(1, settings.SchemaVersion);
        settings.Volume = Math.Clamp(settings.Volume, 0, 1);
        settings.WindowWidth = Math.Clamp(settings.WindowWidth, 960, 10000);
        settings.WindowHeight = Math.Clamp(settings.WindowHeight, 600, 10000);
        settings.SidebarWidth = Math.Clamp(settings.SidebarWidth, 170, 420);
        settings.AlbumPanelWidth = Math.Clamp(settings.AlbumPanelWidth, 240, 520);
        settings.CacheDirectory = _paths.ResolveCacheDirectory(settings.CacheDirectory);
        settings.LibraryLayout = ViewLayoutFactory.Normalize(settings.LibraryLayout);
        settings.ColumnPresets ??= [];

        var builtIns = ViewLayoutFactory.CreateBuiltInPresets();
        foreach (var builtIn in builtIns)
        {
            if (!settings.ColumnPresets.Any(p => string.Equals(p.Name, builtIn.Name, StringComparison.OrdinalIgnoreCase)))
            {
                settings.ColumnPresets.Add(builtIn);
            }
        }

        foreach (var preset in settings.ColumnPresets)
        {
            preset.Name = preset.Name.Trim();
            preset.Columns = ViewLayoutFactory.Normalize(new PlaylistViewLayout { Columns = preset.Columns }).Columns;
        }

        settings.ColumnPresets = settings.ColumnPresets
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        try
        {
            Directory.CreateDirectory(settings.CacheDirectory);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Configured cache directory is unavailable ({settings.CacheDirectory}): {ex.Message}. Falling back to default.");
            settings.CacheDirectory = _paths.DefaultCacheDirectory;
            Directory.CreateDirectory(settings.CacheDirectory);
        }

        return settings;
    }

    private void TryBackupCorruptSettings()
    {
        try
        {
            var backup = _paths.SettingsFile + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(_paths.SettingsFile, backup, true);
        }
        catch
        {
            // Best effort.
        }
    }
}
