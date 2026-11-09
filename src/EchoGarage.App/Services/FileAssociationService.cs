using Microsoft.Win32;

namespace EchoGarage.App.Services;

/// <summary>
/// Registers EchoGarage in HKCU so Explorer can offer it as a handler for audio files.
/// Windows 10/11 protects the user's chosen default handler, so registration writes the
/// ProgId + OpenWith entries and then guides the user to the system default-apps page.
/// </summary>
public sealed class FileAssociationService
{
    public const string ProgId = "EchoGarage.AudioFile";
    public const string ApplicationName = "EchoGarage.exe";

    private static readonly string[] Extensions =
        [".mp3", ".flac", ".wav", ".m4a", ".aac", ".aiff", ".wma", ".mp4"];

    public bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    public bool Register(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        var command = $"\"{executablePath}\" \"%1\"";
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes", writable: true);
        if (classes is null)
        {
            return false;
        }

        using (var progId = classes.CreateSubKey(ProgId, writable: true))
        {
            progId.SetValue(null, "EchoGarage 音乐文件");
            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{executablePath}\",0");
            using var commandKey = progId.CreateSubKey(@"shell\open\command");
            commandKey.SetValue(null, command);
        }

        using (var application = classes.CreateSubKey($@"Applications\{ApplicationName}", writable: true))
        {
            application.SetValue("FriendlyAppName", "EchoGarage");
            using var applicationCommand = application.CreateSubKey(@"shell\open\command");
            applicationCommand.SetValue(null, command);
            using var supported = application.CreateSubKey("SupportedTypes", writable: true);
            foreach (var extension in Extensions)
            {
                supported.SetValue(extension, string.Empty);
            }
        }

        foreach (var extension in Extensions)
        {
            using var extensionKey = classes.CreateSubKey(extension, writable: true);
            using var openWith = extensionKey.CreateSubKey("OpenWithProgids", writable: true);
            openWith.SetValue(ProgId, string.Empty, RegistryValueKind.String);
            extensionKey.SetValue(null, ProgId, RegistryValueKind.String);
        }

        using var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications", writable: true);
        registered?.SetValue("EchoGarage", @"Software\EchoGarage\Capabilities", RegistryValueKind.String);

        using var capabilities = Registry.CurrentUser.CreateSubKey(@"Software\EchoGarage\Capabilities", writable: true);
        if (capabilities is not null)
        {
            capabilities.SetValue("ApplicationName", "EchoGarage");
            capabilities.SetValue("ApplicationDescription", "EchoGarage 本地音乐播放器");
            using var fileAssociations = capabilities.CreateSubKey("FileAssociations", writable: true);
            foreach (var extension in Extensions)
            {
                fileAssociations?.SetValue(extension, ProgId, RegistryValueKind.String);
            }
        }

        return true;
    }

    public bool Unregister()
    {
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
            classes?.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
            classes?.DeleteSubKeyTree($@"Applications\{ApplicationName}", throwOnMissingSubKey: false);

            foreach (var extension in Extensions)
            {
                using var extensionKey = classes?.OpenSubKey(extension, writable: true);
                using var openWith = extensionKey?.OpenSubKey("OpenWithProgids", writable: true);
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            }

            using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
            registered?.DeleteValue("EchoGarage", throwOnMissingValue: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\EchoGarage", throwOnMissingSubKey: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void OpenDefaultAppsSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:defaultapps",
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore: the user can open Settings manually.
        }
    }
}
