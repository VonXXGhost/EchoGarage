using EchoGarage.Core.Models;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Logging;

namespace EchoGarage.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly AppPaths _paths;
    private readonly JsonSettingsService _settings;

    public SettingsTests()
    {
        _paths = new AppPaths(_workspace.Combine("state"));
        _paths.EnsureCreated();
        _settings = new JsonSettingsService(_paths, new FileLogger(_paths));
    }

    [Fact]
    public async Task Load_CreatesDefaultSettingsFile()
    {
        await _settings.LoadAsync();

        Assert.True(File.Exists(_paths.SettingsFile));
        Assert.Equal(0.75, _settings.Current.Volume, 3);
        Assert.Equal(OutputMode.Shared, _settings.Current.OutputMode);
        Assert.True(_settings.Current.ShowAlbumPanel);
        Assert.Contains(_settings.Current.ColumnPresets, p => p.Name == "标准");
        Assert.False(string.IsNullOrWhiteSpace(_settings.Current.CacheDirectory));
        Assert.True(Directory.Exists(_settings.Current.CacheDirectory));
    }

    [Fact]
    public async Task Save_ClampsOutOfRangeValues()
    {
        await _settings.LoadAsync();
        _settings.Current.Volume = 5;
        _settings.Current.WindowWidth = 10;
        _settings.Current.WindowHeight = 10;
        _settings.Current.SidebarWidth = 5;
        await _settings.SaveAsync();

        var reloaded = new JsonSettingsService(_paths, new FileLogger(_paths));
        await reloaded.LoadAsync();

        Assert.Equal(1, reloaded.Current.Volume, 3);
        Assert.Equal(960, reloaded.Current.WindowWidth);
        Assert.Equal(600, reloaded.Current.WindowHeight);
        Assert.Equal(170, reloaded.Current.SidebarWidth);
    }

    [Fact]
    public async Task ExportAndImport_RoundTripsConfigurationOnly()
    {
        await _settings.LoadAsync();
        _settings.Current.OutputMode = OutputMode.Exclusive;
        _settings.Current.ShowAlbumPanel = false;
        _settings.Current.CacheDirectory = _workspace.Combine("custom-cache");
        Directory.CreateDirectory(_settings.Current.CacheDirectory);
        await _settings.SaveAsync();

        var exportPath = _workspace.Combine("export", "settings.json");
        await _settings.ExportAsync(exportPath);
        Assert.True(File.Exists(exportPath));

        _settings.Current.OutputMode = OutputMode.Shared;
        _settings.Current.ShowAlbumPanel = true;
        await _settings.SaveAsync();

        await _settings.ImportAsync(exportPath);
        Assert.Equal(OutputMode.Exclusive, _settings.Current.OutputMode);
        Assert.False(_settings.Current.ShowAlbumPanel);
        Assert.Equal(_workspace.Combine("custom-cache"), _settings.Current.CacheDirectory);
    }

    [Fact]
    public async Task Import_InvalidFileThrows()
    {
        await _settings.LoadAsync();
        var bad = _workspace.Combine("bad.json");
        await File.WriteAllTextAsync(bad, "{ not json");

        await Assert.ThrowsAnyAsync<Exception>(() => _settings.ImportAsync(bad));
    }

    [Fact]
    public async Task CorruptSettingsFile_FallsBackToDefaults()
    {
        await _settings.LoadAsync();
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ broken");

        var reloaded = new JsonSettingsService(_paths, new FileLogger(_paths));
        await reloaded.LoadAsync();

        Assert.Equal(0.75, reloaded.Current.Volume, 3);
    }

    [Fact]
    public async Task DataRootOverride_IsolatesAllPaths()
    {
        var root = _workspace.Combine("isolated");

        var paths = new AppPaths(root);
        paths.EnsureCreated();

        Assert.StartsWith(root, paths.ConfigDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(root, paths.DataDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(root, paths.LogsDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.True(paths.IsCustomRoot);
        Assert.False(paths.IsPortable);
    }

    [Fact]
    public async Task Save_NormalizesHotkeyBindings()
    {
        await _settings.LoadAsync();
        _settings.Current.Hotkeys =
        [
            new HotkeyBinding { Action = HotkeyAction.PlayPause, Gesture = "ctrl+space" },
            new HotkeyBinding { Action = HotkeyAction.Next, Gesture = "Ctrl+Space" },
            new HotkeyBinding { Action = HotkeyAction.PlayPause, Gesture = "Alt+F5", Global = true },
            new HotkeyBinding { Action = HotkeyAction.Stop, Gesture = "NotAKey" },
            new HotkeyBinding { Action = HotkeyAction.Mute, Gesture = "Ctrl+Alt" }
        ];
        await _settings.SaveAsync();

        var reloaded = new JsonSettingsService(_paths, new FileLogger(_paths));
        await reloaded.LoadAsync();

        var hotkey = Assert.Single(reloaded.Current.Hotkeys);
        Assert.Equal(HotkeyAction.PlayPause, hotkey.Action);
        Assert.Equal("Ctrl+Space", hotkey.Gesture);
        Assert.False(hotkey.Global);
    }

    [Fact]
    public async Task Load_LegacyThemeNamesAreMigrated()
    {
        await _settings.LoadAsync();
        var json = await File.ReadAllTextAsync(_paths.SettingsFile);

        // 旧配置里只有 Light / Dark 两个名字，必须能读出来且不能触发“损坏 → 全部重置”。
        await File.WriteAllTextAsync(_paths.SettingsFile, json.Replace("\"theme\": \"PureStudio\"", "\"theme\": \"Dark\""));
        var dark = new JsonSettingsService(_paths, new FileLogger(_paths));
        await dark.LoadAsync();
        Assert.Equal(ThemeKind.SlateStudio, dark.Current.Theme);

        await File.WriteAllTextAsync(_paths.SettingsFile, json.Replace("\"theme\": \"PureStudio\"", "\"theme\": \"Light\""));
        var light = new JsonSettingsService(_paths, new FileLogger(_paths));
        await light.LoadAsync();
        Assert.Equal(ThemeKind.PureStudio, light.Current.Theme);
    }

    [Fact]
    public async Task Load_UnknownThemeFallsBackToDefault()
    {
        await _settings.LoadAsync();
        var json = await File.ReadAllTextAsync(_paths.SettingsFile);
        await File.WriteAllTextAsync(_paths.SettingsFile, json.Replace("\"theme\": \"PureStudio\"", "\"theme\": \"NoSuchTheme\""));

        var reloaded = new JsonSettingsService(_paths, new FileLogger(_paths));
        await reloaded.LoadAsync();

        Assert.Equal(ThemeKind.PureStudio, reloaded.Current.Theme);
        // 其余配置不能被“损坏文件”分支重置。
        Assert.Equal(0.75, reloaded.Current.Volume, 3);
    }

    [Fact]
    public async Task Save_RoundTripsAllThemes()
    {
        await _settings.LoadAsync();

        foreach (var theme in Enum.GetValues<ThemeKind>())
        {
            _settings.Current.Theme = theme;
            await _settings.SaveAsync();

            var reloaded = new JsonSettingsService(_paths, new FileLogger(_paths));
            await reloaded.LoadAsync();
            Assert.Equal(theme, reloaded.Current.Theme);
        }
    }

    [Fact]
    public void ThemeCatalog_CoversAllKindsInOrder()
    {
        Assert.Equal(Enum.GetValues<ThemeKind>().Length, ThemeCatalog.All.Count);
        for (var index = 0; index < ThemeCatalog.All.Count; index++)
        {
            var theme = ThemeCatalog.All[index];
            Assert.Equal(index, (int)theme.Kind);
            Assert.Equal(theme.Kind, ThemeCatalog.ByIndex(index).Kind);
            Assert.False(string.IsNullOrWhiteSpace(theme.DisplayName));
            Assert.Equal(index, ThemeCatalog.IndexOf(theme.Kind));
        }

        Assert.Equal(ThemeKind.PureStudio, ThemeCatalog.ByIndex(-5).Kind);
        Assert.Equal(ThemeKind.SlateStudio, ThemeCatalog.ByIndex(99).Kind);
        Assert.False(ThemeCatalog.IsDark(ThemeKind.PureStudio));
        Assert.False(ThemeCatalog.IsDark(ThemeKind.WarmPaper));
        Assert.False(ThemeCatalog.IsDark(ThemeKind.NordicIce));
        Assert.True(ThemeCatalog.IsDark(ThemeKind.MidnightVinyl));
        Assert.True(ThemeCatalog.IsDark(ThemeKind.SlateStudio));
    }

    public void Dispose() => _workspace.Dispose();
}
