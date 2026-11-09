using Microsoft.Win32;

namespace EchoGarage.Infrastructure.Windows;

/// <summary>
/// Small indirection over HKCU so the file-association logic (key paths, values, cleanup)
/// can be unit tested without touching the real registry.
/// All paths are relative to HKEY_CURRENT_USER.
/// </summary>
public interface IRegistryStore
{
    bool SetValue(string keyPath, string? valueName, string value, RegistryValueKind kind = RegistryValueKind.String);
    bool DeleteTree(string keyPath);
    bool DeleteValue(string keyPath, string? valueName);
    bool KeyExists(string keyPath);
    object? GetValue(string keyPath, string? valueName);
}

public sealed class WindowsRegistryStore : IRegistryStore
{
    public bool SetValue(string keyPath, string? valueName, string value, RegistryValueKind kind = RegistryValueKind.String)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue(valueName ?? string.Empty, value, kind);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool DeleteTree(string keyPath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool DeleteValue(string keyPath, string? valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(valueName ?? string.Empty, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool KeyExists(string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    public object? GetValue(string keyPath, string? valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName ?? string.Empty);
        }
        catch
        {
            return null;
        }
    }
}
