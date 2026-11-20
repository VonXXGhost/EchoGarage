using System.Security;
using Microsoft.Win32;

namespace EchoGarage.Infrastructure.Windows;

/// <summary>
/// Small indirection over HKCU so the file-association logic (key paths, values, cleanup)
/// can be unit tested without touching the real registry.
/// All paths are relative to HKEY_CURRENT_USER.
/// <see cref="LastError"/> carries the reason of the most recent failed operation so callers
/// can log something actionable — some extension keys are read-only on managed machines.
/// </summary>
public interface IRegistryStore
{
    /// <summary>最近一次操作失败的原因；成功后清空。</summary>
    string? LastError { get; }

    bool SetValue(string keyPath, string? valueName, string value, RegistryValueKind kind = RegistryValueKind.String);
    bool DeleteTree(string keyPath);
    bool DeleteValue(string keyPath, string? valueName);
    bool KeyExists(string keyPath);
    object? GetValue(string keyPath, string? valueName);
}

public sealed class WindowsRegistryStore : IRegistryStore
{
    public string? LastError { get; private set; }

    public bool SetValue(string keyPath, string? valueName, string value, RegistryValueKind kind = RegistryValueKind.String)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            if (key is null)
            {
                LastError = $"无法创建或打开 HKCU\\{keyPath}。";
                return false;
            }

            key.SetValue(valueName ?? string.Empty, value, kind);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = Describe(ex, $"写入 HKCU\\{keyPath}");
            return false;
        }
    }

    public bool DeleteTree(string keyPath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = Describe(ex, $"删除 HKCU\\{keyPath}");
            return false;
        }
    }

    public bool DeleteValue(string keyPath, string? valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key is null)
            {
                // 键不存在说明没有可删除的值；这不是失败。
                LastError = null;
                return true;
            }

            key.DeleteValue(valueName ?? string.Empty, throwOnMissingValue: false);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = Describe(ex, $"删除 HKCU\\{keyPath} 下的值");
            return false;
        }
    }

    public bool KeyExists(string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            LastError = null;
            return key is not null;
        }
        catch (Exception ex)
        {
            LastError = Describe(ex, $"读取 HKCU\\{keyPath}");
            return false;
        }
    }

    public object? GetValue(string keyPath, string? valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            LastError = null;
            return key?.GetValue(valueName ?? string.Empty);
        }
        catch (Exception ex)
        {
            LastError = Describe(ex, $"读取 HKCU\\{keyPath} 下的值");
            return null;
        }
    }

    private static string Describe(Exception exception, string action) => exception switch
    {
        UnauthorizedAccessException => $"{action} 被拒绝：该注册表项是只读的（可能被 Windows 或其他程序保护）。",
        SecurityException => $"{action} 被安全策略拒绝。",
        System.IO.IOException => $"{action} 失败：{exception.Message}",
        _ => $"{action} 失败：{exception.GetType().Name}：{exception.Message}",
    };
}
