using System.ComponentModel;
using System.Windows;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Views;

/// <summary>Non-modal progress window for a conversion batch.</summary>
public partial class ConversionProgressWindow : ThemedWindow
{
    private bool _completed;
    private bool _cancelRequested;

    public ConversionProgressWindow(string presetName, int itemCount)
    {
        InitializeComponent();
        TitleText.Text = $"正在转换：{presetName}";
        SubtitleText.Text = $"共 {itemCount} 个文件";
        Progress.Value = 0;
        PercentText.Text = "0%";
        StageText.Text = "准备中…";
        Closing += OnClosing;
    }

    /// <summary>Raised when the user closes the window or presses Cancel.</summary>
    public event EventHandler? CancelRequested;

    public void Report(int index, int count, string title, ConversionStage stage, double fraction, string? detail)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Report(index, count, title, stage, fraction, detail)));
            return;
        }

        CurrentItemText.Text = title;
        var overall = count <= 0 ? 0 : Math.Clamp((index + fraction) / count, 0, 1);
        Progress.Value = overall * 100;
        PercentText.Text = $"{overall * 100:0}%";
        StageText.Text = detail is { Length: > 0 } ? detail : DescribeStage(stage);
    }

    /// <summary>Closes the window without treating it as a cancellation.</summary>
    public void Complete()
    {
        _completed = true;
        Close();
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

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_completed)
        {
            return;
        }

        e.Cancel = true;
        RequestCancel();
    }

    private void RequestCancel()
    {
        if (_cancelRequested)
        {
            return;
        }

        _cancelRequested = true;
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在取消，请稍候…";
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}
