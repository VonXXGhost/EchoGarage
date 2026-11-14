namespace EchoGarage.Core.Services;

/// <summary>
/// 在歌曲所在目录里寻找可作为专辑封面的图片：只有一个图片时直接使用；
/// 多个时优先使用 cover / folder 命名的图片（忽略大小写与扩展名）。
/// </summary>
public static class AlbumCoverMatcher
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif", ".tif", ".tiff"
    };

    private static readonly string[] PreferredNames = ["cover", "folder"];

    public static IReadOnlyList<string> FindImages(IEnumerable<string> filePaths)
    {
        var images = new List<string>();
        foreach (var path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !ImageExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            images.Add(path);
        }

        images.Sort(StringComparer.OrdinalIgnoreCase);
        return images;
    }

    /// <summary>返回匹配到的封面路径；目录中没有图片或无法按规则确定时返回 null。</summary>
    public static string? MatchCover(IEnumerable<string> filePaths)
    {
        var images = FindImages(filePaths);
        if (images.Count == 0)
        {
            return null;
        }

        if (images.Count == 1)
        {
            return images[0];
        }

        foreach (var preferred in PreferredNames)
        {
            var match = images.FirstOrDefault(path =>
                string.Equals(Path.GetFileNameWithoutExtension(path), preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}
