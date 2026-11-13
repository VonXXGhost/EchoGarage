namespace EchoGarage.Core.Services;

/// <summary>
/// Width math for the grouped playlist's pinned column blocks. Persisted column widths are
/// logical; when both blocks are present the left flow is stretched at display time so that it
/// touches the right-pinned block instead of leaving a blank strip between the two.
/// </summary>
public static class GroupedColumnWidths
{
    /// <summary>Sub-pixel slack smaller than this counts as "no gap" (layout rounding noise).</summary>
    public const double FillTolerance = 0.01;

    /// <summary>
    /// Extra display width the trailing left-pinned column must absorb so the left flow ends
    /// exactly at the right-pinned block. Zero when the left flow already fills or overflows the
    /// available width; an overflowing flow is clipped at the pinned block, not compressed.
    /// </summary>
    public static double ComputeTrailingFill(double availableWidth, IReadOnlyList<double> leftWidths)
    {
        if (leftWidths.Count == 0)
        {
            return 0;
        }

        var total = 0.0;
        foreach (var width in leftWidths)
        {
            total += Math.Max(0, width);
        }

        var fill = availableWidth - total;
        return fill > FillTolerance ? fill : 0;
    }
}
