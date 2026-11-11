using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EchoGarage.App.Services;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.App.Views;

public sealed class ColumnEditRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private bool _isVisible;
    private string _width;

    public ColumnEditRow(string key, string header, bool isVisible, double width)
    {
        Key = key;
        Header = header;
        _isVisible = isVisible;
        _width = width.ToString("0");
    }

    public string Key { get; }
    public string Header { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public string Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    public double ParsedWidth => double.TryParse(Width, out var parsed) && parsed > 20 ? parsed : 100;
}

public partial class ColumnsWindow : ThemedWindow
{
    private readonly ObservableCollection<ColumnEditRow> _rows = [];
    private readonly ObservableCollection<ColumnPreset> _presets = [];
    private readonly double _groupCoverSize;

    public ColumnsWindow(PlaylistViewLayout layout, IReadOnlyList<ColumnPreset> presets)
    {
        InitializeComponent();
        var normalized = ViewLayoutFactory.Normalize(layout);
        _groupCoverSize = normalized.GroupCoverSize;
        foreach (var column in normalized.Columns)
        {
            var descriptor = ColumnCatalog.Find(column.Key);
            if (descriptor is null)
            {
                continue;
            }

            _rows.Add(new ColumnEditRow(column.Key, descriptor.Header, column.Visible, column.Width));
        }

        foreach (var preset in presets)
        {
            _presets.Add(preset);
        }

        ColumnList.ItemsSource = _rows;
        PresetList.ItemsSource = _presets;
        SortColumnBox.ItemsSource = new[] { "（默认顺序）" }.Concat(
            _rows.Where(r => r.IsVisible).Select(r => r.Header)).ToList();

        var currentSortHeader = _rows.FirstOrDefault(r => r.Key == layout.SortColumn)?.Header;
        SortColumnBox.SelectedItem = currentSortHeader ?? "（默认顺序）";
        DescendingBox.IsChecked = layout.SortDescending;
        GroupDiscTrackBox.IsChecked = normalized.GroupSortByDiscTrack;
    }

    public ColumnsEditResult? Result { get; private set; }

    private void OnApplyPreset(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not ColumnPreset preset)
        {
            return;
        }

        var byKey = _rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ColumnEditRow>();
        foreach (var column in preset.Columns)
        {
            if (byKey.TryGetValue(column.Key, out var row))
            {
                row.IsVisible = column.Visible;
                row.Width = column.Width.ToString("0");
                ordered.Add(row);
            }
        }

        foreach (var row in _rows.Where(r => !ordered.Contains(r)))
        {
            ordered.Add(row);
        }

        _rows.Clear();
        foreach (var row in ordered)
        {
            _rows.Add(row);
        }
    }

    private void OnSavePreset(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputWindow("保存为显示预设", "预设名称", "我的布局") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var existing = _presets.FirstOrDefault(p => string.Equals(p.Name, dialog.Value, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _presets.Remove(existing);
        }

        var layout = BuildLayout();
        _presets.Add(ColumnPreset.FromLayout(dialog.Value, layout));
    }

    private void OnDeletePreset(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is ColumnPreset preset && preset.Name is not ("标准" or "简洁" or "文件管理"))
        {
            _presets.Remove(preset);
        }
        else if (PresetList.SelectedItem is ColumnPreset builtIn)
        {
            MessageBox.Show(this, $"内置预设“{builtIn.Name}”不能删除。", "EchoGarage", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ColumnEditRow row })
        {
            var index = _rows.IndexOf(row);
            if (index > 0)
            {
                _rows.Move(index, index - 1);
            }
        }
    }

    private void OnMoveDown(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ColumnEditRow row })
        {
            var index = _rows.IndexOf(row);
            if (index >= 0 && index < _rows.Count - 1)
            {
                _rows.Move(index, index + 1);
            }
        }
    }

    private PlaylistViewLayout BuildLayout()
    {
        // The dialog edits columns only; the grouped album-cover size and the per-group
        // disc/track ordering preference are adjusted outside and must survive a round-trip.
        var layout = new PlaylistViewLayout { GroupCoverSize = _groupCoverSize };
        foreach (var row in _rows)
        {
            layout.Columns.Add(new PlaylistColumn
            {
                Key = row.Key,
                Visible = row.IsVisible,
                Width = row.ParsedWidth
            });
        }

        if (SortColumnBox.SelectedItem is string header && header != "（默认顺序）")
        {
            layout.SortColumn = SortPropertyFor(header);
        }

        layout.SortDescending = DescendingBox.IsChecked == true;
        layout.GroupSortByDiscTrack = GroupDiscTrackBox.IsChecked == true;
        return ViewLayoutFactory.Normalize(layout);
    }

    private string? SortPropertyFor(string header)
    {
        var key = _rows.FirstOrDefault(r => r.Header == header)?.Key;
        return key switch
        {
            "index" => "RowNumber",
            "title" => "Title",
            "artist" => "Artist",
            "album" => "Album",
            "albumArtist" => "AlbumArtist",
            "trackNumber" => "TrackNumberSort",
            "discNumber" => "DiscNumberSort",
            "year" => "YearSort",
            "genre" => "Genre",
            "duration" => "DurationSort",
            "bitrate" => "BitrateSort",
            "sampleRate" => "SampleRateSort",
            "codec" => "Codec",
            "rating" => "RatingSort",
            "addedAt" => "AddedSort",
            "fileSize" => "FileSizeSort",
            "path" => "PathText",
            "comment" => "Comment",
            _ => null
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_rows.All(r => !r.IsVisible))
        {
            MessageBox.Show(this, "至少保留一列可见。", "EchoGarage", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Result = new ColumnsEditResult(BuildLayout(), _presets.ToList());
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
