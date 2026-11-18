using System.Globalization;
using System.Windows;
using System.Windows.Input;
using EchoGarage.App.Services;

namespace EchoGarage.App.Views;

/// <summary>Batch prompt asking whether the remaining above-limit files share the first file's downsampling choice.</summary>
public partial class ConversionResampleScopeWindow : ThemedWindow
{
    public ConversionResampleScopeWindow(ConversionResampleScopePrompt prompt)
    {
        InitializeComponent();
        var target = FormatRate(prompt.TargetSampleRate);
        MessageText.Text = $"还有 {prompt.RemainingCount} 个文件的采样率高于 {target}。"
            + (prompt.FirstChoiceDownsampled
                ? $"是否也全部降采样到 {target}？"
                : "是否也全部保持源采样率（直接交给编码器）？");
        ExampleText.Text = prompt.ExampleFileNames.Count > 0
            ? "例如：" + string.Join("、", prompt.ExampleFileNames)
            : string.Empty;
        SameButton.Content = prompt.FirstChoiceDownsampled
            ? $"是，全部降采样到 {target}"
            : "是，其余保持源采样率";
        OppositeButton.Content = prompt.FirstChoiceDownsampled
            ? "否，其余保持源采样率"
            : $"否，全部降采样到 {target}";
        HintText.Text = prompt.FirstChoiceDownsampled
            ? $"选择“是”会对这些文件全部降采样到 {target}；选择“否”则保留它们的源采样率。"
            : $"选择“是”会保留这些文件的源采样率；选择“否”会将它们全部降采样到 {target}。";
    }

    public ConversionResampleScopeChoice Choice { get; private set; } = ConversionResampleScopeChoice.Cancel;

    private static string FormatRate(int rate) => rate.ToString("N0", CultureInfo.InvariantCulture) + " Hz";

    private void OnSameClick(object sender, RoutedEventArgs e) => Finish(ConversionResampleScopeChoice.SameAsFirst);

    private void OnOppositeClick(object sender, RoutedEventArgs e) => Finish(ConversionResampleScopeChoice.OppositeOfFirst);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Finish(ConversionResampleScopeChoice.Cancel);

    private void Finish(ConversionResampleScopeChoice choice)
    {
        Choice = choice;
        DialogResult = choice != ConversionResampleScopeChoice.Cancel;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Choice = ConversionResampleScopeChoice.Cancel;
            DialogResult = false;
        }
    }
}
