using EchoGarage.Core.Models;

namespace EchoGarage.Tests;

/// <summary>标签“快捷规范化”规则（ADR-025）的纯逻辑与设置归一化测试。</summary>
public sealed class TagNormalizerTests
{
    private const string Album = TagFieldKeys.Album;

    // ---------------------------------------------------------------- 默认规则

    [Fact]
    public void Defaults_Create_ReturnsTwoEnabledRules()
    {
        var rules = TagNormalizationDefaults.Create();

        Assert.Equal(2, rules.Count);
        Assert.All(rules, rule => Assert.True(rule.Enabled));
        Assert.Contains(rules, rule => rule.Id == TagNormalizationDefaults.AlbumEpSingleSuffixRuleId && rule.TargetField == Album);
        Assert.Contains(rules, rule => rule.Id == TagNormalizationDefaults.IsoDateTimeRuleId && rule.TargetField == TagNormalization.AnyField);
        Assert.NotEqual(rules[0].Id, rules[1].Id);
    }

    [Fact]
    public void Defaults_Create_ReturnsFreshInstances()
    {
        var first = TagNormalizationDefaults.Create();
        var second = TagNormalizationDefaults.Create();

        Assert.NotSame(first[0], second[0]);
        first[0].Name = "changed";
        first.Add(new TagNormalizationRule());

        Assert.NotEqual("changed", second[0].Name);
        Assert.Equal(2, second.Count);
    }

    private static TagNormalizationRule DefaultAlbumRule() =>
        TagNormalizationDefaults.Create().Single(rule => rule.Id == TagNormalizationDefaults.AlbumEpSingleSuffixRuleId);

    private static TagNormalizationRule DefaultDateRule() =>
        TagNormalizationDefaults.Create().Single(rule => rule.Id == TagNormalizationDefaults.IsoDateTimeRuleId);

    // ---------------------------------------------------------------- EP / Single 后缀

    [Theory]
    [InlineData("Demo Album A ― EP", "Demo Album A")] // horizontal bar
    [InlineData("Demo Album A ‐ EP", "Demo Album A")] // hyphen
    [InlineData("Demo Album A ‒ EP", "Demo Album A")] // figure dash
    [InlineData("Demo Album A - EP", "Demo Album A")]
    [InlineData("Demo Album A - Single", "Demo Album A")]
    [InlineData("Demo Album A – EP", "Demo Album A")] // en dash
    [InlineData("Demo Album A — EP", "Demo Album A")] // em dash
    [InlineData("Demo Album A ― EP", "Demo Album A")] // horizontal bar
    [InlineData("Demo Album A － EP", "Demo Album A")] // fullwidth minus
    [InlineData("Demo Album A − EP", "Demo Album A")] // minus sign
    [InlineData("Demo Album A - ep", "Demo Album A")]
    [InlineData("Demo Album A - SiNgLe", "Demo Album A")]
    [InlineData("Demo Album A-EP", "Demo Album A")]
    [InlineData("Demo Album A  -  EP  ", "Demo Album A")]
    public void DefaultAlbumRule_StripsEpSingleSuffix(string input, string expected)
    {
        var result = TagNormalization.Apply(DefaultAlbumRule(), input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Demo Album A")]
    [InlineData("Demo Album A (EP)")]
    [InlineData("EP Demo Album A")]
    [InlineData("Demo Album A - EP Edition")]
    [InlineData("Demo Album A - Extended Play")]
    [InlineData("")]
    public void DefaultAlbumRule_LeavesOtherTextUnchanged(string input)
    {
        Assert.Null(TagNormalization.Apply(DefaultAlbumRule(), input));
    }

    // ---------------------------------------------------------------- 日期规范化

    [Theory]
    [InlineData("2026-01-01T00:00:00", "2026-01-01")]
    [InlineData("2026-01-01 00:00:00", "2026-01-01")]
    [InlineData("2026-01-01T00:00", "2026-01-01")]
    [InlineData("2026-01-01T00:00:00.123", "2026-01-01")]
    [InlineData("2026-01-01T00:00:00.1234567", "2026-01-01")]
    [InlineData("2026-01-01T00:00:00Z", "2026-01-01")]
    [InlineData("2026-01-01T00:00:00.500Z", "2026-01-01")]
    [InlineData("2026-01-01T00:00:00+08:00", "2026-01-01")]
    [InlineData("2026-01-01T00:00:00-0500", "2026-01-01")]
    [InlineData("2026-12-31 23:59:59 +09:00", "2026-12-31")]
    [InlineData("  2026-01-01T00:00:00  ", "2026-01-01")]
    public void DefaultDateRule_NormalizesDateTimeVariants(string input, string expected)
    {
        var result = TagNormalization.Apply(DefaultDateRule(), input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("2026-01-01")]
    [InlineData("01 / 2026")]
    [InlineData("2026/01/01 00:00:00")]
    [InlineData("2026-01-01T00:00:00Z extra")]
    [InlineData("Demo Track 01 - 2026-01-01")]
    [InlineData("")]
    public void DefaultDateRule_LeavesNonDateTimeValuesUnchanged(string input)
    {
        Assert.Null(TagNormalization.Apply(DefaultDateRule(), input));
    }

    // ---------------------------------------------------------------- Apply 语义

    [Fact]
    public void Apply_NullRuleOrValue_ReturnsNull()
    {
        var rule = DefaultAlbumRule();

        Assert.Null(TagNormalization.Apply(null, "Demo Album A - EP"));
        Assert.Null(TagNormalization.Apply(rule, null));
    }

    [Fact]
    public void Apply_DisabledRule_ReturnsNull()
    {
        var rule = DefaultAlbumRule();
        rule.Enabled = false;

        Assert.Null(TagNormalization.Apply(rule, "Demo Album A - EP"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_EmptyPattern_ReturnsNull(string pattern)
    {
        var rule = DefaultAlbumRule();
        rule.Pattern = pattern;

        Assert.Null(TagNormalization.Apply(rule, "Demo Album A - EP"));
    }

    [Fact]
    public void Apply_InvalidPattern_ReturnsNull()
    {
        var rule = DefaultAlbumRule();
        rule.Pattern = "(";

        Assert.Null(TagNormalization.Apply(rule, "Demo Album A - EP"));
    }

    [Fact]
    public void Apply_SupportsBackReferences()
    {
        var rule = new TagNormalizationRule
        {
            TargetField = TagFieldKeys.Title,
            Pattern = @"^(\d{4})-(\d{2})-(\d{2})$",
            Replacement = "$3/$2/$1"
        };

        Assert.Equal("02/01/2026", TagNormalization.Apply(rule, "2026-01-02"));
    }

    [Fact]
    public void Apply_ReplacementCanClearValue()
    {
        var rule = new TagNormalizationRule { Pattern = "^.*$", Replacement = string.Empty };

        Assert.Equal(string.Empty, TagNormalization.Apply(rule, "Demo Album A"));
    }

    [Fact]
    public void Apply_ResultEqualInput_ReturnsNull()
    {
        var rule = new TagNormalizationRule { Pattern = @"\s+$", Replacement = string.Empty };

        Assert.Null(TagNormalization.Apply(rule, "Demo Album A"));
    }

    // ---------------------------------------------------------------- IsValidPattern

    [Theory]
    [InlineData(@"^(?i)abc$", true)]
    [InlineData(@"^(\d{4})-(\d{2})$", true)]
    [InlineData("(", false)]
    [InlineData("[a-", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsValidPattern_ReportsPatternValidity(string pattern, bool valid)
    {
        Assert.Equal(valid, TagNormalization.IsValidPattern(pattern, out var error));
        if (valid)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    // ---------------------------------------------------------------- 目标字段

    [Fact]
    public void SupportedTargetFields_ContainsCommonFieldsAndAnyField()
    {
        var fields = TagNormalization.SupportedTargetFields;

        Assert.Contains(TagNormalization.AnyField, fields);
        Assert.Contains(TagFieldKeys.Title, fields);
        Assert.Contains(TagFieldKeys.Artist, fields);
        Assert.Contains(TagFieldKeys.Album, fields);
        Assert.Contains(TagFieldKeys.AlbumArtist, fields);
        Assert.Contains(TagFieldKeys.Composer, fields);
        Assert.Contains(TagFieldKeys.Genre, fields);
        Assert.Contains(TagFieldKeys.Year, fields);
        Assert.Contains(TagFieldKeys.TrackNumber, fields);
        Assert.Contains(TagFieldKeys.DiscNumber, fields);
        Assert.Contains(TagFieldKeys.Comment, fields);
        Assert.Equal(11, fields.Count);
    }

    [Theory]
    [InlineData("album", "album")]
    [InlineData(" Album ", "album")]
    [InlineData("ALBUMARTIST", "albumArtist")]
    [InlineData("*", "*")]
    [InlineData(" * ", "*")]
    [InlineData("BPM", "BPM")]
    [InlineData("", "")]
    public void CanonicalizeTargetField_NormalizesKnownFields(string input, string expected)
    {
        Assert.Equal(expected, TagNormalization.CanonicalizeTargetField(input));
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData(" * ", true)]
    [InlineData("album", false)]
    [InlineData(null, false)]
    public void IsAnyField_DetectsWildcard(string? input, bool expected)
    {
        Assert.Equal(expected, TagNormalization.IsAnyField(input));
    }

    // ---------------------------------------------------------------- ApplyToTag

    [Fact]
    public void ApplyToTag_ChangesMatchingCommonFieldOnly()
    {
        var tag = CreateTag(album: "Demo Album A - EP");

        var result = TagNormalization.ApplyToTag(DefaultAlbumRule(), tag);

        Assert.NotNull(result);
        Assert.Equal("Demo Album A", result!.Fields[Album]);
        Assert.Single(result.Fields);
        Assert.Empty(result.AdditionalTagChanges);
        Assert.True(result.HasChanges);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void ApplyToTag_NoMatch_ReturnsNull()
    {
        var tag = CreateTag(album: "Demo Album A");

        Assert.Null(TagNormalization.ApplyToTag(DefaultAlbumRule(), tag));
        Assert.Null(TagNormalization.ApplyToTag((TagNormalizationRule?)null, tag));
        Assert.Null(TagNormalization.ApplyToTag((IEnumerable<TagNormalizationRule>?)null, tag));
    }

    [Fact]
    public void ApplyToTag_WildcardRule_ChangesAdditionalTags()
    {
        var tag = CreateTag(
            comment: "Demo comment",
            additional:
            [
                new AudioTagEntry("MOOD", "Old mood") { Kind = AdditionalTagKinds.Xiph, Key = "MOOD" },
                new AudioTagEntry("BPM", "120") { Kind = AdditionalTagKinds.Id3Text, Key = "TBPM" }
            ]);
        var rule = new TagNormalizationRule
        {
            TargetField = TagNormalization.AnyField,
            Pattern = "Old",
            Replacement = "New"
        };

        var result = TagNormalization.ApplyToTag(rule, tag);

        Assert.NotNull(result);
        var change = Assert.Single(result!.AdditionalTagChanges);
        Assert.Equal(AdditionalTagKinds.Xiph, change.Kind);
        Assert.Equal("MOOD", change.Key);
        Assert.Equal("New mood", change.Value);
        Assert.False(change.IsRemoval);
    }

    [Fact]
    public void ApplyToTag_EmptyReplacementForAdditionalTag_DoesNotDeleteIt()
    {
        var tag = CreateTag(
            additional: [new AudioTagEntry("MOOD", "Old") { Kind = AdditionalTagKinds.Xiph, Key = "MOOD" }]);
        var rule = new TagNormalizationRule
        {
            TargetField = TagNormalization.AnyField,
            Pattern = "^Old$",
            Replacement = string.Empty
        };

        var result = TagNormalization.ApplyToTag(rule, tag);

        Assert.Null(result);
    }

    [Fact]
    public void ApplyToTag_EmptyReplacementForCommonField_ClearsIt()
    {
        var tag = CreateTag(comment: "Old");
        var rule = new TagNormalizationRule
        {
            TargetField = TagFieldKeys.Comment,
            Pattern = "^Old$",
            Replacement = string.Empty
        };

        var result = TagNormalization.ApplyToTag(rule, tag);

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result!.Fields[TagFieldKeys.Comment]);
    }

    [Fact]
    public void ApplyToTag_DateRuleOnWildcard_DoesNotChangeYear()
    {
        var tag = CreateTag(comment: "2026-01-01T00:00:00", year: 2026);

        var result = TagNormalization.ApplyToTag(DefaultDateRule(), tag);

        Assert.NotNull(result);
        Assert.Equal("2026-01-01", result!.Fields[TagFieldKeys.Comment]);
        Assert.False(result.Fields.ContainsKey(TagFieldKeys.Year));
        Assert.Single(result.Fields);
    }

    [Fact]
    public void ApplyToTag_YearRule_UpdatesYearAndLeavesComment()
    {
        var tag = CreateTag(comment: "2026-01-01T00:00:00", year: 2026);
        var rule = new TagNormalizationRule
        {
            TargetField = TagFieldKeys.Year,
            Pattern = "^20\\d{2}$",
            Replacement = "1999"
        };

        var result = TagNormalization.ApplyToTag(rule, tag);

        Assert.NotNull(result);
        Assert.Equal("1999", result!.Fields[TagFieldKeys.Year]);
        Assert.False(result.Fields.ContainsKey(TagFieldKeys.Comment));
    }

    [Fact]
    public void ApplyToTag_YearRule_InvalidNumericResultIsSkipped()
    {
        var tag = CreateTag(year: 2026);
        var rule = new TagNormalizationRule
        {
            TargetField = TagFieldKeys.Year,
            Pattern = "^\\d+$",
            Replacement = "not-a-year"
        };

        Assert.Null(TagNormalization.ApplyToTag(rule, tag));
    }

    [Fact]
    public void ApplyToTag_YearRule_ZeroMeansUnsetAndIsAccepted()
    {
        var tag = CreateTag(year: 2026);
        var rule = new TagNormalizationRule
        {
            TargetField = TagFieldKeys.Year,
            Pattern = "^\\d+$",
            Replacement = "0"
        };

        var result = TagNormalization.ApplyToTag(rule, tag);

        Assert.NotNull(result);
        Assert.Equal("0", result!.Fields[TagFieldKeys.Year]);
    }

    [Fact]
    public void ApplyToTag_UnknownTargetField_IsSkipped()
    {
        var tag = CreateTag(album: "Demo Album A - EP");
        var rule = DefaultAlbumRule();
        rule.TargetField = "bpm";

        Assert.Null(TagNormalization.ApplyToTag(rule, tag));
    }

    [Fact]
    public void ApplyToTag_DisabledRule_IsSkipped()
    {
        var tag = CreateTag(album: "Demo Album A - EP");
        var rule = DefaultAlbumRule();
        rule.Enabled = false;

        Assert.Null(TagNormalization.ApplyToTag(rule, tag));
    }

    [Fact]
    public void ApplyToTag_MultipleRules_ApplySequentially()
    {
        var tag = CreateTag(comment: "alpha");
        var rules = new[]
        {
            new TagNormalizationRule { TargetField = TagFieldKeys.Comment, Pattern = "alpha", Replacement = "beta" },
            new TagNormalizationRule { TargetField = TagFieldKeys.Comment, Pattern = "beta", Replacement = "gamma" }
        };

        var result = TagNormalization.ApplyToTag(rules, tag);

        Assert.NotNull(result);
        Assert.Equal("gamma", result!.Fields[TagFieldKeys.Comment]);
    }

    [Fact]
    public void ApplyToTag_MultipleRules_OnlyOneRuleMatches_DoesNotDuplicateChanges()
    {
        var tag = CreateTag(album: "Demo Album A - EP");
        var rules = new[]
        {
            DefaultAlbumRule(),
            new TagNormalizationRule { TargetField = Album, Pattern = "Missing", Replacement = "Nope" }
        };

        var result = TagNormalization.ApplyToTag(rules, tag);

        Assert.NotNull(result);
        var change = Assert.Single(result!.Fields);
        Assert.Equal(Album, change.Key);
        Assert.Equal("Demo Album A", change.Value);
    }

    [Fact]
    public void ApplyToTag_AllOptionalFields_UsesElevenCommonFields()
    {
        var tag = CreateTag();

        var result = TagNormalization.ApplyToTag(DefaultAlbumRule(), tag);

        Assert.Null(result);
        Assert.Equal(11, TagBatchEditor.GetFieldValues(tag).Count);
    }

    [Fact]
    public void Result_ApplyTo_MergesIntoBatchEdit()
    {
        var tag = CreateTag(
            album: "Demo Album A - EP",
            additional: [new AudioTagEntry("MOOD", "Old") { Kind = AdditionalTagKinds.Xiph, Key = "MOOD" }]);
        var rules = new[]
        {
            DefaultAlbumRule(),
            new TagNormalizationRule
            {
                TargetField = TagNormalization.AnyField,
                Pattern = "Old",
                Replacement = "New"
            }
        };

        var result = TagNormalization.ApplyToTag(rules, tag);
        Assert.NotNull(result);

        var edit = new TagBatchEdit();
        result!.ApplyTo(edit);

        Assert.Equal("Demo Album A", edit.Fields[Album]);
        var change = Assert.Single(edit.AdditionalTagChanges);
        Assert.Equal("New", change.Value);
    }

    [Fact]
    public void Result_ApplyTo_NullEdit_Throws()
    {
        var tag = CreateTag(album: "Demo Album A - EP");
        var result = TagNormalization.ApplyToTag(DefaultAlbumRule(), tag);

        Assert.NotNull(result);
        Assert.Throws<ArgumentNullException>(() => result!.ApplyTo(null!));
    }

    // ---------------------------------------------------------------- 规则克隆

    [Fact]
    public void Rule_Clone_IsDeepCopy()
    {
        var rule = new TagNormalizationRule
        {
            Id = "rule-1",
            Name = "Demo Rule",
            TargetField = Album,
            Pattern = "demo",
            Replacement = "DEMO",
            Enabled = false
        };

        var clone = rule.Clone();

        Assert.NotSame(rule, clone);
        Assert.Equal(rule.Id, clone.Id);
        Assert.Equal(rule.Name, clone.Name);
        Assert.Equal(rule.TargetField, clone.TargetField);
        Assert.Equal(rule.Pattern, clone.Pattern);
        Assert.Equal(rule.Replacement, clone.Replacement);
        Assert.Equal(rule.Enabled, clone.Enabled);

        clone.Pattern = "changed";
        Assert.Equal("demo", rule.Pattern);
    }

    // ---------------------------------------------------------------- 设置归一化

    [Fact]
    public void NormalizeRules_Null_ReturnsDefaults()
    {
        var rules = TagNormalization.NormalizeRules(null);

        Assert.Equal(2, rules.Count);
        Assert.Equal(TagNormalizationDefaults.AlbumEpSingleSuffixRuleId, rules[0].Id);
        Assert.Equal(TagNormalizationDefaults.IsoDateTimeRuleId, rules[1].Id);
    }

    [Fact]
    public void NormalizeRules_EmptyList_StaysEmpty()
    {
        var rules = TagNormalization.NormalizeRules(new List<TagNormalizationRule>());

        Assert.Empty(rules);
    }

    [Fact]
    public void NormalizeRules_TrimsAndFillsDefaults()
    {
        var rules = TagNormalization.NormalizeRules(
        [
            new TagNormalizationRule
            {
                Id = "  ",
                Name = "   ",
                TargetField = " Album ",
                Pattern = "  ^Demo$  ",
                Replacement = null!
            },
            new TagNormalizationRule { Id = "keep", Name = " Keep me ", TargetField = "ALBUMARTIST", Pattern = "x" }
        ]);

        Assert.Equal(2, rules.Count);
        Assert.False(string.IsNullOrWhiteSpace(rules[0].Id));
        Assert.Equal("规范化规则 1", rules[0].Name);
        Assert.Equal(Album, rules[0].TargetField);
        Assert.Equal("^Demo$", rules[0].Pattern);
        Assert.Equal(string.Empty, rules[0].Replacement);
        Assert.Equal("keep", rules[1].Id);
        Assert.Equal("Keep me", rules[1].Name);
        Assert.Equal(TagFieldKeys.AlbumArtist, rules[1].TargetField);
    }

    [Fact]
    public void NormalizeRules_DuplicateIds_AreReplaced()
    {
        var rules = TagNormalization.NormalizeRules(
        [
            new TagNormalizationRule { Id = "dup", Name = "First", Pattern = "a" },
            new TagNormalizationRule { Id = "dup", Name = "Second", Pattern = "b" }
        ]);

        Assert.Equal("dup", rules[0].Id);
        Assert.NotEqual("dup", rules[1].Id);
        Assert.False(string.IsNullOrWhiteSpace(rules[1].Id));
    }

    [Fact]
    public void NormalizeRules_NullEntries_AreDropped()
    {
        var rules = TagNormalization.NormalizeRules(
            new List<TagNormalizationRule>
            {
                null!,
                new() { Id = "valid", Name = "Valid", Pattern = "a" },
                null!
            });

        Assert.Single(rules);
        Assert.Equal("valid", rules[0].Id);
    }

    [Fact]
    public void NormalizeRules_KeepsInvalidPatternsForUiWarnings()
    {
        var rules = TagNormalization.NormalizeRules(
        [
            new TagNormalizationRule { Id = "broken", Name = "Broken", Pattern = "(", TargetField = "BPM" }
        ]);

        Assert.Single(rules);
        Assert.Equal("(", rules[0].Pattern);
        Assert.Equal("BPM", rules[0].TargetField);
        Assert.Null(TagNormalization.Apply(rules[0], "anything"));
    }

    [Fact]
    public void NormalizeRules_CapsRuleCountAtMax()
    {
        var many = Enumerable.Range(0, TagNormalization.MaxRuleCount + 10)
            .Select(i => new TagNormalizationRule { Id = $"id-{i}", Name = $"Rule {i}", Pattern = "a" })
            .ToList();

        var rules = TagNormalization.NormalizeRules(many);

        Assert.Equal(TagNormalization.MaxRuleCount, rules.Count);
        Assert.Equal("id-0", rules[0].Id);
        Assert.Equal($"id-{TagNormalization.MaxRuleCount - 1}", rules[^1].Id);
    }

    [Fact]
    public void NormalizeRules_DoesNotMutateInputInstances()
    {
        var source = new TagNormalizationRule
        {
            Id = "source",
            Name = "  Source  ",
            TargetField = " Album ",
            Pattern = "  x  "
        };

        var normalized = TagNormalization.NormalizeRules([source]);

        Assert.Single(normalized);
        Assert.Equal("  Source  ", source.Name);
        Assert.Equal(" Album ", source.TargetField);
        Assert.Equal("  x  ", source.Pattern);
        Assert.NotSame(source, normalized[0]);
    }

    private static AudioTagData CreateTag(
        string album = "Demo Album A",
        string? comment = null,
        int? year = null,
        IReadOnlyList<AudioTagEntry>? additional = null) =>
        new(
            Title: "Demo Track 01",
            Artist: "Demo Artist",
            Album: album,
            AlbumArtist: "Demo Artist",
            Composer: string.Empty,
            Genre: "Demo Genre",
            Comment: comment ?? string.Empty,
            Year: year,
            TrackNumber: 1,
            DiscNumber: 1,
            DurationMs: 180_000,
            Bitrate: 320,
            SampleRate: 44_100,
            Channels: 2,
            Codec: "FLAC",
            CoverBytes: null,
            CoverMime: null)
        {
            AdditionalTags = additional ?? []
        };
}
