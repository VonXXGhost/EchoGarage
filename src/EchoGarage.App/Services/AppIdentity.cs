using System.Runtime.InteropServices;

namespace EchoGarage.App.Services;

/// <summary>
/// Gives the process a stable, explicit taskbar identity.
/// Windows derives the default identity from the exe path, and pinned shortcuts cache the
/// icon they were created with. An explicit AppUserModelID makes the running window use its
/// own current icon instead of a stale pinned-shortcut icon (e.g. after the app icon changes).
/// </summary>
internal static class AppIdentity
{
    public const string AppUserModelId = "VonXXGhost.EchoGarage";

    public static void Apply()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch
        {
            // Taskbar identity is cosmetic; never fail startup for it.
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appUserModelId);
}
