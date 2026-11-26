using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.App.ViewModels;

/// <summary>
/// 转换批次窗口的一行（ADR-017 §8）：源文件 → 输出文件，以及该文件的转换状态。
/// 窗口只负责展示，行的状态由 <see cref="Services.ConversionCoordinator"/> 驱动。
/// </summary>
public partial class ConversionResultRowViewModel : ObservableObject
{
    private const string WaitingStatus = "等待中";

    public ConversionResultRowViewModel(ConversionPlanItem item)
    {
        Item = item;
        SourcePath = item.Track.Path;
        SourceName = Path.GetFileName(item.Track.Path);
        OutputPath = item.OutputPath;

        if (item.Skipped)
        {
            Skipped = true;
            Muted = true;
            Status = string.IsNullOrWhiteSpace(item.SkipReason) ? "跳过" : $"跳过：{item.SkipReason}";
        }
    }

    public ConversionPlanItem Item { get; }

    public string SourcePath { get; }

    public string SourceName { get; }

    /// <summary>计划内被跳过的条目（例如目标已存在、源文件缺失），不参与转换。</summary>
    public bool Skipped { get; private set; }

    /// <summary>供 DataGridRow 隐式样式的 ToolTip 绑定使用。</summary>
    public string RowToolTip => string.Join(
        Environment.NewLine,
        $"源文件：{SourcePath}",
        $"输出文件：{(string.IsNullOrWhiteSpace(OutputPath) ? "（无）" : OutputPath)}",
        $"状态：{Status}");

    public bool IsWaiting => Status == WaitingStatus;

    /// <summary>仍在处理中的行（等待或转换中），取消后需要标记为「已取消」。</summary>
    public bool IsPending => IsWaiting || Running;

    public string OutputName => string.IsNullOrWhiteSpace(OutputPath) ? "—" : Path.GetFileName(OutputPath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowToolTip))]
    [NotifyPropertyChangedFor(nameof(OutputName))]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowToolTip))]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private string _status = WaitingStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private bool _running;

    [ObservableProperty]
    private bool _failed;

    [ObservableProperty]
    private bool _warning;

    [ObservableProperty]
    private bool _muted;

    public void MarkRunning()
    {
        Running = true;
        Muted = false;
        Failed = false;
        Warning = false;
        Status = "转换中";
    }

    public void ApplyResult(ConversionResult result)
    {
        Running = false;

        if (result.Success)
        {
            if (!string.IsNullOrWhiteSpace(result.OutputPath))
            {
                OutputPath = result.OutputPath;
            }

            Failed = false;
            Muted = false;
            Warning = !string.IsNullOrWhiteSpace(result.Warning);
            Status = Warning ? $"成功（{result.Warning}）" : "成功";
            return;
        }

        if (result.Skipped)
        {
            Skipped = true;
            Muted = true;
            Failed = false;
            Warning = false;
            Status = string.IsNullOrWhiteSpace(result.Error) ? "跳过" : $"跳过：{result.Error}";
            return;
        }

        Failed = true;
        Muted = false;
        Warning = false;
        Status = string.IsNullOrWhiteSpace(result.Error) ? "失败" : $"失败：{result.Error}";
    }

    public void MarkCancelled()
    {
        Running = false;
        Muted = true;
        Failed = false;
        Warning = false;
        Status = "已取消";
    }
}
