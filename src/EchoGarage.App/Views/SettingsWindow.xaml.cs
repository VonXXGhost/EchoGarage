using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        (HotkeyAction.OpenSearch, "打开高级搜索")
    ];

    private readonly AppHost _host;
    private readonly MainViewModel _vm;
    private readonly List<HotkeyEditRow> _hotkeyRows = [];
    private bool _loadingHotkeys;
    private bool _loadingCache;

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
        UpdateAssociationStatus();
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

            HotkeyList.ItemsSource = _hotkeyRows;
            UpdateHotkeyStatus();
        }
        finally
        {
            _loadingHotkeys = false;
        }
    }

    private void OnHotkeyGestureChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingHotkeys || sender is not TextBox { DataContext: HotkeyEditRow row } textBox)
        {
            return;
        }

        row.Gesture = textBox.Text;
        SaveHotkeys();
    }

    private void OnHotkeyGlobalChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingHotkeys || sender is not CheckBox { DataContext: HotkeyEditRow row } checkBox)
        {
            return;
        }

        row.Global = checkBox.IsChecked == true;
        SaveHotkeys();
    }

    private void SaveHotkeys()
    {
        var bindings = new List<HotkeyBinding>();
        var gestures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                return;
            }

            var canonical = gesture.ToString();
            if (!gestures.Add(canonical))
            {
                HotkeyStatusText.Text = $"快捷键“{canonical}”重复了。";
                return;
            }

            bindings.Add(new HotkeyBinding { Action = row.Action, Gesture = canonical, Global = row.Global });
        }

        _host.Settings.Current.Hotkeys = bindings;
        SaveSettings();
        _vm.NotifyHotkeysChanged();
        UpdateHotkeyStatus();
    }

    private void UpdateHotkeyStatus()
    {
        var configured = _hotkeyRows.Count(r => !string.IsNullOrWhiteSpace(r.Gesture));
        var global = _hotkeyRows.Count(r => r.Global && !string.IsNullOrWhiteSpace(r.Gesture));
        HotkeyStatusText.Text = configured == 0
            ? "尚未设置快捷键。"
            : $"已设置 {configured} 个快捷键，其中 {global} 个为全局快捷键。";
    }

    private sealed class HotkeyEditRow(HotkeyAction action, string actionLabel, string gesture, bool global)
    {
        public HotkeyAction Action { get; } = action;
        public string ActionLabel { get; } = actionLabel;
        public string Gesture { get; set; } = gesture;
        public bool Global { get; set; } = global;
    }

    private void UpdateAssociationStatus()
    {
        AssociationStatusText.Text = _host.Associations.IsRegistered()
            ? "已注册为音频文件的候选打开方式（可在上方按钮中调整格式或取消注册）。"
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
