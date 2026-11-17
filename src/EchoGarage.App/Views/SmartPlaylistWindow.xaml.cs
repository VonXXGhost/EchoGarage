using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.App.Views;

public sealed record OperatorOption(string Key, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class RuleRow : ObservableObject
{
    private const string RegexValueHint = "正则表达式（.NET 语法，忽略大小写），例如 ^\\d{4} 或 (Live|Demo)";

    private SmartFieldDescriptor? _selectedField;
    private OperatorOption? _selectedOperator;

    public RuleRow(SmartRule rule)
    {
        Operators = [];
        SelectedField = SmartFieldCatalog.Find(rule.Field) ?? SmartFieldCatalog.Fields[0];
        SelectedOperator = Operators.FirstOrDefault(o => o.Key == rule.Operator) ?? Operators.FirstOrDefault();
        Value = rule.Value;
    }

    public ObservableCollection<OperatorOption> Operators { get; }

    public SmartFieldDescriptor? SelectedField
    {
        get => _selectedField;
        set
        {
            if (!SetProperty(ref _selectedField, value) || value is null)
            {
                return;
            }

            var previous = SelectedOperator?.Key;
            Operators.Clear();
            foreach (var op in value.Operators)
            {
                Operators.Add(new OperatorOption(op, SmartFieldCatalog.OperatorLabel(op)));
            }

            SelectedOperator = Operators.FirstOrDefault(o => o.Key == previous) ?? Operators.FirstOrDefault();
            OnPropertyChanged(nameof(IsValueEnabled));
        }
    }

    public OperatorOption? SelectedOperator
    {
        get => _selectedOperator;
        set
        {
            if (SetProperty(ref _selectedOperator, value))
            {
                OnPropertyChanged(nameof(IsValueEnabled));
                OnPropertyChanged(nameof(IsRegexOperator));
                OnPropertyChanged(nameof(ValueHint));
                OnPropertyChanged(nameof(ShowValueHint));
            }
        }
    }

    [ObservableProperty]
    private string _value = string.Empty;

    public bool IsValueEnabled => SelectedOperator?.Key is not ("isEmpty" or "isNotEmpty");

    /// <summary>当前操作符是否是正则匹配（regex / notRegex）。</summary>
    public bool IsRegexOperator => SelectedOperator?.Key is "regex" or "notRegex";

    /// <summary>值输入框的 ToolTip：仅正则操作符下给出可用性提示，其它操作符不显示。</summary>
    public string? ValueHint => IsRegexOperator ? RegexValueHint : null;

    /// <summary>值输入框为空时把提示语渲染成占位文本，方便用户发现正则语法。</summary>
    public bool ShowValueHint => IsRegexOperator && string.IsNullOrEmpty(Value);

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(ShowValueHint));

    public SmartRule ToRule() => new()
    {
        Field = SelectedField?.Key ?? "artist",
        Operator = SelectedOperator?.Key ?? "contains",
        Value = Value ?? string.Empty
    };
}

public partial class SmartPlaylistWindow : ThemedWindow
{
    private readonly ObservableCollection<RuleRow> _rules = [];

    public SmartPlaylistWindow(string playlistName, SmartPlaylistDefinition definition)
    {
        InitializeComponent();
        TitleText.Text = playlistName;
        RuleList.ItemsSource = _rules;

        var source = definition.Rules.Count > 0
            ? definition.Rules
            : new List<SmartRule> { new() { Field = "artist", Operator = "contains", Value = string.Empty } };
        foreach (var rule in source)
        {
            AddRuleRow(new RuleRow(rule));
        }

        AllRadio.IsChecked = definition.MatchMode == SmartMatchMode.All;
        AnyRadio.IsChecked = definition.MatchMode == SmartMatchMode.Any;
    }

    public SmartPlaylistDefinition? Result { get; private set; }

    private void AddRuleRow(RuleRow row)
    {
        // 用户一改动条件就清掉上一次的校验提示。
        row.PropertyChanged += (_, _) => ClearStatus();
        _rules.Add(row);
    }

    private void ClearStatus() => StatusText.Text = string.Empty;

    private void OnAddRule(object sender, RoutedEventArgs e) =>
        AddRuleRow(new RuleRow(new SmartRule { Field = "artist", Operator = "contains", Value = string.Empty }));

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RuleRow row } && _rules.Count > 1)
        {
            _rules.Remove(row);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        ClearStatus();
        var rules = _rules.Select(r => r.ToRule()).ToList();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule.Operator is not ("regex" or "notRegex") ||
                string.IsNullOrWhiteSpace(rule.Value) ||
                SmartRegex.IsValid(rule.Value))
            {
                continue;
            }

            StatusText.Text = $"第 {i + 1} 条条件的正则表达式无效：{rule.Value}\n" +
                              "请检查括号、方括号与转义字符是否成对，例如 ^\\d{4} 或 (Live|Demo)。";
            return;
        }

        Result = new SmartPlaylistDefinition
        {
            MatchMode = AnyRadio.IsChecked == true ? SmartMatchMode.Any : SmartMatchMode.All,
            Rules = rules
        };
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
