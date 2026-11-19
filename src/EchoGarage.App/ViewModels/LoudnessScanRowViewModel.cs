using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using EchoGarage.App.Services;
using EchoGarage.Core.Models;

namespace EchoGarage.App.ViewModels;

/// <summary>
/// 响度扫描窗口的一行（ADR-028 §3.4）。
/// 只做展示，不向文件标签 / 数据库 / 缓存写入任何内容。
/// </summary>
public partial class LoudnessScanRowViewModel : ObservableObject
{
    public LoudnessScanRowViewModel(Track track)
    {
        Track = track;
        FileName = track.FileName;
        Folder = Path.GetDirectoryName(track.Path) ?? string.Empty;
        DurationText = track.DurationMs > 0
            ? DisplayFormatter.Duration(track.DurationMs)
            : DisplayFormatter.Duration(track.Duration);
        FormatText = DescribeFormat(track, track.SampleRate, track.Channels);
    }

    public Track Track { get; }

    public string FileName { get; }

    public string Folder { get; }

    public string DurationText { get; }

    /// <summary>供 DataGridRow 隐式样式的 ToolTip 绑定使用。</summary>
    public string RowToolTip => Track.Path;

    public bool IsWaiting => Status == "等待中";

    [ObservableProperty]
    private string _status = "等待中";

    [ObservableProperty]
    private bool _failed;

    [ObservableProperty]
    private bool _canceled;

    [ObservableProperty]
    private string _formatText = "—";

    [ObservableProperty]
    private string _integratedText = "—";

    [ObservableProperty]
    private string _loudnessRangeText = "—";

    [ObservableProperty]
    private string _truePeakText = "—";

    [ObservableProperty]
    private string _samplePeakText = "—";

    [ObservableProperty]
    private double? _integratedLufs;

    [ObservableProperty]
    private double? _loudnessRange;

    [ObservableProperty]
    private double? _truePeakDb;

    [ObservableProperty]
    private double? _samplePeakDb;

    public void Reset()
    {
        Status = "等待中";
        Failed = false;
        Canceled = false;
        IntegratedLufs = null;
        LoudnessRange = null;
        TruePeakDb = null;
        SamplePeakDb = null;
        IntegratedText = "—";
        LoudnessRangeText = "—";
        TruePeakText = "—";
        SamplePeakText = "—";
        FormatText = DescribeFormat(Track, Track.SampleRate, Track.Channels);
    }

    public void MarkAnalyzing(int index, int count)
    {
        Status = $"分析中（{index}/{count}）";
        Failed = false;
        Canceled = false;
    }

    public void ApplyMeasurement(LoudnessMeasurement measurement)
    {
        IntegratedLufs = FiniteOrNull(measurement.IntegratedLufs);
        LoudnessRange = FiniteOrNull(measurement.LoudnessRange);
        TruePeakDb = FiniteOrNull(measurement.TruePeakDb);
        SamplePeakDb = FiniteOrNull(measurement.SamplePeakDb);

        IntegratedText = FormatValue(measurement.IntegratedLufs);
        LoudnessRangeText = FormatValue(measurement.LoudnessRange);
        TruePeakText = FormatValue(measurement.TruePeakDb);
        SamplePeakText = FormatValue(measurement.SamplePeakDb);

        // 测量结果里的采样率 / 声道数来自实际解码的音频，比标签缓存更可信。
        FormatText = DescribeFormat(
            Track,
            measurement.SampleRate > 0 ? measurement.SampleRate : Track.SampleRate,
            measurement.Channels > 0 ? measurement.Channels : Track.Channels);

        Status = "完成";
        Failed = false;
        Canceled = false;
    }

    public void MarkFailed(string reason)
    {
        Status = string.IsNullOrWhiteSpace(reason) ? "失败" : $"失败：{reason}";
        Failed = true;
        Canceled = false;
    }

    public void MarkCanceled()
    {
        Status = "已取消";
        Canceled = true;
        Failed = false;
    }

    private static double? FiniteOrNull(double? value)
        => value is double number && double.IsFinite(number) ? number : null;

    private static string FormatValue(double? value)
        => value is double number && double.IsFinite(number)
            ? number.ToString("0.0")
            : "—";

    private static string DescribeFormat(Track track, int sampleRate, int channels)
    {
        var codec = string.IsNullOrWhiteSpace(track.Codec)
            ? Path.GetExtension(track.Path).TrimStart('.').ToUpperInvariant()
            : track.Codec.ToUpperInvariant();

        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(codec))
        {
            parts.Add(codec);
        }

        if (sampleRate > 0)
        {
            parts.Add($"{sampleRate / 1000.0:0.#} kHz");
        }

        if (channels > 0)
        {
            parts.Add(channels == 2 ? "立体声" : $"{channels} 声道");
        }

        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }
}
