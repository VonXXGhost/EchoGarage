using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;

namespace EchoGarage.App.ViewModels;

/// <summary>
/// Orders track rows by the same property/direction pairs that used to be handed to
/// <see cref="System.Windows.Data.ListCollectionView.SortDescriptions"/>.
///
/// WPF sorts a collection view on the dispatcher as part of its refresh; with a large library that
/// single refresh blocks the UI for hundreds of milliseconds (it was the dominant cost of a source
/// switch and prevented the loading overlay from painting while it ran). Sorting the rows up front
/// on a background thread and leaving the views without sort descriptors keeps the order identical
/// while the dispatcher stays free to render the overlay and its spinner.
///
/// Property getters are compiled once per property name; the sort itself is a stable sort over
/// pre-extracted keys so ties keep their original (database) order.
/// </summary>
internal static class TrackRowSorter
{
    private static readonly Dictionary<string, Func<TrackRowViewModel, object?>> Getters = CreateGetters();

    /// <summary>
    /// Returns a new list with the rows ordered by <paramref name="descriptions"/>. Rows whose
    /// property is not known (defensive: sort descriptors only ever use row view model properties)
    /// are left at their original position relative to each other.
    /// </summary>
    public static List<TrackRowViewModel> Sort(
        IReadOnlyList<TrackRowViewModel> rows,
        IReadOnlyList<SortDescription> descriptions)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(descriptions);

        var result = new List<TrackRowViewModel>(rows);
        if (result.Count <= 1 || descriptions.Count == 0)
        {
            return result;
        }

        var keys = new (Func<TrackRowViewModel, object?> Getter, ListSortDirection Direction)[descriptions.Count];
        var keyCount = 0;
        foreach (var description in descriptions)
        {
            if (Getters.TryGetValue(description.PropertyName, out var getter))
            {
                keys[keyCount++] = (getter, description.Direction);
            }
        }

        if (keyCount == 0)
        {
            return result;
        }

        if (keyCount != keys.Length)
        {
            Array.Resize(ref keys, keyCount);
        }

        var entries = new Entry[result.Count];
        for (var index = 0; index < result.Count; index++)
        {
            var row = result[index];
            var values = new object?[keys.Length];
            for (var keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                values[keyIndex] = keys[keyIndex].Getter(row);
            }

            entries[index] = new Entry(index, row, values);
        }

        Array.Sort(entries, (left, right) => Compare(left, right, keys));
        for (var index = 0; index < entries.Length; index++)
        {
            result[index] = entries[index].Row;
        }

        return result;
    }

    private static int Compare(Entry left, Entry right, (Func<TrackRowViewModel, object?> Getter, ListSortDirection Direction)[] keys)
    {
        for (var keyIndex = 0; keyIndex < keys.Length; keyIndex++)
        {
            var compare = CompareValues(left.Values[keyIndex], right.Values[keyIndex]);
            if (compare == 0)
            {
                continue;
            }

            return keys[keyIndex].Direction == ListSortDirection.Descending ? -compare : compare;
        }

        // Stable sort: equal keys keep their original order.
        return left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static int CompareValues(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        if (left.GetType() == right.GetType() && left is IComparable comparable)
        {
            return comparable.CompareTo(right);
        }

        return string.Compare(left.ToString(), right.ToString(), StringComparison.CurrentCulture);
    }

    private static Dictionary<string, Func<TrackRowViewModel, object?>> CreateGetters()
    {
        var getters = new Dictionary<string, Func<TrackRowViewModel, object?>>(StringComparer.Ordinal);
        foreach (var property in typeof(TrackRowViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var parameter = Expression.Parameter(typeof(TrackRowViewModel), "row");
            var body = Expression.Convert(Expression.Property(parameter, property), typeof(object));
            getters[property.Name] = Expression.Lambda<Func<TrackRowViewModel, object?>>(body, parameter).Compile();
        }

        return getters;
    }

    private readonly record struct Entry(
        int OriginalIndex,
        TrackRowViewModel Row,
        object?[] Values);
}
