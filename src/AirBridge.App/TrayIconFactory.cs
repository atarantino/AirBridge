using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AirBridge.Core;

namespace AirBridge.App;

public static class TrayIconFactory
{
    /// <summary>Creates an owned icon. The caller must dispose it after replacing it on NotifyIcon.</summary>
    public static Icon Create(StreamState state, bool aiAnalysis = false, int size = 32, ThemePalette? palette = null)
    {
        size = Math.Clamp(size, 16, 64);
        var colors = palette ?? ThemePalette.Current();
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        var foreground = colors.IsHighContrast ? SystemColors.WindowText : Color.FromArgb(238, 242, 248);
        var body = new RectangleF(size * .13f, size * .19f, size * .69f, size * .60f);
        using var bodyBrush = new SolidBrush(colors.IsHighContrast ? SystemColors.Window : Color.FromArgb(42, 48, 58));
        using var linePen = new Pen(foreground, Math.Max(1.5f, size * .07f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.FillRoundedRectangle(bodyBrush, body, size * .13f);
        graphics.DrawLine(linePen, size * .28f, size * .58f, size * .28f, size * .42f);
        graphics.DrawLine(linePen, size * .45f, size * .64f, size * .45f, size * .35f);
        graphics.DrawLine(linePen, size * .62f, size * .55f, size * .62f, size * .44f);

        var indicator = aiAnalysis ? Color.FromArgb(155, 105, 255) : colors.StateColor(state);
        using var indicatorBrush = new SolidBrush(indicator);
        using var outlinePen = new Pen(colors.IsHighContrast ? SystemColors.WindowText : Color.White, Math.Max(1, size * .045f));
        var dot = new RectangleF(size * .64f, size * .59f, size * .27f, size * .27f);
        graphics.FillEllipse(indicatorBrush, dot);
        graphics.DrawEllipse(outlinePen, dot);

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
