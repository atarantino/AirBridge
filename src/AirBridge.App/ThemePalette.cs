using Microsoft.Win32;
using AirBridge.Core;

namespace AirBridge.App;

public enum AppThemeMode { System, Light, Dark }

/// <summary>Centralized, high-contrast-aware colors for the WinForms surfaces.</summary>
public sealed record ThemePalette(
    Color Window,
    Color Surface,
    Color SurfaceHover,
    Color SurfacePressed,
    Color SurfaceSelected,
    Color Border,
    Color Text,
    Color SecondaryText,
    Color Accent,
    Color Success,
    Color Warning,
    Color Error,
    Color Focus,
    Color OnAccent,
    Color SliderTrack,
    Color Thumb,
    bool IsDark,
    bool IsHighContrast)
{
    public Color Available => IsHighContrast ? SystemColors.WindowText : Success;
    public Color Unavailable => IsHighContrast ? SystemColors.WindowText : SecondaryText;
    public Color Streaming => IsHighContrast ? SystemColors.Highlight : Accent;
    public Color SliderTrackDimmed => Color.FromArgb(IsDark ? 105 : 115, SliderTrack);
    public Color SliderFillDimmed => Color.FromArgb(IsDark ? 125 : 135, SecondaryText);
    public Color ThumbDimmed => Color.FromArgb(IsDark ? 145 : 155, Thumb);

    public static ThemePalette Current(AppThemeMode mode = AppThemeMode.System)
    {
        if (SystemInformation.HighContrast)
        {
            return new(
                SystemColors.Window, SystemColors.Control, SystemColors.ControlLight, SystemColors.ControlDark,
                SystemColors.Highlight,
                SystemColors.WindowText, SystemColors.WindowText, SystemColors.GrayText,
                SystemColors.Highlight, SystemColors.Highlight, SystemColors.Highlight,
                SystemColors.Highlight, SystemColors.Highlight, SystemColors.HighlightText,
                SystemColors.ControlDark, SystemColors.Window, false, true);
        }

        var dark = mode == AppThemeMode.Dark || mode == AppThemeMode.System && AppsPreferDark();
        return dark
            ? new(
                Color.FromArgb(20, 21, 24), Color.FromArgb(31, 33, 38), Color.FromArgb(40, 43, 49),
                Color.FromArgb(48, 52, 59), Color.FromArgb(51, 55, 63), Color.FromArgb(58, 62, 70),
                Color.FromArgb(244, 245, 247), Color.FromArgb(164, 169, 178), Color.FromArgb(88, 156, 255),
                Color.FromArgb(73, 190, 118), Color.FromArgb(237, 178, 70), Color.FromArgb(242, 99, 104),
                Color.FromArgb(126, 177, 255), Color.White, Color.FromArgb(68, 72, 81), Color.White, true, false)
            : new(
                Color.FromArgb(245, 246, 248), Color.FromArgb(253, 253, 254), Color.FromArgb(245, 247, 250),
                Color.FromArgb(235, 238, 243), Color.White, Color.FromArgb(218, 222, 229),
                Color.FromArgb(29, 31, 35), Color.FromArgb(100, 105, 114), Color.FromArgb(23, 104, 214),
                Color.FromArgb(28, 133, 74), Color.FromArgb(159, 105, 10), Color.FromArgb(190, 48, 56),
                Color.FromArgb(23, 104, 214), Color.White, Color.FromArgb(221, 225, 231), Color.White, false, false);
    }

    public Color StateColor(StreamState state) => state switch
    {
        StreamState.Streaming => Streaming,
        StreamState.Connecting or StreamState.Negotiating or StreamState.Buffering or StreamState.Discovering or StreamState.Standby => Accent,
        StreamState.Degraded or StreamState.Reconnecting => Warning,
        StreamState.Failed => Error,
        StreamState.Idle => Available,
        _ => Unavailable
    };

    public void Apply(Control root)
    {
        ApplyRecursive(root);
        root.Invalidate(true);
    }

    private void ApplyRecursive(Control control)
    {
        control.ForeColor = Text;
        control.BackColor = control is Form ? Window : Surface;
        foreach (Control child in control.Controls) ApplyRecursive(child);
    }

    private static bool AppsPreferDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
