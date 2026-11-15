using EchoGarage.Core.Models;

namespace EchoGarage.App.Services;

/// <summary>
/// Swaps the first merged resource dictionary (the theme) at runtime. Controls and views
/// reference theme tokens with DynamicResource so switching does not require a restart.
/// </summary>
public static class ThemeManager
{
    private static readonly Dictionary<ThemeKind, Uri> ThemeUris = new()
    {
        [ThemeKind.PureStudio] = new(
            "pack://application:,,,/EchoGarage;component/Resources/Themes/PureStudioTheme.xaml", UriKind.Absolute),
        [ThemeKind.WarmPaper] = new(
            "pack://application:,,,/EchoGarage;component/Resources/Themes/WarmPaperTheme.xaml", UriKind.Absolute),
        [ThemeKind.NordicIce] = new(
            "pack://application:,,,/EchoGarage;component/Resources/Themes/NordicIceTheme.xaml", UriKind.Absolute),
        [ThemeKind.MidnightVinyl] = new(
            "pack://application:,,,/EchoGarage;component/Resources/Themes/MidnightVinylTheme.xaml", UriKind.Absolute),
        [ThemeKind.SlateStudio] = new(
            "pack://application:,,,/EchoGarage;component/Resources/Themes/SlateStudioTheme.xaml", UriKind.Absolute)
    };

    public static ThemeKind Current { get; private set; } = ThemeKind.PureStudio;

    public static bool IsDark => ThemeCatalog.IsDark(Current);

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
            Source = ThemeUris[theme]
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
