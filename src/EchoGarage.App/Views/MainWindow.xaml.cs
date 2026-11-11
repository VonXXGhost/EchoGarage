using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EchoGarage.App.Services;
using EchoGarage.App.ViewModels;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.App.Views;

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly MainViewModel _vm;
    private readonly DialogService _dialogs;
    private readonly Dictionary<DataGridColumn, string> _columnKeys = [];
    private bool _seekInteracting;
    private double _seekTargetSeconds;
    private double _seekStartX;
    private double _seekStartPosition;
    private bool _seekDragged;
    private long _lastSeekLogTimestamp;
    private bool _closing;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        _dialogs = new DialogService(this);
        _vm = new MainViewModel(host, _dialogs);
        _vm.LayoutProvider = CaptureLayoutFromGrid;
        _vm.CellTemplateProvider = key => TryFindResource("Cell." + ToPascalCase(key)) as DataTemplate;
        DataContext = _vm;

        _vm.LayoutChanged += (_, _) => ApplyLayout();
        _vm.SortChanged += (_, _) => UpdateSortGlyphs();
        _vm.SettingsRequested += (_, _) => OpenSettings();
        _vm.CurrentRowChanged += (_, _) => ScrollToCurrentRow();

        ApplyWindowPlacement();
        HookSeekSlider();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        DragOver += OnWindowDragOver;
        Drop += OnWindowDrop;
        ThemeManager.ThemeApplied += OnThemeApplied;
    }

    private void OnThemeApplied(object? sender, EchoGarage.Core.Models.ThemeKind theme) => TryEnableDarkTitleBar();

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeManager.ThemeApplied -= OnThemeApplied;
        Closed -= OnClosed;
    }

    public MainViewModel ViewModel => _vm;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _vm.InitializeAsync();
            ApplyLayout();

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
        AlbumPanelColumn.Width = new GridLength(Math.Clamp(settings.AlbumPanelWidth, 240, 520));

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
            ScrollToCurrentRow();
            return;
        }

        _host.Logger.Debug($"Applying layout: groupCoverSize={_vm.GroupCoverSize:F1} signature={signature}");
        RebuildColumns(layout, signature);
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
        // Before the first layout pass the grid has no columns; capturing then would
        // mark every column as hidden and destroy the saved layout.
        if (TrackGrid.Columns.Count == 0)
        {
            return null;
        }

        var layout = _vm.CurrentLayout.Clone();
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
            ordered.Add(new PlaylistColumn { Key = stored.Key, Width = width, Visible = true });
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

    private void OnTrackSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.SelectedRows.Clear();
        foreach (var item in TrackGrid.SelectedItems)
        {
            if (item is TrackRowViewModel row)
            {
                row.IsSelected = true;
                _vm.SelectedRows.Add(row);
            }
        }

        foreach (var item in e.RemovedItems)
        {
            if (item is TrackRowViewModel row)
            {
                row.IsSelected = false;
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
        _vm.SelectedRows.Clear();
        foreach (var item in GroupedList.SelectedItems)
        {
            if (item is TrackRowViewModel row)
            {
                row.IsSelected = true;
                _vm.SelectedRows.Add(row);
            }
        }

        foreach (var item in e.RemovedItems)
        {
            if (item is TrackRowViewModel row)
            {
                row.IsSelected = false;
            }
        }
    }

    private void OnGroupedDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GroupedList.SelectedItem is TrackRowViewModel row)
        {
            _ = _vm.PlayRowCommand.ExecuteAsync(row);
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

    private void OnGroupCoverThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        _vm.GroupCoverSize = Math.Clamp(_vm.GroupCoverSize + e.HorizontalChange,
            PlaylistViewLayout.MinGroupCoverSize, PlaylistViewLayout.MaxGroupCoverSize);
        e.Handled = true;
    }

    private void OnGroupCoverThumbDragCompleted(object sender, DragCompletedEventArgs e) =>
        _vm.PersistGroupCoverSize();

    private void OnGroupCoverLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CollectionViewGroup group })
        {
            var first = group.Items.OfType<TrackRowViewModel>().FirstOrDefault();
            if (first is not null)
            {
                _ = _vm.LoadGroupCoverAsync(first);
            }
        }
    }

    private void OnGroupCoverUnloaded(object sender, RoutedEventArgs e)
    {
        // Release the large cover while the album group is virtualized away.
        if (sender is FrameworkElement { DataContext: CollectionViewGroup group })
        {
            var first = group.Items.OfType<TrackRowViewModel>().FirstOrDefault();
            if (first is not null)
            {
                first.GroupCoverImage = null;
            }
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
        if (_vm.CurrentRow is null)
        {
            return;
        }

        if (_vm.IsGroupedViewActive)
        {
            if (GroupedList.Items.Contains(_vm.CurrentRow))
            {
                GroupedList.ScrollIntoView(_vm.CurrentRow);
            }

            return;
        }

        if (TrackGrid.Items.Contains(_vm.CurrentRow))
        {
            TrackGrid.ScrollIntoView(_vm.CurrentRow);
        }
    }

    // =====================================================================
    // Menus and context menu actions
    // =====================================================================
    private void OnMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not ContextMenu menu)
        {
            return;
        }

        menu.DataContext = _vm;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnExitMenuClick(object sender, RoutedEventArgs e) => Close();

    private void OnPlaySelectedClick(object sender, RoutedEventArgs e)
    {
        if (TrackGrid.SelectedItem is TrackRowViewModel row)
        {
            _ = _vm.PlayRowCommand.ExecuteAsync(row);
        }
    }

    private async void OnPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (TrackGrid.SelectedItem is TrackRowViewModel row)
        {
            await _vm.EditTagsCommand.ExecuteAsync(row);
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
            if (!string.IsNullOrWhiteSpace(name))
            {
                _vm.SaveCurrentLayoutAsPreset(name);
            }
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

    // =====================================================================
    // Keyboard and drag & drop
    // =====================================================================
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        var isTextInput = focused is TextBox ||
                          (focused is FrameworkElement fe && fe.TemplatedParent is TextBox);

        switch (e.Key)
        {
            case Key.Space when !isTextInput:
                e.Handled = true;
                await _vm.PlayPauseCommand.ExecuteAsync(null);
                break;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Alt && !isTextInput &&
                                _vm.SelectedRow is { } propertiesRow:
                e.Handled = true;
                await _vm.EditTagsCommand.ExecuteAsync(propertiesRow);
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
                e.Handled = true;
                _vm.ToggleMuteCommand.Execute(null);
                break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                _vm.EditColumnsCommand.Execute(null);
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

        var files = new List<string>();
        foreach (var item in dropped)
        {
            if (Directory.Exists(item))
            {
                var directory = item;
                files.AddRange(await Task.Run(() => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
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
            await _vm.AddFilesToCurrentPlaylistAsync(files);
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
            settings.AlbumPanelWidth = AlbumPanelColumn.ActualWidth;

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
