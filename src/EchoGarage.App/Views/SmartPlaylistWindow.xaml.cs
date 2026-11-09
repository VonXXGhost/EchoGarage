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
            }
        }
    }

    [ObservableProperty]
    private string _value = string.Empty;

    public bool IsValueEnabled => SelectedOperator?.Key is not ("isEmpty" or "isNotEmpty");

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
            _rules.Add(new RuleRow(rule));
        }

        AllRadio.IsChecked = definition.MatchMode == SmartMatchMode.All;
        AnyRadio.IsChecked = definition.MatchMode == SmartMatchMode.Any;
    }

    public SmartPlaylistDefinition? Result { get; private set; }

    private void OnAddRule(object sender, RoutedEventArgs e) =>
        _rules.Add(new RuleRow(new SmartRule { Field = "artist", Operator = "contains", Value = string.Empty }));

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RuleRow row } && _rules.Count > 1)
        {
            _rules.Remove(row);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = new SmartPlaylistDefinition
        {
            MatchMode = AnyRadio.IsChecked == true ? SmartMatchMode.Any : SmartMatchMode.All,
            Rules = _rules.Select(r => r.ToRule()).ToList()
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
