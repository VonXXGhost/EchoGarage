using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using EchoGarage.App.ViewModels;

namespace EchoGarage.App.Views;

/// <summary>
/// 响度扫描窗口（ADR-028 §3.4）：非模态，打开后自动开始分析，只读展示结果。
/// 窗口本身不持有分析逻辑，进度与结果行由 <see cref="LoudnessCoordinator"/> 驱动。
/// </summary>
public partial class LoudnessScanWindow : ThemedWindow
{
    private bool _running;
    private bool _closeRequested;
    private bool _cancelRequested;

    public LoudnessScanWindow(IReadOnlyList<LoudnessScanRowViewModel> rows)
    {
        InitializeComponent();
        Rows = new ObservableCollection<LoudnessScanRowViewModel>(rows);
        ScanGrid.ItemsSource = Rows;
        Closing += OnClosing;
    }

    public ObservableCollection<LoudnessScanRowViewModel> Rows { get; }

    public bool IsScanning => _running;

    public event EventHandler? CancelRequested;

    public event EventHandler? RescanRequested;

    public void BeginScan(int count)
    {
        _running = true;
        _closeRequested = false;
        _cancelRequested = false;

        Title = "响度计算";
        CurrentFileText.Text = count == 0 ? "没有可分析的文件。" : $"已选择 {count} 个文件，准备开始…";
        PercentText.Text = "0%";
        ScanProgress.IsIndeterminate = false;
        ScanProgress.Value = 0;
        SummaryText.Text = string.Empty;
        CancelButton.Content = "取消";
        CancelButton.IsEnabled = count > 0;
        RescanButton.IsEnabled = false;
    }

    /// <summary>更新当前文件在总任务中的进度。<paramref name="fileFraction"/> 为 -1 时按 0 处理。</summary>
    public void ReportFile(int index, int count, string fileName, double fileFraction)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ReportFile(index, count, fileName, fileFraction)));
            return;
        }

        if (!_running)
        {
            return;
        }

        var fraction = fileFraction >= 0 ? Math.Clamp(fileFraction, 0, 1) : 0;
        var overall = count > 0 ? Math.Clamp((index + fraction) / count, 0, 1) : 0;
        ScanProgress.IsIndeterminate = false;
        ScanProgress.Value = overall * 100;
        PercentText.Text = $"{overall * 100:0}%";
        CurrentFileText.Text = $"正在分析 {index + 1}/{count}：{fileName}";
    }

    /// <summary>分析结束（完成或取消）。若用户此前已请求关闭窗口，则在结束后真正关闭。</summary>
    public void Complete(string summary, bool canceled)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Complete(summary, canceled)));
            return;
        }

        _running = false;
        ScanProgress.IsIndeterminate = false;
        if (!canceled)
        {
            ScanProgress.Value = 100;
            PercentText.Text = "100%";
        }

        CurrentFileText.Text = canceled ? "已取消。" : "分析完成。";
        SummaryText.Text = summary;
        CancelButton.IsEnabled = false;
        RescanButton.IsEnabled = true;

        if (_closeRequested)
        {
            Close();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => RequestCancel();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnRescanClick(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            return;
        }

        RescanRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RequestCancel()
    {
        if (!_running || _cancelRequested)
        {
            return;
        }

        _cancelRequested = true;
        CancelButton.IsEnabled = false;
        CancelButton.Content = "正在取消…";
        CurrentFileText.Text = "正在取消，请稍候…";
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_running)
        {
            return;
        }

        e.Cancel = true;
        _closeRequested = true;
        RequestCancel();
    }
}
