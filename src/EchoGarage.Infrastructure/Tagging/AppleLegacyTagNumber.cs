using System.Globalization;

namespace EchoGarage.Infrastructure.Tagging;

/// <summary>
/// 旧式 M4A 标签器（Nero AAC、早期 Apple 工具）会把音轨号/碟片号写成 Alt-1 自由标签
/// （<c>----:com.apple.iTunes:track</c> = "1/3"），而不是标准的 <c>trkn</c> / <c>disk</c> atom。
/// foobar2000 会把这类自由标签当作音轨号/碟片号读取，TagLib# 不会；
/// 这里集中处理这种文本的解析与写回格式，避免同一个值同时以两种形式存在。
/// </summary>
public static class AppleLegacyTagNumber
{
    /// <summary>iTunes / Nero AAC 写入自由标签时使用的 mean。</summary>
    public const string ITunesMean = "com.apple.iTunes";

    /// <summary>音轨号自由标签的 name。</summary>
    public const string TrackName = "track";

    /// <summary>碟片号自由标签的 name。</summary>
    public const string DiscName = "disc";

    /// <summary>
    /// 解析 "1"、"1/3"、"01/3" 这类文本并取第一个整数（即音轨号本身，忽略 "/总数" 部分）。
    /// 0、负数、非数字开头、空文本以及超出 int 范围时返回 false。
    /// </summary>
    public static bool TryParse(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().TrimStart();
        var length = 0;
        while (length < span.Length && char.IsAsciiDigit(span[length]))
        {
            length++;
        }

        if (length == 0 ||
            !int.TryParse(span[..length], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed <= 0)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// 把编辑后的整数格式化成写回自由标签的文本：原文本带 "/总数" 时保留总数
    /// （"1/3" 改成 2 → "2/3"），否则只写数字。value ≤ 0 返回空串，表示删除该自由标签。
    /// </summary>
    public static string Format(string? originalText, int value)
    {
        if (value <= 0)
        {
            return string.Empty;
        }

        var total = ReadTotal(originalText);
        return total > 0
            ? value.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
    }

    private static int ReadTotal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var slash = text.IndexOf('/');
        if (slash < 0 || slash == text.Length - 1)
        {
            return 0;
        }

        var span = text.AsSpan(slash + 1).Trim();
        var length = 0;
        while (length < span.Length && char.IsAsciiDigit(span[length]))
        {
            length++;
        }

        return length > 0 &&
               int.TryParse(span[..length], NumberStyles.None, CultureInfo.InvariantCulture, out var total) &&
               total > 0
            ? total
            : 0;
    }
}
