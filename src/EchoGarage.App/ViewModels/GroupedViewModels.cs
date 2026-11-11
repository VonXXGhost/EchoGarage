using System.Windows;
using EchoGarage.Core.Services;

namespace EchoGarage.App.ViewModels;

/// <summary>One cell of a song row in the grouped list (column key + width).</summary>
public sealed class TrackCellViewModel
{
    public required string Key { get; init; }
    public required double Width { get; init; }
    public required TrackRowViewModel Row { get; init; }
    /// <summary>Cell template resolved from the window's "Cell.&lt;Column&gt;" resources.</summary>
    public DataTemplate? Template { get; init; }
}

/// <summary>Header cell above the grouped song list, aligned with the row cells.</summary>
public sealed class GroupedColumnHeaderViewModel
{
    public required string Key { get; init; }
    public required string Header { get; init; }
    public required double Width { get; init; }
    public string SortGlyph { get; init; } = string.Empty;
}

/// <summary>Maps playlist column keys to the row property used for sorting.</summary>
public static class TrackSortMap
{
    /// <summary>
    /// Derived from <see cref="ColumnCatalog"/> so header sorting, the grouped list and layout
    /// normalization all use the same column → sort-property mapping.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> ByKey =
        ColumnCatalog.All.ToDictionary(
            c => c.Key,
            c => c.SortProperty,
            StringComparer.OrdinalIgnoreCase);
}
