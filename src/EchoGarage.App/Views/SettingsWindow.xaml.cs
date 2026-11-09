using System.Windows;
using System.Windows.Input;
using EchoGarage.App.Services;
using EchoGarage.App.ViewModels;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Views;

public partial class SettingsWindow : ThemedWindow
{
    private readonly AppHost _host;
    private readonly MainViewModel _vm;

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
        RefreshFolders();
        UpdateAssociationStatus();
    }

    private string vm_LibrarySummary() => _vm.LibrarySummary;

    private void RefreshFolders()
    {
        FolderList.ItemsSource = _vm.Sources
            .Where(s => s.Kind == SourceKind.Folder && s.Folder is not null)
            .ToList();
    }

    private void UpdateAssociationStatus()
    {
        AssociationStatusText.Text = _host.Associations.IsRegistered()
            ? "已注册为音频文件候选播放器。"
            : "尚未注册为音频文件候选播放器。";
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

    private async void OnRegister(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !_host.Associations.Register(exe))
        {
            MessageBox.Show(this, "注册失败：无法写入注册表。", "EchoGarage", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        UpdateAssociationStatus();
        if (MessageBox.Show(this, "注册成功。是否打开 Windows“默认应用”设置继续选择 EchoGarage？", "EchoGarage",
                MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK)
        {
            FileAssociationService.OpenDefaultAppsSettings();
        }
    }

    private void OnUnregister(object sender, RoutedEventArgs e)
    {
        _host.Associations.Unregister();
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
