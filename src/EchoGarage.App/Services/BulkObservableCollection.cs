using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace EchoGarage.App.Services;

/// <summary>
/// Observable collection optimized for replacing its whole content in one shot. The stock
/// <see cref="ObservableCollection{T}"/> raises one event per item for Clear + Add, which forces
/// every bound collection view (and WPF's item container generator) to redo its work N times when
/// a playlist is switched. <see cref="ReplaceAll"/> swaps the content and raises a single Reset.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Replaces all items with <paramref name="items"/> using a single Reset notification.
    /// The collection itself may be passed in as the source.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // Materialize before touching Items: the caller may pass this very collection, and a lazy
        // sequence is expected to be fully evaluated before the notification fires.
        var buffer = items as IList<T> ?? items.ToList();
        if (ReferenceEquals(buffer, this))
        {
            buffer = this.ToList();
        }

        CheckReentrancy();
        Items.Clear();
        foreach (var item in buffer)
        {
            Items.Add(item);
        }

        // The same notifications ObservableCollection raises for Clear(), but with one Reset
        // instead of one CollectionChanged per removed item, so a switch is one atomic refresh.
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
