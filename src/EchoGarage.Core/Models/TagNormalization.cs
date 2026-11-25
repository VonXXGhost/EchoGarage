using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using EchoGarage.Core.Abstractions;

namespace EchoGarage.Core.Models;

/// <summary>
/// 一条“快捷规范化”规则：用 .NET 正则改写某个标签字段的文本。
/// 规则本身只是配置数据，不做匹配；实际执行见 <see cref="TagNormalization"/>。
/// 规则保存在 settings.json 中，只对用户主动执行的批量 / 快捷操作生效，不会在写文件时静默改动标签。
/// </summary>
public sealed class TagNormalizationRule
{
    /// <summary>稳定的规则 id（JSON 持久化与 UI 列表身份依据）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名称；为空时由设置归一化补成“规范化规则 N”。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 目标字段：<see cref="TagFieldKeys"/> 之一；或 <see cref="TagNormalization.AnyField"/> 表示
    /// 所有常用字段与文件里的其它（额外）标签。无法识别的值会在应用时被跳过。
    /// </summary>
    public string TargetField { get; set; } = TagFieldKeys.Album;

    /// <summary>
    /// .NET 正则表达式（首尾空白会被忽略），支持反向引用；空或非法 pattern 留给 UI 提示，
    /// <see cref="TagNormalization.Apply"/> / <see cref="TagNormalization.ApplyToTag(TagNormalizationRule, AudioTagData)"/>
    /// 会直接跳过而不抛异常。
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>替换文本；空字符串表示删除匹配内容，支持 $1、$2、${name} 反向引用。</summary>
    public string Replacement { get; set; } = string.Empty;

    /// <summary>是否启用；禁用规则不参与应用，但仍保留在设置中供 UI 编辑。</summary>
    public bool Enabled { get; set; } = true;

    public TagNormalizationRule Clone() => new()
    {
        Id = Id,
        Name = Name,
        TargetField = TargetField,
        Pattern = Pattern,
        Replacement = Replacement,
        Enabled = Enabled
    };
}

/// <summary>
/// 内置默认规则与规则列表归一化。默认规则每次调用 <see cref="Create"/> 都返回全新实例，
/// 不存在可被外部修改的共享静态集合。
/// </summary>
public static class TagNormalizationDefaults
{
    /// <summary>内置规则 id：移除专辑名末尾的 “ - EP” / “ - Single” 后缀。</summary>
    public const string AlbumEpSingleSuffixRuleId = "builtin-album-ep-single-suffix";

    /// <summary>内置规则 id：ISO 日期时间只保留日期部分。</summary>
    public const string IsoDateTimeRuleId = "builtin-iso-datetime-to-date";

    /// <summary>
    /// 创建两条默认规则：专辑名去除 EP / Single 后缀（忽略大小写，兼容 -、–、—、－、− 等破折号），
    /// 以及日期时间规范化（“2026-01-01T00:00:00” 之类 → “2026-01-01”，覆盖空格分隔 / 毫秒 / Z / 时区变体）。
    /// </summary>
    public static List<TagNormalizationRule> Create() =>
        new()
        {
            new TagNormalizationRule
            {
                Id = AlbumEpSingleSuffixRuleId,
                Name = "移除专辑名 EP / Single 后缀",
                TargetField = TagFieldKeys.Album,
                // 破折号字符集：- U+2010..U+2015（连字符、figure dash、en/em dash、horizontal bar）、
                // 全角减号 U+FF0D、数学减号 U+2212。
                Pattern = @"(?i)\s*[-\u2010\u2011\u2012\u2013\u2014\u2015\uFF0D\u2212]\s*(?:EP|Single)\s*$",
                Replacement = string.Empty,
                Enabled = true
            },
            new TagNormalizationRule
            {
                Id = IsoDateTimeRuleId,
                Name = "日期时间只保留日期",
                TargetField = TagNormalization.AnyField,
                Pattern = @"(?i)^\s*(\d{4}-\d{2}-\d{2})[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:\s*(?:Z|[+-]\d{2}:?\d{2}))?\s*$",
                Replacement = "$1",
                Enabled = true
            }
        };
}

/// <summary>
/// 一次规范化应用的结果：只包含实际发生变化的项。
/// 通过 <see cref="TagNormalization.ApplyToTag(TagNormalizationRule, AudioTagData)"/> 获得；
/// 没有任何变化时该 API 返回 null。
/// </summary>
public sealed class TagNormalizationResult
{
    private readonly Dictionary<string, string> _fields;
    private readonly List<AudioTagChange> _additionalTagChanges;

    internal TagNormalizationResult(Dictionary<string, string> fields, List<AudioTagChange> additionalTagChanges)
    {
        _fields = fields;
        _additionalTagChanges = additionalTagChanges;
    }

    /// <summary>常用字段的新文本值，key 为 <see cref="TagFieldKeys"/> 之一；只包含实际变化项。</summary>
    public IReadOnlyDictionary<string, string> Fields => _fields;

    /// <summary>
    /// 额外标签变更，<see cref="AudioTagChange.Value"/> 必为非空文本；本 API 不会通过规范化删除额外标签
    /// （替换结果为空时保留原值，避免“删后缀”类规则误删整条标签）。
    /// </summary>
    public IReadOnlyList<AudioTagChange> AdditionalTagChanges => _additionalTagChanges;

    /// <summary>是否有任何变化。</summary>
    public bool HasChanges => _fields.Count > 0 || _additionalTagChanges.Count > 0;

    /// <summary>变化项总数（常用字段 + 额外标签）。</summary>
    public int Count => _fields.Count + _additionalTagChanges.Count;

    /// <summary>把本次结果合并进一次批量标签编辑。</summary>
    public void ApplyTo(TagBatchEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        foreach (var (key, value) in _fields)
        {
            edit.Fields[key] = value;
        }

        edit.AdditionalTagChanges.AddRange(_additionalTagChanges);
    }
}

/// <summary>
/// 纯逻辑的标签“快捷规范化”：正则校验、单值替换、整条标签的变更计算，以及规则列表归一化。
/// 不访问文件、不依赖 UI，可在后台线程批量调用。
/// </summary>
public static class TagNormalization
{
    /// <summary>目标字段通配符：表示所有常用字段 + 文件里的其它（额外）标签。</summary>
    public const string AnyField = "*";

    /// <summary>设置中允许保存的规则数量上限。</summary>
    public const int MaxRuleCount = 50;

    /// <summary>单次正则匹配超时；防止用户配置的灾难性回溯 pattern 卡死界面。</summary>
    public static TimeSpan PatternMatchTimeout { get; } = TimeSpan.FromSeconds(1);

    /// <summary>规则目标字段下拉框可选项：常用字段 + <see cref="AnyField"/>（顺序即 UI 建议顺序）。</summary>
    public static IReadOnlyList<string> SupportedTargetFields { get; } = Array.AsReadOnly(
        new[]
        {
            TagFieldKeys.Title,
            TagFieldKeys.Artist,
            TagFieldKeys.Album,
            TagFieldKeys.AlbumArtist,
            TagFieldKeys.Composer,
            TagFieldKeys.Genre,
            TagFieldKeys.Year,
            TagFieldKeys.TrackNumber,
            TagFieldKeys.DiscNumber,
            TagFieldKeys.Comment,
            AnyField
        });

    private static readonly ConcurrentDictionary<string, PatternEntry> PatternCache = new(StringComparer.Ordinal);

    /// <summary>目标字段是否是通配符（忽略首尾空白）。</summary>
    public static bool IsAnyField(string? targetField) =>
        string.Equals(targetField?.Trim(), AnyField, StringComparison.Ordinal);

    /// <summary>
    /// 目标字段是否是已知的常用字段（忽略大小写与首尾空白，并返回规范大小写，如 albumArtist）。
    /// </summary>
    public static bool IsKnownField(string? targetField) =>
        TryCanonicalizeField(targetField, out _);

    /// <summary>
    /// 规范化目标字段文本：通配符或已知字段返回规范写法（如 "AlbumArtist" → "albumArtist"），
    /// 其它值只做 Trim 后原样返回（由 UI 提示，应用时跳过）。
    /// </summary>
    public static string CanonicalizeTargetField(string? targetField)
    {
        var text = targetField?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return TryCanonicalizeField(text, out var canonical) ? canonical : text;
    }

    /// <summary>校验正则是否可用；空 / 只有空白的 pattern 视为非法。</summary>
    public static bool IsValidPattern(string? pattern, out string? error)
    {
        var text = pattern?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "正则表达式不能为空。";
            return false;
        }

        var entry = GetPatternEntry(text);
        error = entry.Error;
        return entry.Regex is not null;
    }

    /// <summary>
    /// 对单个文本值执行规则。返回 null 表示“无需改动”：规则为 null / 已禁用、pattern 为空或非法、
    /// 正则超时、替换串非法或没有匹配 / 结果与输入相同。返回非 null 时为替换后的文本，
    /// 可能是空字符串（表示清空该字段）。
    /// </summary>
    public static string? Apply(TagNormalizationRule? rule, string? value)
    {
        if (rule is null || !rule.Enabled || value is null)
        {
            return null;
        }

        var pattern = rule.Pattern?.Trim() ?? string.Empty;
        if (pattern.Length == 0)
        {
            return null;
        }

        var entry = GetPatternEntry(pattern);
        if (entry.Regex is null)
        {
            return null;
        }

        try
        {
            var result = entry.Regex.Replace(value, rule.Replacement ?? string.Empty);
            return string.Equals(result, value, StringComparison.Ordinal) ? null : result;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // 非法替换串（例如引用了不存在的命名分组）。
            return null;
        }
    }

    /// <summary>对一条标签应用单条规则；没有实际变化时返回 null。</summary>
    public static TagNormalizationResult? ApplyToTag(TagNormalizationRule? rule, AudioTagData tag) =>
        rule is null ? null : ApplyToTag(new[] { rule }, tag);

    /// <summary>
    /// 按列表顺序叠加应用多条规则（后一条能看到前一条的结果），返回相对原始标签的实际变化；
    /// 没有变化时返回 null。禁用 / pattern 非法 / 目标字段无法识别的规则会被跳过。
    /// 数字字段（year / track / disc）只接受解析为非负整数的结果（0 表示未设置，会作为“清空”写入）；
    /// 额外标签的替换结果为空时保留原值（不会删除额外标签）。
    /// </summary>
    public static TagNormalizationResult? ApplyToTag(IEnumerable<TagNormalizationRule>? rules, AudioTagData tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (rules is null)
        {
            return null;
        }

        var originalFields = TagBatchEditor.GetFieldValues(tag);
        var workingFields = new Dictionary<string, string>(originalFields, StringComparer.OrdinalIgnoreCase);

        var additionalTags = tag.AdditionalTags;
        var workingAdditional = additionalTags.Select(entry => entry.Value).ToArray();

        foreach (var rule in rules)
        {
            if (rule is null || !rule.Enabled)
            {
                continue;
            }

            var target = CanonicalizeTargetField(rule.TargetField);
            if (target == AnyField)
            {
                foreach (var key in workingFields.Keys.ToArray())
                {
                    var transformed = Apply(rule, workingFields[key]);
                    if (transformed is not null)
                    {
                        TryUpdateField(key, transformed, workingFields);
                    }
                }

                for (var i = 0; i < workingAdditional.Length; i++)
                {
                    var transformed = Apply(rule, workingAdditional[i]);
                    // 额外标签的空结果保留原值：规范化不应隐式删除整条标签。
                    if (transformed is not null && !string.IsNullOrWhiteSpace(transformed))
                    {
                        workingAdditional[i] = transformed;
                    }
                }
            }
            else if (workingFields.ContainsKey(target))
            {
                var transformed = Apply(rule, workingFields[target]);
                if (transformed is not null)
                {
                    TryUpdateField(target, transformed, workingFields);
                }
            }

            // 目标字段无法识别：跳过该规则，留给 UI 提示。
        }

        var fieldChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in workingFields)
        {
            if (!string.Equals(originalFields[key], value, StringComparison.Ordinal))
            {
                fieldChanges[key] = value;
            }
        }

        var additionalChanges = new List<AudioTagChange>();
        for (var i = 0; i < workingAdditional.Length; i++)
        {
            var value = workingAdditional[i];
            if (!string.Equals(additionalTags[i].Value, value, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(value))
            {
                additionalChanges.Add(new AudioTagChange(additionalTags[i].Kind, additionalTags[i].Key, value));
            }
        }

        return fieldChanges.Count == 0 && additionalChanges.Count == 0
            ? null
            : new TagNormalizationResult(fieldChanges, additionalChanges);
    }

    /// <summary>
    /// 归一化设置里的规则列表：null → 内置默认规则；空列表保持为空（用户主动删空后不复活默认规则）。
    /// 去空项、id 去重 / 补全、name / pattern 修剪、目标字段规范大小写、数量上限 <see cref="MaxRuleCount"/>；
    /// pattern 非法的规则保留（供 UI 提示），但应用时会被跳过。
    /// </summary>
    public static List<TagNormalizationRule> NormalizeRules(IEnumerable<TagNormalizationRule>? rules, IAppLogger? logger = null)
    {
        if (rules is null)
        {
            return TagNormalizationDefaults.Create();
        }

        var result = new List<TagNormalizationRule>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var rule in rules)
        {
            if (rule is null)
            {
                continue;
            }

            index++;
            var normalized = rule.Clone();

            normalized.Name = normalized.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized.Name))
            {
                normalized.Name = $"规范化规则 {index}";
            }

            normalized.Id = normalized.Id?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized.Id) || !usedIds.Add(normalized.Id))
            {
                normalized.Id = Guid.NewGuid().ToString("N");
                usedIds.Add(normalized.Id);
            }

            normalized.TargetField = CanonicalizeTargetField(normalized.TargetField);
            normalized.Pattern = normalized.Pattern?.Trim() ?? string.Empty;
            normalized.Replacement ??= string.Empty;

            if (normalized.TargetField != AnyField && !IsKnownField(normalized.TargetField))
            {
                logger?.Warn(
                    $"Tag normalization rule \"{normalized.Name}\" has an unknown target field \"{normalized.TargetField}\"; it will be kept for the UI and skipped when applying.");
            }

            if (!IsValidPattern(normalized.Pattern, out var patternError))
            {
                logger?.Warn(
                    $"Tag normalization rule \"{normalized.Name}\" has an invalid pattern; it will be kept for the UI and skipped when applying ({patternError}).");
            }

            result.Add(normalized);
            if (result.Count >= MaxRuleCount)
            {
                logger?.Warn($"Too many tag normalization rules (max {MaxRuleCount}); the remaining ones are ignored.");
                break;
            }
        }

        return result;
    }

    private static bool TryCanonicalizeField(string? targetField, out string canonical)
    {
        var text = targetField?.Trim() ?? string.Empty;
        foreach (var field in SupportedTargetFields)
        {
            // 通配符不是“常用字段”，由 IsAnyField 单独判断。
            if (field == AnyField)
            {
                continue;
            }

            if (string.Equals(field, text, StringComparison.OrdinalIgnoreCase))
            {
                canonical = field;
                return true;
            }
        }

        canonical = text;
        return false;
    }

    /// <summary>
    /// 更新工作区里的普通字段。year / track / disc 只接受可解析的非负整数结果（0 表示未设置 / 清空），
    /// 解析失败或为负数时保留原值；文本字段允许被清空。
    /// </summary>
    private static bool TryUpdateField(string key, string transformed, Dictionary<string, string> workingFields)
    {
        if (string.Equals(key, TagFieldKeys.Bpm, StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = transformed.Trim();
            if (trimmed.Length == 0)
            {
                if (workingFields[key].Length == 0)
                {
                    return false;
                }

                workingFields[key] = string.Empty;
                return true;
            }

            if (!Services.BpmFormatter.TryParse(trimmed, out var bpm))
            {
                return false;
            }

            var formatted = Services.BpmFormatter.FormatInput(bpm);
            if (string.Equals(workingFields[key], formatted, StringComparison.Ordinal))
            {
                return false;
            }

            workingFields[key] = formatted;
            return true;
        }

        if (IsNumericField(key))
        {
            if (!int.TryParse(transformed.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                || number < 0)
            {
                return false;
            }

            var text = number.ToString(CultureInfo.InvariantCulture);
            if (string.Equals(workingFields[key], text, StringComparison.Ordinal))
            {
                return false;
            }

            workingFields[key] = text;
            return true;
        }

        if (string.Equals(workingFields[key], transformed, StringComparison.Ordinal))
        {
            return false;
        }

        workingFields[key] = transformed;
        return true;
    }

    private static bool IsNumericField(string key) =>
        string.Equals(key, TagFieldKeys.Year, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, TagFieldKeys.TrackNumber, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, TagFieldKeys.DiscNumber, StringComparison.OrdinalIgnoreCase);

    private static PatternEntry GetPatternEntry(string pattern) =>
        PatternCache.GetOrAdd(pattern, static text =>
        {
            try
            {
                return new PatternEntry(
                    new Regex(text, RegexOptions.CultureInvariant, PatternMatchTimeout),
                    null);
            }
            catch (ArgumentException ex)
            {
                return new PatternEntry(null, $"正则表达式无效：{ex.Message}");
            }
        });

    private sealed class PatternEntry
    {
        public PatternEntry(Regex? regex, string? error)
        {
            Regex = regex;
            Error = error;
        }

        public Regex? Regex { get; }

        public string? Error { get; }
    }
}
