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
}
