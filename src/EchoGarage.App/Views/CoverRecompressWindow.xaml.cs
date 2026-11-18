using System.Windows;
using System.Windows.Input;
using EchoGarage.App.Services;

namespace EchoGarage.App.Views;

/// <summary>「封面已按当前设置压缩过」提示需要展示的数据（分辨率与体积来自当前封面字节）。</summary>
public sealed record CoverRecompressPrompt(
    int Width,
    int Height,
    long ByteSize,
    int Quality,
    int MaxEdge,
    int FileCount);

/// <summary>用户对「是否再压一次封面」的选择。</summary>
public enum CoverRecompressChoice
{
    /// <summary>关闭提示并留在属性页，不保存。</summary>
    Cancel,

    /// <summary>照常保存其它修改，封面字节保持原样。</summary>
    KeepOriginal,

    /// <summary>按当前质量 / 最大边长重新编码封面。</summary>
    Compress
}

/// <summary>
/// 保存标签时，如果封面已经是 JPEG 且最长边正好等于设置的最大边长，说明它很可能已经被
/// 这套转换设置压缩过。这里把当前分辨率、体积和本次设置摆给用户看，由用户决定是再压一次
/// （体积可能略小、画质再损失一点）还是保持原样，避免重复压缩已经转换好的封面。
/// </summary>
public partial class CoverRecompressWindow : ThemedWindow
{
    public CoverRecompressWindow(CoverRecompressPrompt prompt)
    {
        InitializeComponent();
        MessageText.Text = $"当前封面已经是 JPEG，最长边正好等于设置的最大边长（{prompt.MaxEdge} px），"
            + "可能已经按这套设置压缩过。再次保存会重新编码同一张图。";
        CoverInfoText.Text = $"当前封面：JPEG · {prompt.Width}×{prompt.Height} · "
            + $"{DisplayFormatter.FileSize(prompt.ByteSize)}";
        SettingInfoText.Text = $"本次设置：JPEG · 质量 {prompt.Quality} · 最大边长 {prompt.MaxEdge} px";

        if (prompt.FileCount > 1)
        {
            ScopeText.Text = $"这个选择会应用到 {prompt.FileCount} 个文件。";
            ScopeText.Visibility = Visibility.Visible;
        }

        HintText.Text = "再次压缩不会改变分辨率，体积通常只会小一点，但画质会继续损失；"
            + "选择「保持原样」则照常保存其它标签，封面保持不变。";
    }

    /// <summary>用户的选择；关闭窗口（取消）时为 <see cref="CoverRecompressChoice.Cancel"/>。</summary>
    public CoverRecompressChoice Choice { get; private set; } = CoverRecompressChoice.Cancel;

    private void OnKeepClick(object sender, RoutedEventArgs e) => Finish(CoverRecompressChoice.KeepOriginal);

    private void OnCompressClick(object sender, RoutedEventArgs e) => Finish(CoverRecompressChoice.Compress);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Finish(CoverRecompressChoice.Cancel);

    private void Finish(CoverRecompressChoice choice)
    {
        Choice = choice;
        DialogResult = choice != CoverRecompressChoice.Cancel;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Choice = CoverRecompressChoice.Cancel;
            DialogResult = false;
        }
    }
}
