using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using EchoGarage.App.ViewModels;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Services;

/// <summary>
/// Applies the playback hotkeys from <see cref="AppSettings.Hotkeys"/>.
/// Local bindings are matched in the main window's PreviewKeyDown; global bindings are
/// registered with Win32 <c>RegisterHotKey</c> and dispatched from WM_HOTKEY, so they also
/// fire while another application has focus. Registration failures (another program already
/// owns the combination) are logged and skipped instead of crashing.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int ModAlt = 0x0001;
    private const int ModControl = 0x0002;
    private const int ModShift = 0x0004;
    private const int ModWin = 0x0008;
    private const int ModNoRepeat = 0x4000;

    /// <summary>Private id range for WM_HOTKEY; ids only need to be unique per window.</summary>
    private const int FirstGlobalId = 0xE900;

    private readonly MainViewModel _vm;
    private readonly IAppLogger _logger;
    private readonly List<LocalBinding> _local = [];
    private readonly List<GlobalBinding> _global = [];
    private HwndSource? _source;
    private Window? _window;
    private int _nextGlobalId = FirstGlobalId;
    private bool _disposed;

    public HotkeyService(MainViewModel vm, IAppLogger logger)
    {
        _vm = vm;
        _logger = logger;
    }

    /// <summary>Reads the settings and registers everything. Safe to call again via <see cref="Reload"/>.</summary>
    public void Initialize(Window window)
    {
        _window = window;
        Reload();
    }

    /// <summary>Re-reads settings after the user edited the hotkey table.</summary>
    public void Reload()
    {
        if (_disposed || _window is null)
        {
            return;
        }

        UnregisterGlobal();
        _local.Clear();
        _global.Clear();
        _nextGlobalId = FirstGlobalId;

        foreach (var binding in _vm.Settings.Hotkeys)
        {
            if (!HotkeyGesture.TryParse(binding.Gesture, out var gesture) || gesture is null)
            {
                _logger.Warn($"Hotkey skipped (invalid gesture): {binding.Gesture}");
                continue;
            }

            AddBinding(binding, gesture);
        }

        // Conversion presets carry their own shortcut instead of using the playback hotkey table.
        foreach (var preset in _vm.Settings.ConversionPresets)
        {
            if (string.IsNullOrWhiteSpace(preset.Hotkey))
            {
                continue;
            }

            if (!HotkeyGesture.TryParse(preset.Hotkey, out var gesture) || gesture is null)
            {
                _logger.Warn($"Conversion hotkey skipped (invalid gesture): {preset.Hotkey}");
                continue;
            }

            AddBinding(new HotkeyBinding
            {
                Action = HotkeyAction.ConvertPreset,
                Gesture = gesture.ToString(),
                Global = preset.HotkeyGlobal,
                PresetId = preset.Id
            }, gesture);
        }

        if (_global.Count == 0)
        {
            _logger.Info($"Hotkeys loaded: local={_local.Count} global=0");
            return;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
        {
            _logger.Warn("Global hotkeys were not registered: window handle is not available yet.");
            return;
        }

        if (_source is null)
        {
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WndProc);
        }

        var registered = 0;
        foreach (var entry in _global)
        {
            var key = ToWpfKey(entry.Gesture.Key);
            var virtualKey = key is null ? 0 : KeyInterop.VirtualKeyFromKey(key.Value);
            if (virtualKey == 0)
            {
                _logger.Warn($"Global hotkey skipped (unsupported key): {entry.Gesture}");
                continue;
            }

            var modifiers = ToWin32Modifiers(entry.Gesture.Modifiers) | ModNoRepeat;
            if (RegisterHotKey(handle, entry.Id, (uint)modifiers, (uint)virtualKey))
            {
                registered++;
            }
            else
            {
                _logger.Warn($"Global hotkey registration failed (already in use?): {entry.Gesture}");
            }
        }

        _logger.Info($"Hotkeys loaded: local={_local.Count} global={registered}/{_global.Count}");
    }

    /// <summary>
    /// Matches a key press against the local bindings. Plain (unmodified) keys are ignored
    /// while a text box has focus so typing in the search box is not stolen.
    /// </summary>
    public bool TryHandleLocal(KeyEventArgs e)
    {
        if (_disposed || _local.Count == 0)
        {
            return false;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = ToHotkeyModifiers(Keyboard.Modifiers);
        var focused = Keyboard.FocusedElement as DependencyObject;
        var isTextInput = focused is TextBox ||
                          (focused is FrameworkElement element && element.TemplatedParent is TextBox);

        foreach (var entry in _local)
        {
            if (entry.Gesture.Modifiers != modifiers || !MatchesKey(entry.Gesture.Key, key))
            {
                continue;
            }

            if (isTextInput && modifiers == HotkeyModifiers.None)
            {
                continue;
            }

            Execute(entry.Binding);
            return true;
        }

        return false;
    }

    private void AddBinding(HotkeyBinding binding, HotkeyGesture gesture)
    {
        if (binding.Global)
        {
            _global.Add(new GlobalBinding(binding, gesture, _nextGlobalId++));
        }
        else
        {
            _local.Add(new LocalBinding(binding, gesture));
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
        {
            return IntPtr.Zero;
        }

        var id = wParam.ToInt32();
        foreach (var entry in _global)
        {
            if (entry.Id != id)
            {
                continue;
            }

            Execute(entry.Binding);
            handled = true;
            break;
        }

        return IntPtr.Zero;
    }

    private void Execute(HotkeyBinding binding)
    {
        switch (binding.Action)
        {
            case HotkeyAction.PlayPause:
                Await(_vm.PlayPauseCommand.ExecuteAsync(null));
                break;
            case HotkeyAction.Next:
                Await(_vm.PlayNextCommand.ExecuteAsync(null));
                break;
            case HotkeyAction.Previous:
                Await(_vm.PlayPreviousCommand.ExecuteAsync(null));
                break;
            case HotkeyAction.Stop:
                _vm.StopCommand.Execute(null);
                break;
            case HotkeyAction.Mute:
                _vm.ToggleMuteCommand.Execute(null);
                break;
            case HotkeyAction.OpenSearch:
                _vm.OpenAdvancedSearchCommand.Execute(null);
                break;
            case HotkeyAction.ConvertPreset:
                _vm.RequestConversionPreset(binding.PresetId);
                break;
        }
    }

    private void Await(Task task) => _ = AwaitAsync(task);

    private async Task AwaitAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            _logger.Error("Hotkey action failed", ex);
        }
    }

    private void UnregisterGlobal()
    {
        if (_window is not null && _global.Count > 0)
        {
            var handle = new WindowInteropHelper(_window).Handle;
            if (handle != IntPtr.Zero)
            {
                foreach (var entry in _global)
                {
                    UnregisterHotKey(handle, entry.Id);
                }
            }
        }

        _global.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterGlobal();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        _local.Clear();
        _window = null;
    }

    private static bool MatchesKey(string canonicalName, Key key) =>
        ToWpfKey(canonicalName) == key;

    private static Key? ToWpfKey(string name)
    {
        if (name.Length == 1)
        {
            if (name[0] is >= 'A' and <= 'Z')
            {
                return Key.A + (name[0] - 'A');
            }

            if (name[0] is >= '0' and <= '9')
            {
                return Key.D0 + (name[0] - '0');
            }

            return null;
        }

        return Enum.TryParse<Key>(name, ignoreCase: true, out var key) ? key : null;
    }

    private static HotkeyModifiers ToHotkeyModifiers(ModifierKeys modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result |= HotkeyModifiers.Control;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            result |= HotkeyModifiers.Alt;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            result |= HotkeyModifiers.Shift;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            result |= HotkeyModifiers.Windows;
        }

        return result;
    }

    private static int ToWin32Modifiers(HotkeyModifiers modifiers)
    {
        var result = 0;
        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            result |= ModControl;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            result |= ModAlt;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            result |= ModShift;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            result |= ModWin;
        }

        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private sealed record LocalBinding(HotkeyBinding Binding, HotkeyGesture Gesture);

    private sealed record GlobalBinding(HotkeyBinding Binding, HotkeyGesture Gesture, int Id);
}
