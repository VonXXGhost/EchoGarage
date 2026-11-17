using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class SmartQueryTests
{
    [Fact]
    public void EmptyDefinition_ReturnsBaseLibraryFilter()
    {
        var query = SmartQueryBuilder.Build(new SmartPlaylistDefinition());

        Assert.Contains("t.IsInLibrary = 1", query.WhereSql);
        Assert.Contains("t.IsMissing = 0", query.WhereSql);
        Assert.Empty(query.Parameters);
    }

    [Fact]
    public void AllMode_JoinsRulesWithAnd()
    {
        var definition = new SmartPlaylistDefinition
        {
            MatchMode = SmartMatchMode.All,
            Rules =
            [
                new SmartRule { Field = "artist", Operator = "contains", Value = "a" },
                new SmartRule { Field = "album", Operator = "equals", Value = "b" }
            ]
        };

        var query = SmartQueryBuilder.Build(definition);

        Assert.Contains("AND", query.WhereSql);
        Assert.DoesNotContain(" OR ", query.WhereSql);
        Assert.Equal(2, query.Parameters.Count);
        Assert.Contains("%a%", query.Parameters.Values.Select(v => v?.ToString()));
    }

    [Fact]
    public void AnyMode_JoinsRulesWithOr()
    {
        var definition = new SmartPlaylistDefinition
        {
            MatchMode = SmartMatchMode.Any,
            Rules =
            [
                new SmartRule { Field = "artist", Operator = "contains", Value = "a" },
                new SmartRule { Field = "genre", Operator = "isEmpty" }
            ]
        };

        var query = SmartQueryBuilder.Build(definition);

        Assert.Contains(" OR ", query.WhereSql);
        Assert.Single(query.Parameters);
    }

    [Fact]
    public void LikeValue_IsEscaped()
    {
        var query = SmartQueryBuilder.Build(new SmartPlaylistDefinition
        {
            Rules = [new SmartRule { Field = "title", Operator = "contains", Value = "50%_off" }]
        });

        Assert.Equal("%50\\%\\_off%", query.Parameters.Values.Single()?.ToString());
        Assert.Contains("ESCAPE", query.WhereSql);
    }

    [Fact]
    public void NumericRule_UsesInvariantCulture()
    {
        var query = SmartQueryBuilder.Build(new SmartPlaylistDefinition
        {
            Rules = [new SmartRule { Field = "durationSeconds", Operator = "greaterThan", Value = "12.5" }]
        });

        Assert.Contains("t.DurationMs / 1000.0 > @r0", query.WhereSql);
        Assert.Equal(12.5, Assert.IsType<double>(query.Parameters["@r0"]));
    }

    [Fact]
    public void InvalidFieldOrOperator_IsIgnored()
    {
        var query = SmartQueryBuilder.Build(new SmartPlaylistDefinition
        {
            Rules =
            [
                new SmartRule { Field = "nope", Operator = "contains", Value = "x" },
                new SmartRule { Field = "title", Operator = "greaterThan", Value = "x" },
                new SmartRule { Field = "year", Operator = "equals", Value = "not-a-number" }
            ]
        });

        Assert.Empty(query.Parameters);
        Assert.DoesNotContain("nope", query.WhereSql);
    }

    [Fact]
    public void FieldCatalog_ExposesOperatorsForUi()
    {
        var field = SmartFieldCatalog.Find("title");

        Assert.NotNull(field);
        Assert.Contains("contains", field!.Operators);
        Assert.Equal("标题", SmartFieldCatalog.FieldLabel("title"));
    }

    [Fact]
    public void DirectoryField_UsesTextOperatorsIncludingRegex()
    {
        var field = SmartFieldCatalog.Find("directory");

        Assert.NotNull(field);
        Assert.Equal("文件目录", field!.Label);
        Assert.False(field.IsNumeric);
        Assert.Same(SmartFieldCatalog.TextOperators, field.Operators);
        Assert.Contains("regex", field.Operators);
        Assert.Contains("notRegex", field.Operators);
    }

    [Fact]
    public void DirectoryRule_UsesDirnameScalarFunction()
    {
        var query = SmartQueryBuilder.Build(new SmartPlaylistDefinition
        {
            Rules = [new SmartRule { Field = "directory", Operator = "contains", Value = "Demo Album" }]
        });

        Assert.Contains("COALESCE(dirname(t.Path), '') LIKE @r0 ESCAPE '\\'", query.WhereSql);
        Assert.Equal("%Demo Album%", query.Parameters["@r0"]);
    }

    [Fact]
    public void RegexRule_UsesRegexpOperatorWithParameterizedPattern()
    {
        var query = SmartQueryBuilder.Build(new SmartPlaylistDefinition
        {
            Rules = [new SmartRule { Field = "title", Operator = "regex", Value = @"^\d{4}" }]
        });

        Assert.Contains("COALESCE(t.Title, '') REGEXP @r0", query.WhereSql);
        Assert.Equal(@"^\d{4}", query.Parameters["@r0"]);
    }

    [Fact]
    public void NotRegexRule_NegatesRegexpPredicate()
    {
        var query = SmartQueryBuilder.Build(new SmartPlaylistDefinition
        {
            Rules = [new SmartRule { Field = "directory", Operator = "notRegex", Value = @"Live$" }]
        });

        Assert.Contains("NOT (COALESCE(dirname(t.Path), '') REGEXP @r0)", query.WhereSql);
        Assert.Equal(@"Live$", query.Parameters["@r0"]);
    }

    [Fact]
    public void InvalidRegexRule_IsSkipped()
    {
        var query = SmartQueryBuilder.Build(new SmartPlaylistDefinition
        {
            Rules =
            [
                new SmartRule { Field = "title", Operator = "regex", Value = "(" },
                new SmartRule { Field = "artist", Operator = "notRegex", Value = "[a-" }
            ]
        });

        Assert.Empty(query.Parameters);
        Assert.DoesNotContain("REGEXP", query.WhereSql);
        Assert.DoesNotContain("@r", query.WhereSql);
    }

    [Fact]
    public void EmptyRegexValue_IsSkipped()
    {
        var query = SmartQueryBuilder.Build(new SmartPlaylistDefinition
        {
            Rules = [new SmartRule { Field = "title", Operator = "regex", Value = "   " }]
        });

        Assert.Empty(query.Parameters);
        Assert.DoesNotContain("REGEXP", query.WhereSql);
    }

    [Fact]
    public void SkippedRegexRule_KeepsParameterIndexesContiguous()
    {
        // 跳过非法正则后参数编号必须保持连续，否则 SQL 里的 @r1 会没有对应参数。
        var query = SmartQueryBuilder.Build(new SmartPlaylistDefinition
        {
            Rules =
            [
                new SmartRule { Field = "title", Operator = "regex", Value = "(" },
                new SmartRule { Field = "artist", Operator = "contains", Value = "Demo" }
            ]
        });

        Assert.Single(query.Parameters);
        Assert.Contains("LIKE @r0", query.WhereSql);
        Assert.DoesNotContain("@r1", query.WhereSql);
    }

    [Fact]
    public void RegexOperators_HaveChineseLabels()
    {
        Assert.Equal("匹配正则", SmartFieldCatalog.OperatorLabel("regex"));
        Assert.Equal("不匹配正则", SmartFieldCatalog.OperatorLabel("notRegex"));
    }

    [Fact]
    public void SmartRegex_ValidationMatchesDotNetCompilation()
    {
        Assert.True(SmartRegex.IsValid(@"^Demo \d{2}$"));
        Assert.True(SmartRegex.IsValid("(Live|Demo)"));
        Assert.False(SmartRegex.IsValid("("));
        Assert.False(SmartRegex.IsValid(string.Empty));
        Assert.False(SmartRegex.IsValid(null));
    }
}
