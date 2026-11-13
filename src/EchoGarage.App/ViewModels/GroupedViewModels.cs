using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.App.ViewModels;

/// <summary>One cell of a song row in the grouped list (column key + width).</summary>
public sealed class TrackCellViewModel
{
    public required string Key { get; init; }
    public required GroupedColumnHeaderViewModel HeaderSource { get; init; }
    public required TrackRowViewModel Row { get; init; }
    /// <summary>Cell template resolved from the window's "Cell.&lt;Column&gt;" resources.</summary>
    public DataTemplate? Template { get; init; }
}

/// <summary>
/// Header cell above the grouped song list, aligned with the row cells. The width is mutable so
/// dragging the gripper on the header updates the header and every (realized) row cell live;
/// the row cells bind to the same instance instead of copying the value.
/// </summary>
public sealed class GroupedColumnHeaderViewModel : INotifyPropertyChanged
{
    private double _width;
    private double _fillWidth;
    private bool _showResizeGripper = true;

    public required string Key { get; init; }

    public required string Header { get; init; }

    /// <summary>Minimum width from <see cref="ColumnCatalog"/>; the gripper cannot go below it.</summary>
    public required double MinWidth { get; init; }

    public required double Width
    {
        get => _width;
        set
        {
            if (Math.Abs(_width - value) < 0.01)
            {
                return;
            }

            _width = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayWidth));
        }
    }

    /// <summary>
    /// Display-only extra width for the trailing left-pinned column. The left flow is stretched to
    /// meet the right-pinned block, so the divider between the two blocks only redistributes space;
    /// the logical <see cref="Width"/> (the persisted value) is not affected by this.
    /// </summary>
    public double FillWidth
    {
        get => _fillWidth;
        set
        {
            if (Math.Abs(_fillWidth - value) < 0.01)
            {
                return;
            }

            _fillWidth = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayWidth));
        }
    }

    /// <summary>Width actually used by the header cell and the row cells.</summary>
    public double DisplayWidth => _width + _fillWidth;

    public string SortGlyph { get; init; } = string.Empty;

    /// <summary>
    /// Header anchor: left columns flow after the cover gutter, right columns stay glued to the
    /// right edge of the list (so they never scroll out of view when a left column grows).
    /// </summary>
    public ColumnPin Pin { get; init; }

    /// <summary>
    /// True for the first right-pinned column: its left edge is the boundary between the left flow
    /// and the pinned block, so dragging it compresses/expands the last left-pinned column.
    /// </summary>
    public bool ShowLeftBoundaryGripper { get; set; }

    public bool IsLeftPinned => Pin == ColumnPin.Left;

    public bool IsRightPinned => Pin == ColumnPin.Right;

    /// <summary>
    /// False for the trailing right-pinned column whose right edge is the list border: that edge
    /// is anchored, so dragging it cannot change the width without detaching the block.
    /// </summary>
    public bool ShowResizeGripper
    {
        get => _showResizeGripper;
        set
        {
            if (_showResizeGripper == value)
            {
                return;
            }

            _showResizeGripper = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
