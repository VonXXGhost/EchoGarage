using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EchoGarage.App.Services;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using Track = EchoGarage.Core.Models.Track;

namespace EchoGarage.App.Views;

public partial class TagPropertiesWindow : ThemedWindow
{
    private const string MultiValueText = "<多值>";

    private static readonly (string Key, string Label)[] FieldDefinitions =
    [
        (TagFieldKeys.Title, "标题"),
        (TagFieldKeys.Artist, "艺术家"),
        (TagFieldKeys.Album, "专辑"),
        (TagFieldKeys.AlbumArtist, "专辑艺术家"),
        (TagFieldKeys.Composer, "作曲家"),
        (TagFieldKeys.Genre, "流派"),
        (TagFieldKeys.Year, "年份"),
        (TagFieldKeys.TrackNumber, "音轨号"),
        (TagFieldKeys.DiscNumber, "碟片号"),
        (TagFieldKeys.Comment, "备注")
    ];

    private enum CoverAction
    {
        None,
        Replace,
        Remove
    }

    private sealed class FieldEditor
    {
        public required string Key { get; init; }
        public required TextBox TextBox { get; init; }
        public required Button Picker { get; init; }
        public string InitialText { get; set; } = string.Empty;
        public IReadOnlyList<string> Values { get; set; } = [];
    }

    /// <summary>其他标签的一行编辑状态；<see cref="Value"/> 为空表示删除。</summary>
    public sealed class AdditionalTagEditor
    {
        public required string Name { get; init; }
        public required string Kind { get; init; }
        public required string Key { get; init; }
        public string InitialValue { get; init; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private readonly IReadOnlyList<Track> _tracks;
    private readonly IReadOnlyList<AudioTagData> _tags;
    private readonly List<FieldEditor> _editors = [];
    private readonly ObservableCollection<AdditionalTagEditor> _additionalTagEditors = [];
    private readonly List<AudioTagChange> _removedAdditionalTags = [];

    private byte[]? _coverBytes;
    private string? _coverMime;
    private CoverAction _coverAction = CoverAction.None;

    /// <summary>True when the selected files do not share the same cover (some have one, some don't, or they differ).</summary>
    private bool _coverMixed;

    public TagPropertiesWindow(IReadOnlyList<Track> tracks, IReadOnlyList<AudioTagData> tags)
    {
        if (tracks.Count == 0 || tags.Count == 0)
        {
            throw new ArgumentException("至少需要一个文件。", nameof(tracks));
        }

        InitializeComponent();

        _tracks = tracks;
        _tags = tags;

        Title = tracks.Count > 1 ? $"标签属性 - {tracks.Count} 个文件" : "标签属性";
        SaveButton.Content = tracks.Count > 1 ? $"保存到 {tracks.Count} 个文件" : "保存到文件";

        BuildFieldEditors();
        PopulateFieldValues();
        PopulateAdditionalTags(tags[0].AdditionalTags);

        if (tracks.Count > 1)
        {
            SelectionHint.Text = $"正在编辑 {tracks.Count} 个文件：只保存你修改过的字段，未修改的字段保持每个文件自己的原值。"
                + "其他标签只显示第一个文件的当前值，修改会应用到所有文件（清空或点 × 会删除该标签）。";
            SelectionHint.Visibility = Visibility.Visible;
        }

        // Cover handling mirrors the text fields: identical covers are shown once, different
        // covers are shown as <多值> and are only applied when the user picks/removes one.
        var covers = tags.Select(tag => tag.CoverBytes).ToList();
        var withCover = covers.Where(bytes => bytes is { Length: > 0 }).ToList();
        if (withCover.Count == 0)
        {
            _coverBytes = null;
            _coverMime = null;
        }
        else if (withCover.Count == covers.Count && withCover.All(bytes => bytes!.SequenceEqual(withCover[0]!)))
        {
            _coverBytes = withCover[0];
            _coverMime = tags.First(tag => tag.CoverBytes is { Length: > 0 }).CoverMime;
        }
        else
        {
            _coverMixed = true;
            _coverBytes = null;
            _coverMime = null;
        }

        _coverAction = CoverAction.None;
        RefreshCoverPreview();
        CoverConversionOptions.IsEnabled = true;
    }

    public TagBatchEdit? Result { get; private set; }

    private void BuildFieldEditors()
    {
        for (var i = 0; i < FieldDefinitions.Length; i++)
        {
            var definition = FieldDefinitions[i];
            var cell = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 76 });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = definition.Label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            cell.Children.Add(label);

            var editorHost = new Grid();
            editorHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            editorHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textBox = new TextBox
            {
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            editorHost.Children.Add(textBox);

            var picker = new Button
            {
                Content = "▾",
                Width = 24,
                Height = 24,
                Margin = new Thickness(4, 0, 0, 0),
                Visibility = Visibility.Collapsed,
                ToolTip = "该字段在多个文件中不一致，可从下拉框选择一个值",
                Focusable = false
            };
            picker.Style = (Style)FindResource("ValuePickerButton");
            Grid.SetColumn(picker, 1);
            editorHost.Children.Add(picker);

            Grid.SetColumn(editorHost, 1);
            cell.Children.Add(editorHost);

            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, (i % 2) * 2);
            FieldsGrid.Children.Add(cell);

            var editor = new FieldEditor
            {
                Key = definition.Key,
                TextBox = textBox,
                Picker = picker
            };
            textBox.TextChanged += (_, _) => UpdateEditorForeground(editor);
            picker.Click += (_, _) => OpenValuePicker(editor);
            _editors.Add(editor);
        }
    }

    private void PopulateFieldValues()
    {
        var valuesByKey = FieldDefinitions.ToDictionary(
            definition => definition.Key,
            definition => _tags
                .Select(tag => TagBatchEditor.GetFieldValues(tag)[definition.Key])
                .Distinct(StringComparer.Ordinal)
                .ToList());

        foreach (var editor in _editors)
        {
            var values = valuesByKey[editor.Key];
            editor.Values = values;
            editor.InitialText = values.Count == 1 ? values[0] : MultiValueText;
            editor.TextBox.Text = editor.InitialText;

            if (values.Count > 1)
            {
                editor.Picker.Visibility = Visibility.Visible;
                var menu = new ContextMenu();
                foreach (var value in values)
                {
                    var item = new MenuItem { Header = string.IsNullOrEmpty(value) ? "（空）" : value };
                    item.Click += (_, _) => editor.TextBox.Text = value;
                    menu.Items.Add(item);
                }

                menu.Items.Add(new Separator());
                var keep = new MenuItem { Header = "保持不变（多值）" };
                keep.Click += (_, _) => editor.TextBox.Text = editor.InitialText;
                menu.Items.Add(keep);
                editor.Picker.ContextMenu = menu;
            }
        }
    }

    private void UpdateEditorForeground(FieldEditor editor)
    {
        if (editor.TextBox.Text == MultiValueText)
        {
            editor.TextBox.SetResourceReference(Control.ForegroundProperty, "TextMutedBrush");
        }
        else
        {
            editor.TextBox.ClearValue(Control.ForegroundProperty);
        }
    }

    private void OpenValuePicker(FieldEditor editor)
    {
        if (editor.Picker.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = editor.Picker;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void PopulateAdditionalTags(IReadOnlyList<AudioTagEntry> tags)
    {
        foreach (var tag in tags)
        {
            var value = tag.Value.Trim();
            _additionalTagEditors.Add(new AdditionalTagEditor
            {
                Name = tag.Name,
                Kind = tag.Kind,
                Key = tag.Key,
                InitialValue = value,
                Value = value
            });
        }

        ExtraTagsList.ItemsSource = _additionalTagEditors;
        UpdateAdditionalTagsVisibility();
        ExtraTagsHeader.Text = _tags.Count > 1 ? "其他标签（第一个文件）" : "其他标签";
    }

    private void UpdateAdditionalTagsVisibility()
    {
        var visibility = _additionalTagEditors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExtraTagsHeader.Visibility = visibility;
        ExtraTagsPanel.Visibility = visibility;
    }

    private void OnRemoveAdditionalTag(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AdditionalTagEditor editor })
        {
            return;
        }

        _removedAdditionalTags.Add(new AudioTagChange(editor.Kind, editor.Key, null));
        _additionalTagEditors.Remove(editor);
        UpdateAdditionalTagsVisibility();
    }

    private void RefreshCoverPreview()
    {
        var bytes = _coverAction == CoverAction.Remove ? null : _coverBytes;
        RemoveCoverButton.IsEnabled = bytes is { Length: > 0 } || _tags.Any(tag => tag.CoverBytes is { Length: > 0 });

        if (_coverAction == CoverAction.None && _coverMixed && bytes is null)
        {
            CoverImage.Source = null;
            CoverPlaceholder.Text = MultiValueText;
            CoverPlaceholder.Visibility = Visibility.Visible;
            CoverInfoText.Text = DescribeMixedCover();
            return;
        }

        CoverPlaceholder.Text = "无封面";

        if (bytes is not { Length: > 0 })
        {
            CoverImage.Source = null;
            CoverPlaceholder.Visibility = Visibility.Visible;
            CoverInfoText.Text = "无封面";
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 360;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            CoverImage.Source = image;
            CoverPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            CoverImage.Source = null;
            CoverPlaceholder.Visibility = Visibility.Visible;
        }

        var info = CoverImageProcessor.ReadInfo(bytes);
        CoverInfoText.Text = info is null
            ? "封面信息不可用"
            : $"{info.FormatName} · {info.Width}×{info.Height} · {DisplayFormatter.FileSize(info.ByteSize)}";
    }

    private string DescribeMixedCover()
    {
        var total = _tags.Count;
        var withCover = _tags.Count(tag => tag.CoverBytes is { Length: > 0 });
        if (withCover == 0)
        {
            return "无封面";
        }

        if (withCover == total)
        {
            return "封面：<多值>（各文件封面不同）";
        }

        return $"封面：<多值>（{withCover} 个文件有封面，{total - withCover} 个无封面）";
    }

    private void SetCover(byte[]? bytes, string? mime, CoverAction action)
    {
        _coverBytes = bytes;
        _coverMime = mime;
        _coverAction = action;
        RefreshCoverPreview();
    }

    private void OnPickCover(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择封面图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.gif;*.tif;*.tiff|所有文件|*.*",
            InitialDirectory = GetSongDirectory() ?? string.Empty
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            SetCover(bytes, MimeForPath(dialog.FileName), CoverAction.Replace);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取图片失败：{ex.Message}", "EchoGarage", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnQuickMatchCover(object sender, RoutedEventArgs e)
    {
        var directory = GetSongDirectory();
        if (directory is null || !Directory.Exists(directory))
        {
            MessageBox.Show(this, "无法确定歌曲所在目录。", "EchoGarage", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取目录失败：{ex.Message}", "EchoGarage", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var images = AlbumCoverMatcher.FindImages(files);
        if (images.Count == 0)
        {
            MessageBox.Show(this, "歌曲所在目录没有图片文件。", "EchoGarage", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var match = AlbumCoverMatcher.MatchCover(images);
        if (match is null)
        {
            MessageBox.Show(this,
                "目录中有多个图片，但没有找到名为 cover 或 folder 的图片，请手动选择。",
                "EchoGarage", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(match);
            SetCover(bytes, MimeForPath(match), CoverAction.Replace);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取图片失败：{ex.Message}", "EchoGarage", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string? GetSongDirectory()
    {
        var path = _tracks[0].Path;
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
    }

    private static string MimeForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".tif" or ".tiff" => "image/tiff",
        _ => "image/jpeg"
    };

    private void OnRemoveCover(object sender, RoutedEventArgs e) => SetCover(null, null, CoverAction.Remove);

    private void OnConvertCoverChanged(object sender, RoutedEventArgs e)
    {
        if (CoverConversionOptions is not null)
        {
            CoverConversionOptions.IsEnabled = ConvertCoverCheck.IsChecked == true;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = BuildResult();
        DialogResult = true;
    }

    /// <summary>根据当前控件值生成批量编辑结果（只包含被修改的字段与封面改动）。</summary>
    private TagBatchEdit BuildResult()
    {
        var edit = new TagBatchEdit();
        foreach (var editor in _editors)
        {
            var text = editor.TextBox.Text;
            if (text == editor.InitialText)
            {
                continue;
            }

            edit.Fields[editor.Key] = text.Trim();
        }

        foreach (var editor in _additionalTagEditors)
        {
            var value = editor.Value?.Trim() ?? string.Empty;
            if (value == editor.InitialValue)
            {
                continue;
            }

            edit.AdditionalTagChanges.Add(new AudioTagChange(
                editor.Kind,
                editor.Key,
                string.IsNullOrWhiteSpace(value) ? null : value));
        }

        edit.AdditionalTagChanges.AddRange(_removedAdditionalTags);

        var bytes = _coverAction == CoverAction.Remove ? null : _coverBytes;
        var mime = _coverAction == CoverAction.Remove ? null : _coverMime;
        var coverChanged = _coverAction == CoverAction.Replace;

        if (bytes is { Length: > 0 } && ConvertCoverCheck.IsChecked == true)
        {
            var quality = ParseIntInRange(CoverQualityBox.Text, 1, 100, 90);
            var maxEdge = ParseIntInRange(CoverMaxEdgeBox.Text, 0, 20000, 1000);
            var converted = CoverImageProcessor.ConvertToJpeg(bytes, quality, maxEdge);
            if (converted.Converted)
            {
                bytes = converted.Bytes;
                mime = CoverImageProcessor.JpegMime;
                coverChanged = true;
            }
        }

        edit.CoverChanged = coverChanged || _coverAction == CoverAction.Remove;
        edit.RemoveCover = _coverAction == CoverAction.Remove;
        edit.NewCoverBytes = edit.RemoveCover ? null : bytes;
        edit.NewCoverMime = edit.RemoveCover ? null : mime;
        return edit;
    }

    private static int ParseIntInRange(string text, int min, int max, int fallback) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
