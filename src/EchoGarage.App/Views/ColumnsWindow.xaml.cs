using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private string _secondaryFormat;

    public ColumnEditRow(
        string key,
        string header,
        bool isVisible,
        double width,
        ColumnPin pin,
        string? secondaryFormat)
    {
        Key = key;
        Header = header;
        _isVisible = isVisible;
        _width = width.ToString("0");
        Pin = pin;
        _secondaryFormat = secondaryFormat ?? string.Empty;
    }

    public string Key { get; }
    public string Header { get; }

    /// <summary>Header pin side; preserved through the dialog so column pinning survives editing.</summary>
    public ColumnPin Pin { get; }

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

    /// <summary>Second-row text template, e.g. <c>{bitrate}/{sampleRate}</c>.</summary>
    public string SecondaryFormat
    {
        get => _secondaryFormat;
        set => SetProperty(ref _secondaryFormat, value);
    }

    public static string FormatHint { get; } =
        "次要信息格式；留空则不显示。可用占位符：" + SecondaryTextFormat.DescribeTokens() +
        "。示例：{bitrate}/{sampleRate}";

    /// <summary>Cover / status / index cells are single-line by design and cannot show a second line.</summary>
    public bool SupportsSecondary => Key is not ("cover" or "status" or "index");

    public Visibility SecondaryRowVisibility => SupportsSecondary ? Visibility.Visible : Visibility.Collapsed;

    public double ParsedWidth => double.TryParse(Width, out var parsed) && parsed > 20 ? parsed : 100;
}

public partial class ColumnsWindow : ThemedWindow
{
    private sealed record SortOption(string Label, string? Property);

    private readonly ObservableCollection<ColumnEditRow> _rows = [];
    private readonly ObservableCollection<ColumnPreset> _presets = [];
    private readonly List<SortOption> _sortOptions = [];
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

            _rows.Add(new ColumnEditRow(
                column.Key,
                descriptor.Header,
                column.Visible,
                column.Width,
                column.Pin,
                column.SecondaryFormat));
        }

        foreach (var preset in presets)
        {
            _presets.Add(preset);
        }

        ColumnList.ItemsSource = _rows;
        PresetList.ItemsSource = _presets;

        // Sorting does not require the column to be visible: hiding "album" or
        // "album artist" is a normal way to keep the grouped view while saving width.
        _sortOptions.Add(new SortOption("（默认顺序）", null));
        foreach (var row in _rows)
        {
            var descriptor = ColumnCatalog.Find(row.Key);
            if (descriptor is not { Sortable: true, SortProperty: not null })
            {
                continue;
            }

            _sortOptions.Add(new SortOption(row.Header, descriptor.SortProperty));
        }

        SortColumnBox.ItemsSource = _sortOptions;
        var currentSort = _sortOptions.FirstOrDefault(option =>
            option.Property is not null &&
            string.Equals(option.Property, layout.SortColumn, StringComparison.OrdinalIgnoreCase));
        SortColumnBox.SelectedItem = currentSort ?? _sortOptions[0];
        DescendingBox.IsChecked = layout.SortDescending;
        GroupDiscTrackBox.IsChecked = normalized.GroupSortByDiscTrack;

        // Users commonly fill in per-column formats but forget the global switch. Turn it
        // on automatically when any format is already configured (or when one is typed),
        // so "确定" always produces the rows they just described.
        ShowSecondaryBox.IsChecked = normalized.ShowSecondaryInfo ||
                                     _rows.Any(r => !string.IsNullOrWhiteSpace(r.SecondaryFormat));
        foreach (var row in _rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ColumnEditRow.SecondaryFormat) &&
            sender is ColumnEditRow row &&
            !string.IsNullOrWhiteSpace(row.SecondaryFormat))
        {
            ShowSecondaryBox.IsChecked = true;
        }
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
                row.SecondaryFormat = column.SecondaryFormat ?? string.Empty;
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
        var layout = new PlaylistViewLayout
        {
            GroupCoverSize = _groupCoverSize,
            ShowSecondaryInfo = ShowSecondaryBox.IsChecked == true
        };
        foreach (var row in _rows)
        {
            layout.Columns.Add(new PlaylistColumn
            {
                Key = row.Key,
                Visible = row.IsVisible,
                Width = row.ParsedWidth,
                Pin = row.Pin,
                SecondaryFormat = row.SecondaryFormat
            });
        }

        if (SortColumnBox.SelectedItem is SortOption sortOption)
        {
            layout.SortColumn = sortOption.Property;
        }

        layout.SortDescending = DescendingBox.IsChecked == true;
        layout.GroupSortByDiscTrack = GroupDiscTrackBox.IsChecked == true;
        return ViewLayoutFactory.Normalize(layout);
    }

    private void OnShowFieldGuide(object sender, RoutedEventArgs e) =>
        new FieldGuideWindow { Owner = this }.ShowDialog();

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
