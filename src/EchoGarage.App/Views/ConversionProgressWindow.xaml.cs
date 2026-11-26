using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using EchoGarage.App.ViewModels;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Views;

/// <summary>
/// 转换批次窗口（ADR-017 §8）：非模态，运行中显示进度与逐文件状态；
/// 结束后原地切换为结果列表，不再另行弹出完成提示。
/// 窗口本身不持有转换逻辑，行状态由 <see cref="Services.ConversionCoordinator"/> 驱动。
/// </summary>
public partial class ConversionProgressWindow : ThemedWindow
{
    private readonly string _presetName;
    private bool _running;
    private bool _closeRequested;
    private bool _cancelRequested;

    public ConversionProgressWindow(string presetName, IReadOnlyList<ConversionResultRowViewModel> rows)
    {
        InitializeComponent();
        _presetName = presetName;
        _running = true;
        Rows = new ObservableCollection<ConversionResultRowViewModel>(rows);
        ResultGrid.ItemsSource = Rows;

        Title = $"转换：{presetName}";
        TitleText.Text = $"正在转换：{presetName}";
        SubtitleText.Text = $"共 {Rows.Count} 个文件";
        Progress.Value = 0;
        PercentText.Text = "0%";
        StageText.Text = "准备中…";
        Closing += OnClosing;
    }

    public ObservableCollection<ConversionResultRowViewModel> Rows { get; }

    public bool IsRunning => _running;

    /// <summary>Raised when the user presses Cancel or closes the window during the batch.</summary>
    public event EventHandler? CancelRequested;

    /// <summary>更新当前文件在总任务中的进度；<paramref name="fraction"/> 为负时按 0 处理。</summary>
    public void Report(int index, int count, string fileName, ConversionStage stage, double fraction, string? detail)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Report(index, count, fileName, stage, fraction, detail)));
            return;
        }

        if (!_running)
        {
            return;
        }

        var itemFraction = fraction >= 0 ? Math.Clamp(fraction, 0, 1) : 0;
        var overall = count > 0 ? Math.Clamp((index + itemFraction) / count, 0, 1) : 0;
        Progress.Value = overall * 100;
        PercentText.Text = $"{overall * 100:0}%";
        CurrentItemText.Text = $"正在转换第 {index + 1}/{count} 首：{fileName}";
        StageText.Text = string.IsNullOrWhiteSpace(detail)
            ? DescribeStage(stage)
            : $"{DescribeStage(stage)} · {detail}";
    }

    /// <summary>
    /// 批次结束（完成或取消）：窗口保持打开，原地切换为结果列表。
    /// 若用户此前已请求关闭窗口，则在结束后真正关闭。
    /// </summary>
    public void Complete(string summary, string hint, bool cancelled)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Complete(summary, hint, cancelled)));
            return;
        }

        if (!_running)
        {
            return;
        }

        _running = false;
        Title = cancelled ? $"转换已取消：{_presetName}" : $"转换完成：{_presetName}";
        TitleText.Text = cancelled ? "转换已取消" : "转换完成";
        SubtitleText.Text = summary;
        SummaryText.Text = hint;
        CurrentItemText.Text = cancelled ? "未转换的文件保持原样。" : "所有文件处理完毕。";
        StageText.Text = cancelled ? "已取消" : "已完成";

        if (!cancelled)
        {
            Progress.Value = 100;
            PercentText.Text = "100%";
        }

        CancelButton.Visibility = Visibility.Collapsed;
        CloseButton.IsEnabled = true;
        CloseButton.IsDefault = true;
        CloseButton.Focus();

        if (_closeRequested)
        {
            Close();
        }
    }

    private static string DescribeStage(ConversionStage stage) => stage switch
    {
        ConversionStage.Preparing => "准备中…",
        ConversionStage.Decoding => "解码中…",
        ConversionStage.Resampling => "重采样中…",
        ConversionStage.Encoding => "编码中…",
        ConversionStage.WritingTags => "写入标签…",
        ConversionStage.Done => "完成",
        _ => "处理中…"
    };

    private void OnCancelClick(object sender, RoutedEventArgs e) => RequestCancel();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_running)
        {
            return;
        }

        if (!_closeRequested &&
            MessageBox.Show(this,
                "转换正在进行，关闭窗口将取消剩余任务。是否继续？",
                "取消转换",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _closeRequested = true;
        RequestCancel();
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
        SummaryText.Text = "正在取消，请稍候…";
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}
