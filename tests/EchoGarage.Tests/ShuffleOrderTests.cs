using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class ShuffleOrderTests
{
    private sealed class Item(string name)
    {
        public string Name { get; } = name;

        public override string ToString() => Name;
    }

    private static List<Item> Items(params string[] names) => names.Select(n => new Item(n)).ToList();

    [Fact]
    public void BuildRound_KeepsCurrentTrackFirst_AndEveryOtherTrackOnce()
    {
        var items = Items("a", "b", "c", "d", "e");
        var current = items[2];

        var round = ShuffleOrder.BuildRound(items, current, new Random(1234));

        Assert.Equal(items.Count, round.Count);
        Assert.Same(current, round[0]);
        Assert.Equal(
            items.Select(i => i.Name).OrderBy(n => n),
            round.Select(i => i.Name).OrderBy(n => n));
    }

    [Fact]
    public void BuildRound_WithoutCurrent_ShufflesAllTracks()
    {
        var items = Items("a", "b", "c");

        var round = ShuffleOrder.BuildRound(items, null, new Random(7));

        Assert.Equal(items.Count, round.Count);
        Assert.Equal(
            items.Select(i => i.Name).OrderBy(n => n),
            round.Select(i => i.Name).OrderBy(n => n));
    }

    [Fact]
    public void BuildRound_CurrentNotInItems_IsIgnored()
    {
        var items = Items("a", "b");
        var stranger = new Item("x");

        var round = ShuffleOrder.BuildRound(items, stranger, new Random(1));

        Assert.Equal(2, round.Count);
        Assert.DoesNotContain(stranger, round);
    }

    [Fact]
    public void BuildRound_SingleItem_ReturnsThatItem()
    {
        var items = Items("only");

        var round = ShuffleOrder.BuildRound(items, items[0], new Random(3));

        Assert.Single(round);
        Assert.Same(items[0], round[0]);
    }

    [Fact]
    public void BuildRound_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(ShuffleOrder.BuildRound(Items(), null, new Random(5)));
    }

    [Fact]
    public void Shuffle_ProducesPermutation_AndIsDeterministicForSeed()
    {
        var source = Enumerable.Range(0, 20).ToList();
        var first = source.ToList();
        var second = source.ToList();

        ShuffleOrder.Shuffle(first, new Random(42));
        ShuffleOrder.Shuffle(second, new Random(42));

        Assert.Equal(first, second);
        Assert.Equal(source, first.OrderBy(v => v));
        Assert.NotEqual(source, first);
    }

    [Fact]
    public void Shuffle_EmptyAndSingleItem_AreSafe()
    {
        var empty = new List<int>();
        ShuffleOrder.Shuffle(empty, new Random(1));
        Assert.Empty(empty);

        var single = new List<int> { 7 };
        ShuffleOrder.Shuffle(single, new Random(1));
        Assert.Equal([7], single);
    }
}
