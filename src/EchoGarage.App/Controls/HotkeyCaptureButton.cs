using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EchoGarage.App.Services;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Controls;

/// <summary>
/// A button that records a keyboard shortcut instead of opening a text box: click it, press
/// the combination, and the <see cref="Gesture"/> property is updated. Esc cancels (restoring
/// the previous value), Backspace/Delete clears the binding and unmodified modifier keys keep
/// waiting for the actual key. Media keys (play/pause, next/previous track, stop and the
/// volume keys) are captured through the low-level keyboard hook because WPF never sees them.
/// </summary>
public sealed class HotkeyCaptureButton : Button
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture),
        typeof(string),
        typeof(HotkeyCaptureButton),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnGestureChanged));

    private static readonly DependencyPropertyKey IsCapturingPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsCapturing),
        typeof(bool),
        typeof(HotkeyCaptureButton),
        new PropertyMetadata(false));

    public static readonly DependencyProperty IsCapturingProperty = IsCapturingPropertyKey.DependencyProperty;

    private Window? _captureWindow;
    private bool _sessionOpen;
    private string _gestureBeforeCapture = string.Empty;
    private HotkeyModifiers _pendingModifiers;

    public HotkeyCaptureButton()
    {
        IsTabStop = false;
        ToolTip = "点击后按下组合键完成绑定；Esc 取消并恢复原值，Backspace 或 Delete 清除。"
                  + "支持媒体键（播放/暂停、上一首/下一首、停止等）。";
        Unloaded += OnUnloaded;
        UpdateDisplay();
    }

    /// <summary>Raised after a new gesture was recorded. <see cref="Gesture"/> already holds the new value.</summary>
    public event EventHandler<HotkeyGestureCommittedEventArgs>? GestureCommitted;

    /// <summary>Raised when the user pressed Esc or the capture was aborted; the old value is restored.</summary>
    public event EventHandler? CaptureCancelled;

    /// <summary>Canonical gesture text (empty means "not set").</summary>
    public string Gesture
    {
        get => (string?)GetValue(GestureProperty) ?? string.Empty;
        set => SetValue(GestureProperty, value ?? string.Empty);
    }

    /// <summary>True while the button is waiting for a key press.</summary>
    public bool IsCapturing => (bool)GetValue(IsCapturingProperty);

    /// <summary>Starts capturing (normally triggered by a click).</summary>
    public void BeginCapture()
    {
        if (_sessionOpen || !IsEnabled)
        {
            return;
        }

        _sessionOpen = true;
        _gestureBeforeCapture = Gesture ?? string.Empty;
        _pendingModifiers = HotkeyModifiers.None;
        SetValue(IsCapturingPropertyKey, true);

        // The hook is needed for media keys while recording; it suppresses them for other apps.
        MediaKeyHookService.BeginCapture(OnMediaKeyCaptured);
        AttachWindowEvents();
        UpdateDisplay();

        Focus();
        Keyboard.Focus(this);
    }

    /// <summary>Leaves capture mode without changing the current gesture.</summary>
    public void CancelCapture()
    {
        if (!_sessionOpen)
        {
            return;
        }

        EndCaptureSession();
        Gesture = _gestureBeforeCapture;
        UpdateDisplay();
        CaptureCancelled?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClick()
    {
        if (_sessionOpen)
        {
            CancelCapture();
            return;
        }

        base.OnClick();
        BeginCapture();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_sessionOpen)
        {
            CancelCapture();
        }
    }

    private static void OnGestureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HotkeyCaptureButton button && !button._sessionOpen)
        {
            button.UpdateDisplay();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => CancelCapture();

    private void AttachWindowEvents()
    {
        _captureWindow = Window.GetWindow(this);
        if (_captureWindow is null)
        {
            return;
        }

        _captureWindow.PreviewKeyDown += OnCaptureWindowPreviewKeyDown;
        _captureWindow.Deactivated += OnCaptureWindowDeactivated;
        _captureWindow.Closing += OnCaptureWindowClosing;
    }

    private void DetachWindowEvents()
    {
        if (_captureWindow is null)
        {
            return;
        }

        _captureWindow.PreviewKeyDown -= OnCaptureWindowPreviewKeyDown;
        _captureWindow.Deactivated -= OnCaptureWindowDeactivated;
        _captureWindow.Closing -= OnCaptureWindowClosing;
        _captureWindow = null;
    }

    private void OnCaptureWindowDeactivated(object? sender, EventArgs e) => CancelCapture();

    private void OnCaptureWindowClosing(object? sender, CancelEventArgs e) => CancelCapture();

    private void OnCaptureWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_sessionOpen)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        // Swallow everything so the window's own Esc / Enter / Space bindings never fire while
        // the user is recording a gesture.
        e.Handled = true;

        if (key == Key.Escape)
        {
            CancelCapture();
            return;
        }

        if (key is Key.Back or Key.Delete && modifiers == ModifierKeys.None)
        {
            CommitGesture(string.Empty);
            return;
        }

        if (IsModifierKey(key))
        {
            // Wait for the actual key; show the pressed modifiers so the user gets feedback.
            _pendingModifiers = ToHotkeyModifiers(modifiers);
            UpdateDisplay();
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        var keyName = HotkeyGesture.KeyNameFromVirtualKey(virtualKey);
        if (keyName is null)
        {
            return;
        }

        CommitGesture(new HotkeyGesture(ToHotkeyModifiers(modifiers), keyName).ToString());
    }

    /// <summary>Called by the low-level hook on the UI thread while this button is capturing.</summary>
    private void OnMediaKeyCaptured(string keyName)
    {
        if (!_sessionOpen)
        {
            return;
        }

        // Reading Keyboard.Modifiers inside the hook callback is accurate; deferring the actual
        // commit keeps the hook callback short.
        var modifiers = ToHotkeyModifiers(Keyboard.Modifiers);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_sessionOpen)
            {
                return;
            }

            CommitGesture(new HotkeyGesture(modifiers, keyName).ToString());
        }));
    }

    private void CommitGesture(string canonicalGesture)
    {
        if (!_sessionOpen)
        {
            return;
        }

        var previous = _gestureBeforeCapture;
        EndCaptureSession();
        Gesture = canonicalGesture;
        UpdateDisplay();
        GestureCommitted?.Invoke(this, new HotkeyGestureCommittedEventArgs(canonicalGesture, previous));
    }

    private void EndCaptureSession()
    {
        if (!_sessionOpen)
        {
            return;
        }

        _sessionOpen = false;
        SetValue(IsCapturingPropertyKey, false);
        DetachWindowEvents();
        MediaKeyHookService.EndCapture(OnMediaKeyCaptured);
        _pendingModifiers = HotkeyModifiers.None;
    }

    private void UpdateDisplay()
    {
        if (_sessionOpen)
        {
            Content = _pendingModifiers == HotkeyModifiers.None
                ? "按下快捷键…（Esc 退出）"
                : FormatModifiers(_pendingModifiers) + "…";
            return;
        }

        var gesture = (Gesture ?? string.Empty).Trim();
        Content = gesture.Length == 0 ? "未设置（点击设置）" : gesture;
    }

    private static string FormatModifiers(HotkeyModifiers modifiers)
    {
        var builder = new StringBuilder();
        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            builder.Append("Ctrl+");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            builder.Append("Alt+");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            builder.Append("Shift+");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            builder.Append("Win+");
        }

        return builder.ToString().TrimEnd('+');
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

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
}

/// <summary>Payload for <see cref="HotkeyCaptureButton.GestureCommitted"/>.</summary>
public sealed class HotkeyGestureCommittedEventArgs : EventArgs
{
    public HotkeyGestureCommittedEventArgs(string gesture, string previousGesture)
    {
        Gesture = gesture;
        PreviousGesture = previousGesture;
    }

    /// <summary>The newly recorded canonical gesture; empty when the binding was cleared.</summary>
    public string Gesture { get; }

    /// <summary>The gesture that was set before recording started, used to roll back conflicts.</summary>
    public string PreviousGesture { get; }
}
