using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EchoGarage.App.Services;

namespace EchoGarage.App.Views;

/// <summary>Dialog base that follows the current app theme for the native title bar.</summary>
public class ThemedWindow : Window
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarTheme();
        ThemeManager.ThemeApplied += OnThemeApplied;
        Closed += OnClosed;
    }

    private void OnThemeApplied(object? sender, Core.Models.ThemeKind theme) => ApplyTitleBarTheme();

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeManager.ThemeApplied -= OnThemeApplied;
        Closed -= OnClosed;
    }

    private void ApplyTitleBarTheme()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var enabled = ThemeManager.IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            }
        }
        catch
        {
            // Older builds keep the default title bar.
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
