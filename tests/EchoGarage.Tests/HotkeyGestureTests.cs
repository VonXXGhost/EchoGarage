using EchoGarage.Core.Models;

namespace EchoGarage.Tests;

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", HotkeyModifiers.Control | HotkeyModifiers.Alt, "Space")]
    [InlineData("ctrl+shift+f5", HotkeyModifiers.Control | HotkeyModifiers.Shift, "F5")]
    [InlineData("MediaPlayPause", HotkeyModifiers.None, "MediaPlayPause")]
    [InlineData("Esc", HotkeyModifiers.None, "Escape")]
    [InlineData("a", HotkeyModifiers.None, "A")]
    [InlineData("5", HotkeyModifiers.None, "5")]
    public void TryParse_AcceptsSupportedGestures(string text, HotkeyModifiers modifiers, string key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.NotNull(gesture);
        Assert.Equal(modifiers, gesture!.Modifiers);
        Assert.Equal(key, gesture.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]
    [InlineData("NotAKey")]
    [InlineData("Hyper+A")]
    public void TryParse_RejectsInvalidGestures(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }

    [Fact]
    public void ToString_UsesCanonicalOrderAndRoundTrips()
    {
        Assert.True(HotkeyGesture.TryParse("shift+win+ctrl+alt+space", out var gesture));
        Assert.Equal("Ctrl+Alt+Shift+Win+Space", gesture!.ToString());

        Assert.True(HotkeyGesture.TryParse(gesture.ToString(), out var again));
        Assert.Equal(gesture, again);
    }

    [Theory]
    [InlineData("MediaPlayPause")]
    [InlineData("mediaplaypause")]
    [InlineData("MediaNextTrack")]
    [InlineData("MediaPreviousTrack")]
    [InlineData("MediaStop")]
    [InlineData("VolumeUp")]
    [InlineData("VolumeDown")]
    [InlineData("VolumeMute")]
    public void TryParse_AcceptsMediaKeys(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.NotNull(gesture);
        Assert.Equal(HotkeyModifiers.None, gesture!.Modifiers);
        Assert.True(HotkeyGesture.IsMediaGesture(gesture));
        Assert.True(HotkeyGesture.IsMediaKeyName(gesture.Key));
    }

    [Theory]
    [InlineData("Space")]
    [InlineData("Escape")]
    [InlineData("Ctrl+F5")]
    public void IsMediaGesture_RejectsRegularKeys(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.False(HotkeyGesture.IsMediaGesture(gesture));
        Assert.False(HotkeyGesture.IsMediaKeyName(gesture!.Key));
    }

    [Fact]
    public void IsMediaGesture_RejectsNull()
    {
        Assert.False(HotkeyGesture.IsMediaGesture(null));
        Assert.False(HotkeyGesture.IsMediaKeyName(null));
    }

    [Theory]
    [InlineData(0x20, "Space")]
    [InlineData(0x0D, "Enter")]
    [InlineData(0x1B, "Escape")]
    [InlineData(0x08, "Backspace")]
    [InlineData(0x2E, "Delete")]
    [InlineData(0x41, "A")]
    [InlineData(0x39, "9")]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    [InlineData(0xAD, "VolumeMute")]
    [InlineData(0xAE, "VolumeDown")]
    [InlineData(0xAF, "VolumeUp")]
    [InlineData(0xB0, "MediaNextTrack")]
    [InlineData(0xB1, "MediaPreviousTrack")]
    [InlineData(0xB2, "MediaStop")]
    [InlineData(0xB3, "MediaPlayPause")]
    public void KeyNameFromVirtualKey_MapsKnownKeys(int virtualKey, string expected)
    {
        Assert.Equal(expected, HotkeyGesture.KeyNameFromVirtualKey(virtualKey));

        // Every mapped name must round-trip through the parser as its canonical spelling.
        Assert.True(HotkeyGesture.TryParse(expected, out var gesture));
        Assert.Equal(expected, gesture!.Key);
        Assert.Equal(expected, gesture.ToString());
    }

    [Fact]
    public void KeyNameFromVirtualKey_ReturnsNullForUnknownKeys()
    {
        Assert.Null(HotkeyGesture.KeyNameFromVirtualKey(0x01));
        Assert.Null(HotkeyGesture.KeyNameFromVirtualKey(0xFF));
    }

    [Theory]
    [InlineData(0xB0, true)]
    [InlineData(0xB1, true)]
    [InlineData(0xB2, true)]
    [InlineData(0xB3, true)]
    [InlineData(0xAD, true)]
    [InlineData(0xAE, true)]
    [InlineData(0xAF, true)]
    [InlineData(0x20, false)]
    [InlineData(0x41, false)]
    [InlineData(0x01, false)]
    public void IsMediaVirtualKey_RecognizesMediaKeys(int virtualKey, bool expected)
    {
        Assert.Equal(expected, HotkeyGesture.IsMediaVirtualKey(virtualKey));
    }

    [Fact]
    public void MediaGesture_WithModifiersRoundTrips()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+MediaNextTrack", out var gesture));
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, gesture!.Modifiers);
        Assert.True(HotkeyGesture.IsMediaGesture(gesture));
        Assert.Equal("Ctrl+Alt+MediaNextTrack", gesture.ToString());

        Assert.True(HotkeyGesture.TryParse(gesture.ToString(), out var again));
        Assert.Equal(gesture, again);
    }

    [Theory]
    [InlineData("MediaPlayPause", HotkeyAction.PlayPause)]
    [InlineData("MediaNextTrack", HotkeyAction.Next)]
    [InlineData("MediaPreviousTrack", HotkeyAction.Previous)]
    [InlineData("MediaStop", HotkeyAction.Stop)]
    public void ResolveMediaAction_MapsStandardMediaKeysWithoutConfiguration(
        string text,
        HotkeyAction expected)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(expected, HotkeyDefaults.ResolveMediaAction(gesture));
    }

    [Theory]
    [InlineData("Ctrl+MediaPlayPause")]
    [InlineData("Shift+MediaNextTrack")]
    [InlineData("VolumeMute")]
    [InlineData("Space")]
    public void ResolveMediaAction_IgnoresModifiedAndNonDefaultKeys(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Null(HotkeyDefaults.ResolveMediaAction(gesture));
    }

    [Fact]
    public void ResolveMediaAction_ReturnsNullForNull()
    {
        Assert.Null(HotkeyDefaults.ResolveMediaAction(null));
    }

    [Fact]
    public void OpenTagProperties_IsDefinedAndDistinct()
    {
        Assert.True(Enum.IsDefined(HotkeyAction.OpenTagProperties));
        Assert.Equal(
            Enum.GetValues<HotkeyAction>().Length,
            Enum.GetValues<HotkeyAction>().Distinct().Count());
    }
}
