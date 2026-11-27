using System.Text;
using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// Pure helpers for conversion output paths: title-formatting style template expansion,
/// Windows-safe path segment cleanup and conflict-free file names.
/// </summary>
public static class ConversionNaming
{
    public const string DefaultTemplate = "%artist% - %title%";
    public const int MaxTemplateLength = 200;

    private const int MaxSegmentLength = 120;
    private const int MaxFullPathLength = 240;

    public static readonly int[] SupportedSampleRates = [44100, 48000, 88200, 96000, 176400, 192000];

    private static readonly char[] TextSeparators = [' ', '-', '_', '.', ','];
    private static readonly char[] InvalidFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string GetExtension(ConversionFormat format) => format switch
    {
        ConversionFormat.Aac => ".m4a",
        ConversionFormat.Flac => ".flac",
        ConversionFormat.Mp3 => ".mp3",
        ConversionFormat.Wav => ".wav",
        _ => ".bin"
    };

    /// <summary>Expands the template into a relative path (sub-directories allowed) including the extension.</summary>
    public static string BuildRelativePath(ConversionPreset preset, Track track)
    {
        var template = string.IsNullOrWhiteSpace(preset.FileNameTemplate) ? DefaultTemplate : preset.FileNameTemplate;
        var expanded = ExpandTemplate(template, track);
        var segments = SplitAndSanitize(expanded);
        if (segments.Count == 0)
        {
            var fallback = SanitizeSegment(Path.GetFileNameWithoutExtension(track.Path));
            segments.Add(string.IsNullOrWhiteSpace(fallback) ? "track" : fallback);
        }

        var relative = string.Join(Path.DirectorySeparatorChar, segments) + GetExtension(preset.Format);
        return LimitLength(relative);
    }

    /// <summary>Full output path; null when the base folder cannot be determined (e.g. empty custom folder).</summary>
    public static string? TryResolveOutputPath(ConversionPreset preset, Track track)
    {
        var baseDirectory = preset.OutputMode == ConversionOutputMode.CustomFolder
            ? preset.OutputFolder?.Trim()
            : Path.GetDirectoryName(track.Path);

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(baseDirectory, BuildRelativePath(preset, track)));
        }
        catch
        {
            return null;
        }
    }

    public static string ExpandTemplate(string template, Track track)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(template.Length + 32);
        var index = 0;

        while (index < template.Length)
        {
            var percent = template.IndexOf('%', index);
            if (percent < 0)
            {
                AppendLiteral(builder, template[index..]);
                break;
            }

            AppendLiteral(builder, template[index..percent]);

            var closing = template.IndexOf('%', percent + 1);
            if (closing < 0)
            {
                AppendLiteral(builder, template[percent..]);
                break;
            }

            var token = template[(percent + 1)..closing];
            if (!TryGetTokenValue(token, track, out var value))
            {
                // Unknown token: keep it as literal text so typos are visible in the preview.
                AppendLiteral(builder, template[percent..(closing + 1)]);
                index = closing + 1;
                continue;
            }

            if (!string.IsNullOrEmpty(value))
            {
                // 标签值里的 / 与 \ 属于文件名内容，不能当成模板中的子目录分隔符，统一替换为下划线。
                AppendTokenValue(builder, value);
                index = closing + 1;
                continue;
            }

            // Empty token: drop the separator run that follows it, otherwise the one before it.
            index = closing + 1;
            var next = index;
            if (next < template.Length && template[next] == '/')
            {
                index++;
            }
            else
            {
                while (next < template.Length && IsTextSeparator(template[next]))
                {
                    next++;
                }

                if (next > index)
                {
                    index = next;
                }
                else
                {
                    TrimTrailingSeparators(builder);
                }
            }
        }

        return Cleanup(builder.ToString());
    }

    public static string SanitizeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(segment.Length);
        foreach (var ch in segment)
        {
            if (ch == '*')
            {
                // 通配符按惯例写成 x（"a*b" → "axb"）；其余非法字符仍替换成下划线。
                builder.Append('x');
                continue;
            }

            if (char.IsControl(ch) || InvalidFileNameChars.Contains(ch))
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(ch);
            }
        }

        var result = CollapseSpaces(builder.ToString()).Trim().TrimEnd('.', ' ');
        if (result.Length > MaxSegmentLength)
        {
            result = result[..MaxSegmentLength].TrimEnd('.', ' ');
        }

        var deviceName = result.Contains('.') ? result[..result.IndexOf('.')] : result;
        if (ReservedDeviceNames.Contains(deviceName))
        {
            result = "_" + result;
        }

        return result;
    }

    /// <summary>Returns <paramref name="path"/> or the first free "name (n)" variant.</summary>
    public static string MakeUniquePath(string path, Func<string, bool> exists)
    {
        if (!exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        return path;
    }

    public static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool TryGetTokenValue(string token, Track track, out string value)
    {
        switch (token.Trim().ToLowerInvariant())
        {
            case "title":
                value = track.DisplayTitle;
                return true;
            case "artist":
                value = track.Artist;
                return true;
            case "album":
                value = track.Album;
                return true;
            case "albumartist":
                value = track.AlbumArtist;
                return true;
            case "composer":
                value = track.Composer;
                return true;
            case "genre":
                value = track.Genre;
                return true;
            case "year":
                value = track.Year is > 0 ? track.Year.Value.ToString() : string.Empty;
                return true;
            case "track":
                value = track.TrackNumber > 0 ? track.TrackNumber.ToString("D2") : string.Empty;
                return true;
            case "disc":
                value = track.DiscNumber > 0 ? track.DiscNumber.ToString("D2") : string.Empty;
                return true;
            case "filename":
                value = Path.GetFileNameWithoutExtension(track.Path);
                return true;
            case "codec":
                value = track.FormatDisplay;
                return true;
            default:
                value = string.Empty;
                return false;
        }
    }

    private static List<string> SplitAndSanitize(string expanded)
    {
        var segments = new List<string>();
        foreach (var raw in expanded.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = SanitizeSegment(raw);
            if (!string.IsNullOrEmpty(segment))
            {
                segments.Add(segment);
            }
        }

        return segments;
    }

    private static void AppendLiteral(StringBuilder builder, string literal)
    {
        if (string.IsNullOrEmpty(literal))
        {
            return;
        }

        builder.Append(literal);
    }

    /// <summary>把标签值里的路径分隔符转义成下划线，避免被当成模板的子目录。</summary>
    private static void AppendTokenValue(StringBuilder builder, string value)
    {
        foreach (var ch in value)
        {
            builder.Append(ch is '/' or '\\' ? '_' : ch);
        }
    }

    private static bool IsTextSeparator(char ch) => TextSeparators.Contains(ch);

    private static void TrimTrailingSeparators(StringBuilder builder)
    {
        while (builder.Length > 0 && (IsTextSeparator(builder[^1]) || builder[^1] == '/' || builder[^1] == '\\'))
        {
            builder.Length--;
        }
    }

    private static string Cleanup(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = CollapseSpaces(text);
        result = result.Replace("()", string.Empty).Replace("( )", string.Empty);
        result = result.Trim().Trim(TextSeparators);
        result = result.TrimEnd('.', ' ');
        return result;
    }

    private static string CollapseSpaces(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var previousSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousSpace)
                {
                    builder.Append(' ');
                }

                previousSpace = true;
            }
            else
            {
                builder.Append(ch);
                previousSpace = false;
            }
        }

        return builder.ToString();
    }

    private static string LimitLength(string relative)
    {
        if (relative.Length <= MaxFullPathLength)
        {
            return relative;
        }

        var directory = Path.GetDirectoryName(relative) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(relative);
        var extension = Path.GetExtension(relative);
        var budget = MaxFullPathLength - directory.Length - extension.Length - 1;
        if (budget < 1)
        {
            budget = 1;
        }

        if (name.Length > budget)
        {
            name = name[..budget].TrimEnd('.', ' ');
        }

        return Path.Combine(directory, name + extension);
    }
}
