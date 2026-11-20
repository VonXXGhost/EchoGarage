using System.Windows;
using EchoGarage.Core.Models;
using EchoGarage.Infrastructure.Windows;

namespace EchoGarage.App.Views;

/// <summary>
/// Explicit, opt-in file association registration (ADR-008). The user chooses which extensions
/// to register; the app never registers itself automatically at startup, never overwrites the
/// user's current default handler, and reports exactly which formats the system let us register.
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
                Content = FormatExtension(extension) + "  (" + extension + ")",
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
        if (string.IsNullOrWhiteSpace(executable))
        {
            StatusText.Text = "注册失败：无法确定 EchoGarage.exe 的路径（请直接用发布版程序运行本功能）。";
            UpdateStatus();
            return;
        }

        var result = _host.Associations.Register(executable, selected);
        if (!result.ExecutableAvailable)
        {
            StatusText.Text = $"注册失败：找不到程序路径 {executable}。";
            UpdateStatus();
            return;
        }

        // 只有选中格式都拿到了候选注册（可能缺少 OpenWithProgids）才记住选择，
        // 否则下次打开对话框会勾选实际没注册成功的格式。
        if (result.CoreRegistered && result.FailedExtensions.Count == 0)
        {
            _host.Settings.Current.AssociationExtensions = selected.ToList();
            await _host.Settings.SaveAsync();
        }

        StatusText.Text = BuildRegisterMessage(result);
        UpdateStatus();
    }

    private async void OnUnregister(object sender, RoutedEventArgs e)
    {
        var result = _host.Associations.Unregister();
        if (!result.CoreCleared)
        {
            StatusText.Text = "取消注册失败：无法清理核心注册表项，请查看日志了解被拒绝的具体项。";
            UpdateStatus();
            return;
        }

        await _host.Settings.SaveAsync();
        StatusText.Text = result.LockedExtensions.Count == 0
            ? "已取消注册。"
            : $"已取消注册；{JoinExtensions(result.LockedExtensions)} 的旧式“打开方式”条目因注册表项只读未能删除，" +
              "不影响使用。";
        UpdateStatus();
    }

    private void OnOpenDefaultApps(object sender, RoutedEventArgs e) => FileAssociationService.OpenDefaultAppsSettings();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void UpdateStatus()
    {
        var state = _host.Associations.GetRegistrationState();
        var candidates = state.Where(s => s.Registered || s.OpenWithRegistered).ToList();
        if (candidates.Count == 0)
        {
            RegistrationStatusText.Text = _host.Associations.IsRegistered()
                ? "当前状态：检测到残留的注册条目，但未登记任何格式，请重新注册。"
                : "当前状态：尚未注册。EchoGarage 不会在启动时自动注册。";
            return;
        }

        var legacyMissing = state
            .Where(s => s.Registered && !s.OpenWithRegistered)
            .Select(s => s.Extension)
            .ToList();
        RegistrationStatusText.Text = legacyMissing.Count == 0
            ? $"当前状态：已注册为 {candidates.Count} 种音频格式的候选打开方式。"
            : $"当前状态：已注册为 {candidates.Count} 种音频格式的候选打开方式；" +
              $"{JoinExtensions(legacyMissing)} 未写入旧式“打开方式”条目（注册表项被系统保护）。";
    }

    private static string BuildRegisterMessage(FileAssociationResult result)
    {
        if (!result.CoreRegistered)
        {
            return "注册失败：无法写入核心注册表项（ProgId / 打开命令 / RegisteredApplications），" +
                   "请确认当前用户对 HKCU\\Software 有写权限。";
        }

        if (result.FailedExtensions.Count > 0)
        {
            var message = $"注册未完全成功：{JoinExtensions(result.FailedExtensions)} 无法写入注册表（系统权限受限）；" +
                          $"已注册 {result.RegisteredExtensions.Count + result.PartialExtensions.Count} 种格式。";
            if (result.PartialExtensions.Count > 0)
            {
                message += $"{JoinExtensions(result.PartialExtensions)} 只完成了“默认应用”注册。";
            }

            return message;
        }

        if (result.PartialExtensions.Count > 0)
        {
            return $"已注册 {result.SelectedCount} 种格式。其中 {JoinExtensions(result.PartialExtensions)} 的旧式“打开方式”条目" +
                   "被系统拒绝（对应注册表项只读），已改用“默认应用”入口注册；仍可在 Windows“默认应用”中把 EchoGarage 设为这些格式的默认程序。";
        }

        return $"已注册 {result.SelectedCount} 种格式。请在 Windows“默认应用”中确认把 EchoGarage 设为默认。";
    }

    private static string JoinExtensions(IReadOnlyList<string> extensions) =>
        string.Join("、", extensions.Select(FormatExtension));

    private static string FormatExtension(string extension) => extension.TrimStart('.').ToUpperInvariant();
}
