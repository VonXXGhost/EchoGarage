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
    public async Task Save_RoundTripsAllPlaybackModes()
    {
        await _settings.LoadAsync();

        foreach (var mode in Enum.GetValues<PlaybackMode>())
        {
            _settings.Current.PlaybackMode = mode;
            await _settings.SaveAsync();

            var reloaded = new JsonSettingsService(_paths, new FileLogger(_paths));
            await reloaded.LoadAsync();
            Assert.Equal(mode, reloaded.Current.PlaybackMode);
        }
    }

    [Fact]
    public async Task Load_MissingPlaybackModeDefaultsToSequential()
    {
        await _settings.LoadAsync();
        var json = await File.ReadAllTextAsync(_paths.SettingsFile);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        Assert.True(node.Remove("playbackMode"));
        await File.WriteAllTextAsync(_paths.SettingsFile, node.ToJsonString());

        var reloaded = new JsonSettingsService(_paths, new FileLogger(_paths));
        await reloaded.LoadAsync();

        Assert.Equal(PlaybackMode.Sequential, reloaded.Current.PlaybackMode);
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
    public async Task Load_DefaultsLoudnessTargetToMinusTen()
    {
        await _settings.LoadAsync();

        Assert.Equal(-10.0, _settings.Current.LoudnessDefaultTargetLufs, 3);
    }

    [Fact]
    public async Task Save_ClampsAndRoundsLoudnessDefaultTarget()
    {
        await _settings.LoadAsync();
        _settings.Current.LoudnessDefaultTargetLufs = -200;
        await _settings.SaveAsync();

        var reloaded = new JsonSettingsService(_paths, new FileLogger(_paths));
        await reloaded.LoadAsync();
        Assert.Equal(-60.0, reloaded.Current.LoudnessDefaultTargetLufs, 3);

        reloaded.Current.LoudnessDefaultTargetLufs = 4392.6;
        await reloaded.SaveAsync();

        var reloadedAgain = new JsonSettingsService(_paths, new FileLogger(_paths));
        await reloadedAgain.LoadAsync();
        Assert.Equal(0.0, reloadedAgain.Current.LoudnessDefaultTargetLufs, 3);

        reloadedAgain.Current.LoudnessDefaultTargetLufs = -11.26;
        await reloadedAgain.SaveAsync();

        var reloadedRounded = new JsonSettingsService(_paths, new FileLogger(_paths));
        await reloadedRounded.LoadAsync();
        Assert.Equal(-11.3, reloadedRounded.Current.LoudnessDefaultTargetLufs, 3);
    }

    [Fact]
    public async Task Load_MissingLoudnessTargetDefaultsToMinusTen()
    {
        await _settings.LoadAsync();
        var json = await File.ReadAllTextAsync(_paths.SettingsFile);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        Assert.True(node.Remove("loudnessDefaultTargetLufs"));
        await File.WriteAllTextAsync(_paths.SettingsFile, node.ToJsonString());

        var reloaded = new JsonSettingsService(_paths, new FileLogger(_paths));
        await reloaded.LoadAsync();

        Assert.Equal(-10.0, reloaded.Current.LoudnessDefaultTargetLufs, 3);
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

    [Fact]
    public async Task Load_CreatesDefaultTagNormalizationRules()
    {
        await _settings.LoadAsync();

        var rules = _settings.Current.TagNormalizationRules;
        Assert.Equal(2, rules.Count);
        Assert.Equal(TagNormalizationDefaults.AlbumEpSingleSuffixRuleId, rules[0].Id);
        Assert.Equal(TagNormalizationDefaults.IsoDateTimeRuleId, rules[1].Id);

        var json = await File.ReadAllTextAsync(_paths.SettingsFile);
        Assert.True(json.Contains("\"tagNormalizationRules\"", StringComparison.Ordinal));
        Assert.True(json.Contains(TagNormalizationDefaults.AlbumEpSingleSuffixRuleId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_EmptyTagNormalizationRules_DoesNotResurrectDefaults()
    {
        await _settings.LoadAsync();
        _settings.Current.TagNormalizationRules = [];
        await _settings.SaveAsync();

        var reloaded = new JsonSettingsService(_paths, new FileLogger(_paths));
        await reloaded.LoadAsync();

        Assert.Empty(reloaded.Current.TagNormalizationRules);
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 1 }")]
    [InlineData("{ \"schemaVersion\": 1, \"tagNormalizationRules\": null }")]
    public async Task Load_MissingTagNormalizationRules_FillsDefaults(string json)
    {
        await _settings.LoadAsync();
        await File.WriteAllTextAsync(_paths.SettingsFile, json);

        var reloaded = new JsonSettingsService(_paths, new FileLogger(_paths));
        await reloaded.LoadAsync();

        var rules = reloaded.Current.TagNormalizationRules;
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, rule => rule.Id == TagNormalizationDefaults.AlbumEpSingleSuffixRuleId);
        Assert.Contains(rules, rule => rule.Id == TagNormalizationDefaults.IsoDateTimeRuleId);
    }

    [Fact]
    public async Task Save_NormalizesTagNormalizationRules()
    {
        await _settings.LoadAsync();
        _settings.Current.TagNormalizationRules =
        [
            new TagNormalizationRule
            {
                Id = "   ",
                Name = "   ",
                TargetField = " Album ",
                Pattern = "  ^Demo$  ",
                Replacement = null!
            },
            new TagNormalizationRule
            {
                Id = "dup",
                Name = "  Dup  ",
                TargetField = "*",
                Pattern = "(",
                Replacement = string.Empty
            },
            new TagNormalizationRule
            {
                Id = "dup",
                Name = "Dup Second",
                TargetField = "comment",
                Pattern = "x"
            }
        ];
        await _settings.SaveAsync();

        var reloaded = new JsonSettingsService(_paths, new FileLogger(_paths));
        await reloaded.LoadAsync();

        var rules = reloaded.Current.TagNormalizationRules;
        Assert.Equal(3, rules.Count);
        Assert.False(string.IsNullOrWhiteSpace(rules[0].Id));
        Assert.Equal("规范化规则 1", rules[0].Name);
        Assert.Equal(TagFieldKeys.Album, rules[0].TargetField);
        Assert.Equal("^Demo$", rules[0].Pattern);
        Assert.Equal(string.Empty, rules[0].Replacement);
        Assert.Equal("dup", rules[1].Id);
        Assert.Equal("(", rules[1].Pattern); // 非法 pattern 保留给 UI 提示
        Assert.Equal("Dup Second", rules[2].Name);
        Assert.NotEqual("dup", rules[2].Id); // 重复 id 被替换
    }

    [Fact]
    public async Task ExportAndImport_RoundTripsTagNormalizationRules()
    {
        await _settings.LoadAsync();
        _settings.Current.TagNormalizationRules =
        [
            new TagNormalizationRule
            {
                Id = "custom-rule",
                Name = "Demo Rule",
                TargetField = TagFieldKeys.Comment,
                Pattern = @"(\d{4})-(\d{2})-(\d{2})",
                Replacement = "$3/$2/$1",
                Enabled = false
            }
        ];
        await _settings.SaveAsync();

        var exportPath = _workspace.Combine("export", "settings-tags.json");
        await _settings.ExportAsync(exportPath);

        _settings.Current.TagNormalizationRules = [];
        await _settings.SaveAsync();

        await _settings.ImportAsync(exportPath);

        var rule = Assert.Single(_settings.Current.TagNormalizationRules);
        Assert.Equal("custom-rule", rule.Id);
        Assert.Equal("Demo Rule", rule.Name);
        Assert.Equal(TagFieldKeys.Comment, rule.TargetField);
        Assert.Equal(@"(\d{4})-(\d{2})-(\d{2})", rule.Pattern);
        Assert.Equal("$3/$2/$1", rule.Replacement);
        Assert.False(rule.Enabled);
    }

    [Fact]
    public void AppSettings_Clone_DeepCopiesTagNormalizationRules()
    {
        var settings = new AppSettings();
        var clone = settings.Clone();

        Assert.NotSame(settings.TagNormalizationRules, clone.TagNormalizationRules);
        Assert.Equal(2, clone.TagNormalizationRules.Count);
        Assert.NotSame(settings.TagNormalizationRules[0], clone.TagNormalizationRules[0]);

        clone.TagNormalizationRules[0].Name = "changed";
        clone.TagNormalizationRules.Add(new TagNormalizationRule { Id = "extra", Pattern = "x" });

        Assert.NotEqual("changed", settings.TagNormalizationRules[0].Name);
        Assert.Equal(2, settings.TagNormalizationRules.Count);
    }

    public void Dispose() => _workspace.Dispose();
}
