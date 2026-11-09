using Microsoft.Win32;

namespace EchoGarage.Infrastructure.Windows;

/// <summary>
/// Registers EchoGarage in HKCU so Explorer can offer it as a handler for audio files.
/// Windows 10/11 protects the user's chosen default handler, so registration writes the
/// ProgId + OpenWith entries and then guides the user to the system default-apps page.
/// Identifiers and the registry backend are injectable so tests can verify every key path.
/// </summary>
public sealed class FileAssociationService
{
    public const string DefaultProgId = "EchoGarage.AudioFile";
    public const string DefaultApplicationName = "EchoGarage.exe";
    public const string DefaultDisplayName = "EchoGarage";

    public static readonly IReadOnlyList<string> AudioExtensions =
        [".mp3", ".flac", ".wav", ".m4a", ".aac", ".aiff", ".wma", ".mp4"];

    private readonly IRegistryStore _store;

    public FileAssociationService(
        string progId = DefaultProgId,
        string applicationName = DefaultApplicationName,
        string displayName = DefaultDisplayName,
        IRegistryStore? store = null)
    {
        ProgId = progId;
        ApplicationName = applicationName;
        DisplayName = displayName;
        _store = store ?? new WindowsRegistryStore();
    }

    public string ProgId { get; }
    public string ApplicationName { get; }
    public string DisplayName { get; }

    private string ProgIdPath => $@"Software\Classes\{ProgId}";
    private string CapabilitiesPath => $@"Software\{ProgId}\Capabilities";

    public bool IsRegistered() => _store.KeyExists($@"{ProgIdPath}\shell\open\command");

    public bool Register(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        var command = $"\"{executablePath}\" \"%1\"";
        var executable = $"\"{executablePath}\",0";

        var operations = new List<(string KeyPath, string? Name, string Value)>
        {
            (ProgIdPath, null, $"{DisplayName} 音乐文件"),
            ($@"{ProgIdPath}\DefaultIcon", null, executable),
            ($@"{ProgIdPath}\shell\open\command", null, command),
            ($@"Software\Classes\Applications\{ApplicationName}", "FriendlyAppName", DisplayName),
            ($@"Software\Classes\Applications\{ApplicationName}\shell\open\command", null, command),
            ($@"Software\RegisteredApplications", DisplayName, CapabilitiesPath),
            (CapabilitiesPath, "ApplicationName", DisplayName),
            (CapabilitiesPath, "ApplicationDescription", $"{DisplayName} 本地音乐播放器")
        };

        foreach (var extension in AudioExtensions)
        {
            operations.Add(($@"Software\Classes\Applications\{ApplicationName}\SupportedTypes", extension, string.Empty));
            operations.Add(($@"Software\Classes\{extension}\OpenWithProgids", ProgId, string.Empty));
            operations.Add(($@"{CapabilitiesPath}\FileAssociations", extension, ProgId));

            // Deliberately do NOT overwrite the extension's default handler. Windows 10/11
            // keeps the user's explicit choice in UserChoice and ignores writes here;
            // overwriting would look like hijacking and cannot be undone on unregister.
        }

        foreach (var (keyPath, name, value) in operations)
        {
            if (!_store.SetValue(keyPath, name, value))
            {
                return false;
            }
        }

        return true;
    }

    public bool Unregister()
    {
        var success = true;
        success &= _store.DeleteTree(ProgIdPath);
        success &= _store.DeleteTree($@"Software\Classes\Applications\{ApplicationName}");
        foreach (var extension in AudioExtensions)
        {
            success &= _store.DeleteValue($@"Software\Classes\{extension}\OpenWithProgids", ProgId);
        }

        success &= _store.DeleteValue(@"Software\RegisteredApplications", DisplayName);
        success &= _store.DeleteTree($@"Software\{ProgId}");
        return success;
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
