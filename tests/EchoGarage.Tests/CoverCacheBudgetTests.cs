using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

/// <summary>
/// 封面内存缓存的容量账本：字节上限、条目上限、LRU 顺序与“至少保留一条”的边界。
/// 该账本是纯逻辑，真实缓存的记账错误（例如只按条目数淘汰）会在这里暴露。
/// </summary>
public sealed class CoverCacheBudgetTests
{
    [Fact]
    public void Add_AccumulatesBytesAndCount()
    {
        var budget = new CoverCacheBudget(byteLimit: 1000, entryLimit: 10);

        budget.Add("a", 100);
        budget.Add("b", 250);

        Assert.Equal(2, budget.Count);
        Assert.Equal(350, budget.Bytes);
    }

    [Fact]
    public void Add_SameKey_ReplacesBytesInsteadOfDuplicating()
    {
        var budget = new CoverCacheBudget(byteLimit: 1000, entryLimit: 10);

        budget.Add("a", 400);
        budget.Add("a", 150);

        Assert.Equal(1, budget.Count);
        Assert.Equal(150, budget.Bytes);
    }

    [Fact]
    public void Trim_EvictsOldestEntriesUntilWithinByteLimit()
    {
        var budget = new CoverCacheBudget(byteLimit: 250, entryLimit: 100);
        budget.Add("a", 100);
        budget.Add("b", 100);
        budget.Add("c", 100);
        budget.Add("d", 100);

        var evicted = budget.Trim();

        Assert.Equal(["a", "b"], evicted);
        Assert.Equal(200, budget.Bytes);
        Assert.False(budget.Contains("a"));
        Assert.True(budget.Contains("d"));
    }

    [Fact]
    public void Trim_EvictsOldestEntriesUntilWithinEntryLimit()
    {
        var budget = new CoverCacheBudget(byteLimit: 1_000_000, entryLimit: 3);
        budget.Add("a", 10);
        budget.Add("b", 10);
        budget.Add("c", 10);
        budget.Add("d", 10);
        budget.Add("e", 10);

        var evicted = budget.Trim();

        Assert.Equal(["a", "b"], evicted);
        Assert.Equal(3, budget.Count);
    }

    [Fact]
    public void Touch_MovesEntryToTheBackOfTheQueue()
    {
        var budget = new CoverCacheBudget(byteLimit: 300, entryLimit: 100);
        budget.Add("a", 100);
        budget.Add("b", 100);
        budget.Add("c", 100);

        Assert.True(budget.Touch("a"));
        budget.Add("d", 100);
        var evicted = budget.Trim();

        Assert.Equal(["b"], evicted);
        Assert.Equal(300, budget.Bytes);
    }

    [Fact]
    public void Touch_UnknownKey_ReturnsFalse()
    {
        var budget = new CoverCacheBudget(byteLimit: 1000, entryLimit: 10);

        Assert.False(budget.Touch("missing"));
    }

    [Fact]
    public void Remove_SubtractsBytes()
    {
        var budget = new CoverCacheBudget(byteLimit: 1000, entryLimit: 10);
        budget.Add("a", 300);
        budget.Add("b", 200);

        Assert.True(budget.Remove("a"));
        Assert.False(budget.Remove("a"));

        Assert.Equal(1, budget.Count);
        Assert.Equal(200, budget.Bytes);
    }

    [Fact]
    public void Trim_KeepsAtLeastOneEntry_EvenWhenItExceedsTheByteLimit()
    {
        var budget = new CoverCacheBudget(byteLimit: 100, entryLimit: 10);
        budget.Add("huge", 5000);

        var evicted = budget.Trim();

        Assert.Empty(evicted);
        Assert.Equal(1, budget.Count);
        Assert.Equal(5000, budget.Bytes);
    }

    [Fact]
    public void Trim_OnEmptyBudget_ReturnsNoKeys()
    {
        var budget = new CoverCacheBudget(byteLimit: 1000, entryLimit: 10);

        Assert.Empty(budget.Trim());
        Assert.Equal(0, budget.Bytes);
    }

    [Fact]
    public void NegativeImageProbes_AreAccountedAsTinyEntries()
    {
        // 负缓存（确认没有封面的曲目）以 0 字节登记：只占条目额度，不影响字节上限。
        var budget = new CoverCacheBudget(byteLimit: 10, entryLimit: 2);
        budget.Add("missing-a", 0);
        budget.Add("missing-b", 0);
        budget.Add("missing-c", 0);

        var evicted = budget.Trim();

        Assert.Equal(["missing-a"], evicted);
        Assert.Equal(2, budget.Count);
        Assert.Equal(0, budget.Bytes);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoverCacheBudget(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoverCacheBudget(1000, 0));
    }
}
