using System.Text;

namespace EchoGarage.Core.Services;

/// <summary>
/// Formatting for the optional secondary (second-row) text shown under a list column.
/// A format is free text with {token} placeholders, for example "{genre}" or
/// "{bitrate}/{sampleRate}". Tokens whose value is empty are removed together with the
/// separator next to them, so "{bitrate}/{sampleRate}" degrades to "402K" or "48K" instead
/// of leaving a dangling slash. When every token is empty the result is an empty string,
/// which the UI uses to hide the secondary line.
/// </summary>
public static class SecondaryTextFormat
{
    public const int MaxFormatLength = 160;

    public sealed record Token(string Key, string Label);

    public static IReadOnlyList<Token> Tokens { get; } =
    [
        new("title", "标题"),
        new("artist", "艺术家"),
        new("album", "专辑"),
        new("albumArtist", "专辑艺术家"),
        new("composer", "作曲家"),
        new("genre", "流派"),
        new("year", "年份"),
        new("trackNumber", "音轨号"),
        new("discNumber", "碟号"),
        new("duration", "时长"),
        new("bitrate", "码率"),
        new("sampleRate", "采样率"),
        new("channels", "声道"),
        new("codec", "格式"),
        new("fileSize", "文件大小"),
        new("addedAt", "添加时间"),
        new("path", "文件路径"),
        new("fileName", "文件名"),
        new("comment", "备注")
    ];

    private static readonly Dictionary<string, Token> TokensByKey =
        Tokens.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>All tokens and their Chinese labels, e.g. for the column settings help text.</summary>
    public static string DescribeTokens() =>
        string.Join("、", Tokens.Select(t => $"{{{t.Key}}} {t.Label}"));

    /// <summary>Trims and bounds a user supplied format; blank formats become <c>null</c>.</summary>
    public static string? Normalize(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        var builder = new StringBuilder(format.Length);
        foreach (var ch in format.Trim())
        {
            var normalizedChar = ch is '\r' or '\n' or '\t' ? ' ' : ch;
            if (normalizedChar == ' ' && builder.Length > 0 && builder[^1] == ' ')
            {
                continue;
            }

            builder.Append(normalizedChar);
        }

        var normalized = builder.ToString().Trim();
        if (normalized.Length > MaxFormatLength)
        {
            normalized = normalized[..MaxFormatLength].Trim();
        }

        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>Renders a format with the given token values.</summary>
    public static string Format(string? format, IReadOnlyDictionary<string, string?> values)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return string.Empty;
        }

        var rendered = new StringBuilder(format.Length + 32);
        var hasValue = false;
        var literal = new StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length == 0)
            {
                return;
            }

            rendered.Append(literal);
            literal.Clear();
        }

        for (var i = 0; i < format.Length; i++)
        {
            var ch = format[i];
            if (ch == '{')
            {
                if (i + 1 < format.Length && format[i + 1] == '{')
                {
                    literal.Append('{');
                    i++;
                    continue;
                }

                var close = format.IndexOf('}', i + 1);
                if (close < 0)
                {
                    literal.Append(ch);
                    continue;
                }

                FlushLiteral();
                var key = format[(i + 1)..close].Trim();
                if (key.Length > 0 && TokensByKey.TryGetValue(key, out var token))
                {
                    var value = values.TryGetValue(token.Key, out var raw) ? raw?.Trim() : null;
                    if (!string.IsNullOrEmpty(value))
                    {
                        rendered.Append(value);
                        hasValue = true;
                    }
                    else
                    {
                        rendered.Append(EmptyMarker);
                    }
                }
                else
                {
                    // Unknown placeholders render as nothing; separators around them are cleaned up below.
                    rendered.Append(EmptyMarker);
                }

                i = close;
                continue;
            }

            if (ch == '}' && i + 1 < format.Length && format[i + 1] == '}')
            {
                literal.Append('}');
                i++;
                continue;
            }

            literal.Append(ch);
        }

        FlushLiteral();
        return hasValue ? CleanUpEmptyMarkers(rendered.ToString()) : string.Empty;
    }

    private const char EmptyMarker = '\u0001';

    private static bool IsSeparator(char ch) =>
        char.IsWhiteSpace(ch) || ch is '/' or '|' or ',' or ';' or '·' or '-' or '_';

    private static string CleanUpEmptyMarkers(string text)
    {
        var index = text.IndexOf(EmptyMarker);
        while (index >= 0)
        {
            var after = index + 1;
            while (after < text.Length && IsSeparator(text[after]))
            {
                after++;
            }

            if (after > index + 1)
            {
                text = text.Remove(index, after - index);
            }
            else
            {
                var before = index;
                while (before > 0 && IsSeparator(text[before - 1]))
                {
                    before--;
                }

                text = before < index ? text.Remove(before, index - before + 1) : text.Remove(index, 1);
            }

            index = text.IndexOf(EmptyMarker);
        }

        var start = 0;
        var end = text.Length;
        while (start < end && IsSeparator(text[start]))
        {
            start++;
        }

        while (end > start && IsSeparator(text[end - 1]))
        {
            end--;
        }

        return text[start..end].Trim();
    }
}
