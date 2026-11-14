using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EchoGarage.App.Services;
using EchoGarage.App.ViewModels;
using EchoGarage.Core.Models;
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

    private void OnRefreshDevices(object sender, RoutedEventArgs e) => _vm.RefreshDevices();

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

    private async void OnClearCache(object sender, RoutedEventArgs e)
    {
        if (!_vm.Settings.CacheDirectory.Equals(CacheDirBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            CacheStatusText.Text = "请先点击“应用目录”再清理。";
            return;
        }

        try
        {
            await _host.Covers!.ClearAsync();
            _host.CoverImages.ClearMemoryCache();
            CacheStatusText.Text = "封面缓存已清空，滚动列表时会重新生成。";
            _vm.SetStatus("封面缓存已清空");
        }
        catch (Exception ex)
        {
            CacheStatusText.Text = $"清理失败：{ex.Message}";
        }
    }

    private void OnOpenCache(object sender, RoutedEventArgs e) => OpenFolder(CacheDirBox.Text);

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
