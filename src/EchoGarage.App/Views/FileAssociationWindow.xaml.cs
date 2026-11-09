using System.Windows;
using EchoGarage.Core.Models;
using EchoGarage.Infrastructure.Windows;

namespace EchoGarage.App.Views;

/// <summary>
/// Explicit, opt-in file association registration. The user chooses which extensions to
/// register; the app never registers itself automatically at startup.
/// </summary>
public partial class FileAssociationWindow : ThemedWindow
{
    private readonly AppHost _host;
    private readonly Dictionary<string, System.Windows.Controls.CheckBox> _boxes = [];

    public FileAssociationWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        BuildExtensionList();
        UpdateStatus();
    }

    private void BuildExtensionList()
    {
        var saved = _host.Settings.Current.AssociationExtensions;
        foreach (var extension in FileAssociationService.AudioExtensions)
        {
            var box = new System.Windows.Controls.CheckBox
            {
                Content = extension.TrimStart('.').ToUpperInvariant() + "  (" + extension + ")",
                Tag = extension,
                Margin = new Thickness(0, 4, 18, 4),
                MinWidth = 150,
                IsChecked = saved.Count == 0 ||
                            saved.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase))
            };

            _boxes[extension] = box;
            ExtensionPanel.Children.Add(box);
        }
    }

    private IReadOnlyList<string> SelectedExtensions() =>
        _boxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var box in _boxes.Values)
        {
            box.IsChecked = true;
        }
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var box in _boxes.Values)
        {
            box.IsChecked = false;
        }
    }

    private async void OnRegister(object sender, RoutedEventArgs e)
    {
        var selected = SelectedExtensions();
        if (selected.Count == 0)
        {
            StatusText.Text = "请至少选择一种音频格式。";
            return;
        }

        var executable = Environment.ProcessPath;
        if (executable is null || !_host.Associations.Register(executable, selected))
        {
            StatusText.Text = "注册失败：无法写入注册表，或程序路径不可用。";
            return;
        }

        _host.Settings.Current.AssociationExtensions = selected.ToList();
        await _host.Settings.SaveAsync();
        StatusText.Text = $"已注册 {selected.Count} 种格式。请在 Windows“默认应用”中确认把 EchoGarage 设为默认。";
        UpdateStatus();
    }

    private async void OnUnregister(object sender, RoutedEventArgs e)
    {
        if (!_host.Associations.Unregister())
        {
            StatusText.Text = "取消注册失败：无法写入注册表。";
            return;
        }

        await _host.Settings.SaveAsync();
        StatusText.Text = "已取消注册。";
        UpdateStatus();
    }

    private void OnOpenDefaultApps(object sender, RoutedEventArgs e) => FileAssociationService.OpenDefaultAppsSettings();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void UpdateStatus()
    {
        RegistrationStatusText.Text = _host.Associations.IsRegistered()
            ? "当前状态：已注册为音频文件的候选打开方式。"
            : "当前状态：尚未注册。EchoGarage 不会在启动时自动注册。";
    }
}
