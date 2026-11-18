using System.Globalization;
using System.Windows;
using System.Windows.Input;
using EchoGarage.App.Services;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Views;

/// <summary>Per-file prompt shown before converting a source above the lossy-format sample-rate limit.</summary>
public partial class ConversionResampleWindow : ThemedWindow
{
    public ConversionResampleWindow(ConversionResamplePrompt prompt)
    {
        InitializeComponent();
        MessageText.Text = $"“{prompt.FileName}”的源采样率为 {FormatRate(prompt.SourceSampleRate)}。";
        RateText.Text = $"源采样率：{FormatRate(prompt.SourceSampleRate)}　　目标采样率：{FormatRate(prompt.TargetSampleRate)}";
        DownsampleButton.Content = $"降采样到 {FormatRate(prompt.TargetSampleRate)}";
        var target = FormatRate(prompt.TargetSampleRate);
        HintText.Text = prompt.Format == ConversionFormat.Mp3
            ? $"MP3 不支持 {target} 以上的采样率；建议先降采样再转换。"
            : $"AAC 支持更高采样率，但在 {target} 以上音质损失更大；建议先降采样再转换。";
        NoteText.Text = "选择“保持源采样率”会直接交给编码器处理；若编码器不支持该采样率，转换会失败。";
    }

    public ConversionResampleChoice Choice { get; private set; } = ConversionResampleChoice.Cancel;

    private static string FormatRate(int rate) => rate.ToString("N0", CultureInfo.InvariantCulture) + " Hz";

    private void OnDownsampleClick(object sender, RoutedEventArgs e) => Finish(ConversionResampleChoice.Downsample);

    private void OnKeepClick(object sender, RoutedEventArgs e) => Finish(ConversionResampleChoice.KeepSource);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Finish(ConversionResampleChoice.Cancel);

    private void Finish(ConversionResampleChoice choice)
    {
        Choice = choice;
        DialogResult = choice != ConversionResampleChoice.Cancel;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Choice = ConversionResampleChoice.Cancel;
            DialogResult = false;
        }
    }
}
