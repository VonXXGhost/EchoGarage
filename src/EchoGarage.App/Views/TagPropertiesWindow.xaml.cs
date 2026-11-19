using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EchoGarage.App.Services;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using Track = EchoGarage.Core.Models.Track;

namespace EchoGarage.App.Views;

public partial class TagPropertiesWindow : ThemedWindow
{
    private const string MultiValueText = "<多值>";

    /// <summary>支持从媒体库候选值补全的字段（ADR-025（候选值））；其余字段保持纯手动输入。</summary>
    private static readonly string[] SuggestionFieldKeys =
    [
        TagFieldKeys.Artist,
        TagFieldKeys.AlbumArtist,
        TagFieldKeys.Composer,
        TagFieldKeys.Genre
    ];

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
    public sealed class AdditionalTagEditor : INotifyPropertyChanged
    {
        private string _value = string.Empty;

        public required string Name { get; init; }
        public required string Kind { get; init; }
        public required string Key { get; init; }
        public string InitialValue { get; init; } = string.Empty;

        public string Value
        {
            get => _value;
            set
            {
                if (_value == value)
                {
                    return;
                }

                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly IReadOnlyList<Track> _tracks;
    private readonly IReadOnlyList<AudioTagData> _tags;
    private readonly IReadOnlyList<TagNormalizationRule> _normalizationRules;
    private readonly ITagSuggestionService? _suggestions;
    private readonly List<FieldEditor> _editors = [];
    private readonly ObservableCollection<AdditionalTagEditor> _additionalTagEditors = [];
    private readonly List<AudioTagChange> _removedAdditionalTags = [];

    /// <summary>字段键 → 媒体库里的同字段候选值；补全浮层打开时按当前输入过滤。</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _suggestionValues =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前候选浮层的数据源；与 SuggestionList 绑定，避免每敲一个键重建控件。</summary>
    private readonly ObservableCollection<string> _suggestionMatches = [];

    private FieldEditor? _suggestionEditor;
    private bool _suppressSuggestionPopup;

    private byte[]? _coverBytes;
    private string? _coverMime;
    private CoverAction _coverAction = CoverAction.None;

    /// <summary>当前预览封面的解码信息（格式 / 分辨率 / 体积）；没有封面或封面不一致时为 null。</summary>
    private CoverImageInfo? _coverInfo;

    /// <summary>本次保存时用户选择「保持原样」，跳过封面转换（避免重复压缩已转换过的封面）。</summary>
    private bool _skipCoverCompression;

    /// <summary>True when the selected files do not share the same cover (some have one, some don't, or they differ).</summary>
    private bool _coverMixed;

    /// <param name="normalizationRules">
    /// 设置页配置的标签规范化规则；为 null 时使用内置默认规则（供诊断/截图等两参调用）。
    /// 快捷规范化只改变当前编辑框里的文本，保存时仍按“只提交被修改字段”的规则处理。
    /// </param>
    /// <param name="tagSuggestions">
    /// 媒体库标签候选值服务（ADR-025（候选值））；为 null 时不提供自动补全（诊断/截图与单元测试无需数据库）。
    /// </param>
    public TagPropertiesWindow(
        IReadOnlyList<Track> tracks,
        IReadOnlyList<AudioTagData> tags,
        IReadOnlyList<TagNormalizationRule>? normalizationRules = null,
        ITagSuggestionService? tagSuggestions = null)
    {
        if (tracks.Count == 0 || tags.Count == 0)
        {
            throw new ArgumentException("至少需要一个文件。", nameof(tracks));
        }

        InitializeComponent();

        _tracks = tracks;
        _tags = tags;
        _suggestions = tagSuggestions;

        // null 交给 Core 归一化为内置默认规则；设置页里被用户删空时是空列表，保持“没有规则”。
        _normalizationRules = TagNormalization.NormalizeRules(normalizationRules);

        SuggestionList.ItemsSource = _suggestionMatches;
        SuggestionPopup.Closed += (_, _) =>
        {
            _suggestionEditor = null;
            _suggestionMatches.Clear();
        };
        PreviewMouseDown += OnWindowPreviewMouseDown;

        // 候选项异步加载，不阻塞窗口打开；加载失败时服务会返回空快照。
        if (_suggestions is not null)
        {
            Loaded += OnSuggestionsLoaded;
        }

        Title = tracks.Count > 1 ? $"标签属性 - {tracks.Count} 个文件" : "标签属性";
        SaveButton.Content = tracks.Count > 1 ? $"保存到 {tracks.Count} 个文件" : "保存到文件";
        UpdateFilePathDisplay();

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

    /// <summary>
    /// 显示正在编辑的文件路径。单文件显示完整路径；多文件显示数量与公共目录
    /// （各文件不在同一目录时显示首个文件路径）。路径框是只读 TextBox，
    /// 可选中复制，长路径会自动换行，不会把窗口撑宽。
    /// </summary>
    private void UpdateFilePathDisplay()
    {
        var firstPath = _tracks[0].Path;
        if (_tracks.Count == 1)
        {
            FilePathBox.Text = firstPath;
            FilePathBox.ToolTip = string.IsNullOrWhiteSpace(firstPath)
                ? "该文件没有可显示的路径。"
                : "可选中路径后按 Ctrl+C 复制。";
            return;
        }

        var commonDirectory = GetCommonDirectory();
        FilePathBox.Text = commonDirectory is not null
            ? $"共 {_tracks.Count} 个文件 · 公共目录：{commonDirectory}"
            : $"共 {_tracks.Count} 个文件 · 首个文件：{firstPath}";
        FilePathBox.ToolTip = $"首个文件：{firstPath}\n可选中路径后按 Ctrl+C 复制。";
    }

    /// <summary>所有文件位于同一目录时返回该目录，否则返回 null。</summary>
    private string? GetCommonDirectory()
    {
        string? common = null;
        foreach (var track in _tracks)
        {
            if (string.IsNullOrWhiteSpace(track.Path))
            {
                return null;
            }

            var directory = Path.GetDirectoryName(track.Path);
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            if (common is null)
            {
                common = directory;
            }
            else if (!string.Equals(common, directory, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return common;
    }

    private void BuildFieldEditors()
    {
        for (var i = 0; i < FieldDefinitions.Length; i++)
        {
            var definition = FieldDefinitions[i];
            var cell = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            // 名称列收紧到最长标签（“专辑艺术家”约 60px）+ 8px 间距，值输入框从 68px 处开始，
            // 与上方“文件路径”行对齐，整体更接近 foobar2000 的紧凑名称/值排版。
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 68 });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = definition.Label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
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
            if (IsSuggestionField(definition.Key))
            {
                textBox.ToolTip = "输入时会列出媒体库中其它歌曲使用过的同字段值；↓/↑ 选择，Enter 采用，Esc 关闭。";
                textBox.PreviewKeyDown += OnSuggestionPreviewKeyDown;
                textBox.LostKeyboardFocus += OnSuggestionTextBoxLostKeyboardFocus;
                textBox.TextChanged += (_, _) => UpdateSuggestionPopup(editor);
            }

            _editors.Add(editor);
        }
    }

    private void PopulateFieldValues()
    {
        var valuesByKey = FieldDefinitions.ToDictionary(
            definition => definition.Key,
            definition => _tags
                .Select(tag => GetDisplayValue(tag, definition.Key))
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

    /// <summary>
    /// 属性面板显示的字段值。年份字段优先显示文件里的原始年份/日期文本
    /// （如 "2025.06.04"、"2023-05-12"），以便编辑其它字段时保留详细日期；
    /// 原始文本缺失时退回 4 位年份。多值去重与“保持不变”逻辑仍按字符串比较。
    /// </summary>
    private static string GetDisplayValue(AudioTagData tag, string key)
    {
        if (key == TagFieldKeys.Year && !string.IsNullOrWhiteSpace(tag.YearText))
        {
            return tag.YearText.Trim();
        }

        return TagBatchEditor.GetFieldValues(tag)[key];
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

    // =====================================================================
    // 标签补全候选（ADR-025（候选值））：只对艺术家 / 专辑艺术家 / 作曲家 / 流派生效。
    // 候选值来自媒体库缓存，异步加载一次；之后在内存里按输入前缀/包含匹配。
    // =====================================================================

    private static bool IsSuggestionField(string key) =>
        key is TagFieldKeys.Artist or TagFieldKeys.AlbumArtist or TagFieldKeys.Composer or TagFieldKeys.Genre;

    private async void OnSuggestionsLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnSuggestionsLoaded;
        if (_suggestions is null)
        {
            return;
        }

        try
        {
            var values = await _suggestions.GetAsync();
            foreach (var key in SuggestionFieldKeys)
            {
                _suggestionValues[key] = values.Get(key);
            }

            // 候选项加载完成时用户可能已经开始输入：对当前聚焦的输入框补弹一次。
            foreach (var editor in _editors)
            {
                if (IsSuggestionField(editor.Key) && editor.TextBox.IsKeyboardFocusWithin)
                {
                    UpdateSuggestionPopup(editor);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 窗口已关闭 / 加载被取消：没有候选项也不影响标签编辑。
        }
    }

    private void UpdateSuggestionPopup(FieldEditor editor)
    {
        if (_suppressSuggestionPopup || _suggestions is null || !IsSuggestionField(editor.Key))
        {
            return;
        }

        if (!editor.TextBox.IsKeyboardFocusWithin)
        {
            if (ReferenceEquals(_suggestionEditor, editor))
            {
                CloseSuggestionPopup();
            }

            return;
        }

        if (!_suggestionValues.TryGetValue(editor.Key, out var candidates) || candidates.Count == 0)
        {
            if (ReferenceEquals(_suggestionEditor, editor))
            {
                CloseSuggestionPopup();
            }

            return;
        }

        var matches = TagSuggestionMatcher.Match(candidates, editor.TextBox.Text);
        if (matches.Count == 0)
        {
            if (ReferenceEquals(_suggestionEditor, editor))
            {
                CloseSuggestionPopup();
            }

            return;
        }

        ShowSuggestionPopup(editor, matches);
    }

    private void ShowSuggestionPopup(FieldEditor editor, IReadOnlyList<string> matches)
    {
        var targetChanged = !ReferenceEquals(_suggestionEditor, editor);
        _suggestionEditor = editor;
        _suggestionMatches.Clear();
        foreach (var match in matches)
        {
            _suggestionMatches.Add(match);
        }

        SuggestionList.SelectedIndex = -1;
        SuggestionPopup.PlacementTarget = editor.TextBox;
        SuggestionPopup.Width = Math.Max(editor.TextBox.ActualWidth, 180);
        if (targetChanged || !SuggestionPopup.IsOpen)
        {
            SuggestionPopup.IsOpen = true;
        }
    }

    private void CloseSuggestionPopup()
    {
        if (SuggestionPopup.IsOpen)
        {
            SuggestionPopup.IsOpen = false;
        }

        _suggestionEditor = null;
        _suggestionMatches.Clear();
    }

    private void ApplySuggestion(string value)
    {
        if (_suggestionEditor is not { } editor)
        {
            return;
        }

        _suppressSuggestionPopup = true;
        try
        {
            editor.TextBox.Text = value;
            editor.TextBox.CaretIndex = value.Length;
        }
        finally
        {
            _suppressSuggestionPopup = false;
        }

        editor.TextBox.Focus();
        CloseSuggestionPopup();
    }

    private void OnSuggestionPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!SuggestionPopup.IsOpen || _suggestionEditor?.TextBox != sender)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down when _suggestionMatches.Count > 0:
                SuggestionList.SelectedIndex = Math.Min(
                    SuggestionList.SelectedIndex + 1,
                    _suggestionMatches.Count - 1);
                SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when _suggestionMatches.Count > 0:
                SuggestionList.SelectedIndex = Math.Max(SuggestionList.SelectedIndex - 1, -1);
                e.Handled = true;
                break;
            case Key.Enter when SuggestionList.SelectedIndex >= 0:
                ApplySuggestion(_suggestionMatches[SuggestionList.SelectedIndex]);
                e.Handled = true;
                break;
            case Key.Tab when SuggestionList.SelectedIndex >= 0:
                // 采用候选项后继续走默认的 Tab 焦点切换。
                ApplySuggestion(_suggestionMatches[SuggestionList.SelectedIndex]);
                break;
            case Key.Escape:
                CloseSuggestionPopup();
                e.Handled = true;
                break;
        }
    }

    private void OnSuggestionItemPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: string value })
        {
            ApplySuggestion(value);
            e.Handled = true;
        }
    }

    private void OnSuggestionTextBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _suggestionEditor?.TextBox))
        {
            return;
        }

        // 候选列表不接收键盘焦点，且点击候选项时焦点会短暂离开输入框。
        // 延迟到输入事件处理完之后再判断，否则鼠标还没点到候选项浮层就被关掉，
        // 表现为“鼠标点击候选没有反应”。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (SuggestionPopup.IsOpen && _suggestionEditor?.TextBox.IsKeyboardFocusWithin != true)
            {
                CloseSuggestionPopup();
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!SuggestionPopup.IsOpen || _suggestionEditor?.TextBox.IsMouseOver == true)
        {
            return;
        }

        // 候选浮层里的鼠标事件也会出现在窗口的预览路由上；点击落在浮层内部时
        // 必须放行，否则浮层先被关掉，候选项的点击事件就拿不到数据了。
        if (SuggestionPopup.Child is { } popupRoot &&
            IsWithinElement(popupRoot, e.OriginalSource as DependencyObject))
        {
            return;
        }

        // 点击窗口其它区域（包括另一个输入框）时关闭候选浮层，随后正常处理点击。
        CloseSuggestionPopup();
    }

    private static bool IsWithinElement(DependencyObject root, DependencyObject? candidate)
    {
        for (var node = candidate; node is not null; node = GetLogicalOrVisualParent(node))
        {
            if (ReferenceEquals(node, root))
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetLogicalOrVisualParent(DependencyObject node) =>
        node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // 点击候选浮层会让独立的 Popup 窗口短暂接管激活状态；这里同样延迟关闭，
        // 保证候选项的鼠标点击事件先被处理。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (SuggestionPopup.IsOpen && !IsActive)
            {
                CloseSuggestionPopup();
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
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

    /// <summary>
    /// 「快捷规范化」：弹出设置页里启用的规则，把规则作用到当前编辑框的文本上。
    /// 只改 TextBox.Text / 其他标签的 Value，不改 InitialText，因此保存时会作为本次修改提交；
    /// 多值字段（显示 &lt;多值&gt;）不自动改写，只在有匹配时计数提示。
    /// </summary>
    private void OnQuickNormalizeClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = QuickNormalizeButton,
            Placement = PlacementMode.Bottom,
        };

        foreach (var rule in _normalizationRules.Where(rule => rule.Enabled))
        {
            var item = new MenuItem
            {
                Header = string.IsNullOrWhiteSpace(rule.Name) ? "未命名规则" : rule.Name,
                ToolTip = string.IsNullOrWhiteSpace(rule.Pattern) ? null : $"正则：{rule.Pattern}",
            };
            item.Click += (_, _) => ApplyQuickNormalization(rule);
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "（没有启用的规则）", IsEnabled = false });
        }

        menu.IsOpen = true;
    }

    private void ApplyQuickNormalization(TagNormalizationRule rule)
    {
        if (!TagNormalization.IsValidPattern(rule.Pattern, out var error))
        {
            ShowQuickNormalizeStatus(error ?? "该规则的正则表达式无效，未应用。");
            return;
        }

        var changed = 0;
        var multiValueSkipped = 0;
        var numericSkipped = 0;
        foreach (var editor in _editors)
        {
            if (!TagNormalization.IsAnyField(rule.TargetField) &&
                !string.Equals(editor.Key, rule.TargetField, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = editor.TextBox.Text;
            if (text == MultiValueText)
            {
                if (editor.Values.Any(value => TagNormalization.Apply(rule, value) is not null))
                {
                    multiValueSkipped++;
                }

                continue;
            }

            var normalized = TagNormalization.Apply(rule, text);
            if (normalized is null)
            {
                continue;
            }

            if (IsNumericField(editor.Key))
            {
                if (!TryFormatNumeric(normalized, out var formatted))
                {
                    // 与 ADR-025 的数字字段语义一致：结果不是非负整数时保留原值，避免保存时被静默清空。
                    numericSkipped++;
                    continue;
                }

                normalized = formatted;
            }

            editor.TextBox.Text = normalized;
            changed++;
        }

        // 通配规则同时作用于「其他标签」；空替换在额外标签上保留原值（见 ADR-025）。
        if (TagNormalization.IsAnyField(rule.TargetField))
        {
            foreach (var editor in _additionalTagEditors)
            {
                var normalized = TagNormalization.Apply(rule, editor.Value);
                if (normalized is null || string.IsNullOrWhiteSpace(normalized))
                {
                    // 额外标签只改不删：空白结果保留原值（与 ADR-025 的批量语义一致）。
                    continue;
                }

                editor.Value = normalized;
                changed++;
            }
        }

        var ruleName = string.IsNullOrWhiteSpace(rule.Name) ? "未命名规则" : rule.Name;
        if (changed == 0 && multiValueSkipped == 0 && numericSkipped == 0)
        {
            ShowQuickNormalizeStatus("当前标签没有匹配项");
            return;
        }

        var message = $"规则「{ruleName}」：修改 {changed} 个字段";
        if (multiValueSkipped > 0)
        {
            message += $"，已跳过 {multiValueSkipped} 个多值字段";
        }

        if (numericSkipped > 0)
        {
            message += $"，已跳过 {numericSkipped} 个结果不是非负整数的数字字段";
        }

        ShowQuickNormalizeStatus(message + "。");
    }

    private static bool IsNumericField(string key) =>
        key is TagFieldKeys.Year or TagFieldKeys.TrackNumber or TagFieldKeys.DiscNumber;

    private static bool TryFormatNumeric(string text, out string formatted)
    {
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0)
        {
            formatted = value.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        formatted = string.Empty;
        return false;
    }

    private void ShowQuickNormalizeStatus(string message)
    {
        QuickNormalizeStatusText.Text = message;
        QuickNormalizeStatusText.Visibility = Visibility.Visible;
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
            _coverInfo = null;
            return;
        }

        CoverPlaceholder.Text = "无封面";

        if (bytes is not { Length: > 0 })
        {
            CoverImage.Source = null;
            CoverPlaceholder.Visibility = Visibility.Visible;
            CoverInfoText.Text = "无封面";
            _coverInfo = null;
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
        _coverInfo = info;
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
        if (!ConfirmCoverRecompression())
        {
            // 用户在“是否再压一次封面”里选择了取消：留在属性页继续编辑，不保存。
            return;
        }

        Result = BuildResult();
        DialogResult = true;
    }

    /// <summary>
    /// 保存前的封面重复压缩确认。封面已经是 JPEG 且最长边正好等于当前设置的最大边长时，
    /// 再保存只会以相同分辨率重新做一次有损编码（体积可能略小、画质继续损失），
    /// 因此先把当前分辨率与体积摆给用户，由用户决定仍然压缩还是保持原样。
    /// </summary>
    /// <returns>false 表示用户取消了本次保存。</returns>
    private bool ConfirmCoverRecompression()
    {
        _skipCoverCompression = false;
        if (ConvertCoverCheck.IsChecked != true)
        {
            return true;
        }

        var bytes = _coverAction == CoverAction.Remove ? null : _coverBytes;
        if (bytes is not { Length: > 0 } || _coverInfo is not { } info)
        {
            return true;
        }

        var (quality, maxEdge) = ReadCoverConversionOptions();
        if (!CoverConversionPolicy.IsAlreadyAtTargetEdge(info.IsJpeg, info.Width, info.Height, maxEdge))
        {
            return true;
        }

        var window = new CoverRecompressWindow(
            new CoverRecompressPrompt(info.Width, info.Height, info.ByteSize, quality, maxEdge, _tracks.Count))
        {
            Owner = this,
        };
        window.ShowDialog();

        switch (window.Choice)
        {
            case CoverRecompressChoice.Compress:
                return true;
            case CoverRecompressChoice.KeepOriginal:
                _skipCoverCompression = true;
                return true;
            default:
                return false;
        }
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

        if (!_skipCoverCompression && bytes is { Length: > 0 } && ConvertCoverCheck.IsChecked == true)
        {
            var (quality, maxEdge) = ReadCoverConversionOptions();
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

    /// <summary>读取封面转换设置（质量 / 最大边长）；保存与重复压缩提示共用同一套归一化规则。</summary>
    private (int Quality, int MaxEdge) ReadCoverConversionOptions() => (
        ParseIntInRange(CoverQualityBox.Text, 1, 100, 90),
        ParseIntInRange(CoverMaxEdgeBox.Text, 0, 20000, 1000));

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
