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
}
