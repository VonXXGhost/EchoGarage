using System.Windows;
using System.Windows.Input;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Views;

/// <summary>
/// 「音频信息」窗口：以 Poweramp 风格的纵向管线展示当前歌曲从文件到设备的完整音频处理路径。
/// 段落与说明文字由 <see cref="EchoGarage.Core.Services.AudioPipelineFormatter"/> 生成，
/// 窗口只负责呈现；所有颜色走主题令牌，支持运行中热切换主题。
/// </summary>
public partial class AudioInfoWindow : ThemedWindow
{
    public AudioInfoWindow(AudioPipelineReport report, string? trackTitle, string? trackSubtitle)
    {
        InitializeComponent();

        var stages = new List<AudioStageEntry>(report.Stages.Count);
        for (var index = 0; index < report.Stages.Count; index++)
        {
            stages.Add(new AudioStageEntry(report.Stages[index], index > 0));
        }

        DataContext = new AudioInfoModel(
            string.IsNullOrWhiteSpace(trackTitle) ? "未在播放" : trackTitle,
            trackSubtitle ?? string.Empty,
            report.Summary,
            stages);

        if (!string.IsNullOrWhiteSpace(report.Hint))
        {
            HintText.Text = report.Hint;
            HintText.Visibility = Visibility.Visible;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}

/// <summary>一段管线的展示条目；<see cref="HasConnectorAbove"/> 控制段前“↓”连接符。</summary>
public sealed record AudioStageEntry(
    string Title,
    string Headline,
    IReadOnlyList<AudioPipelineDetail> Details,
    bool HasConnectorAbove)
{
    public AudioStageEntry(AudioPipelineStage stage, bool hasConnectorAbove)
        : this(stage.Title, stage.Headline, stage.Details, hasConnectorAbove)
    {
    }
}

/// <summary>窗口数据上下文（record，纯展示属性）。</summary>
public sealed record AudioInfoModel(
    string TrackTitle,
    string TrackSubtitle,
    string Summary,
    IReadOnlyList<AudioStageEntry> Stages);
