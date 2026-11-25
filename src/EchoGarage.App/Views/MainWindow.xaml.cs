using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EchoGarage.App.Services;
using EchoGarage.App.ViewModels;
using EchoGarage.App.Views.Controls;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.App.Views;

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly MainViewModel _vm;
    private readonly IReadOnlyList<string> _startupFiles;
    private readonly DialogService _dialogs;
    private readonly HotkeyService _hotkeys;
    private readonly ConversionCoordinator _conversion;
    private readonly LoudnessCoordinator _loudness;
    private readonly Dictionary<DataGridColumn, string> _columnKeys = [];
    private bool _seekInteracting;
    private double _seekTargetSeconds;
    private double _seekStartX;
    private double _seekStartPosition;
    private bool _seekDragged;
    private long _lastSeekLogTimestamp;
    private ContextMenu? _playModeMenu;
    private bool _suppressSelectionSync;

    /// <summary>Last user-chosen album-panel width; the column itself collapses to 0 while the panel is hidden.</summary>
    private double _albumPanelWidth = 320;
    private double _sidebarWidth = 236;
    private bool _closing;
    private DateTime _scrollBurstWindow = DateTime.MinValue;
    private int _scrollBurstEvents;
    private double _scrollBurstTravel;
    private double _scrollBurstLastOffset;
    private DateTime _scrollBurstLastLog = DateTime.MinValue;
    private DateTime _lastHeaderInsetCheck = DateTime.MinValue;

    /// <summary>Upper bound for a grouped-view column dragged by the header gripper (DIPs).</summary>
    private const double MaxGroupedColumnWidth = 1600;

    /// <summary>Song-column width that must stay visible next to a pinned header block (DIPs).</summary>
    private const double MinGroupedContentWidth = 160;

    public MainWindow(AppHost host, bool waveformModeDiagnostics = false,
        IReadOnlyList<string>? startupFiles = null)
    {
        _host = host;
        _startupFiles = startupFiles ?? [];
        InitializeComponent();

        _dialogs = new DialogService(this, host);
        _vm = new MainViewModel(host, _dialogs, waveformModeDiagnostics,
            restoreSessionOnStartup: _startupFiles.Count == 0);
        _hotkeys = new HotkeyService(_vm, host.Logger);
        _conversion = new ConversionCoordinator(host, _vm, _dialogs, this);
        _loudness = new LoudnessCoordinator(host, _vm, _dialogs, this);
        _vm.HotkeysChanged += (_, _) => _hotkeys.Reload();
        _vm.ConversionPresetRequested += (_, presetId) => _ = _conversion.ConvertWithPresetAsync(presetId);
        _vm.LayoutProvider = CaptureLayoutFromGrid;
        _vm.CellTemplateProvider = key => TryFindResource("Cell." + ToPascalCase(key)) as DataTemplate;
        DataContext = _vm;

        _vm.LayoutChanged += (_, _) => ApplyLayout();
        _vm.SortChanged += (_, _) => UpdateSortGlyphs();
        _vm.SettingsRequested += (_, _) => OpenSettings();
        _vm.CurrentRowChanged += (_, _) =>
        {
            CloseBpmPopup();
            ScrollToCurrentRow();
        };
        _vm.SelectionRestored += (_, row) => ScheduleCenterRow(row);
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        ApplyGroupCoverMetrics();

        // Watch for scroll feedback loops: a huge number of ScrollChanged events with almost no
        // movement means the ScrollViewer's anchoring is re-posting itself instead of scrolling.
        GroupedList.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnListScrollChanged), handledEventsToo: true);
        TrackGrid.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnListScrollChanged), handledEventsToo: true);
        // 「正在播放」页右侧列表是 Recycling 虚拟化：滚动时容器被复用，Loaded 不会再次触发，
        // 只靠 ItemContainerStyle 的 Loaded 会漏掉滑入视口的行封面。这里监听滚动与容器生成，
        // 把当前已实化的行统一补一次缩略图。
        NowPlayingPageList.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnNowPlayingPageListScrollChanged), handledEventsToo: true);
        NowPlayingPageList.ItemContainerGenerator.StatusChanged += OnNowPlayingPageListGeneratorStatusChanged;
        // DataGrid column resizing is handled by a Thumb inside DataGridColumnHeader; persist
        // the layout as soon as the drag ends so a width change survives switching sources,
        // crashes or an unexpected shutdown instead of waiting for the window to close.
        TrackGrid.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(OnTrackGridColumnDragCompleted), handledEventsToo: true);
        GroupedList.SizeChanged += (_, _) => UpdateGroupedHeaderInset();
        GroupedHeaderGrid.SizeChanged += (_, _) => UpdateGroupedHeaderFill();
        // Pin and layout changes replace the grouped header cells; re-stretch the left flow right
        // away so the new cells are arranged without a blank strip before the pinned block.
        _vm.GroupedColumnsRebuilt += (_, _) => UpdateGroupedHeaderFill();

        ApplyWindowPlacement();
        HookSeekSlider();
        HookWaveformSeek();
        // Build the track context menu before it opens; see OnTrackMenuOpening.
        TrackGrid.ContextMenuOpening += OnTrackMenuOpening;
        GroupedList.ContextMenuOpening += OnTrackMenuOpening;
        Loaded += OnLoaded;
        // Tags of files outside the library folders are never picked up by the scanner or the
        // watcher, so refresh the visible rows whenever the window is activated again.
        Activated += (_, _) => _ = _vm.RefreshStaleTracksAsync();
        Closing += OnClosing;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        DragOver += OnWindowDragOver;
        Drop += OnWindowDrop;
        ThemeManager.ThemeApplied += OnThemeApplied;
        UpdateThemeMenuChecks();
    }

    private void OnThemeApplied(object? sender, EchoGarage.Core.Models.ThemeKind theme)
    {
        TryEnableDarkTitleBar();
        UpdateThemeMenuChecks();
        // 柔光的颜色按主题亮度/饱和度重新映射（只重映射缓存里的原始色，不重新取色）。
        _vm.RefreshNowPlayingGlow();
    }

    /// <summary>主题菜单的勾选状态以 ThemeManager.Current 为唯一真相，避免菜单与配置不一致。</summary>
    private void UpdateThemeMenuChecks()
    {
        if (ThemeMenu is null)
        {
            return;
        }

        var currentIndex = ThemeCatalog.IndexOf(ThemeManager.Current);
        foreach (var item in ThemeMenu.Items)
        {
            if (item is MenuItem menuItem)
            {
                menuItem.IsChecked = int.TryParse(menuItem.Tag as string, out var index) && index == currentIndex;
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeManager.ThemeApplied -= OnThemeApplied;
        Closed -= OnClosed;
        _hotkeys.Dispose();
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.GroupCoverSize))
        {
            ApplyGroupCoverMetrics();
            // The cover gutter width feeds the width left for the pinned song columns.
            UpdateGroupedHeaderFill();
        }
        else if (e.PropertyName is nameof(MainViewModel.ShowAlbumPanel))
        {
            if (!_vm.ShowAlbumPanel)
            {
                // Remember the width the user last dragged to before collapsing the column.
                _albumPanelWidth = Math.Clamp(AlbumPanelColumn.ActualWidth, 240, 520);
            }

            ApplyAlbumPanelWidth(_vm.ShowPlaylistAlbumPanel);
        }
        else if (e.PropertyName is nameof(MainViewModel.IsNowPlayingPageActive))
        {
            if (!_vm.IsNowPlayingPageActive)
            {
                CloseBpmPopup();
            }

            ApplyNowPlayingPageChrome();
        }
        else if (e.PropertyName is nameof(MainViewModel.HasCurrentTrack))
        {
            if (!_vm.HasCurrentTrack)
            {
                CloseBpmPopup();
            }

            // 播放 / 停止会切换标签卡片的显隐，需要按当前页尺寸重新评估一次。
            ApplyNowPlayingPageMetrics(NowPlayingPageRoot.RenderSize);
        }
    }

    /// <summary>
    /// 「正在播放」页激活时收起左侧资料库与右侧专辑面板，让新页面占满整个列表区域；
    /// 回到播放列表页时恢复用户原来的两栏宽度（不覆盖用户拖拽过的值）。
    /// </summary>
    private void ApplyNowPlayingPageChrome()
    {
        var nowPlaying = _vm.IsNowPlayingPageActive;
        if (!nowPlaying && SidebarColumn.Width.Value >= 170)
        {
            _sidebarWidth = Math.Clamp(SidebarColumn.Width.Value, 170, 420);
        }

        SidebarColumn.MinWidth = nowPlaying ? 0 : 170;
        SidebarColumn.MaxWidth = nowPlaying ? 0 : 420;
        SidebarColumn.Width = new GridLength(nowPlaying ? 0 : Math.Clamp(_sidebarWidth, 170, 420));
        ApplyAlbumPanelWidth(_vm.ShowPlaylistAlbumPanel);

        if (nowPlaying)
        {
            // 右侧列表的 ItemsSource 在同一轮才刷新；等布局完成后再把当前行滚到视口中央，
            // 并补一次已实化行的缩略图（虚拟化下 Loaded 只覆盖首次生成的行）。
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                CenterNowPlayingListRow();
                LoadVisibleNowPlayingCovers();
            }));
        }
    }

    /// <summary>把「正在播放」页右侧列表里的当前播放行滚动到视口中央（复用播放列表页的居中逻辑）。</summary>
    private void CenterNowPlayingListRow()
    {
        if (!_vm.IsNowPlayingPageActive || _vm.CurrentRow is not { } row)
        {
            return;
        }

        if (!NowPlayingPageList.Items.Contains(row))
        {
            return;
        }

        CenterItem(NowPlayingPageList, row,
            () => NowPlayingPageList.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement,
            NowPlayingPageList.ScrollIntoView);
    }

    /// <summary>
    /// Shows or collapses the album-panel column. Collapsing the whole column (not just the
    /// panel border) keeps the track list full-width when the panel is hidden.
    /// </summary>
    private void ApplyAlbumPanelWidth(bool visible)
    {
        AlbumPanelColumn.MinWidth = visible ? 240 : 0;
        AlbumPanelColumn.MaxWidth = visible ? 520 : 0;
        AlbumPanelColumn.Width = new GridLength(visible ? Math.Clamp(_albumPanelWidth, 240, 520) : 0);
    }

    /// <summary>
    /// Pushes the current album cover metrics into the grouped list. The album group template
    /// reads the size through the inheritable <see cref="GroupCoverMetrics.CoverSize"/> attached
    /// property instead of an ancestor binding, so group containers created later by
    /// virtualization/recycling receive the same value.
    /// </summary>
    private void ApplyGroupCoverMetrics()
    {
        GroupedList.SetValue(GroupCoverMetrics.CoverSizeProperty, _vm.GroupCoverSize);
    }

    public MainViewModel ViewModel => _vm;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            ApplyDpiScale();
            DpiChanged += (_, _) => ApplyDpiScale();
            // Files opened through the shell (double-click / "Open with") must not restore
            // the previous playlist or selection; they are loaded after initialization instead.
            await _vm.InitializeAsync(loadInitialSource: _startupFiles.Count == 0);
            ApplyLayout();

            if (_startupFiles.Count > 0)
            {
                await _vm.HandleExternalFilesAsync(_startupFiles);
            }

            // Audio device enumeration talks to WASAPI/COM and can take a moment on
            // first use; keep it off the critical path so the window paints immediately.
            _ = Dispatcher.BeginInvoke(new Action(_vm.RefreshDevices), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Main window initialization failed", ex);
            _vm.SetStatus($"初始化失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 封面按“显示尺寸 × DPI”解码：4K/150% 下相册组封面只有 96 DIP（144 px），
    /// 固定解码到 512 px 会让每组多占约 1 MB 非托管内存。
    /// </summary>
    private void ApplyDpiScale()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _vm.DpiScale = dpi.DpiScaleX;
    }

    /// <summary>
    /// ADR-023 诊断入口：右栏频谱控件，供 --visualizer 开关统计帧率并截图。
    /// </summary>
    public SpectrumView Visualizer => NowPlayingSpectrum;

    /// <summary>
    /// Saves the full window content as a PNG. Used by the hidden --screenshot switch
    /// for visual regression checks on high-DPI machines.
    /// </summary>
    public void SaveScreenshot(string path)
    {
        try
        {
            var width = Math.Max(1, (int)Math.Round(ActualWidth));
            var height = Math.Max(1, (int)Math.Round(ActualHeight));
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(this);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            encoder.Save(stream);
            _host.Logger.Info($"Screenshot saved to {path} ({width}x{height}).");
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Screenshot failed", ex);
        }
    }

    private void ApplyWindowPlacement()
    {
        var settings = _host.Settings.Current;
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Math.Max(MinWidth, settings.WindowWidth), Math.Max(MinWidth, workArea.Width - 24));
        Height = Math.Min(Math.Max(MinHeight, settings.WindowHeight), Math.Max(MinHeight, workArea.Height - 24));

        var left = settings.WindowLeft ?? (workArea.Width - Width) / 2;
        var top = settings.WindowTop ?? (workArea.Height - Height) / 2;

        // Keep the window visible on the current monitor setup.
        if (left < workArea.Left || left + 120 > workArea.Right)
        {
            left = workArea.Left + 40;
        }

        if (top < workArea.Top || top + 80 > workArea.Bottom)
        {
            top = workArea.Top + 40;
        }

        Left = left;
        Top = top;
        SidebarColumn.Width = new GridLength(Math.Clamp(settings.SidebarWidth, 170, 420));
        _albumPanelWidth = Math.Clamp(settings.AlbumPanelWidth, 240, 520);
        ApplyAlbumPanelWidth(_vm.ShowPlaylistAlbumPanel);

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    // =====================================================================
    // Columns
    // =====================================================================
    private static readonly IReadOnlyDictionary<string, string?> SortPropertyMap = TrackSortMap.ByKey;

    private string? _appliedLayoutSignature;

    private void ApplyLayout()
    {
        var layout = _vm.CurrentLayout;
        var signature = BuildLayoutSignature(layout);
        if (TrackGrid.Columns.Count > 0 &&
            string.Equals(signature, _appliedLayoutSignature, StringComparison.Ordinal))
        {
            UpdateSortGlyphs();
            UpdateGroupedHeaderInset();
            ScrollToCurrentRow();
            return;
        }

        _host.Logger.Debug($"Applying layout: groupCoverSize={_vm.GroupCoverSize:F1} signature={signature}");
        RebuildColumns(layout, signature);
        UpdateGroupedHeaderInset();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateGroupedHeaderInset));
        ScrollToCurrentRow();
    }

    private static string BuildLayoutSignature(PlaylistViewLayout layout) =>
        "u|" + string.Join("|", layout.Columns
            .Where(c => c.Visible)
            .Select(c => $"{c.Key}:{(int)Math.Round(c.Width)}"));

    private void RebuildColumns(PlaylistViewLayout layout, string signature)
    {
        // Changing DataGrid.Columns while rows own cells can crash DataGridCellsPanel
        // ("index out of range" during arrange). Detach the items first, rebuild, reattach.
        var selectedRow = _vm.SelectedRow;
        TrackGrid.ItemsSource = null;
        _columnKeys.Clear();
        TrackGrid.Columns.Clear();

        foreach (var column in layout.Columns.Where(c => c.Visible))
        {
            var descriptor = ColumnCatalog.Find(column.Key);
            if (descriptor is null)
            {
                continue;
            }

            var cellTemplate = TryFindResource("Cell." + ToPascalCase(column.Key)) as DataTemplate;
            if (cellTemplate is null)
            {
                continue;
            }

            SortPropertyMap.TryGetValue(column.Key, out var sortProperty);
            var width = Math.Max(descriptor.MinWidth, column.Width);

            var gridColumn = new DataGridTemplateColumn
            {
                Header = descriptor.Header,
                CellTemplate = cellTemplate,
                Width = new DataGridLength(width, DataGridLengthUnitType.Pixel),
                MinWidth = descriptor.MinWidth,
                MaxWidth = 1400,
                SortMemberPath = sortProperty,
                CanUserSort = descriptor.Sortable && sortProperty is not null,
                IsReadOnly = true
            };

            if (sortProperty is not null &&
                string.Equals(layout.SortColumn, sortProperty, StringComparison.Ordinal))
            {
                gridColumn.SortDirection = layout.SortDescending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
            }

            _columnKeys[gridColumn] = descriptor.Key;
            TrackGrid.Columns.Add(gridColumn);
        }

        BindingOperations.SetBinding(TrackGrid, ItemsControl.ItemsSourceProperty,
            new Binding(nameof(MainViewModel.TracksView)) { Source = _vm });
        // Rebinding ItemsSource makes the DataGrid clear the view's SortDescriptions; restore
        // the saved sort before rows are rendered.
        _vm.ReapplySort();
        if (selectedRow is not null)
        {
            TrackGrid.SelectedItem = selectedRow;
        }

        _appliedLayoutSignature = signature;
        UpdateSortGlyphs();
    }

    private static string ToPascalCase(string key) =>
        string.IsNullOrEmpty(key) ? key : char.ToUpperInvariant(key[0]) + key[1..];

    private PlaylistViewLayout? CaptureLayoutFromGrid()
    {
        var layout = _vm.CurrentLayout.Clone();

        // While the grouped list is visible the flat DataGrid is hidden, but its columns keep
        // the widths they had before the last grouped header drag. Capturing them here (for
        // example on shutdown) silently reverted grouped resizes, which were persisted
        // immediately by PersistGroupedColumnWidth. The in-memory layout already holds the
        // authoritative widths in that state.
        if (_vm.IsGroupedViewActive)
        {
            return layout;
        }

        // Before the first layout pass the grid has no columns; capturing then would
        // mark every column as hidden and destroy the saved layout.
        if (TrackGrid.Columns.Count == 0)
        {
            return null;
        }

        var ordered = new List<PlaylistColumn>();
        foreach (var gridColumn in TrackGrid.Columns)
        {
            if (!_columnKeys.TryGetValue(gridColumn, out var key))
            {
                continue;
            }

            var stored = layout.Columns.FirstOrDefault(c =>
                string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
            if (stored is null)
            {
                continue;
            }

            var width = gridColumn.ActualWidth > 10 ? gridColumn.ActualWidth : stored.Width;
            ordered.Add(new PlaylistColumn
            {
                Key = stored.Key,
                Width = width,
                Visible = true,
                Pin = stored.Pin,
                SecondaryFormat = stored.SecondaryFormat
            });
        }

        // Columns that exist in the layout but are not rendered right now keep their
        // stored visibility (hidden columns stay hidden; a missing visible column is kept
        // visible so a later layout pass can restore it).
        foreach (var remaining in layout.Columns.Where(c =>
                     ordered.All(o => !string.Equals(o.Key, c.Key, StringComparison.OrdinalIgnoreCase))))
        {
            ordered.Add(remaining.Clone());
        }

        layout.Columns = ordered;
        return layout;
    }

    private void UpdateSortGlyphs()
    {
        var layout = _vm.CurrentLayout;
        foreach (var (column, key) in _columnKeys)
        {
            SortPropertyMap.TryGetValue(key, out var sortProperty);
            column.SortDirection = sortProperty is not null &&
                                   string.Equals(layout.SortColumn, sortProperty, StringComparison.Ordinal)
                ? layout.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending
                : null;
        }
    }

    private void OnTrackGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var sortProperty = e.Column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(sortProperty))
        {
            return;
        }

        _vm.PersistCurrentLayout(CaptureLayoutFromGrid());
        _vm.SortBy(sortProperty);
        UpdateSortGlyphs();
    }

    private void OnTrackGridColumnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.OriginalSource is not Thumb)
        {
            return;
        }

        // Persisting rebuilds the DataGrid columns, which must not happen from inside a drag
        // event on one of those headers; let the current input event finish first.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var captured = CaptureLayoutFromGrid();
            if (captured is not null)
            {
                _vm.PersistCurrentLayout(captured);
            }
        }), DispatcherPriority.Background);
    }

    // =====================================================================
    // Rows, selection and covers
    // =====================================================================
    private void OnTrackGridLoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is TrackRowViewModel row)
        {
            row.IsSelected = TrackGrid.SelectedItems.Contains(row);
            _ = _vm.LoadRowCoverAsync(row);
        }
    }

    private void OnTrackGridUnloadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is TrackRowViewModel row)
        {
            row.CoverImage = null;
        }
    }

    private void OnTrackSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SyncSelectedRows(TrackGrid.SelectedItems, e.RemovedItems);

    /// <summary>
    /// 把列表控件的选中项同步到 <see cref="MainViewModel.SelectedRows"/>（批量命令的唯一数据源），
    /// 并维护 <see cref="TrackRowViewModel.IsSelected"/>，让行模板显示选中背景。
    /// </summary>
    private void SyncSelectedRows(System.Collections.IList current, System.Collections.IList removed)
    {
        foreach (var item in removed)
        {
            if (item is TrackRowViewModel row)
            {
                row.IsSelected = false;
            }
        }

        _vm.SelectedRows.Clear();
        foreach (var item in current)
        {
            if (item is TrackRowViewModel row)
            {
                row.IsSelected = true;
                _vm.SelectedRows.Add(row);
            }
        }
    }

    private void OnTrackGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TrackGrid.SelectedItem is TrackRowViewModel row)
        {
            _ = _vm.PlayRowCommand.ExecuteAsync(row);
        }
    }

    // =====================================================================
    // Grouped list (foobar2000-style album gutter)
    // =====================================================================
    private void OnGroupedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSync)
        {
            // 分组栏批量选中会逐项触发 SelectionChanged，等批量改完再一次性同步。
            return;
        }

        SyncSelectedRows(GroupedList.SelectedItems, e.RemovedItems);
    }

    /// <summary>
    /// 分组栏单击：选中该分组下的全部歌曲，随后可直接做批量操作（移除、转换、文件操作…）。
    /// 按住 Ctrl 时把整组追加到现有选择。直接改 <see cref="GroupedList"/> 的选中集会逐项
    /// 触发 SelectionChanged，因此用 <see cref="_suppressSelectionSync"/> 压住回调后一次性同步。
    /// </summary>
    private void OnGroupHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            sender is not FrameworkElement { DataContext: CollectionViewGroup group })
        {
            return;
        }

        var rows = group.Items.OfType<TrackRowViewModel>().ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var target = (Keyboard.Modifiers & ModifierKeys.Control) != 0
            ? GroupedList.SelectedItems.Cast<TrackRowViewModel>().Concat(rows).Distinct().ToList()
            : rows;

        _suppressSelectionSync = true;
        try
        {
            GroupedList.SelectedItems.Clear();
            foreach (var row in target)
            {
                GroupedList.SelectedItems.Add(row);
            }
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        SyncSelectedRows(GroupedList.SelectedItems, Array.Empty<object>());
        e.Handled = true;
    }

    private void OnGroupedDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GroupedList.SelectedItem is TrackRowViewModel row)
        {
            _ = _vm.PlayRowCommand.ExecuteAsync(row);
        }
    }

    // =====================================================================
    // Track context menu content
    // =====================================================================
    /// <summary>
    /// The track context menu is shared by the grid and the grouped list, and its submenus
    /// (conversion presets, file operations, tag normalization) are rebuilt from current state.
    /// Rebuild them in <see cref="FrameworkElement.ContextMenuOpening"/> — i.e. before the menu
    /// is shown — because a MenuItem without children never opens its submenu, while touching the
    /// items of an already displayed menu (Opened / SubmenuOpened) makes WPF recreate the visual
    /// containers and the whole menu chain collapses as soon as the pointer enters a submenu.
    /// </summary>
    private void OnTrackMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextMenu is not { } menu)
        {
            return;
        }

        var items = menu.Items.OfType<MenuItem>().ToList();
        var remove = items.FirstOrDefault(item => item.Header as string == "移除列表");
        if (remove is not null)
        {
            // 「移除列表」只对普通播放列表有效：媒体库 / 智能播放列表里置灰，避免点了没反应。
            remove.IsEnabled = _vm.SelectedSource is { Kind: SourceKind.Playlist, IsSmart: false };
        }

        var conversion = items.FirstOrDefault(item => item.Header as string == "转换");
        if (conversion is not null)
        {
            RebuildConversionMenu(conversion);
        }

        var fileOperations = items.FirstOrDefault(item => item.Header as string == "文件操作");
        if (fileOperations is not null)
        {
            fileOperations.IsEnabled = _vm.GetFileOperationTargets().Count > 0;
            RebuildFileOperationMenu(fileOperations);
        }

        var tagNormalization = items.FirstOrDefault(item => item.Header as string == "标签规范化");
        if (tagNormalization is not null)
        {
            tagNormalization.IsEnabled = _vm.SelectedRows.Count > 0 || _vm.SelectedRow is not null;
            RebuildTagNormalizationMenu(tagNormalization);
        }

        var loudnessScan = items.FirstOrDefault(item => item.Header as string == "响度计算…");
        if (loudnessScan is not null)
        {
            // 响度计算：选中任意数量（含回退当前行）即可。
            loudnessScan.IsEnabled = _vm.GetFileOperationTargets().Count > 0;
        }

        var loudnessAdjust = items.FirstOrDefault(item => item.Header as string == "响度调整…");
        if (loudnessAdjust is not null)
        {
            // 响度调整：一次只处理一个文件。
            loudnessAdjust.IsEnabled = _vm.GetFileOperationTargets().Count == 1;
        }
    }

    // =====================================================================
    // 响度计算 / 调整（ADR-028）
    // =====================================================================

    private void OnLoudnessScanClick(object sender, RoutedEventArgs e) => _ = _loudness.ScanSelectedAsync();

    private async void OnLoudnessAdjustClick(object sender, RoutedEventArgs e)
    {
        var targets = _vm.GetFileOperationTargets();
        if (targets.Count != 1)
        {
            _dialogs.Info("请先在歌曲列表中选中一首歌曲，再调整响度。", "响度调整");
            return;
        }

        await _loudness.AdjustSingleAsync(targets[0].Track);
    }

    // =====================================================================
    // File operations (ADR-021)
    // =====================================================================

    /// <summary>
    /// 「文件操作」二级菜单：复制到 / 移动到（各自三级菜单列出记住的目标文件夹）+ 删除文件。
    /// 内容在菜单显示前的 ContextMenuOpening 里一次构建完成，菜单显示期间不再修改子项。
    /// </summary>
    private void RebuildFileOperationMenu(MenuItem menu)
    {
        var hasTargets = _vm.GetFileOperationTargets().Count > 0;
        menu.Items.Clear();

        var copyTo = new MenuItem { Header = "复制 到", IsEnabled = hasTargets };
        RebuildDestinationMenu(copyTo, move: false);
        menu.Items.Add(copyTo);

        var moveTo = new MenuItem { Header = "移动 到", IsEnabled = hasTargets };
        RebuildDestinationMenu(moveTo, move: true);
        menu.Items.Add(moveTo);

        menu.Items.Add(new MenuItem { Header = "重命名 到…", IsEnabled = false });
        menu.Items.Add(new Separator());

        var delete = new MenuItem { Header = "删除文件", IsEnabled = hasTargets };
        delete.Click += (_, _) => _ = _vm.DeleteSelectedFilesAsync();
        menu.Items.Add(delete);
    }

    private void RebuildDestinationMenu(MenuItem parent, bool move)
    {
        parent.Items.Clear();
        var destinations = _vm.Settings.FileDestinations;
        foreach (var directory in destinations)
        {
            var name = System.IO.Path.GetFileName(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            var item = new MenuItem
            {
                Header = string.IsNullOrWhiteSpace(name) ? directory : name,
                ToolTip = directory,
                Tag = directory,
            };
            item.Click += (_, _) =>
            {
                if (item.Tag is string destination)
                {
                    _ = move ? _vm.MoveSelectedFilesAsync(destination) : _vm.CopySelectedFilesAsync(destination);
                }
            };
            parent.Items.Add(item);
        }

        if (destinations.Count > 0)
        {
            parent.Items.Add(new Separator());
        }

        var choose = new MenuItem { Header = "选择文件夹…" };
        choose.Click += (_, _) => _ = move ? _vm.MoveSelectedFilesAsync() : _vm.CopySelectedFilesAsync();
        parent.Items.Add(choose);

        var manage = new MenuItem { Header = "管理目标文件夹…" };
        manage.Click += (_, _) => OpenSettings();
        parent.Items.Add(manage);
    }

    private void RebuildConversionMenu(MenuItem menu)
    {
        menu.Items.Clear();
        foreach (var preset in _vm.Settings.ConversionPresets)
        {
            var item = new MenuItem { Header = preset.Name, Tag = preset.Id };
            item.Click += (_, _) => _ = _conversion.ConvertWithPresetAsync((string)item.Tag);
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "没有可用的转换预设", IsEnabled = false });
        }

        menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "转换器设置…" };
        settings.Click += (_, _) => _ = _conversion.OpenSettingsAsync();
        menu.Items.Add(settings);
    }

    /// <summary>
    /// 「标签规范化」二级菜单（ADR-025）：列出设置页里启用的规则，外加“应用全部启用规则”。
    /// 与转换 / 文件操作子菜单一样在菜单显示前构建；没有启用规则时显示禁用的占位项。
    /// </summary>
    private void RebuildTagNormalizationMenu(MenuItem menu)
    {
        menu.Items.Clear();
        var rules = _vm.Settings.TagNormalizationRules.Where(rule => rule.Enabled).ToList();
        foreach (var rule in rules)
        {
            var item = new MenuItem
            {
                Header = string.IsNullOrWhiteSpace(rule.Name) ? "未命名规则" : rule.Name,
                Tag = rule.Id,
                ToolTip = string.IsNullOrWhiteSpace(rule.Pattern) ? null : $"正则：{rule.Pattern}",
            };
            item.Click += (_, _) =>
            {
                if (item.Tag is string ruleId)
                {
                    _ = _vm.ApplyTagNormalizationAsync(ruleId);
                }
            };
            menu.Items.Add(item);
        }

        if (rules.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "（没有启用的规则）", IsEnabled = false });
        }

        menu.Items.Add(new Separator());
        var applyAll = new MenuItem { Header = "应用全部启用规则", IsEnabled = rules.Count > 0 };
        applyAll.Click += (_, _) => _ = _vm.ApplyTagNormalizationAsync(ruleId: null);
        menu.Items.Add(applyAll);
    }

    private void OnConversionSettingsClick(object sender, RoutedEventArgs e) => _ = _conversion.OpenSettingsAsync();

    /// <summary>
    /// Right-clicking a row that is not part of the current selection selects that row first, so
    /// context-menu commands (convert, remove, tags) act on what the user actually clicked.
    /// </summary>
    private void OnTrackGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not TrackRowViewModel model || TrackGrid.SelectedItems.Contains(model))
        {
            return;
        }

        TrackGrid.SelectedItems.Clear();
        TrackGrid.SelectedItem = model;
    }

    private void OnGroupedListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not TrackRowViewModel model || GroupedList.SelectedItems.Contains(model))
        {
            return;
        }

        GroupedList.SelectedItems.Clear();
        GroupedList.SelectedItem = model;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    // =====================================================================
    // Rubber-band (marquee) selection
    // =====================================================================

    private const double MarqueeThreshold = 4;

    private bool _marqueeActive;
    private bool _marqueeAdditive;
    private bool _marqueeMoved;
    private Point _marqueeStart;
    private DataGrid? _marqueeGrid;
    private ListView? _marqueeList;
    private ScrollViewer? _marqueeScroll;
    private readonly HashSet<object> _marqueeBaseItems = [];

    private void OnTrackGridMarqueeStart(object sender, MouseButtonEventArgs e) => StartMarquee(TrackGrid, e);

    private void OnGroupedListMarqueeStart(object sender, MouseButtonEventArgs e) => StartMarquee(GroupedList, e);

    private void OnTrackGridMarqueeMove(object sender, MouseEventArgs e) => ContinueMarquee(e);

    private void OnGroupedListMarqueeMove(object sender, MouseEventArgs e) => ContinueMarquee(e);

    private void OnTrackGridMarqueeEnd(object sender, MouseButtonEventArgs e) => EndMarquee(e);

    private void OnGroupedListMarqueeEnd(object sender, MouseButtonEventArgs e) => EndMarquee(e);

    private void StartMarquee(FrameworkElement host, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _marqueeActive)
        {
            return;
        }

        // Only start on empty list space: rows/items keep their normal click, drag and
        // header-resize behaviour.
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<DataGridRow>(source) is not null ||
            FindAncestor<ListViewItem>(source) is not null ||
            FindAncestor<GroupItem>(source) is not null ||
            FindAncestor<DataGridColumnHeader>(source) is not null ||
            FindAncestor<ScrollBar>(source) is not null ||
            FindAncestor<TextBox>(source) is not null)
        {
            return;
        }

        _marqueeActive = true;
        _marqueeMoved = false;
        _marqueeAdditive = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        _marqueeStart = e.GetPosition(MarqueeLayer);
        _marqueeGrid = host as DataGrid;
        _marqueeList = host as ListView;
        _marqueeScroll = FindDescendant<ScrollViewer>(host);

        _marqueeBaseItems.Clear();
        if (_marqueeGrid is not null)
        {
            foreach (var item in _marqueeGrid.SelectedItems)
            {
                _marqueeBaseItems.Add(item);
            }

            if (!_marqueeAdditive)
            {
                _marqueeGrid.SelectedItems.Clear();
            }
        }
        else if (_marqueeList is not null)
        {
            foreach (var item in _marqueeList.SelectedItems)
            {
                _marqueeBaseItems.Add(item);
            }

            if (!_marqueeAdditive)
            {
                _marqueeList.SelectedItems.Clear();
            }
        }

        MarqueeLayer.Visibility = Visibility.Visible;
        MarqueeBorder.Visibility = Visibility.Collapsed;
        host.CaptureMouse();
    }

    private void ContinueMarquee(MouseEventArgs e)
    {
        if (!_marqueeActive)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelMarquee();
            return;
        }

        var rect = new Rect(_marqueeStart, e.GetPosition(MarqueeLayer));
        if (!_marqueeMoved && rect.Width < MarqueeThreshold && rect.Height < MarqueeThreshold)
        {
            return;
        }

        _marqueeMoved = true;
        Canvas.SetLeft(MarqueeBorder, rect.X);
        Canvas.SetTop(MarqueeBorder, rect.Y);
        MarqueeBorder.Width = rect.Width;
        MarqueeBorder.Height = rect.Height;
        MarqueeBorder.Visibility = Visibility.Visible;

        UpdateMarqueeSelection(rect);
        AutoScrollMarquee();
        e.Handled = true;
    }

    private void EndMarquee(MouseButtonEventArgs e)
    {
        if (!_marqueeActive)
        {
            return;
        }

        var host = (IInputElement?)_marqueeGrid ?? _marqueeList;
        _marqueeActive = false;
        _marqueeGrid = null;
        _marqueeList = null;
        _marqueeScroll = null;
        _marqueeBaseItems.Clear();
        MarqueeLayer.Visibility = Visibility.Collapsed;
        MarqueeBorder.Visibility = Visibility.Collapsed;

        try
        {
            host?.ReleaseMouseCapture();
        }
        catch
        {
            // Ignore capture errors while finishing.
        }

        e.Handled = true;
    }

    private void OnMarqueeLostCapture(object sender, MouseEventArgs e)
    {
        if (_marqueeActive)
        {
            CancelMarquee();
        }
    }

    private void CancelMarquee()
    {
        _marqueeActive = false;
        _marqueeGrid = null;
        _marqueeList = null;
        _marqueeScroll = null;
        _marqueeBaseItems.Clear();
        MarqueeLayer.Visibility = Visibility.Collapsed;
        MarqueeBorder.Visibility = Visibility.Collapsed;
    }

    private void UpdateMarqueeSelection(Rect rect)
    {
        if (_marqueeGrid is not null)
        {
            var desired = new HashSet<object>();
            for (var i = 0; i < _marqueeGrid.Items.Count; i++)
            {
                if (_marqueeGrid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow row &&
                    row.IsVisible &&
                    Intersects(row, rect))
                {
                    desired.Add(row.Item);
                }
            }

            foreach (var item in _marqueeGrid.SelectedItems.Cast<object>().ToList())
            {
                if (!desired.Contains(item) && !(_marqueeAdditive && _marqueeBaseItems.Contains(item)))
                {
                    _marqueeGrid.SelectedItems.Remove(item);
                }
            }

            foreach (var item in desired)
            {
                if (!_marqueeGrid.SelectedItems.Contains(item))
                {
                    _marqueeGrid.SelectedItems.Add(item);
                }
            }

            return;
        }

        if (_marqueeList is null)
        {
            return;
        }

        foreach (var item in EnumerateDescendants<ListViewItem>(_marqueeList))
        {
            if (!item.IsVisible)
            {
                continue;
            }

            var shouldSelect = Intersects(item, rect) ||
                               (_marqueeAdditive && _marqueeBaseItems.Contains(item.DataContext));
            if (item.IsSelected != shouldSelect)
            {
                item.IsSelected = shouldSelect;
            }
        }
    }

    private bool Intersects(FrameworkElement element, Rect rect)
    {
        try
        {
            var origin = element.TranslatePoint(new Point(0, 0), MarqueeLayer);
            return rect.IntersectsWith(new Rect(origin, element.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void AutoScrollMarquee()
    {
        if (_marqueeScroll is null)
        {
            return;
        }

        try
        {
            const double edge = 28;
            var point = Mouse.GetPosition(_marqueeScroll);
            if (point.Y < edge)
            {
                _marqueeScroll.LineUp();
            }
            else if (point.Y > _marqueeScroll.ViewportHeight - edge)
            {
                _marqueeScroll.LineDown();
            }
        }
        catch
        {
            // Ignore scrolling errors during drag.
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<T> EnumerateDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in EnumerateDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private void OnGroupedHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GroupedColumnHeaderViewModel header } &&
            SortPropertyMap.TryGetValue(header.Key, out var sortProperty) &&
            !string.IsNullOrWhiteSpace(sortProperty))
        {
            _vm.SortBy(sortProperty);
            e.Handled = true;
        }
    }

    private void OnGroupedHeaderPinLeftClick(object sender, RoutedEventArgs e) =>
        SetGroupedColumnPin(sender, ColumnPin.Left);

    private void OnGroupedHeaderPinRightClick(object sender, RoutedEventArgs e) =>
        SetGroupedColumnPin(sender, ColumnPin.Right);

    private void SetGroupedColumnPin(object sender, ColumnPin pin)
    {
        if (sender is FrameworkElement { DataContext: GroupedColumnHeaderViewModel header })
        {
            _vm.SetGroupedColumnPin(header.Key, pin);
        }
    }

    private void OnGroupedColumnThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GroupedColumnHeaderViewModel header })
        {
            return;
        }

        if (IsSeamResizeGripper(header))
        {
            // This gripper sits on the divider between the stretched left flow and the pinned
            // block; it moves the divider instead of the left column's own persisted width.
            ResizeFirstRightColumn(e.HorizontalChange);
            e.Handled = true;
            return;
        }

        // The header cell and every realized row cell bind to this same instance, so updating
        // the width here resizes the whole column live while the gripper is dragged.
        header.Width = Math.Clamp(header.Width + e.HorizontalChange,
            header.MinWidth, MaxAllowedGroupedColumnWidth(header));
        UpdateGroupedHeaderFill();
        e.Handled = true;
    }

    /// <summary>
    /// True for the trailing left-pinned header when a right-pinned block exists: its right
    /// gripper lies on the same divider as the block's left edge, so it has the divider semantics
    /// below. (Resizing the left column itself would be swallowed by the stretched display width.)
    /// </summary>
    private bool IsSeamResizeGripper(GroupedColumnHeaderViewModel header) =>
        header.Pin == ColumnPin.Left &&
        ReferenceEquals(header, _vm.GroupedLeftColumns.LastOrDefault()) &&
        _vm.GroupedRightColumns.Count > 0;

    /// <summary>
    /// Largest width a header may take without dragging the pinned block out of its visible area:
    /// left-pinned columns must fit in the left flow, right-pinned columns must leave the song
    /// content readable next to the right-aligned block.
    /// </summary>
    private double MaxAllowedGroupedColumnWidth(GroupedColumnHeaderViewModel header)
    {
        if (GroupedHeaderGrid is null || GroupedHeaderGrid.ActualWidth <= 0)
        {
            return MaxGroupedColumnWidth;
        }

        var siblings = header.Pin == ColumnPin.Right ? _vm.GroupedRightColumns : _vm.GroupedLeftColumns;
        var siblingWidth = siblings
            .Where(h => !ReferenceEquals(h, header))
            .Sum(h => h.Width);
        // The left flow may not shrink below the minimum widths of its own columns, otherwise
        // widening a right-pinned column would silently clip whole columns away.
        var leftMinTotal = _vm.GroupedLeftColumns.Sum(h => h.MinWidth);
        var contentReserve = leftMinTotal > 0 ? leftMinTotal : MinGroupedContentWidth;
        var available = header.Pin == ColumnPin.Right
            ? GroupedHeaderGrid.ActualWidth
              - GroupedHeaderGrid.ColumnDefinitions[0].ActualWidth
              - GroupedRightHeaderItems.Margin.Right
              - contentReserve
              - siblingWidth
            : GroupedHeaderGrid.ColumnDefinitions[1].ActualWidth - siblingWidth;
        var maxWidth = Math.Max(header.Width, Math.Max(header.MinWidth,
            Math.Min(MaxGroupedColumnWidth, available)));
        _host.Logger.Debug(
            $"Grouped column drag clamp: key={header.Key} pin={header.Pin} grid={GroupedHeaderGrid.ActualWidth:F1} " +
            $"cover={GroupedHeaderGrid.ColumnDefinitions[0].ActualWidth:F1} " +
            $"left={GroupedHeaderGrid.ColumnDefinitions[1].ActualWidth:F1} " +
            $"right={GroupedHeaderGrid.ColumnDefinitions[2].ActualWidth:F1} inset={GroupedRightHeaderItems.Margin.Right:F1} " +
            $"siblings={siblingWidth:F1} reserve={contentReserve:F1} max={maxWidth:F1}");
        return maxWidth;
    }

    private void OnGroupedColumnThumbDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GroupedColumnHeaderViewModel header })
        {
            return;
        }

        var target = IsSeamResizeGripper(header) ? _vm.GroupedRightColumns.FirstOrDefault() : header;
        if (target is not null)
        {
            _vm.PersistGroupedColumnWidth(target.Key, target.Width);
        }
    }

    private void OnGroupedBoundaryThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFirstRightColumn(e.HorizontalChange);
        e.Handled = true;
    }

    /// <summary>
    /// Moves the divider between the left flow and the pinned block: dragging it to the left
    /// widens the first pinned column, which shrinks the stretched left flow (the pinned block's
    /// right edge stays glued to the list border, so the two blocks only redistribute space).
    /// </summary>
    private void ResizeFirstRightColumn(double horizontalChange)
    {
        var firstRight = _vm.GroupedRightColumns.FirstOrDefault();
        if (firstRight is null)
        {
            return;
        }

        firstRight.Width = Math.Clamp(firstRight.Width - horizontalChange,
            firstRight.MinWidth, MaxAllowedGroupedColumnWidth(firstRight));
        UpdateGroupedHeaderFill();
    }

    private void OnGroupedBoundaryThumbDragCompleted(object sender, DragCompletedEventArgs e)
    {
        var firstRight = _vm.GroupedRightColumns.FirstOrDefault();
        if (firstRight is not null)
        {
            _vm.PersistGroupedColumnWidth(firstRight.Key, firstRight.Width);
        }
    }

    private void OnGroupCoverThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        var maxCoverSize = PlaylistViewLayout.MaxGroupCoverSize;
        if (GroupedHeaderGrid is { ActualWidth: > 0 })
        {
            // Growing the cover gutter squeezes the song columns; stop before the content area
            // disappears from the visible range.
            maxCoverSize = Math.Min(maxCoverSize, Math.Max(PlaylistViewLayout.MinGroupCoverSize,
                GroupedHeaderGrid.ActualWidth - 16 - MinGroupedContentWidth));
        }

        _vm.GroupCoverSize = Math.Clamp(_vm.GroupCoverSize + e.HorizontalChange,
            PlaylistViewLayout.MinGroupCoverSize, maxCoverSize);
        UpdateGroupedHeaderFill();
        e.Handled = true;
    }

    private void OnGroupCoverThumbDragCompleted(object sender, DragCompletedEventArgs e) =>
        _vm.PersistGroupCoverSize();

    /// <summary>
    /// Keeps the right-pinned header block flush with the song rows: rows live inside the list's
    /// ScrollViewer, so they end where the vertical scroll bar begins, while the header spans the
    /// full width.
    /// </summary>
    private void UpdateGroupedHeaderInset()
    {
        if (GroupedRightHeaderItems is null)
        {
            return;
        }

        var viewer = FindScrollViewer(GroupedList);
        var bar = viewer is null ? null : FindVerticalScrollBar(viewer);
        var inset = bar is { IsVisible: true } ? bar.ActualWidth : 0;
        if (Math.Abs(GroupedRightHeaderItems.Margin.Right - inset) > 0.5)
        {
            GroupedRightHeaderItems.Margin = new Thickness(0, 0, inset, 0);
            _host.Logger.Debug($"Grouped header inset: {inset:F1} (viewer={viewer is not null}, bar={bar is not null})");
        }

        // The inset changes how much width the left flow has, so re-stretch it after every pass
        // (also when only the list/viewport size changed while the inset stayed the same).
        UpdateGroupedHeaderFill();
    }

    /// <summary>
    /// Stretches the trailing left-pinned column so the left flow touches the right-pinned block.
    /// The logical column widths (and their persistence) stay untouched; only the display width
    /// grows, which makes the divider between the two blocks a pure space-distribution control.
    /// </summary>
    private void UpdateGroupedHeaderFill()
    {
        if (GroupedHeaderGrid is null || _vm.GroupedLeftColumns.Count == 0)
        {
            return;
        }

        var left = _vm.GroupedLeftColumns;
        if (_vm.GroupedRightColumns.Count == 0 || GroupedHeaderGrid.ActualWidth <= 0)
        {
            ApplyLeftFlowFill(left, 0);
            return;
        }

        // Same width the left flow gets in the song rows: the header spans the list's full width,
        // so subtract the cover gutter and the scroll bar inset that the pinned block leaves out.
        var inset = GroupedRightHeaderItems?.Margin.Right ?? 0;
        var available = GroupedHeaderGrid.ActualWidth
                        - _vm.GroupCoverColumnWidth
                        - inset
                        - _vm.GroupedRightColumns.Sum(h => h.Width);
        var fill = GroupedColumnWidths.ComputeTrailingFill(
            available, left.Select(h => h.Width).ToList());
        ApplyLeftFlowFill(left, fill);
    }

    private static void ApplyLeftFlowFill(
        IReadOnlyList<GroupedColumnHeaderViewModel> left, double fill)
    {
        for (var i = 0; i < left.Count; i++)
        {
            left[i].FillWidth = i == left.Count - 1 ? fill : 0;
        }
    }

    private static ScrollBar? FindVerticalScrollBar(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollBar { Orientation: Orientation.Vertical } bar)
            {
                return bar;
            }

            var nested = FindVerticalScrollBar(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnGroupCoverLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CollectionViewGroup group } element)
        {
            // Containers can be created for measuring before they become visible; loading here
            // would decode artwork for groups the user may never see. IsVisibleChanged handles
            // the moment the group actually appears.
            if (element.IsVisible)
            {
                RequestGroupCover(group);
                RepairGroupCoverSizeIfNeeded(element, group);
            }
        }
    }

    /// <summary>
    /// Last line of defence for the giant-cover bug (2026-11-12): the cover Border must be
    /// <see cref="MainViewModel.GroupCoverSize"/> wide. A NaN width means the size never arrived
    /// (in the old template an ancestor binding that stayed unresolved), in which case the Border
    /// grows to the decoded artwork's natural size. Repair the element and leave a WARN behind.
    /// </summary>
    private void RepairGroupCoverSizeIfNeeded(FrameworkElement cover, CollectionViewGroup group)
    {
        var expected = _vm.GroupCoverSize;
        if (!double.IsNaN(cover.Width) && Math.Abs(cover.Width - expected) <= 0.5)
        {
            return;
        }

        var first = group.Items.OfType<TrackRowViewModel>().FirstOrDefault();
        _host.Logger.Warn(
            $"Album group cover size was not applied (width={cover.Width:F1}, expected={expected:F1}, " +
            $"group={first?.AlbumGroupTitle ?? "?"}); repairing the element.");
        cover.Width = expected;
        cover.Height = expected;
    }

    private void OnGroupCoverIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // The ListView creates group containers for measuring before they enter the visible
        // tree; those containers never raise Loaded but do become visible later, when the user
        // scrolls. IsVisibleChanged is the only event that fires for every path, so it drives
        // both loading the album art and releasing it once the group leaves the viewport.
        if (sender is not FrameworkElement element ||
            element.DataContext is not CollectionViewGroup group)
        {
            return;
        }

        var nowVisible = e.NewValue is true;
        if (nowVisible)
        {
            RequestGroupCover(group);
            return;
        }

        // Releasing the image mutates a binding, which invalidates measure. Defer it until the
        // current layout pass is over so container recycling/visibility changes cannot feed
        // back into the measure pass that produced this event.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!element.IsVisible && ReferenceEquals(element.DataContext, group))
            {
                ReleaseGroupCover(group);
            }
        }));
    }

    private void OnGroupCoverDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is CollectionViewGroup previous && !ReferenceEquals(e.NewValue, previous))
        {
            ReleaseGroupCover(previous);
        }

        if (sender is FrameworkElement { IsVisible: true } && e.NewValue is CollectionViewGroup group)
        {
            RequestGroupCover(group);
        }
    }

    private void OnGroupCoverUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CollectionViewGroup group })
        {
            ReleaseGroupCover(group);
        }
    }

    private void RequestGroupCover(CollectionViewGroup group)
    {
        var first = group.Items.OfType<TrackRowViewModel>().FirstOrDefault();
        if (first is not null)
        {
            _ = _vm.LoadGroupCoverAsync(first);
        }
    }

    private void ReleaseGroupCover(CollectionViewGroup group)
    {
        var first = group.Items.OfType<TrackRowViewModel>().FirstOrDefault();
        if (first is not null && first.GroupCoverImage is not null)
        {
            first.GroupCoverImage = null;
        }
    }

    private void OnSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceList.SelectedItem is SourceNodeViewModel node && !ReferenceEquals(node, _vm.SelectedSource))
        {
            _vm.SelectedSource = node;
        }
    }

    private void ScrollToCurrentRow()
    {
        // The actual centering is deferred one dispatcher turn (see MainWindow.NowPlaying.cs):
        // right after playback starts or a playlist/view switch the row containers are usually
        // not generated yet, and WPF's ScrollIntoView only makes a row barely visible.
        ScheduleCenterCurrentRow();
    }

    /// <summary>
    /// Detects a scroll feedback loop (ScrollViewer anchoring re-posting itself instead of
    /// scrolling) and tries to break it by snapping the offset to a whole pixel value.
    /// </summary>
    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (ReferenceEquals(sender, GroupedList) &&
            (now - _lastHeaderInsetCheck).TotalMilliseconds >= 250)
        {
            // The scroll bar appears/disappears as the grouped list grows and shrinks, which moves
            // the right edge of the song rows; keep the pinned header block aligned with it.
            _lastHeaderInsetCheck = now;
            UpdateGroupedHeaderInset();
        }

        if ((now - _scrollBurstWindow).TotalSeconds >= 1.0)
        {
            _scrollBurstWindow = now;
            _scrollBurstEvents = 0;
            _scrollBurstTravel = 0;
            _scrollBurstLastOffset = e.VerticalOffset;
        }

        _scrollBurstEvents++;
        _scrollBurstTravel += Math.Abs(e.VerticalOffset - _scrollBurstLastOffset);
        _scrollBurstLastOffset = e.VerticalOffset;

        if (_scrollBurstEvents < 200 || (now - _scrollBurstLastLog).TotalSeconds < 5.0)
        {
            return;
        }

        _scrollBurstLastLog = now;
        _host.Logger.Warn(
            $"Suspicious scroll burst: events={_scrollBurstEvents} travel={_scrollBurstTravel:F1}px " +
            $"offset={e.VerticalOffset:F1} extent={e.ExtentHeight:F1} viewport={e.ViewportHeight:F1} " +
            $"source={(sender as FrameworkElement)?.Name ?? sender?.GetType().Name}");

        // A fractional offset that the anchoring logic can never reach is one of the ways the
        // ScrollViewer can keep re-posting scroll operations. Snapping to a whole pixel value
        // gives the loop a fixed point; this is a no-op when no loop is running.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var viewer = sender is DependencyObject root ? FindScrollViewer(root) : null;
            viewer?.ScrollToVerticalOffset(Math.Round(viewer.VerticalOffset));
        }));
    }

    // =====================================================================
    // Menus and context menu actions
    // =====================================================================
    private void OnPlayModeButtonClick(object sender, RoutedEventArgs e)
    {
        if (_playModeMenu is { IsOpen: true })
        {
            _playModeMenu.IsOpen = false;
            return;
        }

        if (sender is not Button button)
        {
            return;
        }

        // PlacementTarget + Top：按钮在传输栏右端，菜单向上展开，避免贴到屏幕下缘。
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Top,
            DataContext = _vm
        };

        menu.Items.Add(CreatePlayModeMenuItem("顺序播放", PlaybackMode.Sequential, "PlayModeSequential"));
        menu.Items.Add(CreatePlayModeMenuItem("随机播放", PlaybackMode.Shuffle, "PlayModeShuffle"));
        menu.Items.Add(CreatePlayModeMenuItem("单曲循环", PlaybackMode.RepeatOne, "PlayModeRepeatOne"));

        _playModeMenu = menu;
        menu.IsOpen = true;
    }

    private MenuItem CreatePlayModeMenuItem(string header, PlaybackMode mode, string automationId)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = true,
            IsChecked = _vm.PlayMode == mode
        };

        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) => _vm.PlayMode = mode;
        return item;
    }

    private void OnVolumeAreaMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 与音量快捷键共用同一套 5% 步进逻辑（见 MainViewModel.AdjustVolume）：值会吸附到
        // 5% 的整数倍，滚轮与快捷键因此得到完全一致的显示百分比。
        var direction = Math.Sign(e.Delta);
        if (direction == 0)
        {
            return;
        }

        _vm.AdjustVolume(direction);
        e.Handled = true;
    }

    private void OnExitMenuClick(object sender, RoutedEventArgs e) => Close();

    private void OnFieldGuideMenuClick(object sender, RoutedEventArgs e) =>
        new FieldGuideWindow { Owner = this }.ShowDialog();

    private async void OnPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (TrackGrid.SelectedItem is TrackRowViewModel row)
        {
            await _vm.EditTagsCommand.ExecuteAsync(row);
        }
    }

    private async void OnWebTagSearchClick(object sender, RoutedEventArgs e)
    {
        if (TrackGrid.SelectedItem is TrackRowViewModel row)
        {
            await _vm.SearchWebTagsCommand.ExecuteAsync(row);
        }
    }

    private void OnPresetButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var menu = new ContextMenu { DataContext = _vm };
        foreach (var preset in _vm.Presets.ToList())
        {
            var item = new MenuItem { Header = preset.Name };
            var captured = preset;
            item.Click += (_, _) => _vm.ApplyPreset(captured);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var saveItem = new MenuItem { Header = "保存当前布局为预设…" };
        saveItem.Click += (_, _) =>
        {
            var name = _dialogs.AskText("保存显示预设", "预设名称", _vm.CurrentLayout.PresetName ?? "我的布局");
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            // 同名预设先确认是否覆盖；取消则保持现有预设不变。
            if (_vm.Presets.Any(preset =>
                    string.Equals(preset.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                !_dialogs.Confirm($"已存在名为“{name.Trim()}”的显示预设，是否覆盖？", "覆盖显示预设"))
            {
                return;
            }

            _vm.SaveCurrentLayoutAsPreset(name);
        };
        menu.Items.Add(saveItem);

        var manageItem = new MenuItem { Header = "列与布局…" };
        manageItem.Click += (_, _) => _vm.EditColumnsCommand.Execute(null);
        menu.Items.Add(manageItem);

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async void OnAddToPlaylistClick(object sender, RoutedEventArgs e) =>
        await _vm.AddSelectedToPlaylistCommand.ExecuteAsync(null);

    private async void OnRemoveSelectedClick(object sender, RoutedEventArgs e) =>
        await _vm.RemoveSelectedCommand.ExecuteAsync(null);

    private void OnRevealClick(object sender, RoutedEventArgs e) => _vm.RevealInExplorerCommand.Execute(null);

    // =====================================================================
    // Seek / volume
    // =====================================================================
    // The Slider itself handles PreviewMouseLeftButtonDown when IsMoveToPointEnabled is
    // true, and that class handler both marks the event handled (so XAML handlers never
    // ran) and assigns Value directly (which drops the OneWay binding). We therefore
    // implement click + drag ourselves and listen with handledEventsToo so nothing can
    // swallow the gesture.
    private void HookSeekSlider()
    {
        SeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnSeekMouseDown), handledEventsToo: true);
        SeekSlider.AddHandler(PreviewMouseMoveEvent,
            new MouseEventHandler(OnSeekMouseMove), handledEventsToo: true);
        SeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnSeekMouseUp), handledEventsToo: true);
        SeekSlider.LostMouseCapture += OnSeekLostCapture;
    }

    private void OnSeekMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _host.Logger.Debug(
            $"Seek pointer down: x={e.GetPosition(SeekSlider).X:F1} width={SeekSlider.ActualWidth:F1} " +
            $"sliderValue={SeekSlider.Value:F2}s position={_vm.PositionSeconds:F2}s duration={_vm.DurationSeconds:F2}s " +
            $"source={e.OriginalSource?.GetType().Name}");

        if (_vm.DurationSeconds <= 0)
        {
            return;
        }

        _seekStartX = e.GetPosition(SeekSlider).X;
        _seekStartPosition = _vm.PositionSeconds;
        _seekDragged = false;
        _vm.BeginSeek();
        // CaptureMouse can synchronously deliver a synthetic MouseMove; keep the gesture
        // flag false until it has settled so the log contains exactly one "down" entry.
        SeekSlider.CaptureMouse();
        _seekInteracting = true;
        ApplySeekTarget(ComputeSeekValue(e), "down");
        e.Handled = true;
    }

    private void OnSeekMouseMove(object sender, MouseEventArgs e)
    {
        if (!_seekInteracting || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (Math.Abs(e.GetPosition(SeekSlider).X - _seekStartX) >= 2)
        {
            _seekDragged = true;
        }

        ApplySeekTarget(ComputeSeekValue(e), "move");
        e.Handled = true;
    }

    private void OnSeekMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seekInteracting || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        ApplySeekTarget(ComputeSeekValue(e), "up");
        e.Handled = true;
        CommitSeekInteraction();
        SeekSlider.ReleaseMouseCapture();
    }

    private void OnSeekLostCapture(object sender, MouseEventArgs e)
    {
        if (!_seekInteracting)
        {
            return;
        }

        _host.Logger.Debug("Seek lost mouse capture before mouse-up; committing current target.");
        CommitSeekInteraction();
    }

    private double ComputeSeekValue(MouseEventArgs e)
    {
        // Ratio against the whole slider is deterministic and independent of the template's
        // track/thumb sizing (which is only a few pixels of difference at any duration).
        var position = e.GetPosition(SeekSlider);
        var ratio = Math.Clamp(position.X / Math.Max(SeekSlider.ActualWidth, 1), 0, 1);
        var value = SeekSlider.Minimum + ratio * (SeekSlider.Maximum - SeekSlider.Minimum);
        _host.Logger.Debug(
            $"Seek point: x={position.X:F1} width={SeekSlider.ActualWidth:F1} ratio={ratio:F3} value={value:F2}s");
        return value;
    }

    private void ApplySeekTarget(double value, string phase)
    {
        _seekTargetSeconds = value;
        // SetCurrentValue keeps the OneWay binding to PositionSeconds alive.
        SeekSlider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty, value);

        var now = Environment.TickCount64;
        if (phase != "move" || now - _lastSeekLogTimestamp >= 120)
        {
            _lastSeekLogTimestamp = now;
            _host.Logger.Debug($"Seek target ({phase}): {value:F2}s");
        }
    }

    private void CommitSeekInteraction()
    {
        // MouseUp, LostCapture and double-clicks can all end up here; commit only once.
        if (!_seekInteracting)
        {
            return;
        }

        _seekInteracting = false;
        var target = _seekTargetSeconds;
        var delta = Math.Abs(target - _seekStartPosition);
        var shouldSeek = _seekDragged || delta >= 0.75;
        _host.Logger.Debug(
            $"Seek gesture end: target={target:F2}s startPosition={_seekStartPosition:F2}s delta={delta:F2}s " +
            $"dragged={_seekDragged} performSeek={shouldSeek}");

        if (shouldSeek)
        {
            _vm.CommitSeek(target);
        }
        else
        {
            // A plain click on the thumb is not a seek request; just resume position updates.
            _vm.CancelSeek();
        }
    }

    // ADR-015: the waveform control raises the gesture in three phases; this adapter keeps the
    // view model unchanged (same BeginSeek / CommitSeek / CancelSeek contract as the slider).
    private void HookWaveformSeek()
    {
        WaveformView.SeekStarted += OnWaveformSeekStarted;
        WaveformView.SeekPreview += OnWaveformSeekPreview;
        WaveformView.SeekCommitted += OnWaveformSeekCommitted;
        WaveformView.SeekCancelled += OnWaveformSeekCancelled;
    }

    private void OnWaveformSeekStarted(object? sender, EventArgs e)
    {
        _host.Logger.Debug(
            $"Waveform seek start: position={_vm.PositionSeconds:F2}s duration={_vm.DurationSeconds:F2}s " +
            $"width={WaveformView.ActualWidth:F1} peaks={WaveformView.Peaks?.ColumnCount ?? 0}");
        if (_vm.DurationSeconds <= 0)
        {
            return;
        }

        _vm.BeginSeek();
    }

    private void OnWaveformSeekPreview(object? sender, WaveformSeekEventArgs e) => _vm.PreviewSeek(e.Seconds);

    private void OnWaveformSeekCommitted(object? sender, WaveformSeekEventArgs e)
    {
        _host.Logger.Debug($"Waveform seek commit: target={e.Seconds:F2}s");
        if (_vm.DurationSeconds <= 0)
        {
            _vm.CancelSeek();
            return;
        }

        // Mouse-up always commits (ADR-015): unlike the slider there is no click-dead-zone,
        // because clicking a waveform column is an explicit "go there" on a 2-4 s scale.
        _vm.CommitSeek(e.Seconds);
    }

    private void OnWaveformSeekCancelled(object? sender, EventArgs e)
    {
        _host.Logger.Debug("Waveform seek cancelled (Escape).");
        _vm.CancelSeek();
    }

    private static bool IsWaveformNavigationKey(Key key) =>
        key is Key.Left or Key.Right or Key.PageUp or Key.PageDown or Key.Home or Key.End;

    // =====================================================================
    // Keyboard and drag & drop
    // =====================================================================

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // ADR-034：BPM 修正 / Tap 轻点测速浮层打开时，按键留给浮层处理，避免敲空格测速把歌暂停。
        if (NowPlayingBpmPopup.IsOpen)
        {
            return;
        }

        // ADR-015: 波形进度条有键盘焦点时，跳转键（←/→/PageUp/PageDown/Home/End）必须留给控件，
        // 不能被全局播放快捷键或窗口内置动作吃掉。
        if (Keyboard.FocusedElement is WaveformSeekBar && IsWaveformNavigationKey(e.Key))
        {
            _host.Logger.Debug($"Waveform navigation key left to control: {e.Key} modifiers={Keyboard.Modifiers}");
            return;
        }

        // User-configured playback hotkeys win over the built-in shortcuts below.
        if (_hotkeys.TryHandleLocal(e))
        {
            e.Handled = true;
            return;
        }

        var focused = Keyboard.FocusedElement as DependencyObject;
        var isTextInput = focused is TextBox ||
                          (focused is FrameworkElement fe && fe.TemplatedParent is TextBox);

        // Alt+Enter 是“打开标签属性”的内置回退快捷键。WPF 在按住 Alt 时把按键报告为
        // Key.System（真实按键在 SystemKey），因此必须在 switch 之前单独判断，不能直接
        // 匹配 case Key.Enter，否则内置 Alt+Enter 永远不会触发（自定义绑定走
        // TryHandleLocal 时已做过同样的归一化，所以那里的 Alt+Enter 是正常的）。
        // 用户为该动作配置了快捷键后不再响应内置组合，避免同一动作存在两个快捷键。
        var normalizedKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (normalizedKey == Key.Enter && Keyboard.Modifiers == ModifierKeys.Alt && !isTextInput &&
            !HasConfiguredHotkey(HotkeyAction.OpenTagProperties) &&
            _vm.SelectedRow is { } propertiesRow)
        {
            e.Handled = true;
            await _vm.EditTagsCommand.ExecuteAsync(propertiesRow);
            return;
        }

        switch (e.Key)
        {
            case Key.Space when !isTextInput:
                e.Handled = true;
                await _vm.PlayPauseCommand.ExecuteAsync(null);
                break;
            case Key.Enter when !isTextInput && _vm.SelectedRow is { } row:
                e.Handled = true;
                await _vm.PlayRowCommand.ExecuteAsync(row);
                break;
            case Key.Delete when !isTextInput:
                e.Handled = true;
                await _vm.RemoveSelectedCommand.ExecuteAsync(null);
                break;
            case Key.O when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                await _vm.AddFilesCommand.ExecuteAsync(null);
                break;
            case Key.F5:
                e.Handled = true;
                await _vm.RescanCommand.ExecuteAsync(null);
                break;
            case Key.M when Keyboard.Modifiers == ModifierKeys.Control:
                // Ctrl+M 是静音切换的内置回退快捷键；用户自定义该动作后交给上面的
                // TryHandleLocal 处理。
                if (!HasConfiguredHotkey(HotkeyAction.Mute))
                {
                    e.Handled = true;
                    _vm.ToggleMuteCommand.Execute(null);
                }

                break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                // Ctrl+F opens the advanced search unless the user bound that action to a
                // different key in the settings dialog (then TryHandleLocal above ran it).
                if (!HasConfiguredHotkey(HotkeyAction.OpenSearch))
                {
                    e.Handled = true;
                    _vm.OpenAdvancedSearchCommand.Execute(null);
                }

                break;
        }
    }

    /// <summary>
    /// 判断某个可配置动作是否已被用户绑定了快捷键；内置回退快捷键仅在该动作未自定义时生效。
    /// </summary>
    private bool HasConfiguredHotkey(HotkeyAction action) =>
        _vm.Settings.Hotkeys.Any(h => h.Action == action && !string.IsNullOrWhiteSpace(h.Gesture));

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                _vm.ClearFilterCommand.Execute(null);
                break;
            case Key.Enter:
                e.Handled = true;
                // Leave the search box so Enter/Space act on the list selection again.
                if (_vm.IsGroupedViewActive)
                {
                    GroupedList.Focus();
                }
                else
                {
                    TrackGrid.Focus();
                }

                break;
        }
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var dropped = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (dropped is null || dropped.Length == 0)
        {
            return;
        }

        try
        {
            var files = new List<string>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false
            };
            foreach (var item in dropped)
            {
                if (Directory.Exists(item))
                {
                    var directory = item;
                    files.AddRange(await Task.Run(() => Directory.EnumerateFiles(directory, "*", options)
                        .Where(PathUtil.IsSupportedAudio)
                        .ToList()));
                }
                else if (File.Exists(item) && PathUtil.IsSupportedAudio(item))
                {
                    files.Add(item);
                }
            }

            if (files.Count > 0)
            {
                await _vm.AddFilesToCurrentPlaylistAsync(files, skipExisting: true);
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Handling dropped files failed", ex);
            _vm.SetStatus($"拖入文件失败：{ex.Message}");
        }
    }

    // =====================================================================
    // External files (Explorer "open with") and lifecycle
    // =====================================================================
    public void HandleExternalFiles(IReadOnlyList<string> paths)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        _ = _vm.HandleExternalFilesAsync(paths);
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(_host, _vm) { Owner = this };
        window.ShowDialog();
        ApplyLayout();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            var captured = CaptureLayoutFromGrid();
            if (captured is not null)
            {
                _vm.PersistCurrentLayout(captured);
            }
            else
            {
                _vm.PersistCurrentLayout();
            }
            var settings = _host.Settings.Current;
            settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                settings.WindowWidth = ActualWidth;
                settings.WindowHeight = ActualHeight;
                settings.WindowLeft = Left;
                settings.WindowTop = Top;
            }

            settings.SidebarWidth = SidebarColumn.ActualWidth;
            if (_vm.ShowAlbumPanel && AlbumPanelColumn.ActualWidth > 0)
            {
                _albumPanelWidth = Math.Clamp(AlbumPanelColumn.ActualWidth, 240, 520);
            }

            settings.AlbumPanelWidth = _albumPanelWidth;

            await _vm.ShutdownAsync();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"Saving state on close failed: {ex.Message}");
        }
        finally
        {
            _vm.Dispose();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableDarkTitleBar();
        // Global hotkeys need the window handle, which only exists from this point on.
        _hotkeys.Initialize(this);
    }

    private void TryEnableDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var enabled = ThemeManager.IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            }
        }
        catch
        {
            // Older Windows builds simply keep the default title bar.
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
