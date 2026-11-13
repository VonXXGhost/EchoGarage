using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EchoGarage.App.Services;

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

/// <summary>
/// Picks one column's secondary text out of a row's <c>SecondaryTexts</c> dictionary.
/// <c>ConverterParameter</c> is the column key; unknown/missing keys render as an empty
/// string so the secondary line collapses through a style trigger.
/// </summary>
public sealed class SecondaryTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IReadOnlyDictionary<string, string> texts &&
        parameter is string key &&
        texts.TryGetValue(key, out var text)
            ? text
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
