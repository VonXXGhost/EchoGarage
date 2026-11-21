using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    private const int MaxCenterDeferAttempts = 30;
    private const int MaxCenterCorrectionAttempts = 5;

    private bool _centerCurrentRowPending;

    /// <summary>右侧列表的封面补加载是否已排队（滚动事件很密，合并成一次 dispatcher 回调）。</summary>
    private bool _nowPlayingCoverLoadQueued;

    /// <summary>已打开的封面浏览窗口（非模态、由主窗口拥有）；同一时间只保留一个实例。</summary>
    private CoverViewerWindow? _coverViewer;

    /// <summary>
    /// Schedules centering of the currently playing row. Deferred to the next dispatcher
    /// turn because the row container does not exist before the list has laid out (playback
    /// start, playlist switch and layout rebuild all raise the request right away).
    /// </summary>
    /// <remarks>
    /// While the user is dragging the scrollbar (or holding one of its buttons) the centering
    /// is postponed instead of fighting the gesture: forcing a scroll offset in the middle of
    /// a scrollbar drag can make the ScrollViewer's internal anchoring retry forever, which is
    /// experienced as a list that jitters by a few pixels and no longer follows the mouse wheel.
    /// </remarks>
    private void ScheduleCenterCurrentRow()
    {
        if (_centerCurrentRowPending)
        {
            return;
        }

        var row = _vm.CurrentRow;
        if (row is null || !IsCurrentRowBrowsable(row))
        {
            return;
        }

        _centerCurrentRowPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ScheduleCenterAttemptAsync(0)));
    }

    private async void ScheduleCenterAttemptAsync(int attempt)
    {
        try
        {
            if (!_centerCurrentRowPending)
            {
                return;
            }

            if (IsScrollbarInteractionActive())
            {
                if (attempt < MaxCenterDeferAttempts)
                {
                    _host.Logger.Debug($"Auto-center deferred: user is scrolling (attempt {attempt + 1}).");
                    await Task.Delay(200);
                    ScheduleCenterAttemptAsync(attempt + 1);
                    return;
                }

                _centerCurrentRowPending = false;
                _host.Logger.Debug("Auto-center skipped: the user kept interacting with the scrollbar.");
                return;
            }

            _centerCurrentRowPending = false;
            CenterCurrentRow();
        }
        catch (Exception ex)
        {
            _centerCurrentRowPending = false;
            _host.Logger.Debug($"Auto-center attempt failed: {ex.Message}");
        }
    }

    /// <summary>True while the left mouse button is held on a scrollbar (thumb drag or page button).</summary>
    private static bool IsScrollbarInteractionActive()
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            return false;
        }

        if (Mouse.Captured is not DependencyObject captured)
        {
            return false;
        }

        for (var node = captured; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollBar)
            {
                return true;
            }

            if (node is Window)
            {
                return false;
            }
        }

        return false;
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
    /// ScrollIntoView only guarantees that the row becomes visible (often at the top or bottom
    /// edge); the remaining distance to the viewport center is then corrected by setting the
    /// pixel offset directly. Deliberately no synthetic <see cref="FrameworkElement.BringIntoView(Rect)"/>
    /// call with an oversized rectangle: it makes ScrollViewer/VirtualizingStackPanel re-post
    /// MakeVisible requests while their layout estimates change, which can turn into a
    /// scrollbar that keeps fighting the user.
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
            if (container is null)
            {
                // One more layout turn: virtualized containers are sometimes generated only
                // after the first arrange pass that ScrollIntoView triggered.
                items.UpdateLayout();
                container = containerLookup();
            }

            var scrollViewer = FindScrollViewer(items);
            var presenter = scrollViewer is null ? null : FindScrollContentPresenter(scrollViewer);
            if (container is null || scrollViewer is null || presenter is null)
            {
                return;
            }

            if (presenter.ActualHeight <= 0 || container.ActualHeight <= 0 || scrollViewer.ViewportHeight <= 0)
            {
                return;
            }

            var offsetBefore = scrollViewer.VerticalOffset;

            // Scroll units are pixels for both lists (VirtualizingPanel.ScrollUnit=Pixel on the
            // flat DataGrid and the grouped ListView); derive the size of one unit from the
            // viewport anyway so the math stays correct if a unit ever changes.
            var pixelsPerUnit = presenter.ActualHeight / scrollViewer.ViewportHeight;

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
            for (var attempt = 0; attempt < MaxCenterCorrectionAttempts; attempt++)
            {
                var centerErrorPx = MeasureCenterErrorPx();
                if (double.IsNaN(centerErrorPx) || Math.Abs(centerErrorPx) <= 1.0)
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
                $"viewport={presenter.ActualHeight:F0}px unit={pixelsPerUnit:F1}px " +
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

    /// <summary>
    /// Double-clicking the status-bar now-playing text jumps to then centers the playing row of
    /// the list that is currently browsed (owner request, 2026-11-12). When the playing track is
    /// not part of the current list a hint is shown instead of silently doing nothing.
    /// </summary>
    private void OnNowPlayingStatusMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2)
        {
            return;
        }

        e.Handled = true;

        var row = _vm.CurrentRow;
        if (row is null)
        {
            _host.Logger.Debug("Now-playing status double-click: nothing is playing.");
            _vm.SetStatus("当前没有正在播放的歌曲。");
            return;
        }

        if (!IsCurrentRowBrowsable(row))
        {
            _host.Logger.Debug($"Now-playing status double-click: playing row is not in the current list (row={row.Track.Title}).");
            _vm.SetStatus("正在播放的歌曲不在当前列表中。");
            return;
        }

        _host.Logger.Debug($"Now-playing status double-click: centering the playing row (row={row.Track.Title}).");

        // Same path as the automatic centering after a track change: deferred to the next
        // dispatcher turn and postponed while the user is dragging a scrollbar.
        ScheduleCenterCurrentRow();
    }

    /// <summary>
    /// 双击右栏“正在播放”封面打开大图浏览窗口（owner request）。没有封面时保持静默，
    /// 避免弹出空窗口；窗口已打开时只激活，不重复创建（可重复打开/关闭）。
    /// </summary>
    private void OnNowPlayingCoverMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2)
        {
            return;
        }

        e.Handled = true;

        if (_vm.CurrentCover is null)
        {
            _host.Logger.Debug("Cover viewer: double-click ignored because there is no current cover.");
            return;
        }

        if (_coverViewer is { IsLoaded: true })
        {
            if (_coverViewer.WindowState == WindowState.Minimized)
            {
                _coverViewer.WindowState = WindowState.Normal;
            }

            _coverViewer.Activate();
            return;
        }

        var viewer = new CoverViewerWindow(_vm, _host.Covers, _host.Logger) { Owner = this };
        viewer.Closed += OnCoverViewerClosed;
        _coverViewer = viewer;
        _host.Logger.Debug($"Cover viewer opened for '{_vm.CurrentRow?.Track.Path ?? "unknown"}'.");
        viewer.Show();
    }

    /// <summary>
    /// 「正在播放」页右侧列表的行容器生成后按需加载缩略图。这里不做 Unloaded 时的清理：
    /// 行对象与播放列表页共用，清掉封面会让切回列表页时短暂变空（列表页的行自己会清理）。
    /// </summary>
    private void OnNowPlayingPageItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListViewItem { DataContext: TrackRowViewModel row })
        {
            _ = _vm.LoadRowCoverAsync(row);
        }
    }

    /// <summary>
    /// 列表使用 Recycling 虚拟化，滚动时容器被复用、Loaded 不会再次触发；已滑出并复用回来的
    /// 行因此拿不到缩略图。滚动 / 容器生成状态变化时合并成一次回调，补加载当前已实化的行。
    /// </summary>
    private void OnNowPlayingPageListScrollChanged(object? sender, ScrollChangedEventArgs e) =>
        QueueVisibleNowPlayingCoverLoad();

    private void OnNowPlayingPageListGeneratorStatusChanged(object? sender, EventArgs e)
    {
        if (NowPlayingPageList.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
        {
            QueueVisibleNowPlayingCoverLoad();
        }
    }

    private void QueueVisibleNowPlayingCoverLoad()
    {
        if (!_vm.IsNowPlayingPageActive || _nowPlayingCoverLoadQueued)
        {
            return;
        }

        _nowPlayingCoverLoadQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _nowPlayingCoverLoadQueued = false;
            LoadVisibleNowPlayingCovers();
        }));
    }

    /// <summary>对右侧列表当前已实化（在视口内）的行补加载缩略图。</summary>
    private void LoadVisibleNowPlayingCovers()
    {
        if (!_vm.IsNowPlayingPageActive)
        {
            return;
        }

        if (FindDescendant<VirtualizingStackPanel>(NowPlayingPageList) is not { } panel)
        {
            return;
        }

        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is ListViewItem { DataContext: TrackRowViewModel row } &&
                row.CoverImage is null)
            {
                _ = _vm.LoadRowCoverAsync(row);
            }
        }
    }

    /// <summary>双击「正在播放」页右侧列表的一行即播放该曲目。</summary>
    private void OnNowPlayingPageListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (NowPlayingPageList.SelectedItem is TrackRowViewModel row)
        {
            e.Handled = true;
            _ = _vm.PlayRowCommand.ExecuteAsync(row);
        }
    }

    /// <summary>把「正在播放」页右侧列表的当前播放行滚动到视口中央。</summary>
    private void OnNowPlayingLocateClick(object sender, RoutedEventArgs e) => CenterNowPlayingListRow();

    /// <summary>
    /// 「正在播放」页的标签属性入口只针对当前播放曲目，避免误用播放列表页的多选状态。
    /// </summary>
    private void OnNowPlayingEditTagsClick(object sender, RoutedEventArgs e)
    {
        _ = _vm.EditCurrentTrackTagsAsync();
    }

    private void OnNowPlayingPageSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyNowPlayingPageMetrics(e.NewSize);

    /// <summary>
    /// 「正在播放」页在窄 / 矮窗口下按可用尺寸收放封面、标题与频谱，避免布局被裁切。
    /// 尺寸以页面实际 DIP 布局尺寸为准（不依赖屏幕 DPI）：宽窗口用 300 封面（设计稿尺寸），
    /// 窄窗口退到 250、矮窗口退到 210 并缩小标题；标签卡片始终跟随是否在播放。
    /// </summary>
    private void ApplyNowPlayingPageMetrics(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var narrow = size.Width < 1240;
        var shortHeight = size.Height < 560;
        // 低于 520 DIP 时标签卡片（约 200 DIP）会把频谱挤到视口外，优先保证封面与频谱可见。
        var hideTagCard = size.Height < 520;
        var cover = shortHeight ? 210 : narrow ? 250 : 300;

        NowPlayingCoverFrame.Width = cover;
        NowPlayingCoverFrame.Height = cover;
        NowPlayingCoverFramePlaceholder.Width = cover;
        NowPlayingCoverFramePlaceholder.Height = cover;

        NowPlayingPageTitle.FontSize = shortHeight ? 22 : narrow ? 25 : 30;
        NowPlayingTagCardHost.Visibility = _vm.HasCurrentTrack && !hideTagCard
            ? Visibility.Visible
            : Visibility.Collapsed;
        NowPlayingVizCard.MinHeight = shortHeight ? 60 : 120;

        _host.Logger.Debug(
            $"Now-playing page metrics: size={size.Width:F0}x{size.Height:F0}, cover={cover}, vizMin={NowPlayingVizCard.MinHeight}, tagCard={NowPlayingTagCardHost.Visibility}");
    }

    private void OnCoverViewerClosed(object? sender, EventArgs e)
    {
        if (sender is CoverViewerWindow viewer)
        {
            viewer.Closed -= OnCoverViewerClosed;
        }

        _coverViewer = null;
    }
}
