using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
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
        }
    }

    public string SortGlyph { get; init; } = string.Empty;

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
