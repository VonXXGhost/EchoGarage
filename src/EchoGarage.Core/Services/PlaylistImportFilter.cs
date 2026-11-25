using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// 播放列表导入过滤结果：<see cref="PathsToAdd"/> 是规范化后需要新增的音频文件路径，
/// <see cref="SkippedExistingCount"/> 是因已存在于目标播放列表而被自动忽略的去重数量。
/// </summary>
public readonly record struct PlaylistPathFilterResult(
    IReadOnlyList<string> PathsToAdd,
    int SkippedExistingCount);

/// <summary>
/// 向普通播放列表添加文件时的按绝对路径去重逻辑（纯逻辑，不触及文件系统或数据库）。
/// </summary>
public static class PlaylistImportFilter
{
    /// <summary>
    /// 从候选路径中筛出受支持且尚未存在于目标播放列表的音频文件路径：
    /// <list type="bullet">
    /// <item>所有路径先经 <see cref="PathUtil.TryNormalize(string, out string)"/> 规范化为绝对路径；</item>
    /// <item>本批次内的重复路径只保留首次出现，不重复计入 <see cref="PlaylistPathFilterResult.SkippedExistingCount"/>；</item>
    /// <item>与 <paramref name="existingPlaylistPaths"/> 按绝对路径（Windows 语义忽略大小写）命中时忽略并计数。</item>
    /// </list>
    /// </summary>
    public static PlaylistPathFilterResult FilterNewAudioPaths(
        IEnumerable<string> candidatePaths,
        IEnumerable<string> existingPlaylistPaths)
    {
        var existing = BuildNormalizedPathSet(existingPlaylistPaths);
        var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathsToAdd = new List<string>();
        var skippedExisting = 0;

        foreach (var path in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !PathUtil.TryNormalize(path, out var normalized))
            {
                continue;
            }

            if (!PathUtil.IsSupportedAudio(normalized))
            {
                continue;
            }

            if (!seenInBatch.Add(normalized))
            {
                continue;
            }

            if (existing.Contains(normalized))
            {
                skippedExisting++;
                continue;
            }

            pathsToAdd.Add(normalized);
        }

        return new PlaylistPathFilterResult(pathsToAdd, skippedExisting);
    }

    /// <summary>
    /// 过滤准备写入 <c>playlist_items</c> 的曲目列表，忽略已存在及本批次重复的绝对路径。
    /// 用于仓储层在数据库事务内二次兜底去重。
    /// </summary>
    public static IReadOnlyList<Track> FilterNewTracks(
        IReadOnlyList<Track> candidateTracks,
        IEnumerable<string> existingPlaylistPaths)
    {
        if (candidateTracks.Count == 0)
        {
            return [];
        }

        var seen = BuildNormalizedPathSet(existingPlaylistPaths);
        var result = new List<Track>(candidateTracks.Count);
        foreach (var track in candidateTracks)
        {
            if (!TryBuildComparisonKey(track.Path, out var key))
            {
                continue;
            }

            if (seen.Add(key))
            {
                result.Add(track);
            }
        }

        return result;
    }

    private static HashSet<string> BuildNormalizedPathSet(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (TryBuildComparisonKey(path, out var key))
            {
                set.Add(key);
            }
        }

        return set;
    }

    private static bool TryBuildComparisonKey(string? path, out string key)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            key = string.Empty;
            return false;
        }

        key = PathUtil.TryNormalize(path, out var normalized)
            ? normalized
            : path.Trim();
        return key.Length > 0;
    }
}
