using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class TagSuggestionMatcherTests
{
    [Fact]
    public void Match_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(TagSuggestionMatcher.Match(["Rock", "Jazz"], null));
        Assert.Empty(TagSuggestionMatcher.Match(["Rock", "Jazz"], "   "));
        Assert.Empty(TagSuggestionMatcher.Match([], "ro"));
    }

    [Fact]
    public void Match_PrefersPrefixMatches_ThenContains()
    {
        // 前缀命中排在前；仅包含命中（The Beatles）排在后面，同类保持候选原顺序。
        var matches = TagSuggestionMatcher.Match(["The Beatles", "Beatles Tribute", "Beatles"], "beat");

        Assert.Equal(["Beatles Tribute", "Beatles", "The Beatles"], matches);
    }

    [Fact]
    public void Match_IsCaseInsensitive_AndDeduplicates()
    {
        var matches = TagSuggestionMatcher.Match(["Rock", "rock", "ROCK", "Classic Rock"], "ro");

        Assert.Equal(["Rock", "Classic Rock"], matches);
    }

    [Fact]
    public void Match_TrimsCandidates_AndIgnoresEmptyOnes()
    {
        var matches = TagSuggestionMatcher.Match(["  Rock  ", "", "Rap"], "ro");

        Assert.Equal(["Rock"], matches);
    }

    [Fact]
    public void Match_RespectsLimit()
    {
        var matches = TagSuggestionMatcher.Match(["Alpha", "Alpine", "Alps", "Alto"], "al", 2);

        Assert.Equal(["Alpha", "Alpine"], matches);
        Assert.Empty(TagSuggestionMatcher.Match(["Alpha"], "al", 0));
    }
}
