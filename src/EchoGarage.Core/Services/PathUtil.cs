namespace EchoGarage.Core.Services;

public static class PathUtil
{
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".flac", ".mp3", ".m4a", ".aac", ".mp4", ".wma", ".aiff", ".aif"
        };

    public static bool IsSupportedAudio(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return SupportedExtensions.Contains(System.IO.Path.GetExtension(path));
    }

    public static string Normalize(string path)
    {
        var full = System.IO.Path.GetFullPath(path.Trim().Trim('"'));
        var root = System.IO.Path.GetPathRoot(full) ?? string.Empty;
        return full.Length > root.Length
            ? full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            : full;
    }

    public static bool TryNormalize(string path, out string normalized)
    {
        try
        {
            normalized = Normalize(path);
            return true;
        }
        catch
        {
            normalized = string.Empty;
            return false;
        }
    }

    public static bool IsUnderFolder(string filePath, string folderPath)
    {
        try
        {
            var file = Normalize(filePath);
            var folder = Normalize(folderPath)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            return file.StartsWith(folder + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(file, folder, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string ExtensionDisplay(string path) =>
        System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
