using System.Globalization;
using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>BPM 数值校验、格式化、倍频换算与模型下载指引常量（ADR-034）。</summary>
public static class BpmFormatter
{
    public const double MinBpm = 20.0;
    public const double MaxBpm = 300.0;

    /// <summary>变速曲目分段展示的最大段落数；超过此数量时隐藏分段明细并提示变速过多。</summary>
    public const int MaxDisplaySegments = 5;

    public const string Small0FileName = "beat_this_small0.onnx";
    public const string Final0FileName = "beat_this_final0.onnx";

    /// <summary>社区 MIT ONNX 导出仓库（nilsbc/beat-this-onnx，含 small0 与 final0 及 SHA256SUMS）。</summary>
    public const string ModelRepoUrl = "https://huggingface.co/nilsbc/beat-this-onnx";
    public const string Final0DownloadUrl = "https://huggingface.co/nilsbc/beat-this-onnx/resolve/main/beat_this_final0.onnx";
    public const string UpstreamRepoUrl = "https://github.com/CPJKU/beat_this";
    public const string Final0ExpectedSha256 = "a7e2c68b27f9db6a73416017f784ea8551a01373f4955a98db018827760c4a14";

    public static bool IsValid(double value) =>
        double.IsFinite(value) && value >= MinBpm && value <= MaxBpm;

    /// <summary>解析用户或文件标签中的 BPM 文本；支持整数与小数，允许尾部带 "BPM" 后缀。</summary>
    public static bool TryParse(string? text, out double bpm)
    {
        bpm = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.EndsWith("BPM", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3].Trim();
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
        {
            return false;
        }

        if (!IsValid(parsed))
        {
            return false;
        }

        bpm = RoundForStorage(parsed);
        return true;
    }

    public static double RoundForStorage(double bpm) =>
        Math.Round(bpm, 2, MidpointRounding.AwayFromZero);

    public static double RoundForDisplay(double bpm, int decimalPlaces = 1) =>
        decimalPlaces <= 0
            ? Math.Round(bpm, 0, MidpointRounding.AwayFromZero)
            : Math.Round(bpm, 1, MidpointRounding.AwayFromZero);

    /// <summary>「正在播放」页显示文本：默认 1 位小数（如 "128.0" / "127.5"），缺失返回 "—"。</summary>
    public static string FormatDisplay(double? bpm, int decimalPlaces = 1, string emptyText = "—")
    {
        if (bpm is not { } value || !IsValid(value))
        {
            return emptyText;
        }

        var rounded = RoundForDisplay(value, decimalPlaces);
        return decimalPlaces <= 0
            ? rounded.ToString("0", CultureInfo.InvariantCulture)
            : rounded.ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>输入框回填文本：1 位小数但整数省略多余尾零（如 128.0 → "128.0" 或 "128"）。</summary>
    public static string FormatInput(double? bpm, int decimalPlaces = 1)
    {
        if (bpm is not { } value || !IsValid(value))
        {
            return string.Empty;
        }

        return FormatDisplay(value, decimalPlaces, string.Empty);
    }

    /// <summary>
    /// 写入文本类文件标签（Xiph BPM / ID3v2 TBPM / ASF）的格式化规则：
    /// 整拍写整数（如 "128"）兼顾最大兼容性，非整拍保留 1 位小数（如 "128.5"）；
    /// <paramref name="roundToInteger"/> 为 true 时统一四舍五入取整。
    /// </summary>
    public static string FormatTagText(double bpm, bool roundToInteger = false)
    {
        if (!IsValid(bpm))
        {
            return string.Empty;
        }

        if (roundToInteger)
        {
            return Math.Round(bpm, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        }

        var rounded = Math.Round(bpm, 1, MidpointRounding.AwayFromZero);
        return Math.Abs(rounded - Math.Round(rounded)) < 0.001
            ? Math.Round(rounded).ToString("0", CultureInfo.InvariantCulture)
            : rounded.ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>M4A / MP4 / M4B 的 Apple <c>tmpo</c> atom 按规范仅支持 16-bit 无符号整数。</summary>
    public static bool RequiresIntegerTag(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        return ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".m4b", StringComparison.OrdinalIgnoreCase);
    }

    public static uint ToIntegerTag(double bpm) =>
        (uint)Math.Clamp(Math.Round(bpm, 0, MidpointRounding.AwayFromZero), 1, ushort.MaxValue);

    public static bool TryHalve(double bpm, out double halved)
    {
        var candidate = RoundForDisplay(bpm / 2.0, 1);
        if (IsValid(candidate))
        {
            halved = candidate;
            return true;
        }

        halved = bpm;
        return false;
    }

    public static bool TryDouble(double bpm, out double doubled)
    {
        var candidate = RoundForDisplay(bpm * 2.0, 1);
        if (IsValid(candidate))
        {
            doubled = candidate;
            return true;
        }

        doubled = bpm;
        return false;
    }

    public static string FormatSourceBadge(BpmSource source) => source switch
    {
        BpmSource.CachedMeasured => "缓存",
        BpmSource.CachedManual => "已修正",
        _ => string.Empty
    };

    public static string FormatConfidenceLabel(double? confidence) => confidence switch
    {
        null => string.Empty,
        >= 0.80 => "高",
        >= 0.55 => "中",
        _ => "低"
    };

    /// <summary>格式化段落时间点（MM:SS，超过 1 小时自动切换 H:MM:SS）。</summary>
    public static string FormatTimestamp(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var totalSeconds = (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return ts.TotalHours >= 1.0
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    /// <summary>格式化段落时间范围，例如 <c>00:00 – 00:48</c>。</summary>
    public static string FormatTimeRange(double startSeconds, double endSeconds) =>
        $"{FormatTimestamp(startSeconds)} – {FormatTimestamp(Math.Max(startSeconds, endSeconds))}";

    /// <summary>判断该段落与主段落是否存在明显的半速 / 倍速关系（误差 ±5% 内）。</summary>
    public static string FormatRelativeTempoRelation(double segmentBpm, double primaryBpm)
    {
        if (!IsValid(segmentBpm) || !IsValid(primaryBpm))
        {
            return string.Empty;
        }

        var ratio = segmentBpm / primaryBpm;
        if (Math.Abs(ratio - 2.0) <= 0.08)
        {
            return "倍速";
        }

        if (Math.Abs(ratio - 0.5) <= 0.04)
        {
            return "半速";
        }

        return string.Empty;
    }
}
