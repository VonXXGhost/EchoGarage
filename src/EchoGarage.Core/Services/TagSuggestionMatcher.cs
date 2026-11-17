namespace EchoGarage.Core.Services;

/// <summary>
/// 标签编辑补全的匹配规则：在内存中对候选值做前缀/包含匹配。
/// 候选项来自仓储查询（已按使用频率降序），这里保持该顺序：
/// 先列前缀命中的值，再列仅包含命中的值，同类之间保持原顺序。
/// 逻辑放在 Core 里以便单元测试，UI 层只负责显示与键盘交互。
/// </summary>
public static class TagSuggestionMatcher
{
    public const int DefaultLimit = 10;

    /// <param name="candidates">
    /// 候选值列表，通常已按出现频率降序排列；可能包含空白/重复项，这里会过滤与去重。
    /// </param>
    /// <param name="input">当前输入框文本；空白输入返回空列表（不主动弹候选框）。</param>
    /// <param name="limit">最多返回条数。</param>
    public static IReadOnlyList<string> Match(
        IReadOnlyList<string> candidates,
        string? input,
        int limit = DefaultLimit)
    {
        if (candidates.Count == 0 || limit <= 0)
        {
            return [];
        }

        var query = input?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return [];
        }

        var results = new List<string>(Math.Min(limit, 16));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 第一轮：前缀命中（输入越多，前缀匹配越接近用户目标）。
        foreach (var candidate in candidates)
        {
            if (results.Count >= limit)
            {
                return results;
            }

            var value = candidate?.Trim() ?? string.Empty;
            if (value.Length == 0 || !value.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(value))
            {
                results.Add(value);
            }
        }

        // 第二轮：仅包含（例如输入 "rock" 命中 "Classic Rock"）。
        foreach (var candidate in candidates)
        {
            if (results.Count >= limit)
            {
                break;
            }

            var value = candidate?.Trim() ?? string.Empty;
            if (value.Length == 0 || !value.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(value))
            {
                results.Add(value);
            }
        }

        return results;
    }
}
