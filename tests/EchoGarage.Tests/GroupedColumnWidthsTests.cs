using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class GroupedColumnWidthsTests
{
    [Fact]
    public void ComputeTrailingFill_NoLeftColumns_ReturnsZero() =>
        Assert.Equal(0, GroupedColumnWidths.ComputeTrailingFill(900, []));

    [Fact]
    public void ComputeTrailingFill_SingleLeftColumn_AbsorbsWholeGap() =>
        Assert.Equal(610, GroupedColumnWidths.ComputeTrailingFill(780, [170]), 3);

    [Fact]
    public void ComputeTrailingFill_MultipleLeftColumns_OnlyTheRemainderIsFilled() =>
        Assert.Equal(250, GroupedColumnWidths.ComputeTrailingFill(800, [380, 170]), 3);

    [Fact]
    public void ComputeTrailingFill_ExactFit_ReturnsZero() =>
        Assert.Equal(0, GroupedColumnWidths.ComputeTrailingFill(800, [380, 420]));

    [Fact]
    public void ComputeTrailingFill_OverflowingLeftFlow_ReturnsZero() =>
        Assert.Equal(0, GroupedColumnWidths.ComputeTrailingFill(800, [600, 300]));

    [Fact]
    public void ComputeTrailingFill_NegativeAvailableWidth_ReturnsZero() =>
        Assert.Equal(0, GroupedColumnWidths.ComputeTrailingFill(-50, [100]));
}
