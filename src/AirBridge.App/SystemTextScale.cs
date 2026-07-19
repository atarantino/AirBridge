using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace AirBridge.App;

internal sealed class TextScaleChangedEventArgs(float previous, float current) : EventArgs
{
    internal float Previous { get; } = previous;
    internal float Current { get; } = current;
}

/// <summary>Reads the Windows Accessibility text-size setting independently of display DPI.</summary>
internal static class SystemTextScale
{
    private const string AccessibilityKey = @"Software\Microsoft\Accessibility";
    private const string TextScaleValue = "TextScaleFactor";
    private const string PreviewOverride = "AIRBRIDGE_TEXT_SCALE_PERCENT";
    private static float _current = ReadCurrent();

    internal static float Current => _current;
    internal static event EventHandler<TextScaleChangedEventArgs>? Changed;

    internal static void Refresh()
    {
        var next = ReadCurrent();
        if (Math.Abs(next - _current) < 0.001f) return;
        var previous = _current;
        _current = next;
        Changed?.Invoke(null, new(previous, next));
    }

    internal static float Normalize(object? value)
    {
        var percent = value switch
        {
            int number => number,
            long number => (int)Math.Clamp(number, 100L, 225L),
            string text when int.TryParse(text, out var number) => number,
            _ => 100
        };
        return Math.Clamp(percent, 100, 225) / 100f;
    }

    private static float ReadCurrent()
    {
        var preview = Environment.GetEnvironmentVariable(PreviewOverride);
        if (!string.IsNullOrWhiteSpace(preview)) return Normalize(preview);
        try
        {
            return Math.Clamp((float)new UISettings().TextScaleFactor, 1f, 2.25f);
        }
        catch (Exception)
        {
            // The documented WinRT API can fail to activate in restricted sessions.
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AccessibilityKey);
            return Normalize(key?.GetValue(TextScaleValue));
        }
        catch (Exception)
        {
            return 1f;
        }
    }
}
