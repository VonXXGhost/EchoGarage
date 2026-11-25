using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EchoGarage.App.ViewModels;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

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
    private TrackRowViewModel? _centerTargetRow;

    /// <summary>右侧列表的封面补加载是否已排队（滚动事件很密，合并成一次 dispatcher 回调）。</summary>
    private bool _nowPlayingCoverLoadQueued;

    /// <summary>已打开的封面浏览窗口（非模态、由主窗口拥有）；同一时间只保留一个实例。</summary>
    private CoverViewerWindow? _coverViewer;

    /// <summary>BPM 内联修正浮层内的 Tap 轻点测速计算器（ADR-034）。</summary>
    private readonly TapTempoCalculator _tapTempo = new();
    private bool _savingBpmEdit;

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
        var row = _vm.CurrentRow;
        if (row is null || !IsRowBrowsable(row))
        {
            return;
        }

        ScheduleCenterRow(row);
    }

    /// <summary>
    /// Schedules centering of a row in the browsed track list. Used both when playback starts
    /// and when the startup session restoration selects the last played row.
    /// </summary>
    private void ScheduleCenterRow(TrackRowViewModel row)
    {
        _centerTargetRow = row;
        if (_centerCurrentRowPending)
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
                _centerTargetRow = null;
                _host.Logger.Debug("Auto-center skipped: the user kept interacting with the scrollbar.");
                return;
            }

            var target = _centerTargetRow;
            _centerCurrentRowPending = false;
            _centerTargetRow = null;
            if (target is not null)
            {
                CenterRow(target);
            }
        }
        catch (Exception ex)
        {
            _centerCurrentRowPending = false;
            _centerTargetRow = null;
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

    /// <summary>True when the row belongs to the list that is being browsed.</summary>
    private bool IsRowBrowsable(TrackRowViewModel row) =>
        _vm.IsGroupedViewActive ? GroupedList.Items.Contains(row) : TrackGrid.Items.Contains(row);

    private void CenterRow(TrackRowViewModel? row)
    {
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

        if (!IsRowBrowsable(row))
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

    // =====================================================================
    // 「正在播放」页 BPM 测量、内联修正与 Tap 轻点测速（ADR-034）
    // =====================================================================

    private void OnNowPlayingMeasureBpmClick(object sender, RoutedEventArgs e)
    {
        CloseBpmPopup();
        _ = _vm.ToggleMeasureCurrentTrackBpmAsync();
    }

    private void CloseBpmPopup()
    {
        if (NowPlayingBpmPopup.IsOpen)
        {
            NowPlayingBpmPopup.IsOpen = false;
        }

        _tapTempo.Reset();
    }

    private void OnNowPlayingBpmClick(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentRow?.Track is not { } track)
        {
            return;
        }

        if (NowPlayingBpmPopup.IsOpen)
        {
            NowPlayingBpmPopup.IsOpen = false;
            return;
        }

        PopulateBpmPopup(track, _vm.NowPlayingBpmState);
        NowPlayingBpmPopup.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (NowPlayingBpmPopup.IsOpen)
            {
                BpmEditBox.Focus();
                BpmEditBox.SelectAll();
            }
        }));
    }

    private void PopulateBpmPopup(EchoGarage.Core.Models.Track track, BpmDisplayState state)
    {
        _tapTempo.Reset();
        var dp = _vm.Settings.Bpm.DecimalPlaces;
        BpmEditBox.Text = BpmFormatter.FormatInput(state.EffectiveBpm, dp);
        BpmPopupSourceText.Text = BuildBpmPopupSourceSummary(state, dp);
        PopulateBpmSegmentsSection(track, state, dp);
        BpmTapStatusText.Text = "跟着节拍连续点击或敲空格 / T（4 拍后趋稳，停顿 2.5 秒自动重计）。";
        ClearBpmEditError();

        // 候选值筹码：包含算法候选 + 与文件原标签不同的实测/原值，方便一键填入
        BpmCandidatesPanel.Children.Clear();
        var chips = BuildBpmCandidateChips(state, dp);
        foreach (var (label, value) in chips)
        {
            var chip = new Button
            {
                Content = label,
                Style = (Style)FindResource("FlatButton"),
                BorderBrush = (Brush)FindResource("BorderStrongBrush"),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 4),
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 11.5,
                Tag = value
            };
            chip.Click += OnBpmCandidateChipClick;
            BpmCandidatesPanel.Children.Add(chip);
        }

        BpmCandidatesRow.Visibility = chips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 写回音频文件复选框：默认遵循设置；不支持容器标签的格式（如裸 .aac）禁用并提示
        var ext = System.IO.Path.GetExtension(track.Path);
        var writableFormat = !ext.Equals(".aac", StringComparison.OrdinalIgnoreCase);
        BpmWriteToFileCheck.IsEnabled = writableFormat;
        BpmWriteToFileCheck.IsChecked = writableFormat && _vm.Settings.Bpm.WriteToFileByDefault;
        UpdateBpmFormatHint(track.Path, writableFormat);

        // 已有手动修正时提供“恢复原值/清除修正”快捷按钮
        if (state.ManualBpm.HasValue)
        {
            BpmClearManualButton.Content = state.FileTagBpm.HasValue
                ? "恢复文件值"
                : state.MeasuredBpm.HasValue ? "恢复测量值" : "清除修正";
            BpmClearManualButton.Visibility = Visibility.Visible;
        }
        else
        {
            BpmClearManualButton.Visibility = Visibility.Collapsed;
        }
    }

    private static string BuildBpmPopupSourceSummary(BpmDisplayState state, int decimalPlaces)
    {
        var parts = new List<string>();
        switch (state.Source)
        {
            case BpmSource.FileTag:
                parts.Add($"当前值来自文件标签（{BpmFormatter.FormatDisplay(state.FileTagBpm, decimalPlaces)}）");
                break;
            case BpmSource.CachedMeasured:
                var conf = BpmFormatter.FormatConfidenceLabel(state.Confidence);
                parts.Add(string.IsNullOrEmpty(conf)
                    ? "当前值来自本地测量缓存"
                    : $"当前值来自本地测量缓存（置信度：{conf}）");
                break;
            case BpmSource.CachedManual:
                parts.Add("当前值为本地手动修正（清空缓存时保留）");
                break;
            default:
                parts.Add("尚无 BPM 数据，可直接输入或轻点测速");
                break;
        }

        if (state.MeasuredBpm.HasValue && state.Source != BpmSource.CachedMeasured)
        {
            parts.Add($"算法实测 {BpmFormatter.FormatDisplay(state.MeasuredBpm, decimalPlaces)}");
        }

        if (state.FileTagBpm.HasValue && state.Source != BpmSource.FileTag)
        {
            parts.Add($"文件原标签 {BpmFormatter.FormatDisplay(state.FileTagBpm, decimalPlaces)}");
        }

        if (state.TooManyTempoChanges)
        {
            parts.Add($"变速过多（>{BpmFormatter.MaxDisplaySegments}段）");
        }
        else if (state.Segments.Count is >= 2 and <= BpmFormatter.MaxDisplaySegments)
        {
            parts.Add($"共 {state.Segments.Count} 个速度段落");
        }
        else if (state.IsVariableTempoSuspected)
        {
            parts.Add("疑似变速曲目");
        }

        if (state.DurationChangedWarning)
        {
            parts.Add("音频时长已变化，建议重测");
        }

        return string.Join(" · ", parts);
    }

    private static List<(string Label, double Value)> BuildBpmCandidateChips(BpmDisplayState state, int decimalPlaces)
    {
        var result = new List<(string Label, double Value)>();
        void TryAdd(double bpm, string? suffix = null)
        {
            if (!BpmFormatter.IsValid(bpm))
            {
                return;
            }

            if (result.Any(r => Math.Abs(r.Value - bpm) < 0.15))
            {
                return;
            }

            var text = BpmFormatter.FormatDisplay(bpm, decimalPlaces);
            result.Add((string.IsNullOrEmpty(suffix) ? text : $"{text} {suffix}", bpm));
        }

        if (state.MeasuredBpm is { } measured && state.Source != BpmSource.CachedMeasured)
        {
            TryAdd(measured, "实测");
        }

        if (state.FileTagBpm is { } fileTag && state.Source != BpmSource.FileTag)
        {
            TryAdd(fileTag, "文件");
        }

        foreach (var candidate in state.Candidates)
        {
            TryAdd(candidate.Bpm);
            if (result.Count >= 5)
            {
                break;
            }
        }

        return result;
    }

    private void UpdateBpmFormatHint(string filePath, bool writableFormat)
    {
        if (!writableFormat)
        {
            BpmFormatHintText.Text = "当前格式（.aac）不支持写入音频文件标签，仅保存到本地缓存。";
            BpmFormatHintText.Visibility = Visibility.Visible;
            return;
        }

        if (BpmWriteToFileCheck.IsChecked != true)
        {
            BpmFormatHintText.Visibility = Visibility.Collapsed;
            return;
        }

        if (BpmFormatter.RequiresIntegerTag(filePath))
        {
            BpmFormatHintText.Text = "M4A / MP4 标准 tmpo 标签仅支持整数，写入文件时会自动四舍五入取整。";
            BpmFormatHintText.Visibility = Visibility.Visible;
            return;
        }

        var ext = System.IO.Path.GetExtension(filePath);
        if (_vm.Settings.Bpm.RoundId3BpmToInteger &&
            (ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
             ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)))
        {
            BpmFormatHintText.Text = "已开启 ID3 整数兼容选项，写入 TBPM 标签时将四舍五入为整数。";
            BpmFormatHintText.Visibility = Visibility.Visible;
            return;
        }

        BpmFormatHintText.Visibility = Visibility.Collapsed;
    }

    private void OnBpmWriteToFileCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentRow?.Track is { } track)
        {
            UpdateBpmFormatHint(track.Path, BpmWriteToFileCheck.IsEnabled);
        }
    }

    private void OnBpmCandidateChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: double value })
        {
            BpmEditBox.Text = BpmFormatter.FormatInput(value, _vm.Settings.Bpm.DecimalPlaces);
            ClearBpmEditError();
        }
    }

    private void OnBpmHalveClick(object sender, RoutedEventArgs e) => ApplyBpmScale(halve: true);

    private void OnBpmDoubleClick(object sender, RoutedEventArgs e) => ApplyBpmScale(halve: false);

    private void ApplyBpmScale(bool halve)
    {
        if (!BpmFormatter.TryParse(BpmEditBox.Text, out var current))
        {
            ShowBpmEditError("请先输入有效 BPM（20.0 – 300.0），或先轻点测速。");
            return;
        }

        var ok = halve
            ? BpmFormatter.TryHalve(current, out var scaled)
            : BpmFormatter.TryDouble(current, out scaled);
        if (!ok)
        {
            ShowBpmEditError(halve
                ? $"折半后低于最小范围（{BpmFormatter.MinBpm:0} BPM）。"
                : $"翻倍后超出最大范围（{BpmFormatter.MaxBpm:0} BPM）。");
            return;
        }

        BpmEditBox.Text = BpmFormatter.FormatInput(scaled, _vm.Settings.Bpm.DecimalPlaces);
        ClearBpmEditError();
    }

    private void OnBpmTapClick(object sender, RoutedEventArgs e) => RecordBpmTap();

    private void OnBpmTapResetClick(object sender, RoutedEventArgs e)
    {
        _tapTempo.Reset();
        BpmTapStatusText.Text = "已重置，跟着节拍重新点击或按空格 / T。";
        ClearBpmEditError();
    }

    private void RecordBpmTap()
    {
        var snapshot = _tapTempo.TapNow();
        ClearBpmEditError();

        if (snapshot.Bpm is { } bpm)
        {
            var dp = _vm.Settings.Bpm.DecimalPlaces;
            BpmEditBox.Text = BpmFormatter.FormatInput(bpm, dp);
            var formatted = BpmFormatter.FormatDisplay(bpm, dp);
            var suffix = snapshot.IsStable ? "（已稳定，可点保存）" : "（继续轻点更准）";
            var resetHint = snapshot.WasReset ? " · 已自动重计" : string.Empty;
            BpmTapStatusText.Text = $"已敲击 {snapshot.TapCount} 次 → {formatted} BPM {suffix}{resetHint}";
        }
        else
        {
            BpmTapStatusText.Text = snapshot.WasReset
                ? "间隔较长已重计：第 1 拍，请继续敲击…"
                : $"已记录第 {snapshot.TapCount} 拍，请继续按节拍敲击…";
        }
    }

    private void OnBpmPopupPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            NowPlayingBpmPopup.IsOpen = false;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.FocusedElement is not Button)
        {
            e.Handled = true;
            _ = SaveBpmEditFromPopupAsync();
            return;
        }

        // 浮层内按空格或 T 直接触发 Tap 轻点测速（BPM 输入框纯数字不需要空格/字母；
        // 焦点在复选框时仍保留空格勾选行为；忽略长按自动重复避免污染节奏）。
        var isTapKey = (e.Key == Key.Space && Keyboard.FocusedElement is not CheckBox) ||
                       (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.None);
        if (isTapKey)
        {
            e.Handled = true;
            if (!e.IsRepeat)
            {
                RecordBpmTap();
            }
        }
    }

    private void OnBpmPopupClosed(object? sender, EventArgs e)
    {
        _tapTempo.Reset();
    }

    private async void OnBpmSaveClick(object sender, RoutedEventArgs e)
    {
        await SaveBpmEditFromPopupAsync();
    }

    private async void OnBpmClearManualClick(object sender, RoutedEventArgs e)
    {
        if (_savingBpmEdit)
        {
            return;
        }

        _savingBpmEdit = true;
        try
        {
            var writeToFile = BpmWriteToFileCheck.IsEnabled && BpmWriteToFileCheck.IsChecked == true;
            if (await _vm.SaveCurrentTrackBpmAsync(null, writeToFile))
            {
                NowPlayingBpmPopup.IsOpen = false;
            }
        }
        finally
        {
            _savingBpmEdit = false;
        }
    }

    private void OnBpmCancelClick(object sender, RoutedEventArgs e)
    {
        NowPlayingBpmPopup.IsOpen = false;
    }

    private async Task SaveBpmEditFromPopupAsync()
    {
        if (_savingBpmEdit)
        {
            return;
        }

        var raw = BpmEditBox.Text.Trim();
        double? parsed = null;
        if (raw.Length > 0)
        {
            if (!BpmFormatter.TryParse(raw, out var value))
            {
                ShowBpmEditError($"BPM 需为 {BpmFormatter.MinBpm:0} – {BpmFormatter.MaxBpm:0} 之间的数值（可保留 1 位小数），或留空清除。");
                return;
            }

            parsed = value;
        }

        _savingBpmEdit = true;
        BpmSaveButton.IsEnabled = false;
        try
        {
            var writeToFile = BpmWriteToFileCheck.IsEnabled && BpmWriteToFileCheck.IsChecked == true;
            if (await _vm.SaveCurrentTrackBpmAsync(parsed, writeToFile))
            {
                NowPlayingBpmPopup.IsOpen = false;
            }
        }
        finally
        {
            BpmSaveButton.IsEnabled = true;
            _savingBpmEdit = false;
        }
    }

    private void ShowBpmEditError(string message)
    {
        BpmEditErrorText.Text = message;
        BpmEditErrorText.Visibility = Visibility.Visible;
    }

    private void ClearBpmEditError()
    {
        BpmEditErrorText.Text = string.Empty;
        BpmEditErrorText.Visibility = Visibility.Collapsed;
    }

    // 全部使用已定义主题令牌（不硬编码颜色），五套深浅主题下均有清晰层次
    private static readonly string[] BpmSegmentThemeBrushKeys =
    [
        "WaveformHighBrush",
        "WaveformMidBrush",
        "WaveformLowBrush",
        "AccentPressedBrush",
        "TextSecondaryBrush"
    ];

    /// <summary>渲染变速曲目的进度条比例可视化与段落明细列表；超过 5 段时隐藏明细并提示变速过多。</summary>
    private void PopulateBpmSegmentsSection(EchoGarage.Core.Models.Track track, BpmDisplayState state, int decimalPlaces)
    {
        BpmSegmentsBarGrid.Children.Clear();
        BpmSegmentsBarGrid.ColumnDefinitions.Clear();
        BpmSegmentsListPanel.Children.Clear();

        if (state.TooManyTempoChanges)
        {
            BpmSegmentsCard.Visibility = Visibility.Visible;
            BpmSegmentsVisualPanel.Visibility = Visibility.Collapsed;
            BpmSegmentsNoticeText.Text =
                $"检测到曲目变速过于频繁（超过 {BpmFormatter.MaxDisplaySegments} 个速度段落），" +
                "为避免段落过于零碎已隐藏分段明细，仅保留主段落参考速度。";
            BpmSegmentsNoticeText.Visibility = Visibility.Visible;
            return;
        }

        var segments = state.Segments;
        if (segments.Count is >= 2 and <= BpmFormatter.MaxDisplaySegments)
        {
            BpmSegmentsCard.Visibility = Visibility.Visible;
            BpmSegmentsVisualPanel.Visibility = Visibility.Visible;
            BpmSegmentsNoticeText.Visibility = Visibility.Collapsed;

            var totalDuration = Math.Max(
                track.DurationMs / 1000.0,
                segments[^1].EndSeconds);
            if (totalDuration <= 0.1)
            {
                totalDuration = Math.Max(1.0, segments.Sum(s => Math.Max(0.5, s.EndSeconds - s.StartSeconds)));
            }

            BpmSegmentsHeaderText.Text = $"段落速度分布（共 {segments.Count} 段）";
            BpmSegmentsEndTickText.Text = BpmFormatter.FormatTimestamp(totalDuration);

            // 为相近速度段落（±3% 内，如 A-B-A 曲式）复用同一主题色，让时间轴结构一目了然
            var paletteIndices = AssignSegmentPaletteIndices(segments);
            var primaryBpm = segments.FirstOrDefault(s => s.IsPrimary)?.Bpm ?? state.MeasuredBpm ?? state.EffectiveBpm ?? 0;

            BuildProportionalSegmentBar(segments, paletteIndices, totalDuration, decimalPlaces);
            BuildSegmentDetailRows(segments, paletteIndices, totalDuration, primaryBpm, decimalPlaces);
            return;
        }

        if (state.IsVariableTempoSuspected)
        {
            // 旧缓存（升级前记录的变速曲目，尚无分段明细）：引导重测一次即可生成
            BpmSegmentsCard.Visibility = Visibility.Visible;
            BpmSegmentsVisualPanel.Visibility = Visibility.Collapsed;
            BpmSegmentsNoticeText.Text = "检测到该曲目存在变速，重新点击右上角「♩ BPM 测量」即可生成各段落速度分布。";
            BpmSegmentsNoticeText.Visibility = Visibility.Visible;
            return;
        }

        BpmSegmentsCard.Visibility = Visibility.Collapsed;
    }

    private static int[] AssignSegmentPaletteIndices(IReadOnlyList<BpmSegment> segments)
    {
        var indices = new int[segments.Count];
        var groupBpms = new List<double>();
        for (var i = 0; i < segments.Count; i++)
        {
            var bpm = segments[i].Bpm;
            var found = -1;
            for (var g = 0; g < groupBpms.Count; g++)
            {
                var diff = Math.Abs(bpm - groupBpms[g]);
                var minBpm = Math.Min(bpm, groupBpms[g]);
                if (diff < 3.0 || (minBpm > 0 && diff / minBpm < 0.03))
                {
                    found = g;
                    break;
                }
            }

            if (found >= 0)
            {
                indices[i] = found % BpmSegmentThemeBrushKeys.Length;
            }
            else
            {
                var next = groupBpms.Count;
                groupBpms.Add(bpm);
                indices[i] = next % BpmSegmentThemeBrushKeys.Length;
            }
        }

        return indices;
    }

    private void BuildProportionalSegmentBar(
        IReadOnlyList<BpmSegment> segments,
        int[] paletteIndices,
        double totalDuration,
        int decimalPlaces)
    {
        var separatorBrush = ResolveThemeBrush("PopupBackgroundBrush");
        var gapBrush = ResolveThemeBrush("ProgressTrackBrush");
        var monoFont = (FontFamily)FindResource("MonoFont");

        var col = 0;
        var cursor = 0.0;

        void AddGapColumn(double gapStart, double gapEnd)
        {
            var gapDuration = gapEnd - gapStart;
            if (gapDuration <= 1.5)
            {
                return;
            }

            BpmSegmentsBarGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(gapDuration, GridUnitType.Star)
            });
            var gapBlock = new Border
            {
                Background = gapBrush,
                BorderBrush = separatorBrush,
                BorderThickness = col > 0 ? new Thickness(1, 0, 0, 0) : new Thickness(0),
                ToolTip = $"{BpmFormatter.FormatTimeRange(gapStart, gapEnd)}\n无明显节拍 / 休止留白"
            };
            Grid.SetColumn(gapBlock, col++);
            BpmSegmentsBarGrid.Children.Add(gapBlock);
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (seg.StartSeconds > cursor + 1.5)
            {
                AddGapColumn(cursor, seg.StartSeconds);
            }

            var segDuration = Math.Max(0.5, seg.EndSeconds - seg.StartSeconds);
            var ratio = segDuration / totalDuration;
            var fillBrush = ResolveThemeBrush(BpmSegmentThemeBrushKeys[paletteIndices[i]]);
            var labelForeground = ResolveContrastTextBrush(fillBrush);
            var bpmText = BpmFormatter.FormatDisplay(seg.Bpm, decimalPlaces);
            var rangeText = BpmFormatter.FormatTimeRange(seg.StartSeconds, seg.EndSeconds);
            var pctText = $"{Math.Round(ratio * 100):0}%";

            BpmSegmentsBarGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(segDuration, GridUnitType.Star)
            });

            var blockButton = new Button
            {
                Background = fillBrush,
                Foreground = labelForeground,
                BorderBrush = separatorBrush,
                BorderThickness = col > 0 ? new Thickness(1.5, 0, 0, 0) : new Thickness(0),
                Padding = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                FocusVisualStyle = null,
                Tag = seg.StartSeconds,
                ToolTip = $"{rangeText}（占比 {pctText}）\n{bpmText} BPM{(seg.IsPrimary ? " · 主段落" : string.Empty)}\n点击跳转到此处试听",
                Template = CreateSegmentBarBlockTemplate()
            };

            if (ratio >= 0.16)
            {
                blockButton.Content = new TextBlock
                {
                    Text = bpmText,
                    FontFamily = monoFont,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = labelForeground,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
            }

            blockButton.Click += OnBpmSegmentSeekClick;
            Grid.SetColumn(blockButton, col++);
            BpmSegmentsBarGrid.Children.Add(blockButton);
            cursor = Math.Max(cursor, seg.EndSeconds);
        }

        if (totalDuration - cursor > 1.5)
        {
            AddGapColumn(cursor, totalDuration);
        }
    }

    private void BuildSegmentDetailRows(
        IReadOnlyList<BpmSegment> segments,
        int[] paletteIndices,
        double totalDuration,
        double primaryBpm,
        int decimalPlaces)
    {
        var monoFont = (FontFamily)FindResource("MonoFont");
        var flatButtonStyle = (Style)FindResource("FlatButton");
        var primaryTextBrush = ResolveThemeBrush("TextPrimaryBrush");
        var secondaryTextBrush = ResolveThemeBrush("TextSecondaryBrush");
        var mutedTextBrush = ResolveThemeBrush("TextMutedBrush");
        var accentBrush = ResolveThemeBrush("AccentBrush");
        var accentSoftBrush = ResolveThemeBrush("AccentSoftBrush");
        var panelBgBrush = ResolveThemeBrush("PanelBackgroundBrush");
        var borderBrush = ResolveThemeBrush("BorderBrush");
        var activeRowBrush = ResolveThemeBrush("RowSelectedBrush");
        var playPosition = _vm.PositionSeconds;

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            var fillBrush = ResolveThemeBrush(BpmSegmentThemeBrushKeys[paletteIndices[i]]);
            var segDuration = Math.Max(0.0, seg.EndSeconds - seg.StartSeconds);
            var pct = (int)Math.Round(Math.Clamp(segDuration / totalDuration, 0.0, 1.0) * 100);
            var isCurrent = playPosition >= seg.StartSeconds && playPosition < seg.EndSeconds;

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var swatch = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = fillBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };
            Grid.SetColumn(swatch, 0);
            rowGrid.Children.Add(swatch);

            var timeBlock = new TextBlock
            {
                Text = BpmFormatter.FormatTimeRange(seg.StartSeconds, seg.EndSeconds),
                FontFamily = monoFont,
                FontSize = 11,
                Foreground = secondaryTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 94,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(timeBlock, 1);
            rowGrid.Children.Add(timeBlock);

            var valuePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            valuePanel.Children.Add(new TextBlock
            {
                Text = $"{BpmFormatter.FormatDisplay(seg.Bpm, decimalPlaces)} BPM",
                FontFamily = monoFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = primaryTextBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (seg.IsPrimary)
            {
                valuePanel.Children.Add(new Border
                {
                    Background = accentSoftBrush,
                    BorderBrush = accentBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 0, 4, 0),
                    Margin = new Thickness(5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "主",
                        FontSize = 9.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = accentBrush
                    }
                });
            }
            else
            {
                var relation = BpmFormatter.FormatRelativeTempoRelation(seg.Bpm, primaryBpm);
                if (relation.Length > 0)
                {
                    valuePanel.Children.Add(new Border
                    {
                        Background = panelBgBrush,
                        BorderBrush = borderBrush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 0, 4, 0),
                        Margin = new Thickness(5, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = relation,
                            FontSize = 9.5,
                            Foreground = mutedTextBrush
                        }
                    });
                }
            }

            valuePanel.Children.Add(new TextBlock
            {
                Text = $"{pct}%",
                FontFamily = monoFont,
                FontSize = 10,
                Foreground = mutedTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            });
            Grid.SetColumn(valuePanel, 2);
            rowGrid.Children.Add(valuePanel);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            var seekBtn = new Button
            {
                Content = "▶",
                Style = flatButtonStyle,
                Padding = new Thickness(5, 1, 5, 1),
                FontSize = 10,
                Foreground = secondaryTextBrush,
                ToolTip = $"跳转到 {BpmFormatter.FormatTimestamp(seg.StartSeconds)} 试听该段",
                Tag = seg.StartSeconds
            };
            seekBtn.Click += OnBpmSegmentSeekClick;
            actions.Children.Add(seekBtn);

            var useBtn = new Button
            {
                Content = "采用",
                Style = flatButtonStyle,
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(2, 0, 0, 0),
                FontSize = 10.5,
                Foreground = secondaryTextBrush,
                ToolTip = "将该段 BPM 填入上方输入框",
                Tag = seg.Bpm
            };
            useBtn.Click += OnBpmCandidateChipClick;
            actions.Children.Add(useBtn);

            Grid.SetColumn(actions, 3);
            rowGrid.Children.Add(actions);

            var rowHost = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 2.5, 3, 2.5),
                Margin = new Thickness(0, 1, 0, 0),
                Background = isCurrent ? activeRowBrush : Brushes.Transparent,
                Child = rowGrid
            };
            BpmSegmentsListPanel.Children.Add(rowHost);
        }
    }

    private void OnBpmSegmentSeekClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: double startSeconds })
        {
            _vm.SeekToBpmSegment(startSeconds);
        }
    }

    private Brush ResolveThemeBrush(string key) =>
        TryFindResource(key) as Brush ?? Brushes.Transparent;

    /// <summary>
    /// 从现有主题文字/反色令牌中自动挑出与条块底色对比度最高的一支，保证全部五套深浅主题均清晰可读且零硬编码颜色。
    /// </summary>
    private Brush ResolveContrastTextBrush(Brush backgroundBrush)
    {
        var candidates = new[]
        {
            ResolveThemeBrush("AccentContrastBrush"),
            ResolveThemeBrush("TextPrimaryBrush"),
            ResolveThemeBrush("WindowBackgroundBrush")
        };

        if (backgroundBrush is not SolidColorBrush bgSolid)
        {
            return candidates[0];
        }

        var bgLum = RelativeLuminance(bgSolid.Color);
        var best = candidates[0];
        var bestRatio = -1.0;
        foreach (var candidate in candidates)
        {
            if (candidate is not SolidColorBrush fgSolid)
            {
                continue;
            }

            var fgLum = RelativeLuminance(fgSolid.Color);
            var ratio = (Math.Max(bgLum, fgLum) + 0.05) / (Math.Min(bgLum, fgLum) + 0.05);
            if (ratio > bestRatio)
            {
                bestRatio = ratio;
                best = candidate;
            }
        }

        return best;
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static ControlTemplate CreateSegmentBarBlockTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border), "SegBd");
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));

        var presenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        presenterFactory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenterFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(presenterFactory);

        template.VisualTree = borderFactory;

        var hoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(OpacityProperty, 0.85, "SegBd"));
        template.Triggers.Add(hoverTrigger);

        var pressTrigger = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressTrigger.Setters.Add(new Setter(OpacityProperty, 0.70, "SegBd"));
        template.Triggers.Add(pressTrigger);

        return template;
    }

    /// <summary>诊断截图辅助：仅用于开发态界面检查，打开 BPM 浮层以便渲染核验。</summary>
    internal void OpenBpmPopupForDiagnostics()
    {
        if (_vm.CurrentRow?.Track is not { } track)
        {
            return;
        }

        PopulateBpmPopup(track, _vm.NowPlayingBpmState);
        NowPlayingBpmPopup.IsOpen = true;
    }

    /// <summary>
    /// 打开「音频信息」窗口：库里的 Track 不保存文件声明位深，因此这里按需读一次标签；
    /// 其余管线事实来自播放器在输出链建立后采集的快照。
    /// </summary>
    private async void OnNowPlayingAudioInfoClick(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentRow?.Track is not { } track)
        {
            return;
        }

        int? fileBitsPerSample = null;
        try
        {
            fileBitsPerSample = await Task.Run(() =>
            {
                try
                {
                    return _host.TagService.Read(track.Path, includeCover: false).BitsPerSample;
                }
                catch (Exception ex)
                {
                    _host.Logger.Debug(
                        $"Audio info: reading tag properties failed for '{track.Path}': {ex.Message}");
                    return (int?)null;
                }
            });
        }
        catch (Exception ex)
        {
            _host.Logger.Debug($"Audio info: reading tag properties failed: {ex.Message}");
        }

        var report = AudioPipelineFormatter.Build(
            track,
            fileBitsPerSample,
            _host.Player.Volume,
            _host.Player.Pipeline);

        var window = new AudioInfoWindow(report, track.Title, BuildAudioInfoSubtitle(track))
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private static string BuildAudioInfoSubtitle(EchoGarage.Core.Models.Track track)
    {
        var artist = string.IsNullOrWhiteSpace(track.DisplayArtist) ? "未知艺术家" : track.DisplayArtist;
        return string.IsNullOrWhiteSpace(track.Album) ? artist : $"{artist} · {track.Album}";
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
