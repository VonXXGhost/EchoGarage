using System.Text;
using System.Text.Json.Serialization;

namespace EchoGarage.Core.Models;

/// <summary>Playback actions that can be bound to a hotkey.</summary>
public enum HotkeyAction
{
    PlayPause,
    Next,
    Previous,
    Stop,
    Mute,

    /// <summary>Opens the advanced search dialog (defaults to Ctrl+F when not configured).</summary>
    OpenSearch,

    /// <summary>Opens the tag properties dialog for the selected track (defaults to Alt+Enter when not configured).</summary>
    OpenTagProperties,

    /// <summary>Starts the audio converter with one of the configured conversion presets.</summary>
    ConvertPreset
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8
}

/// <summary>
/// A parsed keyboard shortcut such as <c>Ctrl+Alt+Space</c>. The key name is stored in a
/// canonical form (letters upper case, digits as-is, WPF key names such as <c>Space</c>,
/// <c>MediaPlayPause</c> or <c>F5</c>) so the same value can be matched against WPF
/// <c>Key</c> values and registered with Win32 <c>RegisterHotKey</c>.
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    private static readonly Dictionary<string, string> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = "Space",
        ["enter"] = "Enter",
        ["return"] = "Enter",
        ["tab"] = "Tab",
        ["esc"] = "Escape",
        ["escape"] = "Escape",
        ["backspace"] = "Backspace",
        ["back"] = "Backspace",
        ["delete"] = "Delete",
        ["del"] = "Delete",
        ["insert"] = "Insert",
        ["ins"] = "Insert",
        ["home"] = "Home",
        ["end"] = "End",
        ["pageup"] = "PageUp",
        ["pgup"] = "PageUp",
        ["pagedown"] = "PageDown",
        ["pgdn"] = "PageDown",
        ["left"] = "Left",
        ["right"] = "Right",
        ["up"] = "Up",
        ["down"] = "Down",
        ["mediaplaypause"] = "MediaPlayPause",
        ["playpause"] = "MediaPlayPause",
        ["medianexttrack"] = "MediaNextTrack",
        ["nexttrack"] = "MediaNextTrack",
        ["mediaprevioustrack"] = "MediaPreviousTrack",
        ["prevtrack"] = "MediaPreviousTrack",
        ["mediastop"] = "MediaStop",
        ["volumeup"] = "VolumeUp",
        ["volup"] = "VolumeUp",
        ["volumedown"] = "VolumeDown",
        ["voldown"] = "VolumeDown",
        ["volumemute"] = "VolumeMute",
        ["mute"] = "VolumeMute"
    };

    /// <summary>
    /// Parses text like <c>Ctrl+Shift+Right</c> or <c>Alt+F5</c>. The last part is the key;
    /// all preceding parts are modifiers (Ctrl/Control, Alt, Shift, Win/Windows).
    /// </summary>
    public static bool TryParse(string? text, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 5)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "win":
                case "windows":
                case "meta":
                    modifiers |= HotkeyModifiers.Windows;
                    break;
                default:
                    return false;
            }
        }

        var key = NormalizeKey(parts[^1]);
        if (key is null)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    /// <summary>Canonicalizes a single key name; returns null when it is not supported.</summary>
    public static string? NormalizeKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length == 1)
        {
            var ch = char.ToUpperInvariant(trimmed[0]);
            if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))
            {
                return ch.ToString();
            }
        }

        if (trimmed.Length is >= 2 and <= 3 &&
            (trimmed[0] is 'f' or 'F') &&
            int.TryParse(trimmed[1..], out var function) &&
            function is >= 1 and <= 24)
        {
            return "F" + function;
        }

        return KeyNames.TryGetValue(trimmed, out var name) ? name : null;
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            builder.Append("Ctrl+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            builder.Append("Alt+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            builder.Append("Shift+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            builder.Append("Win+");
        }

        builder.Append(Key);
        return builder.ToString();
    }

    private static readonly HashSet<string> MediaKeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "MediaPlayPause",
        "MediaNextTrack",
        "MediaPreviousTrack",
        "MediaStop",
        "VolumeUp",
        "VolumeDown",
        "VolumeMute"
    };

    /// <summary>
    /// True for multimedia / volume keys. WPF's key events and <c>RegisterHotKey</c> never see
    /// these, so they are handled by the low-level keyboard hook instead.
    /// </summary>
    public static bool IsMediaKeyName(string? keyName) =>
        !string.IsNullOrEmpty(keyName) && MediaKeyNames.Contains(keyName);

    /// <summary>True when the gesture must be handled by the low-level keyboard hook.</summary>
    public static bool IsMediaGesture(HotkeyGesture? gesture) =>
        gesture is not null && IsMediaKeyName(gesture.Key);

    /// <summary>True when the Win32 virtual-key code is a multimedia / volume key.</summary>
    public static bool IsMediaVirtualKey(int virtualKey) =>
        KeyNameFromVirtualKey(virtualKey) is { } name && IsMediaKeyName(name);

    /// <summary>
    /// Maps a Win32 virtual-key code to a canonical gesture key name. Returns null for keys
    /// that cannot be represented by <see cref="HotkeyGesture"/>.
    /// </summary>
    public static string? KeyNameFromVirtualKey(int virtualKey) => virtualKey switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Escape",
        0x20 => "Space",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)('0' + (virtualKey - 0x30))).ToString(),
        >= 0x41 and <= 0x5A => ((char)('A' + (virtualKey - 0x41))).ToString(),
        >= 0x70 and <= 0x87 => "F" + (virtualKey - 0x70 + 1),
        0xAD => "VolumeMute",
        0xAE => "VolumeDown",
        0xAF => "VolumeUp",
        0xB0 => "MediaNextTrack",
        0xB1 => "MediaPreviousTrack",
        0xB2 => "MediaStop",
        0xB3 => "MediaPlayPause",
        _ => null
    };
}

/// <summary>
/// Built-in media-key mappings. The standard multimedia keys control playback without any
/// configuration, mirroring their system-wide purpose. Binding the same media key to another
/// action in the settings overrides the default mapping for that key.
/// </summary>
public static class HotkeyDefaults
{
    private static readonly Dictionary<string, HotkeyAction> MediaActions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MediaPlayPause"] = HotkeyAction.PlayPause,
        ["MediaNextTrack"] = HotkeyAction.Next,
        ["MediaPreviousTrack"] = HotkeyAction.Previous,
        ["MediaStop"] = HotkeyAction.Stop
    };

    /// <summary>Keys that respond to their built-in playback action without being configured.</summary>
    public static IReadOnlyCollection<string> MediaKeyNames => MediaActions.Keys;

    /// <summary>
    /// Resolves the default action for a media-key gesture. Only unmodified media keys are
    /// mapped; combinations such as <c>Ctrl+MediaPlayPause</c> are always user-configured.
    /// </summary>
    public static HotkeyAction? ResolveMediaAction(HotkeyGesture? gesture) =>
        gesture is { Modifiers: HotkeyModifiers.None } &&
        MediaActions.TryGetValue(gesture.Key, out var action)
            ? action
            : null;
}

/// <summary>One user-configured playback hotkey, persisted in <c>settings.json</c>.</summary>
public sealed class HotkeyBinding
{
    public HotkeyAction Action { get; set; }

    /// <summary>Gesture text, canonicalized by <see cref="HotkeyGesture"/> (e.g. <c>Ctrl+Alt+Space</c>).</summary>
    public string Gesture { get; set; } = string.Empty;

    /// <summary>When true the shortcut is registered system-wide (Win32 RegisterHotKey).</summary>
    public bool Global { get; set; }

    /// <summary>
    /// Conversion preset id for <see cref="HotkeyAction.ConvertPreset"/>. Conversion preset
    /// shortcuts are stored on the preset itself, so this is only filled in by the hotkey
    /// service and is never serialized.
    /// </summary>
    [JsonIgnore]
    public string? PresetId { get; set; }

    public HotkeyBinding Clone() => (HotkeyBinding)MemberwiseClone();
}
