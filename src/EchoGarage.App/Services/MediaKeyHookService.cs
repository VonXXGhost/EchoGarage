using System.Diagnostics;
using System.Runtime.InteropServices;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Services;

/// <summary>Media key pressed notification raised by <see cref="MediaKeyHookService"/>.</summary>
internal sealed class MediaKeyHookEventArgs : EventArgs
{
    public MediaKeyHookEventArgs(int virtualKey, string keyName, HotkeyModifiers modifiers)
    {
        VirtualKey = virtualKey;
        KeyName = keyName;
        Modifiers = modifiers;
    }

    public int VirtualKey { get; }

    public string KeyName { get; }

    public HotkeyModifiers Modifiers { get; }

    /// <summary>Set to true to swallow the key so the foreground application never sees it.</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// Process-wide WH_KEYBOARD_LL (low-level keyboard) hook for multimedia / volume keys.
/// WPF's <c>PreviewKeyDown</c> and Win32 <c>RegisterHotKey</c> never see VK_MEDIA_* /
/// VK_VOLUME_* presses, so these keys are intercepted here instead.
/// <para>
/// The hook is reference counted: <see cref="HotkeyService"/> keeps it installed while a
/// media-key binding exists, and a capturing control adds a second reference while the user
/// records a gesture. Installation and removal must happen on the UI thread; the callback is
/// invoked on that same thread. Long-running work must not run inside the callback (Windows
/// silently removes hooks that block too long), so subscribers dispatch their actions.
/// </para>
/// </summary>
internal static class MediaKeyHookService
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkAlt = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private static readonly object Gate = new();
    private static readonly LowLevelKeyboardProc HookProc = HookCallback;

    private static IntPtr _hookHandle;
    private static int _installThreadId;
    private static int _refCount;
    private static bool _suppressNextKeyUp;
    private static Action<string>? _captureHandler;
    private static EventHandler<MediaKeyHookEventArgs>? _mediaKeyDown;

    /// <summary>Raised (on the UI thread) when a media key is pressed and no capture is active.</summary>
    public static event EventHandler<MediaKeyHookEventArgs> MediaKeyDown
    {
        add => _mediaKeyDown += value;
        remove => _mediaKeyDown -= value;
    }

    /// <summary>True while the low-level hook is installed.</summary>
    public static bool IsInstalled => _hookHandle != IntPtr.Zero;

    /// <summary>
    /// Routes media keys to <paramref name="handler"/> (suppressing them for every other
    /// application) and installs the hook if needed. Pair with <see cref="EndCapture"/>.
    /// </summary>
    public static void BeginCapture(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        AddRef();
        Volatile.Write(ref _captureHandler, handler);
    }

    /// <summary>Stops routing media keys to <paramref name="handler"/> and releases its hook reference.</summary>
    public static void EndCapture(Action<string> handler)
    {
        var current = Volatile.Read(ref _captureHandler);
        if (current is null || !current.Equals(handler))
        {
            return;
        }

        Volatile.Write(ref _captureHandler, null);
        Release();
    }

    /// <summary>Keeps the hook installed; call once per logical owner and pair with <see cref="Release"/>.</summary>
    public static void AddRef()
    {
        lock (Gate)
        {
            _refCount++;
            if (_refCount == 1)
            {
                Install();
            }
        }
    }

    /// <summary>Drops one hook reference; uninstalls the hook when the last one is gone.</summary>
    public static void Release()
    {
        lock (Gate)
        {
            if (_refCount == 0)
            {
                return;
            }

            _refCount--;
            if (_refCount == 0)
            {
                Uninstall();
            }
        }
    }

    private static void Install()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        _installThreadId = GetCurrentThreadId();
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, HookProc, GetModuleHandle(null), 0);
        if (_hookHandle == IntPtr.Zero)
        {
            Debug.WriteLine($"Media key hook installation failed (error {Marshal.GetLastWin32Error()}).");
        }
    }

    private static void Uninstall()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        if (GetCurrentThreadId() != _installThreadId)
        {
            // UnhookWindowsHookEx must run on the installing thread; leaking one process-wide
            // hook is preferable to an undefined shutdown crash.
            Debug.WriteLine("Media key hook was released from a different thread; hook left installed.");
            return;
        }

        if (!UnhookWindowsHookEx(_hookHandle))
        {
            Debug.WriteLine($"Media key hook removal failed (error {Marshal.GetLastWin32Error()}).");
        }

        _hookHandle = IntPtr.Zero;
        _suppressNextKeyUp = false;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var message = wParam.ToInt32();
                var virtualKey = Marshal.ReadInt32(lParam);

                if ((message is WmKeyDown or WmSysKeyDown) && HotkeyGesture.IsMediaVirtualKey(virtualKey))
                {
                    var keyName = HotkeyGesture.KeyNameFromVirtualKey(virtualKey)!;
                    var capture = Volatile.Read(ref _captureHandler);
                    if (capture is not null)
                    {
                        _suppressNextKeyUp = true;
                        capture(keyName);
                        return new IntPtr(1);
                    }

                    var args = new MediaKeyHookEventArgs(virtualKey, keyName, ReadModifiers());
                    _mediaKeyDown?.Invoke(null, args);
                    if (args.Handled)
                    {
                        _suppressNextKeyUp = true;
                        return new IntPtr(1);
                    }
                }
                else if ((message is WmKeyUp or WmSysKeyUp) &&
                         _suppressNextKeyUp &&
                         HotkeyGesture.IsMediaVirtualKey(virtualKey))
                {
                    _suppressNextKeyUp = false;
                    return new IntPtr(1);
                }
            }
            catch (Exception ex)
            {
                // The hook must never throw into user32; a throwing hook can wedge input.
                Debug.WriteLine($"Media key hook callback failed: {ex}");
            }
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static HotkeyModifiers ReadModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (IsKeyDown(VkControl))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (IsKeyDown(VkAlt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (IsKeyDown(VkShift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (IsKeyDown(VkLWin) || IsKeyDown(VkRWin))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        return modifiers;
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();
}

/// <summary>Small helpers for deciding whether one of our windows currently owns the foreground.</summary>
internal static class ForegroundWindowHelper
{
    /// <summary>True when the foreground window belongs to the current process.</summary>
    public static bool IsOwnProcessForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
