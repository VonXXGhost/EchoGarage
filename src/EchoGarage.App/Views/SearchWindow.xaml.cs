using System.Windows;
using System.Windows.Input;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Views;

/// <summary>
/// Advanced search: keyword plus an explicit field set, so the user can search only one
/// tag (e.g. composer) or look for the same keyword in several fields at once.
/// </summary>
public partial class SearchWindow : ThemedWindow
{
    private static readonly (SearchField Field, string Label)[] FieldCatalog =
    [
        (SearchField.Title, "标题"),
        (SearchField.Artist, "艺术家"),
        (SearchField.Album, "专辑"),
        (SearchField.AlbumArtist, "专辑艺术家"),
        (SearchField.Composer, "作曲家"),
        (SearchField.Genre, "流派"),
        (SearchField.Comment, "备注"),
        (SearchField.FileName, "文件名"),
        (SearchField.Path, "文件路径"),
        (SearchField.Year, "年份"),
        (SearchField.Codec, "格式")
    ];

    private readonly List<FieldOption> _options;

    public SearchWindow(SearchQuery? current)
    {
        InitializeComponent();

        var selected = current?.Fields ?? SearchQuery.InlineFields;
        _options = FieldCatalog
            .Select(item => new FieldOption(item.Field, item.Label)
            {
                IsSelected = selected.Contains(item.Field)
            })
            .ToList();

        FieldItems.ItemsSource = _options;
        KeywordBox.Text = current?.Text ?? string.Empty;
        AnyModeRadio.IsChecked = (current?.Mode ?? SearchFieldMode.Any) == SearchFieldMode.Any;
        AllModeRadio.IsChecked = current?.Mode == SearchFieldMode.All;

        Loaded += (_, _) =>
        {
            KeywordBox.Focus();
            KeywordBox.SelectAll();
        };
    }

    /// <summary>Set when the dialog closes with OK; an empty query means "clear the search".</summary>
    public SearchQuery? Result { get; private set; }

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        var text = KeywordBox.Text.Trim();
        var fields = _options.Where(option => option.IsSelected).Select(option => option.Field).ToArray();

        if (fields.Length == 0)
        {
            StatusText.Text = "请至少选择一个匹配字段。";
            return;
        }

        if (text.Length == 0)
        {
            StatusText.Text = "请输入关键词，或点击“清除搜索”。";
            return;
        }

        Result = new SearchQuery(
            text,
            fields,
            AllModeRadio.IsChecked == true ? SearchFieldMode.All : SearchFieldMode.Any);
        DialogResult = true;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Result = new SearchQuery(string.Empty, SearchQuery.InlineFields);
        DialogResult = true;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private sealed class FieldOption(SearchField field, string label)
    {
        public SearchField Field { get; } = field;

        public string Label { get; } = label;

        public bool IsSelected { get; set; }
    }
}
