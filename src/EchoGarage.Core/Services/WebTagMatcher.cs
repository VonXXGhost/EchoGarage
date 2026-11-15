using System.Globalization;
using System.Text;
using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// 本地文件与远程曲目的自动匹配规则（ADR-022 §4）。
/// 优先碟号+音轨号，其次唯一音轨号，再按标题唯一匹配；绝不按数量或列表顺序强行匹配。
/// </summary>
public static class WebTagMatcher
{
    public static IReadOnlyList<WebTagMatch> Match(
        IReadOnlyList<Track> localTracks,
        WebTagRelease release)
    {
        var remote = release.Tracks;
        var used = new bool[remote.Count];
        var result = new List<WebTagMatch>(localTracks.Count);

        // 第一轮：本地音轨号有效的文件。
        foreach (var local in localTracks)
        {
            if (local.TrackNumber <= 0)
            {
                result.Add(new WebTagMatch(local, null, WebTagMatchKind.None));
                continue;
            }

            var candidates = FindByTrackNumber(local, remote, used);
            var matchIndex = PickUnique(candidates, local, remote);
            if (matchIndex >= 0)
            {
                used[matchIndex] = true;
                result.Add(new WebTagMatch(local, remote[matchIndex], WebTagMatchKind.TrackNumber));
            }
            else
            {
                result.Add(new WebTagMatch(local, null, WebTagMatchKind.None));
            }
        }

        // 第二轮：音轨号未命中或没有音轨号的文件，用唯一标题兜底。
        for (var i = 0; i < result.Count; i++)
        {
            if (result[i].RemoteTrack is not null)
            {
                continue;
            }

            var local = result[i].LocalTrack;
            var candidates = FindByTitle(local, remote, used);
            var matchIndex = PickUnique(candidates, local, remote);
            if (matchIndex >= 0)
            {
                used[matchIndex] = true;
                result[i] = new WebTagMatch(local, remote[matchIndex], WebTagMatchKind.Title);
            }
        }

        return result;
    }

    private static List<int> FindByTrackNumber(
        Track local,
        IReadOnlyList<WebTagTrack> remote,
        bool[] used)
    {
        var candidates = new List<int>();
        for (var i = 0; i < remote.Count; i++)
        {
            if (used[i] || remote[i].TrackNumber != local.TrackNumber)
            {
                continue;
            }

            if (local.DiscNumber > 0 && remote[i].DiscNumber > 0 &&
                remote[i].DiscNumber != local.DiscNumber)
            {
                continue;
            }

            candidates.Add(i);
        }

        return candidates;
    }

    private static List<int> FindByTitle(
        Track local,
        IReadOnlyList<WebTagTrack> remote,
        bool[] used)
    {
        var normalized = NormalizeTitle(local.DisplayTitle);
        if (normalized.Length == 0)
        {
            return [];
        }

        var candidates = new List<int>();
        for (var i = 0; i < remote.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            if (NormalizeTitle(remote[i].DisplayTitle) == normalized)
            {
                candidates.Add(i);
            }
        }

        return candidates;
    }

    /// <summary>候选不唯一时用标题（再不行用时长）消歧；仍不唯一则返回 -1。</summary>
    private static int PickUnique(
        IReadOnlyList<int> candidates,
        Track local,
        IReadOnlyList<WebTagTrack> remote)
    {
        if (candidates.Count == 0)
        {
            return -1;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var normalized = NormalizeTitle(local.DisplayTitle);
        var byTitle = candidates
            .Where(index => NormalizeTitle(remote[index].DisplayTitle) == normalized)
            .ToList();
        if (byTitle.Count == 1)
        {
            return byTitle[0];
        }

        var titleCandidates = byTitle.Count > 0 ? byTitle : candidates;
        if (local.DurationMs > 0)
        {
            var byDuration = titleCandidates
                .Where(index => remote[index].DurationMs is > 0 &&
                    Math.Abs(remote[index].DurationMs!.Value - local.DurationMs) <= 2000)
                .ToList();
            if (byDuration.Count == 1)
            {
                return byDuration[0];
            }
        }

        return -1;
    }

    /// <summary>标题归一化：全角/半角、大小写、首尾空白、连续空白；保留括号内容。</summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var normalized = title.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    // 保留此方法以覆盖运行时区域性差异；标题比较本身不依赖当前文化。
    internal static string ToInvariant(string value) =>
        value.ToLower(CultureInfo.InvariantCulture);
}
