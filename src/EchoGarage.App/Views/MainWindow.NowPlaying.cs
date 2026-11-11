using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EchoGarage.App.ViewModels;

namespace EchoGarage.App.Views;

/// <summary>
/// Now-playing row centering for both track list views (flat DataGrid and album-grouped
/// ListView). Kept in a separate partial file so the cover-loading code in
/// MainWindow.xaml.cs and the scrolling helper can evolve independently.
/// </summary>
public partial class MainWindow
{
    private bool _centerCurrentRowPending;

    /// <summary>
    /// Schedules centering of the currently playing row. Deferred to the next dispatcher
    /// turn because the row container does not exist before the list has laid out (playback
    /// start, playlist switch and layout rebuild all raise the request right away).
    /// </summary>
    private void ScheduleCenterCurrentRow()
    {
        var row = _vm.CurrentRow;
        if (row is null || _centerCurrentRowPending || !IsCurrentRowBrowsable(row))
        {
            return;
        }

        _centerCurrentRowPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _centerCurrentRowPending = false;
            CenterCurrentRow();
        }));
    }

    /// <summary>True when the current row belongs to the list that is being browsed.</summary>
    private bool IsCurrentRowBrowsable(TrackRowViewModel row) =>
        _vm.IsGroupedViewActive ? GroupedList.Items.Contains(row) : TrackGrid.Items.Contains(row);

    private void CenterCurrentRow()
    {
        var row = _vm.CurrentRow;
        if (row is null)
        {
            return;
        }

        if (_vm.IsGroupedViewActive)
        {
            if (GroupedList.Items.Contains(row))
            {
                CenterItem(GroupedList, row, () => FindGroupedItemContainer(GroupedList, row), GroupedList.ScrollIntoView);
            }

            return;
        }

        if (TrackGrid.Items.Contains(row))
        {
            CenterItem(TrackGrid, row, () => TrackGrid.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement,
                TrackGrid.ScrollIntoView);
        }
    }

    /// <summary>
    /// Scrolls <paramref name="item"/> to the vertical center of the list viewport.
    /// <para>
    /// ScrollIntoView is only guaranteed to make the row visible (often at the top or bottom
    /// edge), so it is followed by a centered BringIntoView rect and, when the list scrolls in
    /// item units (grouped ListView), a pixel-based correction of the remaining offset.
    /// </para>
    /// </summary>
    private void CenterItem(ItemsControl items, object item, Func<FrameworkElement?> containerLookup,
        Action<object> scrollIntoView)
    {
        try
        {
            scrollIntoView(item);
            items.UpdateLayout();

            var container = containerLookup();
            var scrollViewer = FindScrollViewer(items);
            var presenter = scrollViewer is null ? null : FindScrollContentPresenter(scrollViewer);
            if (container is null || scrollViewer is null || presenter is null)
            {
                return;
            }

            var viewportPx = presenter.ActualHeight;
            if (viewportPx <= 0 || container.ActualHeight <= 0 || scrollViewer.ViewportHeight <= 0)
            {
                return;
            }

            // A rect that reaches half a viewport above the row makes BringIntoView center it
            // instead of just making it visible (rect coordinates are relative to the row).
            var rectTop = -(viewportPx - container.ActualHeight) / 2;
            container.BringIntoView(
                new Rect(0, rectTop, Math.Max(1, container.ActualWidth), container.ActualHeight));
            items.UpdateLayout();

            // Scroll units are pixels for the DataGrid (ScrollUnit=Pixel) but items for the
            // grouped ListView; derive the pixel size of one scroll unit from the viewport.
            var pixelsPerUnit = viewportPx / scrollViewer.ViewportHeight;
            var offsetBefore = scrollViewer.VerticalOffset;

            // Container positions are measured against the ScrollContentPresenter (the viewport)
            // because virtualizing panels lay their realized children out relative to it.
            double MeasureCenterErrorPx()
            {
                var current = containerLookup();
                if (current is null)
                {
                    return double.NaN;
                }

                var topPx = current.TransformToAncestor(presenter).Transform(new Point(0, 0)).Y;
                return topPx + current.ActualHeight / 2 - presenter.ActualHeight / 2;
            }

            var errorBefore = MeasureCenterErrorPx();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var centerErrorPx = MeasureCenterErrorPx();
                if (double.IsNaN(centerErrorPx) || Math.Abs(centerErrorPx) <= 2)
                {
                    break;
                }

                var targetOffset = Math.Clamp(
                    scrollViewer.VerticalOffset + centerErrorPx / pixelsPerUnit,
                    0,
                    scrollViewer.ScrollableHeight);
                if (Math.Abs(targetOffset - scrollViewer.VerticalOffset) < 0.01)
                {
                    break;
                }

                scrollViewer.ScrollToVerticalOffset(targetOffset);
                items.UpdateLayout();
            }

            var errorAfter = MeasureCenterErrorPx();

            _host.Logger.Debug(
                $"Centered now-playing row: view={(_vm.IsGroupedViewActive ? "grouped" : "flat")} " +
                $"offset={offsetBefore:F1}->{scrollViewer.VerticalOffset:F1} " +
                $"error={errorBefore:F1}->{errorAfter:F1}px " +
                $"viewport={viewportPx:F0}px unit={pixelsPerUnit:F1}px " +
                $"extent={scrollViewer.ExtentHeight:F1} viewportUnits={scrollViewer.ViewportHeight:F1}");
        }
        catch (Exception ex)
        {
            _host.Logger.Debug($"Centering the current row failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The grouped ListView generates GroupItem containers at the top level, so the row's
    /// ListViewItem is not reachable through ContainerFromItem; look it up by DataContext.
    /// </summary>
    private static FrameworkElement? FindGroupedItemContainer(ItemsControl list, object item)
    {
        if (list.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement direct)
        {
            return direct;
        }

        return FindItemContainerByDataContext(list, item);
    }

    private static FrameworkElement? FindItemContainerByDataContext(DependencyObject root, object item)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ListViewItem listItem && ReferenceEquals(listItem.DataContext, item))
            {
                return listItem;
            }

            var nested = FindItemContainerByDataContext(child, item);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            var nested = FindScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static ScrollContentPresenter? FindScrollContentPresenter(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollContentPresenter presenter)
            {
                return presenter;
            }

            var nested = FindScrollContentPresenter(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
