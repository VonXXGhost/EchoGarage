using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EchoGarage.App.Controls;
using EchoGarage.App.Services;
using EchoGarage.App.ViewModels;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Conversion;
using EchoGarage.Infrastructure.Windows;

namespace EchoGarage.App.Views;

public partial class SettingsWindow : ThemedWindow
{
    private static readonly (HotkeyAction Action, string Label)[] HotkeyActions =
    [
        (HotkeyAction.PlayPause, "播放/暂停"),
        (HotkeyAction.Previous, "上一首"),
        (HotkeyAction.Next, "下一首"),
        (HotkeyAction.Stop, "停止"),
        (HotkeyAction.Mute, "静音切换"),
        (HotkeyAction.OpenSearch, "打开高级搜索"),
        (HotkeyAction.OpenTagProperties, "打开标签属性")
    ];

    /// <summary>Built-in shortcuts that are always active; shown read-only on the hotkey page.</summary>
    private static readonly BuiltInHotkeyRow[] BuiltInHotkeys =
    [
        new("媒体键：播放/暂停", "MediaPlayPause"),
        new("媒体键：上一首", "MediaPreviousTrack"),
        new("媒体键：下一首", "MediaNextTrack"),
        new("媒体键：停止", "MediaStop"),
        new("播放/暂停", "Space"),
        new("播放选中项", "Enter"),
        new("打开标签属性", "Alt+Enter"),
        new("从媒体库移除", "Delete"),
        new("添加文件", "Ctrl+O"),
        new("重新扫描媒体库", "F5"),
        new("静音切换", "Ctrl+M"),
        new("打开高级搜索", "Ctrl+F")
    ];

    private readonly AppHost _host;
    private readonly MainViewModel _vm;
    private readonly List<HotkeyEditRow> _hotkeyRows = [];
    private readonly List<PresetHotkeyRow> _presetHotkeyRows = [];
    private readonly ObservableCollection<TagNormalizationEditRow> _tagNormalizationRows = [];
    private bool _loadingHotkeys;
    private bool _loadingTagNormalizationRules;
    private bool _loadingCache;
    private bool _loadingWebSettings;
    private bool _loadingVisualizer;

    public SettingsWindow(AppHost host, MainViewModel vm)
    {
        _host = host;
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Populate();
    }

    private void Populate()
    {
        var settings = _host.Settings.Current;
        AutoScanCheck.IsChecked = settings.AutoScanOnStartup;
        RestoreLastCheck.IsChecked = settings.RestoreLastPlaylist;
        CacheDirBox.Text = settings.CacheDirectory;
        _loadingCache = true;
        WaveformLimitBox.SelectedIndex = CacheLimitToIndex(settings.WaveformCacheLimitMb);
        _loadingCache = false;
        RefreshFileDestinationList();
        LibrarySummaryText.Text = vm_LibrarySummary();
        StartupText.Text = $"启动耗时：{App.Timeline.ElapsedAt("window-shown").TotalMilliseconds:F0} ms（窗口显示） · " +
                           $"日志：{_host.Paths.LogsDirectory}";
        DataDirText.Text = $"数据库：{_host.Paths.DatabaseFile}";
        DataPathText.Text = $"配置目录：{_host.Paths.ConfigDirectory}\n" +
                            $"数据目录：{_host.Paths.DataDirectory}\n" +
                            (_host.Paths.IsPortable
                                ? "（便携模式：数据保存在程序目录）"
                                : "（自定义目录：ECHOGARAGE_DATA_DIR）");
        RefreshFolders();
        PopulateHotkeys();
        PopulateEncoders();
        PopulateLoudnessSettings();
        UpdateAssociationStatus();
        PopulateWebTagSettings();
        PopulateVisualizer();
        PopulateTagNormalizationRules();
        SelectDefaultSettingsPage();
    }

    /// <summary>诊断截图用：按导航 Tag 选中设置页（Appearance/Playback/Hotkeys/Library/Files/Network/About）。</summary>
    public void SelectPage(string tag)
    {
        foreach (var item in SettingsNav.Items)
        {
            if (item is ListBoxItem { Tag: string itemTag } &&
                string.Equals(itemTag, tag, StringComparison.Ordinal))
            {
                SettingsNav.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>默认选中左侧导航的第一项（外观）。</summary>
    private void SelectDefaultSettingsPage()
    {
        if (SettingsNav.SelectedIndex < 0)
        {
            SettingsNav.SelectedIndex = 0;
        }
    }

    /// <summary>根据左侧导航的选中项切换右侧页面可见性。</summary>
    private void OnSettingsNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageAppearance is null)
        {
            // InitializeComponent 期间可能先触发一次，此时页面字段还没就绪。
            return;
        }

        var tag = (SettingsNav.SelectedItem as ListBoxItem)?.Tag as string ?? "Appearance";
        PageAppearance.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        PagePlayback.Visibility = tag == "Playback" ? Visibility.Visible : Visibility.Collapsed;
        PageHotkeys.Visibility = tag == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        PageLibrary.Visibility = tag == "Library" ? Visibility.Visible : Visibility.Collapsed;
        PageFiles.Visibility = tag == "Files" ? Visibility.Visible : Visibility.Collapsed;
        PageNetwork.Visibility = tag == "Network" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
    }

    private string vm_LibrarySummary() => _vm.LibrarySummary;

    private void RefreshFolders()
    {
        FolderList.ItemsSource = _vm.Sources
            .Where(s => s.Kind == SourceKind.Folder && s.Folder is not null)
            .ToList();
    }

    private void PopulateHotkeys()
    {
        _loadingHotkeys = true;
        try
        {
            _hotkeyRows.Clear();
            foreach (var (action, label) in HotkeyActions)
            {
                var binding = _host.Settings.Current.Hotkeys.FirstOrDefault(h => h.Action == action);
                _hotkeyRows.Add(new HotkeyEditRow(action, label, binding?.Gesture ?? string.Empty, binding?.Global == true));
            }

            _presetHotkeyRows.Clear();
            foreach (var preset in _host.Settings.Current.ConversionPresets)
            {
                if (string.IsNullOrWhiteSpace(preset.Hotkey) && preset.HotkeyGlobal)
                {
                    // A global flag without a gesture is meaningless; normalize it on display.
                    preset.HotkeyGlobal = false;
                }

                _presetHotkeyRows.Add(new PresetHotkeyRow(preset));
            }

            HotkeyList.ItemsSource = _hotkeyRows;
            PresetHotkeyList.ItemsSource = _presetHotkeyRows;
            BuiltInHotkeyList.ItemsSource = BuiltInHotkeys;
            UpdateHotkeyStatus();
        }
        finally
        {
            _loadingHotkeys = false;
        }
    }

    private void OnHotkeyGestureCommitted(object sender, HotkeyGestureCommittedEventArgs e)
    {
        if (_loadingHotkeys || sender is not HotkeyCaptureButton { DataContext: HotkeyEditRow row } button)
        {
            return;
        }

        row.Gesture = e.Gesture;
        if (!SaveHotkeys())
        {
            // Keep the UI and the stored rows consistent with the last accepted value.
            button.Gesture = e.PreviousGesture;
            row.Gesture = e.PreviousGesture;
        }
    }

    private void OnHotkeyGlobalChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingHotkeys || sender is not CheckBox { DataContext: HotkeyEditRow row } checkBox)
        {
            return;
        }

        if (checkBox.IsChecked == true && string.IsNullOrWhiteSpace(row.Gesture))
        {
            checkBox.IsChecked = false;
            HotkeyStatusText.Text = "请先为该动作绑定快捷键，再勾选“全局”。";
            return;
        }

        row.Global = checkBox.IsChecked == true;
        SaveHotkeys();
    }

    private void OnPresetHotkeyGestureCommitted(object sender, HotkeyGestureCommittedEventArgs e)
    {
        if (_loadingHotkeys || sender is not HotkeyCaptureButton { DataContext: PresetHotkeyRow row } button)
        {
            return;
        }

        row.Hotkey = e.Gesture;
        if (!SaveHotkeys())
        {
            button.Gesture = e.PreviousGesture;
            row.Hotkey = e.PreviousGesture;
        }
    }

    private void OnPresetHotkeyGlobalChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingHotkeys || sender is not CheckBox { DataContext: PresetHotkeyRow row } checkBox)
        {
            return;
        }

        if (checkBox.IsChecked == true && string.IsNullOrWhiteSpace(row.Hotkey))
        {
            checkBox.IsChecked = false;
            row.Global = false;
            PresetHotkeyStatusText.Text = "请先为该转换预设绑定快捷键，再勾选“全局”。";
            return;
        }

        row.Global = checkBox.IsChecked == true;
        SaveHotkeys();
    }

    /// <summary>
    /// Persists the action rows and the conversion preset shortcuts. Returns false (leaving the
    /// settings untouched) when an entry is malformed or duplicated, so callers can roll the
    /// capture button back to its previous value.
    /// </summary>
    private bool SaveHotkeys()
    {
        var bindings = new List<HotkeyBinding>();
        var gestures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _hotkeyRows)
        {
            var text = row.Gesture.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (!HotkeyGesture.TryParse(text, out var gesture) || gesture is null)
            {
                HotkeyStatusText.Text = $"“{text}”不是有效的快捷键格式。示例：Ctrl+Alt+Space、MediaPlayPause。";
                return false;
            }

            var canonical = gesture.ToString();
            if (gestures.TryGetValue(canonical, out var other))
            {
                HotkeyStatusText.Text = $"快捷键“{canonical}”与“{other}”重复了。";
                return false;
            }

            gestures[canonical] = row.ActionLabel;
            bindings.Add(new HotkeyBinding { Action = row.Action, Gesture = canonical, Global = row.Global });
        }

        // Validate every preset before writing anything, so a conflict never leaves half-edited rows.
        var presetPlans = new List<(PresetHotkeyRow Row, string Canonical)>();
        foreach (var row in _presetHotkeyRows)
        {
            var text = row.Hotkey.Trim();
            if (text.Length == 0)
            {
                presetPlans.Add((row, string.Empty));
                continue;
            }

            if (!HotkeyGesture.TryParse(text, out var gesture) || gesture is null)
            {
                HotkeyStatusText.Text = $"转换预设“{row.Name}”的快捷键“{text}”格式无效。";
                return false;
            }

            var canonical = gesture.ToString();
            if (gestures.TryGetValue(canonical, out var other))
            {
                HotkeyStatusText.Text = $"快捷键“{canonical}”与“{other}”重复了。";
                return false;
            }

            gestures[canonical] = row.Name;
            presetPlans.Add((row, canonical));
        }

        foreach (var (row, canonical) in presetPlans)
        {
            row.Hotkey = canonical;
            if (canonical.Length == 0)
            {
                row.Global = false;
            }
        }

        _host.Settings.Current.Hotkeys = bindings;
        SaveSettings();
        _vm.NotifyHotkeysChanged();
        UpdateHotkeyStatus();
        return true;
    }

    private void UpdateHotkeyStatus()
    {
        var configured = _hotkeyRows.Count(r => !string.IsNullOrWhiteSpace(r.Gesture));
        var global = _hotkeyRows.Count(r => r.Global && !string.IsNullOrWhiteSpace(r.Gesture));
        HotkeyStatusText.Text = configured == 0
            ? "尚未设置快捷键。"
            : $"已设置 {configured} 个快捷键，其中 {global} 个为全局快捷键。";

        var presetConfigured = _presetHotkeyRows.Count(r => !string.IsNullOrWhiteSpace(r.Hotkey));
        var presetGlobal = _presetHotkeyRows.Count(r => r.Global && !string.IsNullOrWhiteSpace(r.Hotkey));
        PresetHotkeyStatusText.Text = presetConfigured == 0
            ? "尚未为转换预设设置快捷键。"
            : $"已为 {presetConfigured} 个转换预设设置快捷键，其中 {presetGlobal} 个为全局快捷键。";
    }

    private sealed class HotkeyEditRow(HotkeyAction action, string actionLabel, string gesture, bool global)
    {
        public HotkeyAction Action { get; } = action;
        public string ActionLabel { get; } = actionLabel;
        public string Gesture { get; set; } = gesture;
        public bool Global { get; set; } = global;
    }

    /// <summary>Row model for a conversion preset shortcut; writes straight through to the preset.</summary>
    private sealed class PresetHotkeyRow(ConversionPreset preset)
    {
        public ConversionPreset Preset { get; } = preset;
        public string Name => string.IsNullOrWhiteSpace(Preset.Name) ? "(未命名预设)" : Preset.Name;
        public string Hotkey
        {
            get => Preset.Hotkey ?? string.Empty;
            set => Preset.Hotkey = value ?? string.Empty;
        }

        public bool Global
        {
            get => Preset.HotkeyGlobal;
            set => Preset.HotkeyGlobal = value;
        }
    }

    private sealed record BuiltInHotkeyRow(string Label, string Gesture);

    private void UpdateAssociationStatus()
    {
        var state = _host.Associations.GetRegistrationState();
        var count = state.Count(s => s.Registered || s.OpenWithRegistered);
        AssociationStatusText.Text = count > 0
            ? $"已注册为 {count} 种音频格式的候选打开方式（可在上方按钮中调整格式或取消注册）。"
            : _host.Associations.IsRegistered()
                ? "检测到残留的注册条目，但未登记任何格式，建议重新打开“文件关联”完成注册。"
                : "尚未注册。注册是可选手动操作，启动时不会自动注册。";
    }

    private void PopulateEncoders()
    {
        NeroAacPathBox.Text = _host.Settings.Current.Encoders.NeroAacPath;
        UpdateEncoderStatus();
    }

    private void UpdateEncoderStatus()
    {
        var locator = new EncoderLocator(AppContext.BaseDirectory);
        var flac = locator.Locate(ConversionTool.Flac, null);
        var lame = locator.Locate(ConversionTool.Lame, null);
        var ssrc = locator.Locate(ConversionTool.Ssrc, null);
        static string State(string? path) => path is null ? "未找到" : "已就绪";
        EncoderStatusText.Text = $"内置编码器状态：FLAC {State(flac)} · LAME {State(lame)} · SSRC {State(ssrc)}。";
    }

    private void OnBrowseNeroAac(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 neroAacEnc.exe",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        var current = NeroAacPathBox.Text.Trim();
        if (File.Exists(current))
        {
            dialog.FileName = current;
        }

        if (dialog.ShowDialog(this) == true)
        {
            NeroAacPathBox.Text = dialog.FileName;
            SaveNeroAacPath();
        }
    }

    private void OnNeroAacPathLostFocus(object sender, RoutedEventArgs e) => SaveNeroAacPath();

    private void SaveNeroAacPath()
    {
        var path = NeroAacPathBox.Text.Trim();
        if (string.Equals(_host.Settings.Current.Encoders.NeroAacPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _host.Settings.Current.Encoders.NeroAacPath = path;
        SaveSettings();
    }

    /// <summary>响度调整窗口的默认目标响度（-60.0 … 0.0 LUFS，默认 -10.0）。</summary>
    private void PopulateLoudnessSettings()
    {
        LoudnessTargetBox.Text = _host.Settings.Current.LoudnessDefaultTargetLufs.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private void OnLoudnessTargetLostFocus(object sender, RoutedEventArgs e) => SaveLoudnessTarget();

    private void SaveLoudnessTarget()
    {
        var current = _host.Settings.Current.LoudnessDefaultTargetLufs;
        var text = LoudnessTargetBox.Text.Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value))
        {
            LoudnessTargetBox.Text = current.ToString("0.0", CultureInfo.InvariantCulture);
            return;
        }

        value = Math.Round(Math.Clamp(value, -60.0, 0.0), 1, MidpointRounding.AwayFromZero);
        LoudnessTargetBox.Text = value.ToString("0.0", CultureInfo.InvariantCulture);
        if (Math.Abs(value - current) < 0.0001)
        {
            return;
        }

        _host.Settings.Current.LoudnessDefaultTargetLufs = value;
        SaveSettings();
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e) => _vm.RefreshDevices();

    // =====================================================================
    // File operation destinations (ADR-021)
    // =====================================================================

    private void RefreshFileDestinationList()
    {
        FileDestinationList.ItemsSource = _host.Settings.Current.FileDestinations.ToList();
    }

    private void ApplyFileDestinations(IEnumerable<string> values, string status)
    {
        var normalized = FileDestinations.Normalize(values);
        _host.Settings.Current.FileDestinations = normalized;
        SaveSettings();
        RefreshFileDestinationList();
        FileDestinationStatusText.Text = status;
    }

    private void OnAddFileDestination(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择目标文件夹",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var values = _host.Settings.Current.FileDestinations.ToList();
        if (!FileDestinations.Add(values, dialog.FolderName))
        {
            FileDestinationStatusText.Text = values.Count >= FileDestinations.MaxCount
                ? $"最多只能记住 {FileDestinations.MaxCount} 个文件夹，请先移除不再需要的目录。"
                : "该文件夹已经在列表中。";
            return;
        }

        ApplyFileDestinations(values, $"已添加：{dialog.FolderName}");
    }

    private void OnRemoveFileDestination(object sender, RoutedEventArgs e)
    {
        if (FileDestinationList.SelectedItem is not string directory)
        {
            FileDestinationStatusText.Text = "请先在列表中选择要移除的文件夹。";
            return;
        }

        var values = _host.Settings.Current.FileDestinations.ToList();
        FileDestinations.Remove(values, directory);
        ApplyFileDestinations(values, $"已移除：{directory}");
    }

    private void OnMoveFileDestinationUp(object sender, RoutedEventArgs e) => MoveFileDestination(-1);

    private void OnMoveFileDestinationDown(object sender, RoutedEventArgs e) => MoveFileDestination(1);

    private void MoveFileDestination(int offset)
    {
        if (FileDestinationList.SelectedItem is not string directory)
        {
            return;
        }

        var values = _host.Settings.Current.FileDestinations.ToList();
        var index = values.FindIndex(item => string.Equals(item, directory, StringComparison.OrdinalIgnoreCase));
        var target = index + offset;
        if (index < 0 || target < 0 || target >= values.Count)
        {
            return;
        }

        (values[index], values[target]) = (values[target], values[index]);
        ApplyFileDestinations(values, "顺序已更新，右键菜单会按新顺序显示。");
        FileDestinationList.SelectedItem = directory;
    }

    // =====================================================================
    // Tag normalization rules (ADR-025)
    // =====================================================================

    /// <summary>目标字段下拉框的选项；顺序与界面展示一致。</summary>
    private static readonly TagNormalizationTargetOption[] TagNormalizationTargetOptionList =
    [
        new(TagNormalization.AnyField, "全部可文本字段"),
        new(TagFieldKeys.Title, "标题"),
        new(TagFieldKeys.Artist, "艺术家"),
        new(TagFieldKeys.Album, "专辑"),
        new(TagFieldKeys.AlbumArtist, "专辑艺术家"),
        new(TagFieldKeys.Composer, "作曲家"),
        new(TagFieldKeys.Genre, "流派"),
        new(TagFieldKeys.Year, "年份"),
        new(TagFieldKeys.TrackNumber, "音轨号"),
        new(TagFieldKeys.DiscNumber, "碟片号"),
        new(TagFieldKeys.Comment, "备注"),
    ];

    private void PopulateTagNormalizationRules()
    {
        _loadingTagNormalizationRules = true;
        try
        {
            _tagNormalizationRows.Clear();
            foreach (var rule in _host.Settings.Current.TagNormalizationRules)
            {
                _tagNormalizationRows.Add(CreateTagNormalizationRow(rule));
            }

            TagNormalizationRuleList.ItemsSource = _tagNormalizationRows;
            UpdateTagNormalizationRuleState();
        }
        finally
        {
            _loadingTagNormalizationRules = false;
        }
    }

    private static TagNormalizationEditRow CreateTagNormalizationRow(TagNormalizationRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        // 手改 settings.json 可能留下未知 / 空的目标字段：下拉框回退到“专辑”，避免显示空白项。
        TargetField = TagNormalization.IsKnownField(rule.TargetField) || TagNormalization.IsAnyField(rule.TargetField)
            ? TagNormalization.CanonicalizeTargetField(rule.TargetField)
            : TagFieldKeys.Album,
        Pattern = rule.Pattern,
        Replacement = rule.Replacement,
        Enabled = rule.Enabled,
    };

    /// <summary>
    /// 把列表行写回设置并保存。正则非法的行保留在列表里便于修正，
    /// Core 应用规则时会跳过它们，界面同时给出行内错误提示。
    /// </summary>
    private void ApplyTagNormalizationRules()
    {
        if (_loadingTagNormalizationRules)
        {
            return;
        }

        var rules = _tagNormalizationRows
            .Select(row => new TagNormalizationRule
            {
                Id = row.Id,
                Name = row.Name,
                TargetField = row.TargetField,
                Pattern = row.Pattern,
                Replacement = row.Replacement,
                Enabled = row.Enabled,
            })
            .ToList();

        // NormalizeRules 在这里不传 logger：输入过程中正则常常处于半截状态，
        // 逐键记录 WARN 只会污染日志；真正的异常配置在加载设置时已经记录过。
        _host.Settings.Current.TagNormalizationRules = TagNormalization.NormalizeRules(rules);
        SaveSettings();
        UpdateTagNormalizationRuleState();
    }

    /// <summary>刷新行内正则错误提示、空状态、规则数上限与底部状态文案。</summary>
    private void UpdateTagNormalizationRuleState()
    {
        var invalid = 0;
        foreach (var row in _tagNormalizationRows)
        {
            if (TagNormalization.IsValidPattern(row.Pattern, out var error))
            {
                row.SetError(null);
            }
            else
            {
                row.SetError(error);
                invalid++;
            }
        }

        var enabled = _tagNormalizationRows.Count(row => row.Enabled);
        var limitReached = _tagNormalizationRows.Count >= TagNormalization.MaxRuleCount;
        var status = $"共 {_tagNormalizationRows.Count} 条规则，已启用 {enabled} 条。";
        status += limitReached
            ? $"已达上限 {TagNormalization.MaxRuleCount} 条，无法再添加。"
            : $"最多 {TagNormalization.MaxRuleCount} 条规则。";
        if (invalid > 0)
        {
            status += $"有 {invalid} 条规则的正则表达式无效，不会被应用。";
        }

        TagNormalizationEmptyHint.Visibility = _tagNormalizationRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddTagNormalizationRuleButton.IsEnabled = !limitReached;
        AddTagNormalizationRuleButton.ToolTip = limitReached
            ? $"最多 {TagNormalization.MaxRuleCount} 条规则，请先删除不再需要的规则。"
            : null;
        TagNormalizationRuleStatusText.Text = status;
    }

    private void OnAddTagNormalizationRule(object sender, RoutedEventArgs e)
    {
        if (_tagNormalizationRows.Count >= TagNormalization.MaxRuleCount)
        {
            UpdateTagNormalizationRuleState();
            return;
        }

        _tagNormalizationRows.Add(new TagNormalizationEditRow
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.Empty,
            TargetField = TagFieldKeys.Album,
            Pattern = string.Empty,
            Replacement = string.Empty,
            Enabled = true,
        });
        ApplyTagNormalizationRules();
    }

    private void OnRemoveTagNormalizationRule(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TagNormalizationEditRow row })
        {
            return;
        }

        _tagNormalizationRows.Remove(row);
        ApplyTagNormalizationRules();
    }

    private void OnResetTagNormalizationRules(object sender, RoutedEventArgs e)
    {
        _loadingTagNormalizationRules = true;
        try
        {
            _tagNormalizationRows.Clear();
            foreach (var rule in TagNormalizationDefaults.Create())
            {
                _tagNormalizationRows.Add(CreateTagNormalizationRow(rule));
            }
        }
        finally
        {
            _loadingTagNormalizationRules = false;
        }

        ApplyTagNormalizationRules();
    }

    private void OnTagNormalizationRuleToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingTagNormalizationRules || sender is not CheckBox { DataContext: TagNormalizationEditRow row } checkBox)
        {
            return;
        }

        row.Enabled = checkBox.IsChecked == true;
        ApplyTagNormalizationRules();
    }

    private void OnTagNormalizationTargetFieldChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTagNormalizationRules || sender is not ComboBox { DataContext: TagNormalizationEditRow row } comboBox)
        {
            return;
        }

        if (comboBox.SelectedValue is string targetField && row.TargetField != targetField)
        {
            row.TargetField = targetField;
            ApplyTagNormalizationRules();
        }
    }

    private void OnTagNormalizationRuleTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingTagNormalizationRules || sender is not TextBox { DataContext: TagNormalizationEditRow row } textBox)
        {
            return;
        }

        switch (textBox.Tag as string)
        {
            case "name":
                if (row.Name == textBox.Text)
                {
                    return;
                }

                row.Name = textBox.Text;
                break;
            case "pattern":
                if (row.Pattern == textBox.Text)
                {
                    return;
                }

                row.Pattern = textBox.Text;
                break;
            case "replacement":
                if (row.Replacement == textBox.Text)
                {
                    return;
                }

                row.Replacement = textBox.Text;
                break;
            default:
                return;
        }

        ApplyTagNormalizationRules();
    }

    private sealed class TagNormalizationTargetOption(string key, string label)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
    }

    private sealed class TagNormalizationEditRow : INotifyPropertyChanged
    {
        public IReadOnlyList<TagNormalizationTargetOption> TargetOptions => TagNormalizationTargetOptionList;

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string TargetField { get; set; } = TagFieldKeys.Album;
        public string Pattern { get; set; } = string.Empty;
        public string Replacement { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;

        private string _errorText = string.Empty;

        public string ErrorText => _errorText;

        public Visibility ErrorVisibility => _errorText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetError(string? error)
        {
            var text = string.IsNullOrWhiteSpace(error) ? string.Empty : error;
            if (text == _errorText)
            {
                return;
            }

            _errorText = text;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorVisibility)));
        }
    }

    private void OnBrowseCache(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择缓存目录",
            InitialDirectory = Directory.Exists(CacheDirBox.Text) ? CacheDirBox.Text : _host.Paths.DefaultCacheDirectory
        };
        if (dialog.ShowDialog(this) == true)
        {
            CacheDirBox.Text = dialog.FolderName;
        }
    }

    private async void OnApplyCache(object sender, RoutedEventArgs e)
    {
        var directory = CacheDirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            await _host.ApplyCacheDirectoryAsync(directory);
            CacheDirBox.Text = _host.Settings.Current.CacheDirectory;
            CacheStatusText.Text = "缓存目录已更新，封面缓存已迁移。";
            _vm.SetStatus("缓存目录已更新");
        }
        catch (Exception ex)
        {
            CacheStatusText.Text = $"应用失败：{ex.Message}";
        }
    }

    private async void OnClearCoverCache(object sender, RoutedEventArgs e)
    {
        await ClearCacheAsync(
            cover: true,
            waveform: false,
            prompt: "清空封面缓存",
            detail: "删除后滚动列表时会重新解码并写入新的封面缓存。",
            done: "封面缓存已清空，滚动列表时会重新生成。");
    }

    private async void OnClearWaveformCache(object sender, RoutedEventArgs e)
    {
        await ClearCacheAsync(
            cover: false,
            waveform: true,
            prompt: "清空波形缓存",
            detail: "删除后再次播放歌曲时会重新分析波形（每首约 0.3–1 秒后台任务）。",
            done: "波形缓存已清空，重新播放时会重新分析。");
    }

    private async void OnClearAllCaches(object sender, RoutedEventArgs e)
    {
        await ClearCacheAsync(
            cover: true,
            waveform: true,
            prompt: "清空全部缓存",
            detail: "封面与波形缓存都会被删除，随后按需重新生成。媒体库与设置不受影响。",
            done: "封面与波形缓存已清空。");
    }

    private async Task ClearCacheAsync(bool cover, bool waveform, string prompt, string detail, string done)
    {
        if (!EnsureCacheDirectoryApplied())
        {
            return;
        }

        var (coverFiles, waveformFiles) = CountCacheFiles();
        var count = (cover ? coverFiles : 0) + (waveform ? waveformFiles : 0);
        if (MessageBox.Show(this, $"{prompt}（{count} 个文件）？\n\n{detail}", "EchoGarage",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            if (cover)
            {
                await _host.Covers!.ClearAsync();
                _host.CoverImages.ClearMemoryCache();
            }

            if (waveform)
            {
                await _host.WaveformCache.ClearAsync();
                // 正在播放的曲目立即重算，避免进度区空白到下次切歌。
                _vm.StartWaveformLoad(_vm.CurrentRow?.Track.Path);
            }

            CacheStatusText.Text = done;
            _vm.SetStatus(done);
        }
        catch (Exception ex)
        {
            CacheStatusText.Text = $"清理失败：{ex.Message}";
            _host.Logger.Error($"Clear cache ({prompt}) failed", ex);
        }
    }

    private bool EnsureCacheDirectoryApplied()
    {
        if (_vm.Settings.CacheDirectory.Equals(CacheDirBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        CacheStatusText.Text = "请先点击“应用目录”再清理。";
        return false;
    }

    private void OnClearCacheMenuClick(object sender, RoutedEventArgs e)
    {
        if (ClearCacheButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = ClearCacheButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnClearCacheMenuOpened(object sender, RoutedEventArgs e)
    {
        // 只在菜单真正打开时枚举目录，设置页打开本身不做任何磁盘 IO。
        var (coverFiles, waveformFiles) = CountCacheFiles();
        ClearCoverCacheItem.Header = $"清空封面缓存（{coverFiles} 个文件）";
        ClearWaveformCacheItem.Header = $"清空波形缓存（{waveformFiles} 个文件）";
        ClearAllCachesItem.Header = $"清空全部缓存（{coverFiles + waveformFiles} 个文件）";
    }

    private (int Covers, int Waveforms) CountCacheFiles()
    {
        var directory = _vm.Settings.CacheDirectory;
        var covers = 0;
        var waveforms = 0;
        try
        {
            if (Directory.Exists(directory))
            {
                covers = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Count();
            }

            var waveformDirectory = Path.Combine(directory, "Waveforms");
            if (Directory.Exists(waveformDirectory))
            {
                waveforms = Directory.EnumerateFiles(waveformDirectory, "*.wfm", SearchOption.TopDirectoryOnly).Count();
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Debug($"Cache file count failed: {ex.Message}");
        }

        return (covers, waveforms);
    }

    private void OnOpenCache(object sender, RoutedEventArgs e) => OpenFolder(CacheDirBox.Text);

    private void OnWaveformLimitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingCache || WaveformLimitBox.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !int.TryParse(tag, out var megabytes))
        {
            return;
        }

        _host.Settings.Current.WaveformCacheLimitMb = megabytes;
        SaveSettings();
        // 立刻按新上限收缩一次，用户不用等下次保存波形才发现磁盘没变小。
        _host.TrimWaveformCache();
        CacheStatusText.Text = megabytes <= 0
            ? "波形缓存上限：不限制。"
            : $"波形缓存上限：{megabytes} MB，超出后自动删除最久未使用的波形。";
    }

    private static int CacheLimitToIndex(int megabytes) => megabytes switch
    {
        <= 0 => 4,
        <= 128 => 0,
        <= 256 => 1,
        <= 512 => 2,
        _ => 3
    };

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenFolder(_host.Paths.LogsDirectory);

    private void OnOpenConfig(object sender, RoutedEventArgs e) => OpenFolder(_host.Paths.ConfigDirectory);

    private void OnOpenData(object sender, RoutedEventArgs e) => OpenFolder(_host.Paths.DataDirectory);

    private static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", path)
                {
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Ignore.
        }
    }

    private async void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SourceNodeViewModel node } || node.Folder is null)
        {
            return;
        }

        if (MessageBox.Show(this, $"从媒体库移除文件夹“{node.Name}”？\n不会删除磁盘上的任何文件。", "EchoGarage",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await _host.LibraryRepository.RemoveFolderAsync(node.Folder.Id);
        _host.Watcher.Stop();
        _host.Watcher.Start(await _host.LibraryRepository.GetFoldersAsync());
        await _vm.ReloadSourcesPublicAsync();
        await _vm.SelectSourceAsync(_vm.Sources.FirstOrDefault());
        RefreshFolders();
        LibrarySummaryText.Text = _vm.LibrarySummary;
    }

    private async void OnRescan(object sender, RoutedEventArgs e)
    {
        await _vm.RescanCommand.ExecuteAsync(null);
        LibrarySummaryText.Text = _vm.LibrarySummary;
        RefreshFolders();
    }

    private void OnManageAssociations(object sender, RoutedEventArgs e)
    {
        var window = new FileAssociationWindow(_host) { Owner = this };
        window.ShowDialog();
        UpdateAssociationStatus();
    }

    private void PopulateWebTagSettings()
    {
        _loadingWebSettings = true;
        try
        {
            var network = _host.Settings.Current.Network;
            ProxyModeBox.SelectedIndex = network.ProxyMode switch
            {
                WebProxyMode.Direct => 1,
                WebProxyMode.Custom => 2,
                _ => 0
            };
            ProxyUrlBox.Text = network.CustomProxyUrl;
            ProxyUrlBox.IsEnabled = network.ProxyMode == WebProxyMode.Custom;
            AppleMusicSourceCheck.IsChecked = _host.Settings.Current.WebTags.EnabledSourceIds
                .Contains(WebTagSourceIds.AppleMusicJp, StringComparer.OrdinalIgnoreCase);
            UpdateProxyHint();
        }
        finally
        {
            _loadingWebSettings = false;
        }
    }

    private WebProxyMode SelectedProxyMode => (ProxyModeBox.SelectedItem as ComboBoxItem)?.Tag switch
    {
        "Direct" => WebProxyMode.Direct,
        "Custom" => WebProxyMode.Custom,
        _ => WebProxyMode.System
    };

    private void UpdateProxyHint()
    {
        var mode = SelectedProxyMode;
        ProxyUrlBox.IsEnabled = mode == WebProxyMode.Custom;
        ProxyHintText.Text = mode switch
        {
            WebProxyMode.Direct => "直接连接，不使用代理。",
            WebProxyMode.Custom when NetworkSettings.TryNormalizeProxyUrl(ProxyUrlBox.Text, out var normalized) =>
                $"将使用代理 {normalized}。",
            WebProxyMode.Custom => "请输入以 http:// 或 https:// 开头的代理地址，例如 http://127.0.0.1:7890。",
            _ => "使用 Windows 系统代理设置；系统未配置代理时直接连接。"
        };
    }

    private void OnProxyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingWebSettings)
        {
            return;
        }

        UpdateProxyHint();
        var network = _host.Settings.Current.Network;
        network.ProxyMode = SelectedProxyMode;
        if (network.ProxyMode != WebProxyMode.Custom ||
            NetworkSettings.TryNormalizeProxyUrl(network.CustomProxyUrl, out _))
        {
            SaveSettings();
        }
    }

    private void OnProxyUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingWebSettings)
        {
            return;
        }

        var network = _host.Settings.Current.Network;
        network.CustomProxyUrl = ProxyUrlBox.Text.Trim();
        UpdateProxyHint();
        if (network.ProxyMode == WebProxyMode.Custom)
        {
            if (NetworkSettings.TryNormalizeProxyUrl(network.CustomProxyUrl, out var normalized))
            {
                network.CustomProxyUrl = normalized;
                SaveSettings();
            }
        }
        else
        {
            SaveSettings();
        }
    }

    private void OnWebSourceChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingWebSettings)
        {
            return;
        }

        if (AppleMusicSourceCheck.IsChecked != true)
        {
            // 第一阶段只有一个源：取消后 Normalize 会重新启用，直接恢复勾选并提示。
            _loadingWebSettings = true;
            AppleMusicSourceCheck.IsChecked = true;
            _loadingWebSettings = false;
            WebConnectionStatusText.Text = "至少需要保留一个 Web 标签源。";
            return;
        }

        var settings = _host.Settings.Current.WebTags;
        if (!settings.EnabledSourceIds.Contains(WebTagSourceIds.AppleMusicJp, StringComparer.OrdinalIgnoreCase))
        {
            settings.EnabledSourceIds.Add(WebTagSourceIds.AppleMusicJp);
        }

        settings.DefaultSourceId = WebTagSourceIds.AppleMusicJp;
        SaveSettings();
        WebConnectionStatusText.Text = string.Empty;
    }

    private async void OnTestWebConnection(object sender, RoutedEventArgs e)
    {
        var mode = SelectedProxyMode;
        string? normalized = null;
        if (mode == WebProxyMode.Custom)
        {
            if (!NetworkSettings.TryNormalizeProxyUrl(ProxyUrlBox.Text, out var validProxy))
            {
                WebConnectionStatusText.Text = "代理地址无效，请输入 http:// 或 https:// 地址。";
                return;
            }

            normalized = validProxy;
        }

        var network = _host.Settings.Current.Network;
        network.ProxyMode = mode;
        if (normalized is not null)
        {
            network.CustomProxyUrl = normalized;
        }

        SaveSettings();
        WebConnectionStatusText.Text = "正在连接 Apple Music…";
        try
        {
            var result = await _host.WebTags.TestConnectionAsync(
                WebTagSourceIds.AppleMusicJp, CancellationToken.None);
            WebConnectionStatusText.Text = result.Success
                ? $"连接成功（{result.Elapsed.TotalMilliseconds:F0} ms）"
                : $"连接失败：{result.Message}";
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"Web tag connection test failed: {ex.Message}");
            WebConnectionStatusText.Text = $"连接失败：{ex.Message}";
        }
    }

    private void OnToggleAutoScan(object sender, RoutedEventArgs e)
    {
        _host.Settings.Current.AutoScanOnStartup = AutoScanCheck.IsChecked == true;
        SaveSettings();
    }

    private void OnToggleRestoreLast(object sender, RoutedEventArgs e)
    {
        _host.Settings.Current.RestoreLastPlaylist = RestoreLastCheck.IsChecked == true;
        SaveSettings();
    }

    /// <summary>
    /// 把配置里的播放可视化参数播种到控件。控件只列出推荐的取值集合，若磁盘上的值不在集合内
    /// （例如手工改过 settings.json），就选中数值最接近的一项。
    /// </summary>
    private void PopulateVisualizer()
    {
        var visualization = _host.Settings.Current.Visualization;
        var spectrum = visualization.Spectrum;

        _loadingVisualizer = true;
        try
        {
            SelectComboTag(VisualizerKindBox, visualization.Kind.ToString());
            SelectComboTag(VisualizerFrameRateBox, visualization.FrameRateLimit.ToString());
            SelectComboNearest(SpectrumBandBox, spectrum.BandCount);
            SelectComboNearest(SpectrumFftBox, spectrum.FftSize);
            SelectComboNearest(SpectrumMinFreqBox, spectrum.MinFrequency);
            SelectComboNearest(SpectrumMaxFreqBox, spectrum.MaxFrequency);
            SelectComboTag(SpectrumSummationBox, spectrum.Summation.ToString());
            SelectComboNearest(SpectrumFloorBox, spectrum.FloorDb);
            SelectComboNearest(SpectrumSlopeBox, spectrum.SlopeDbPerOctave);
            SelectComboNearest(SpectrumReleaseBox, spectrum.ReleaseDbPerSecond);
            SelectComboNearest(SpectrumPeakHoldBox, spectrum.PeakHoldMs);
            SpectrumPeakShowCheck.IsChecked = spectrum.ShowPeaks;
            SpectrumGraticuleCheck.IsChecked = spectrum.ShowGraticule;
            SpectrumFrequencyLabelsCheck.IsChecked = spectrum.ShowFrequencyLabels;
        }
        finally
        {
            _loadingVisualizer = false;
        }
    }

    private void OnVisualizerComboChanged(object sender, SelectionChangedEventArgs e) => ApplyVisualizer();

    private void OnVisualizerToggleChanged(object sender, RoutedEventArgs e) => ApplyVisualizer();

    /// <summary>从控件读回全部可视化参数，规范化后保存并让右栏立即生效。</summary>
    private void ApplyVisualizer()
    {
        if (_loadingVisualizer)
        {
            return;
        }

        var visualization = _host.Settings.Current.Visualization;
        var spectrum = visualization.Spectrum;

        visualization.Kind = ReadEnumTag(VisualizerKindBox, VisualizerKind.Spectrum);
        visualization.FrameRateLimit = ReadEnumTag(VisualizerFrameRateBox, VisualizerFrameRate.Unlimited);
        spectrum.BandCount = ReadIntTag(SpectrumBandBox, spectrum.BandCount);
        spectrum.FftSize = ReadIntTag(SpectrumFftBox, spectrum.FftSize);
        spectrum.MinFrequency = ReadDoubleTag(SpectrumMinFreqBox, spectrum.MinFrequency);
        spectrum.MaxFrequency = ReadDoubleTag(SpectrumMaxFreqBox, spectrum.MaxFrequency);
        spectrum.Summation = ReadEnumTag(SpectrumSummationBox, SpectrumSummation.Rms);
        spectrum.FloorDb = ReadDoubleTag(SpectrumFloorBox, spectrum.FloorDb);
        spectrum.SlopeDbPerOctave = ReadDoubleTag(SpectrumSlopeBox, spectrum.SlopeDbPerOctave);
        spectrum.ReleaseDbPerSecond = ReadDoubleTag(SpectrumReleaseBox, spectrum.ReleaseDbPerSecond);
        spectrum.ShowPeaks = SpectrumPeakShowCheck.IsChecked == true;
        spectrum.PeakHoldMs = ReadDoubleTag(SpectrumPeakHoldBox, spectrum.PeakHoldMs);
        spectrum.ShowGraticule = SpectrumGraticuleCheck.IsChecked == true;
        spectrum.ShowFrequencyLabels = SpectrumFrequencyLabelsCheck.IsChecked == true;

        // 参数之间互相约束（频率上下限比例、FFT 取值等）统一交给 Normalize 裁决，
        // 然后回填控件，保证界面显示的值与频谱引擎实际使用的值一致。
        VisualizationSettings.Normalize(visualization);
        PopulateVisualizer();
        _vm.RefreshVisualizer();
        SaveSettings();
    }

    private void OnResetVisualizerSettings(object sender, RoutedEventArgs e)
    {
        _host.Settings.Current.Visualization = new VisualizationSettings();
        PopulateVisualizer();
        _vm.RefreshVisualizer();
        SaveSettings();
    }

    private static void SelectComboTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }

        box.SelectedIndex = 0;
    }

    private static void SelectComboNearest(ComboBox box, double value)
    {
        ComboBoxItem? best = null;
        var bestDistance = double.MaxValue;
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is not string tag ||
                !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var candidate))
            {
                continue;
            }

            var distance = Math.Abs(candidate - value);
            if (best is not null && distance >= bestDistance)
            {
                continue;
            }

            best = item;
            bestDistance = distance;
        }

        if (best is not null)
        {
            box.SelectedItem = best;
        }
    }

    private static int ReadIntTag(ComboBox box, int fallback) =>
        box.SelectedItem is ComboBoxItem { Tag: string tag } &&
        int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static double ReadDoubleTag(ComboBox box, double fallback) =>
        box.SelectedItem is ComboBoxItem { Tag: string tag } &&
        double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static T ReadEnumTag<T>(ComboBox box, T fallback) where T : struct, Enum =>
        box.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<T>(tag, out var value)
            ? value
            : fallback;

    private void SaveSettings() => _ = _host.Settings.SaveAsync();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
