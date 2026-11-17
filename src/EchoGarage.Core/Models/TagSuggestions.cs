namespace EchoGarage.Core.Models;

/// <summary>
/// 标签编辑的候选值快照：媒体库缓存里其它歌曲出现过的字段值。
/// 仅包含艺术家、专辑艺术家、作曲家、流派四个字段；键使用 <see cref="TagFieldKeys"/> 常量。
/// 值按媒体库中的出现频率降序排列（由仓储查询保证），用于补全下拉框的默认排序。
/// </summary>
public sealed class TagSuggestionValues
{
    /// <summary>每个字段最多读取的候选值数量，避免大媒体库把全部字符串都读进内存。</summary>
    public const int DefaultLimit = 2000;

    /// <summary>读取失败/没有可用值时的空快照；调用方无须判空。</summary>
    public static TagSuggestionValues Empty { get; } =
        new(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

    public TagSuggestionValues(IReadOnlyDictionary<string, IReadOnlyList<string>> byField)
    {
        var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in byField)
        {
            copy[pair.Key] = pair.Value;
        }

        ByField = copy;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ByField { get; }

    /// <summary>取某个字段的候选值；没有该字段时返回空列表（不会抛异常）。</summary>
    public IReadOnlyList<string> Get(string fieldKey) =>
        ByField.TryGetValue(fieldKey, out var values) ? values : [];
}
