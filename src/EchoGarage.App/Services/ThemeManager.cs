using EchoGarage.Core.Models;

namespace EchoGarage.App.Services;

/// <summary>
/// Swaps the first merged resource dictionary (the theme) at runtime. Controls and views
/// reference theme tokens with DynamicResource so switching does not require a restart.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri DarkUri =
        new("pack://application:,,,/EchoGarage;component/Resources/Themes/DarkTheme.xaml", UriKind.Absolute);

    private static readonly Uri LightUri =
        new("pack://application:,,,/EchoGarage;component/Resources/Themes/LightTheme.xaml", UriKind.Absolute);

    public static ThemeKind Current { get; private set; } = ThemeKind.Light;

    public static bool IsDark => Current == ThemeKind.Dark;

    public static event EventHandler<ThemeKind>? ThemeApplied;

    public static void Apply(ThemeKind theme)
    {
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            Current = theme;
            return;
        }

        var dictionaries = application.Resources.MergedDictionaries;
        var dictionary = new System.Windows.ResourceDictionary
        {
            Source = theme == ThemeKind.Light ? LightUri : DarkUri
        };

        if (dictionaries.Count > 0)
        {
            dictionaries[0] = dictionary;
        }
        else
        {
            dictionaries.Add(dictionary);
        }

        Current = theme;
        ThemeApplied?.Invoke(null, theme);
    }
}
