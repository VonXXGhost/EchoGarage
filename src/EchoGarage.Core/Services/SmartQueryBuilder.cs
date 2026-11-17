using System.Globalization;
using System.Text.RegularExpressions;
using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

public sealed record SmartQuery(string WhereSql, IReadOnlyDictionary<string, object?> Parameters);

public sealed record SmartFieldDescriptor(
    string Key,
    string Label,
    bool IsNumeric,
    IReadOnlyList<string> Operators)
{
    // UIA / screen readers use ToString() as the accessible name of combo items.
    public override string ToString() => Label;
}

public static class SmartFieldCatalog
{
    public static readonly IReadOnlyList<string> TextOperators =
    [
        "contains", "notContains", "equals", "notEquals", "startsWith", "endsWith",
        "regex", "notRegex", "isEmpty", "isNotEmpty"
    ];

    public static readonly IReadOnlyList<string> NumberOperators =
        ["equals", "notEquals", "greaterThan", "lessThan", "greaterOrEqual", "lessOrEqual"];

    public static readonly IReadOnlyList<SmartFieldDescriptor> Fields =
    [
        new("title", "标题", false, TextOperators),
        new("artist", "艺术家", false, TextOperators),
        new("album", "专辑", false, TextOperators),
        new("albumArtist", "专辑艺术家", false, TextOperators),
        new("genre", "流派", false, TextOperators),
        new("comment", "备注", false, TextOperators),
        new("codec", "格式", false, TextOperators),
        new("path", "文件路径", false, TextOperators),
        new("directory", "文件目录", false, TextOperators),
        new("year", "年份", true, NumberOperators),
        new("durationSeconds", "时长（秒）", true, NumberOperators),
        new("trackNumber", "音轨号", true, NumberOperators)
    ];

    public static SmartFieldDescriptor? Find(string key) =>
        Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    public static string FieldLabel(string key) => Find(key)?.Label ?? key;

    public static string OperatorLabel(string op) => op switch
    {
        "contains" => "包含",
        "notContains" => "不包含",
        "equals" => "等于",
        "notEquals" => "不等于",
        "startsWith" => "开头是",
        "endsWith" => "结尾是",
        "regex" => "匹配正则",
        "notRegex" => "不匹配正则",
        "greaterThan" => "大于",
        "lessThan" => "小于",
        "greaterOrEqual" => "大于等于",
        "lessOrEqual" => "小于等于",
        "isEmpty" => "为空",
        "isNotEmpty" => "不为空",
        _ => op
    };
}

public static class SmartQueryBuilder
{
    private static readonly Dictionary<string, string> TextColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = "t.Title",
        ["artist"] = "t.Artist",
        ["album"] = "t.Album",
        ["albumArtist"] = "t.AlbumArtist",
        ["genre"] = "t.Genre",
        ["comment"] = "t.Comment",
        ["codec"] = "t.Codec",
        ["path"] = "t.Path",
        // dirname 由 SqliteDatabase 注册的 SQL 标量函数计算（.NET Path.GetDirectoryName），
        // 只包含路径中不含文件名的部分，末尾不带分隔符。
        ["directory"] = "dirname(t.Path)"
    };

    private static readonly Dictionary<string, string> NumberColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["year"] = "COALESCE(t.Year, 0)",
        ["durationSeconds"] = "t.DurationMs / 1000.0",
        ["trackNumber"] = "COALESCE(t.TrackNumber, 0)"
    };

    public static SmartQuery Build(SmartPlaylistDefinition? definition)
    {
        const string baseClause = "t.IsInLibrary = 1 AND t.IsMissing = 0";
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var clauses = new List<string>();

        if (definition?.Rules is { Count: > 0 })
        {
            var index = 0;
            foreach (var rule in definition.Rules)
            {
                var clause = BuildRule(rule, index, parameters);
                if (clause is not null)
                {
                    clauses.Add(clause);
                    index++;
                }
            }
        }

        if (clauses.Count == 0)
        {
            return new SmartQuery(baseClause, parameters);
        }

        var joiner = definition?.MatchMode == SmartMatchMode.Any ? " OR " : " AND ";
        return new SmartQuery($"{baseClause} AND ({string.Join(joiner, clauses)})", parameters);
    }

    private static string? BuildRule(SmartRule rule, int index, IDictionary<string, object?> parameters)
    {
        var field = SmartFieldCatalog.Find(rule.Field);
        if (field is null)
        {
            return null;
        }

        var op = rule.Operator;
        if (!field.Operators.Contains(op, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        if (TextColumns.TryGetValue(field.Key, out var textColumn))
        {
            var column = $"COALESCE({textColumn}, '')";
            var parameter = $"@r{index}";
            switch (op)
            {
                case "isEmpty":
                    return $"{column} = ''";
                case "isNotEmpty":
                    return $"{column} <> ''";
                case "contains":
                    parameters[parameter] = $"%{PathUtil.EscapeLike(rule.Value ?? string.Empty)}%";
                    return $"{column} LIKE {parameter} ESCAPE '\\'";
                case "notContains":
                    parameters[parameter] = $"%{PathUtil.EscapeLike(rule.Value ?? string.Empty)}%";
                    return $"{column} NOT LIKE {parameter} ESCAPE '\\'";
                case "startsWith":
                    parameters[parameter] = $"{PathUtil.EscapeLike(rule.Value ?? string.Empty)}%";
                    return $"{column} LIKE {parameter} ESCAPE '\\'";
                case "endsWith":
                    parameters[parameter] = $"%{PathUtil.EscapeLike(rule.Value ?? string.Empty)}";
                    return $"{column} LIKE {parameter} ESCAPE '\\'";
                case "equals":
                    parameters[parameter] = rule.Value ?? string.Empty;
                    return $"{column} = {parameter} COLLATE NOCASE";
                case "notEquals":
                    parameters[parameter] = rule.Value ?? string.Empty;
                    return $"{column} <> {parameter} COLLATE NOCASE";
                case "regex":
                case "notRegex":
                    // 无效/空白正则在生成 SQL 前就被跳过（与数值解析失败一致），
                    // 绝不能让无法编译的模式进入 SQLite 查询。
                    if (!SmartRegex.TryCompile(rule.Value, out _))
                    {
                        return null;
                    }

                    parameters[parameter] = rule.Value!;
                    return op == "regex"
                        ? $"{column} REGEXP {parameter}"
                        : $"NOT ({column} REGEXP {parameter})";
                default:
                    return null;
            }
        }

        if (!NumberColumns.TryGetValue(field.Key, out var numberColumn))
        {
            return null;
        }

        if (!double.TryParse(rule.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            !double.TryParse(rule.Value, NumberStyles.Float, CultureInfo.CurrentCulture, out number))
        {
            return null;
        }

        var numberParameter = $"@r{index}";
        parameters[numberParameter] = number;
        return op switch
        {
            "equals" => $"{numberColumn} = {numberParameter}",
            "notEquals" => $"{numberColumn} <> {numberParameter}",
            "greaterThan" => $"{numberColumn} > {numberParameter}",
            "lessThan" => $"{numberColumn} < {numberParameter}",
            "greaterOrEqual" => $"{numberColumn} >= {numberParameter}",
            "lessOrEqual" => $"{numberColumn} <= {numberParameter}",
            _ => null
        };
    }
}

/// <summary>
/// 智能列表文本规则的共享正则语义。<see cref="SmartQueryBuilder"/> 用它校验模式，
/// <c>SqliteDatabase</c> 中注册的 SQLite <c>regexp()</c> 函数用同一套选项与超时执行，
/// 因此非法模式永远不会进入 SQL，也不会让查询线程被灾难性回溯卡死。
/// </summary>
public static class SmartRegex
{
    /// <summary>单次匹配的超时上限：病态模式最多拖慢这一行，不会中断查询。</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>忽略大小写（与 SQLite LIKE 的缺省大小写不敏感语义接近），与文化无关。</summary>
    public const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static bool TryCompile(string? pattern, out Regex? regex)
    {
        regex = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        try
        {
            regex = new Regex(pattern, Options, MatchTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            // 语法错误（RegexParseException 是 ArgumentException 的子类）。
            return false;
        }
    }

    public static bool IsValid(string? pattern) => TryCompile(pattern, out _);
}
