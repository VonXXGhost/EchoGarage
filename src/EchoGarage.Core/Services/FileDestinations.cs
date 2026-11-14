namespace EchoGarage.Core.Services;

/// <summary>
/// 右键菜单「文件操作 → 复制到 / 移动到」的目标文件夹列表（ADR-021）。
/// 列表保存在 <c>settings.json</c>，顺序即菜单顺序；这里只做纯数据整理，不碰文件系统。
/// </summary>
public static class FileDestinations
{
    /// <summary>最多记住的目录数；避免菜单被无限拉长，也避免配置文件膨胀。</summary>
    public const int MaxCount = 20;

    /// <summary>
    /// 规范化用户配置的目录：去空白、去引号、转绝对路径、统一大小写去重（保留首次出现顺序），
    /// 丢弃非法的相对路径。返回值一定是新的列表，调用方可以直接赋回设置。
    /// </summary>
    public static List<string> Normalize(IEnumerable<string?>? destinations)
    {
        var normalized = new List<string>();
        if (destinations is null)
        {
            return normalized;
        }

        foreach (var destination in destinations)
        {
            var cleaned = Clean(destination);
            if (cleaned is null)
            {
                continue;
            }

            if (!TryNormalizeAbsolute(cleaned, out var fullPath))
            {
                continue;
            }

            if (normalized.Any(existing => string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            normalized.Add(fullPath);
            if (normalized.Count >= MaxCount)
            {
                break;
            }
        }

        return normalized;
    }

    /// <summary>把目录加入记忆列表；已存在（不区分大小写）或非法时返回 false。</summary>
    public static bool Add(List<string> destinations, string? directory)
    {
        var cleaned = Clean(directory);
        if (destinations.Count >= MaxCount || cleaned is null)
        {
            return false;
        }

        if (!TryNormalizeAbsolute(cleaned, out var fullPath))
        {
            return false;
        }

        if (destinations.Any(existing => string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        destinations.Add(fullPath);
        return true;
    }

    /// <summary>把目录从记忆列表移除；未找到返回 false。</summary>
    public static bool Remove(List<string> destinations, string? directory)
    {
        var cleaned = Clean(directory);
        if (cleaned is null)
        {
            return false;
        }

        var index = destinations.FindIndex(existing =>
            string.Equals(existing, cleaned, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        destinations.RemoveAt(index);
        return true;
    }

    /// <summary>去空白，并剥掉资源管理器「复制为路径」带上的成对双引号。</summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>只接受绝对路径；相对路径会随工作目录变化，不允许进入记忆列表。</summary>
    private static bool TryNormalizeAbsolute(string path, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        if (!PathUtil.TryNormalize(path, out var normalized) || string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        fullPath = normalized;
        return true;
    }
}
