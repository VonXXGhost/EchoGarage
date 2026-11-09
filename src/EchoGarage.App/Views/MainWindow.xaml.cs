using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private bool _closing;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        _dialogs = new DialogService(this);
        _vm = new MainViewModel(host, _dialogs);
        DataContext = _vm;

        _vm.LayoutChanged += (_, _) => ApplyLayout();
        _vm.SortChanged += (_, _) => UpdateSortGlyphs();
        _vm.SettingsRequested += (_, _) => OpenSettings();
        _vm.CurrentRowChanged += (_, _) => ScrollToCurrentRow();

        ApplyWindowPlacement();
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        DragOver += OnWindowDragOver;
        Drop += OnWindowDrop;
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
    private static readonly Dictionary<string, string?> SortPropertyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cover"] = null,
        ["status"] = null,
        ["index"] = nameof(TrackRowViewModel.RowNumber),
        ["title"] = nameof(TrackRowViewModel.Title),
        ["artist"] = nameof(TrackRowViewModel.Artist),
        ["album"] = nameof(TrackRowViewModel.Album),
        ["albumArtist"] = nameof(TrackRowViewModel.AlbumArtist),
        ["trackNumber"] = nameof(TrackRowViewModel.TrackNumberSort),
        ["discNumber"] = nameof(TrackRowViewModel.DiscNumberSort),
        ["year"] = nameof(TrackRowViewModel.YearSort),
        ["genre"] = nameof(TrackRowViewModel.Genre),
        ["duration"] = nameof(TrackRowViewModel.DurationSort),
        ["bitrate"] = nameof(TrackRowViewModel.BitrateSort),
        ["sampleRate"] = nameof(TrackRowViewModel.SampleRateSort),
        ["codec"] = nameof(TrackRowViewModel.Codec),
        ["rating"] = nameof(TrackRowViewModel.RatingSort),
        ["addedAt"] = nameof(TrackRowViewModel.AddedSort),
        ["fileSize"] = nameof(TrackRowViewModel.FileSizeSort),
        ["path"] = nameof(TrackRowViewModel.PathText),
        ["comment"] = nameof(TrackRowViewModel.Comment)
    };

    private void ApplyLayout()
    {
        var layout = _vm.CurrentLayout;
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
            var gridColumn = new DataGridTemplateColumn
            {
                Header = descriptor.Header,
                CellTemplate = cellTemplate,
                Width = new DataGridLength(Math.Max(descriptor.MinWidth, column.Width), DataGridLengthUnitType.Pixel),
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
    }

    private static string ToPascalCase(string key) =>
        string.IsNullOrEmpty(key) ? key : char.ToUpperInvariant(key[0]) + key[1..];

    private PlaylistViewLayout CaptureLayoutFromGrid()
    {
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

        foreach (var hidden in layout.Columns.Where(c => c.Visible &&
                                                          ordered.All(o => !string.Equals(o.Key, c.Key, StringComparison.OrdinalIgnoreCase))))
        {
            // Columns that exist in the layout but are not on screen stay hidden.
            ordered.Add(new PlaylistColumn { Key = hidden.Key, Width = hidden.Width, Visible = false });
        }

        foreach (var hidden in layout.Columns.Where(c => !c.Visible))
        {
            if (ordered.All(o => !string.Equals(o.Key, hidden.Key, StringComparison.OrdinalIgnoreCase)))
            {
                ordered.Add(hidden.Clone());
            }
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

    private void OnGroupHeaderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CollectionViewGroup group })
        {
            var first = group.Items.OfType<TrackRowViewModel>().FirstOrDefault();
            if (first is not null)
            {
                _ = _vm.LoadRowCoverAsync(first);
            }
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

    private void OnSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceList.SelectedItem is SourceNodeViewModel node && !ReferenceEquals(node, _vm.SelectedSource))
        {
            _vm.SelectedSource = node;
        }
    }

    private void ScrollToCurrentRow()
    {
        if (_vm.CurrentRow is not null && TrackGrid.Items.Contains(_vm.CurrentRow))
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

    private async void OnAddToPlaylistClick(object sender, RoutedEventArgs e) =>
        await _vm.AddSelectedToPlaylistCommand.ExecuteAsync(null);

    private async void OnRemoveSelectedClick(object sender, RoutedEventArgs e) =>
        await _vm.RemoveSelectedCommand.ExecuteAsync(null);

    private void OnRevealClick(object sender, RoutedEventArgs e) => _vm.RevealInExplorerCommand.Execute(null);

    // =====================================================================
    // Seek / volume
    // =====================================================================
    private void OnSeekDragStarted(object sender, DragStartedEventArgs e)
    {
        _seekInteracting = true;
        _vm.BeginSeek();
    }

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _seekInteracting = false;
        _vm.CommitSeek(SeekSlider.Value);
    }

    private void OnSeekClick(object sender, MouseButtonEventArgs e)
    {
        _seekInteracting = true;
        _vm.BeginSeek();
    }

    private void OnSeekMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seekInteracting)
        {
            return;
        }

        _seekInteracting = false;
        _vm.CommitSeek(SeekSlider.Value);
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
            case Key.Enter when !isTextInput && TrackGrid.SelectedItem is TrackRowViewModel row:
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
            _vm.PersistCurrentLayout(CaptureLayoutFromGrid());
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
            var enabled = 1;
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
