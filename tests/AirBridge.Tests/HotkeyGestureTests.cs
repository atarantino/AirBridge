using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("Ctrl+Shift+F9")]
    [InlineData("Win+Space")]
    [InlineData("F13")]
    public void ParseAndFormatRoundTrips(string value)
    {
        Assert.True(HotkeyGesture.TryParse(value, out var gesture));
        Assert.Equal(value, gesture.ToString());
        Assert.True(HotkeyGesture.TryParse(gesture.ToString(), out var reparsed));
        Assert.Equal(gesture, reparsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl+NotAKey")]
    [InlineData("Ctrl+Alt")]
    [InlineData("A")]
    public void InvalidGesturesFailToParse(string? value) => Assert.False(HotkeyGesture.TryParse(value, out _));

    [Fact]
    public void AliasesProduceCanonicalNames()
    {
        Assert.True(HotkeyGesture.TryParse("Control+Escape", out var controlEscape));
        Assert.Equal("Ctrl+Esc", controlEscape.ToString());
        Assert.True(HotkeyGesture.TryParse("Ctrl+Esc", out var ctrlEsc));
        Assert.Equal(controlEscape, ctrlEsc);
    }

    [Fact]
    public void RegistrationArgumentsIncludeNoRepeat()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Space", out var gesture));
        var (modifiers, virtualKey) = gesture.ToRegisterHotKeyArgs();
        Assert.Equal(0x4003u, modifiers);
        Assert.Equal(0x20u, virtualKey);
    }
}
