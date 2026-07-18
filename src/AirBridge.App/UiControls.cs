using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AirBridge.App;

internal static class UiGeometry
{
    public static int Scale(Control control, int value) => Math.Max(1, (int)Math.Round(value * control.DeviceDpi / 96f));

    public static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0) return path;
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Font UiFont(float size, FontStyle style = FontStyle.Regular)
    {
        using var candidate = new Font("Segoe UI Variable Text", size, style);
        return candidate.Name.Contains("Segoe UI Variable", StringComparison.OrdinalIgnoreCase)
            ? new Font(candidate.FontFamily, size, style)
            : new Font("Segoe UI", size, style);
    }

    public static Font IconFont(float size)
    {
        using var candidate = new Font("Segoe Fluent Icons", size);
        return candidate.Name.Equals("Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase)
            ? new Font(candidate.FontFamily, size)
            : new Font("Segoe MDL2 Assets", size);
    }
}

internal interface IThemeAware
{
    void ApplyTheme(ThemePalette palette);
}

internal sealed class LoadingIndicator : Control, IThemeAware
{
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 50 };
    private ThemePalette _palette = ThemePalette.Current();
    private int _angle;

    internal LoadingIndicator()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Height = 44;
        Margin = new Padding(0, 2, 0, 2);
        Text = "Looking for speakers…";
        Font = UiGeometry.UiFont(9F);
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "Looking for AirPlay speakers";
        AccessibleDescription = "AirBridge is refreshing the available speakers.";
        _animation.Tick += (_, _) => { _angle = (_angle + 18) % 360; Invalidate(); };
    }

    public void ApplyTheme(ThemePalette palette) { _palette = palette; Invalidate(); }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _animation.Enabled = Visible;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var spinnerSize = UiGeometry.Scale(this, 16);
        var gap = UiGeometry.Scale(this, 9);
        var textSize = TextRenderer.MeasureText(e.Graphics, Text, Font, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var totalWidth = spinnerSize + gap + textSize.Width;
        var left = Math.Max(UiGeometry.Scale(this, 12), (Width - totalWidth) / 2);
        var spinner = new Rectangle(left, (Height - spinnerSize) / 2, spinnerSize, spinnerSize);

        if (_palette.IsHighContrast)
        {
            using var pen = new Pen(SystemColors.Highlight, Math.Max(2, UiGeometry.Scale(this, 2)));
            e.Graphics.DrawArc(pen, spinner, _angle, 250);
        }
        else
        {
            using var track = new Pen(_palette.Border, Math.Max(2, UiGeometry.Scale(this, 2)));
            using var active = new Pen(_palette.Accent, Math.Max(2, UiGeometry.Scale(this, 2)))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            e.Graphics.DrawEllipse(track, spinner);
            e.Graphics.DrawArc(active, spinner, _angle, 105);
        }

        var textBounds = new Rectangle(spinner.Right + gap, 0, Math.Max(1, Width - spinner.Right - gap), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
            _palette.IsHighContrast ? SystemColors.WindowText : _palette.SecondaryText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _animation.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class LetterSpacedLabel : Control, IThemeAware
{
    private ThemePalette _palette = ThemePalette.Current();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal float Tracking { get; set; } = 1.5f;

    public LetterSpacedLabel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }

    public void ApplyTheme(ThemePalette palette) { _palette = palette; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_palette.IsHighContrast)
        {
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, SystemColors.WindowText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }

        e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(_palette.SecondaryText);
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        var x = 0f;
        var y = Math.Max(0, (Height - Font.Height) / 2f);
        var tracking = Tracking * DeviceDpi / 96f;
        foreach (var character in Text)
        {
            var text = character.ToString();
            e.Graphics.DrawString(text, Font, brush, new PointF(x, y), format);
            x += e.Graphics.MeasureString(text, Font, PointF.Empty, format).Width + tracking;
        }
    }
}

internal sealed class AntiAliasedLabel : Control
{
    public AntiAliasedLabel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(ForeColor);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        e.Graphics.DrawString(Text, Font, brush, ClientRectangle, format);
    }
}

internal sealed class RoundedPanel : Panel, IThemeAware
{
    private ThemePalette _palette = ThemePalette.Current();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int Radius { get; set; } = 12;

    public RoundedPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Padding = new Padding(1);
    }

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        BackColor = palette.Surface;
        UpdateRegion();
        Invalidate(true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_palette.IsHighContrast) { base.OnPaintBackground(e); return; }
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? _palette.Window);
        var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
        using var path = UiGeometry.Rounded(bounds, UiGeometry.Scale(this, Radius));
        using var fill = new SolidBrush(_palette.Surface);
        using var border = new Pen(_palette.Border);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
    }

    protected override void OnResize(EventArgs eventargs) { base.OnResize(eventargs); UpdateRegion(); }

    private void UpdateRegion()
    {
        if (_palette.IsHighContrast) { Region = null; return; }
        using var path = UiGeometry.Rounded(ClientRectangle, UiGeometry.Scale(this, Radius));
        Region = new Region(path);
    }
}

internal sealed class PillButton : Button, IThemeAware
{
    private ThemePalette _palette = ThemePalette.Current();
    private bool _hovered;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string? IconGlyph { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool GrayscaleText { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Primary { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Quiet { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool TransparentQuiet { get; set; }

    public PillButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        AutoSize = false;
        Height = 36;
        Cursor = Cursors.Hand;
        Font = UiGeometry.UiFont(9F, FontStyle.Regular);
    }

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        UpdateButtonRegion();
        Invalidate();
    }
    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateButtonRegion(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_palette.IsHighContrast) { base.OnPaint(e); return; }
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (TransparentQuiet || Parent?.BackColor.A is < 255) base.OnPaintBackground(e);
        else e.Graphics.Clear(Parent?.BackColor ?? _palette.Surface);
        var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
        using var path = UiGeometry.Rounded(bounds, UiGeometry.Scale(this, Quiet ? 8 : 18));
        var fillColor = Primary ? _palette.Accent
            : TransparentQuiet && Quiet ? _hovered || Focused ? _palette.SurfaceHover : Color.Transparent
            : _hovered ? _palette.SurfacePressed : Quiet ? _palette.Surface : _palette.SurfaceHover;
        using var fill = new SolidBrush(fillColor);
        if (fillColor.A > 0) e.Graphics.FillPath(fill, path);
        if (!Quiet && !Primary)
        {
            using var border = new Pen(_palette.Border);
            e.Graphics.DrawPath(border, path);
        }
        var textColor = Primary ? _palette.OnAccent : _palette.Text;
        if (GrayscaleText)
        {
            DrawGrayscaleContent(e.Graphics, bounds, textColor);
            DrawFocus(e.Graphics, bounds);
            return;
        }

        var textBounds = bounds;
        if (!string.IsNullOrEmpty(IconGlyph))
        {
            var iconWidth = UiGeometry.Scale(this, 22);
            var combinedWidth = TextRenderer.MeasureText(Text, Font).Width + iconWidth;
            var left = bounds.Left + Math.Max(0, (bounds.Width - combinedWidth) / 2);
            using var iconFont = UiGeometry.IconFont(11F);
            TextRenderer.DrawText(e.Graphics, IconGlyph, iconFont, new Rectangle(left, bounds.Top, iconWidth, bounds.Height), textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            textBounds = new Rectangle(left + iconWidth, bounds.Top, Math.Max(1, bounds.Right - left - iconWidth), bounds.Height);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, Enabled ? textColor : _palette.SecondaryText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        DrawFocus(e.Graphics, bounds);
    }

    private void DrawGrayscaleContent(Graphics graphics, Rectangle bounds, Color color)
    {
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(color);
        using var centered = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        if (string.IsNullOrEmpty(IconGlyph))
        {
            graphics.DrawString(Text, Font, brush, bounds, centered);
            return;
        }

        using var iconFont = UiGeometry.IconFont(11F);
        if (string.IsNullOrEmpty(Text))
        {
            graphics.DrawString(IconGlyph, iconFont, brush, bounds, centered);
            return;
        }

        var iconWidth = UiGeometry.Scale(this, 22);
        var textWidth = graphics.MeasureString(Text, Font, PointF.Empty, StringFormat.GenericTypographic).Width;
        var left = bounds.Left + Math.Max(0, (bounds.Width - textWidth - iconWidth) / 2f);
        using var leftFormat = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };
        graphics.DrawString(IconGlyph, iconFont, brush, new RectangleF(left, bounds.Top, iconWidth, bounds.Height), leftFormat);
        graphics.DrawString(Text, Font, brush, new RectangleF(left + iconWidth, bounds.Top, bounds.Right - left - iconWidth, bounds.Height), leftFormat);
    }

    private void DrawFocus(Graphics graphics, Rectangle bounds)
    {
        if (!Focused || !ShowFocusCues) return;
        var focus = Rectangle.Inflate(bounds, -3, -3);
        using var pen = new Pen(_palette.Focus) { DashStyle = DashStyle.Dot };
        using var focusPath = UiGeometry.Rounded(focus, UiGeometry.Scale(this, 14));
        graphics.DrawPath(pen, focusPath);
    }

    private void UpdateButtonRegion()
    {
        var old = Region;
        Region = null;
        old?.Dispose();
        if (_palette.IsHighContrast || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var path = UiGeometry.Rounded(ClientRectangle, UiGeometry.Scale(this, Quiet ? 8 : 18));
        Region = new Region(path);
    }
}

internal sealed class SegmentedControl : Control, IThemeAware
{
    private ThemePalette _palette = ThemePalette.Current();
    private int _selectedIndex;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string[] Items { get; set; } = ["System", "App"];
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int SelectedIndex
    {
        get => _selectedIndex;
        set { var next = Math.Clamp(value, 0, Math.Max(0, Items.Length - 1)); if (_selectedIndex == next) return; _selectedIndex = next; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
    }
    internal event EventHandler? SelectedIndexChanged;

    public SegmentedControl()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Size = new Size(156, 36);
        Font = UiGeometry.UiFont(9F, FontStyle.Regular);
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Audio source";
        AccessibleDescription = "Choose system audio or a single application.";
    }

    public void ApplyTheme(ThemePalette palette) { _palette = palette; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); SelectedIndex = e.X < Width / 2 ? 0 : 1; base.OnMouseDown(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Left or Keys.Up) { SelectedIndex--; e.Handled = true; }
        if (e.KeyCode is Keys.Right or Keys.Down) { SelectedIndex++; e.Handled = true; }
        if (e.KeyCode is Keys.Space or Keys.Enter) { SelectedIndex = SelectedIndex == 0 ? 1 : 0; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_palette.IsHighContrast) { ControlPaint.DrawBorder(e.Graphics, ClientRectangle, SystemColors.WindowText, ButtonBorderStyle.Solid); }
        else
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var outer = UiGeometry.Rounded(Rectangle.Inflate(ClientRectangle, -1, -1), UiGeometry.Scale(this, 10));
            using var outerBrush = new SolidBrush(_palette.SurfaceHover);
            e.Graphics.FillPath(outerBrush, outer);
            var selected = new Rectangle(_selectedIndex * Width / 2 + 3, 3, Width / 2 - 6, Height - 6);
            using var selectedPath = UiGeometry.Rounded(selected, UiGeometry.Scale(this, 8));
            using var selectedBrush = new SolidBrush(_palette.SurfaceSelected);
            e.Graphics.FillPath(selectedBrush, selectedPath);
        }
        for (var i = 0; i < Items.Length; i++)
        {
            var bounds = new Rectangle(i * Width / 2, 0, Width / 2, Height);
            TextRenderer.DrawText(e.Graphics, Items[i], Font, bounds, _palette.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3), _palette.Focus, Color.Transparent);
    }
}

internal sealed class OwnerDrawnSlider : Control, IThemeAware
{
    private ThemePalette _palette = ThemePalette.Current();
    private int _value = 30;
    private bool _dragging;
    private bool _hovered;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool AccentFill { get; set; } = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool ShowThumbAtRest { get; set; } = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int TrackThickness { get; set; } = 6;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int HorizontalInset { get; set; } = 8;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Dimmed { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int Value { get => _value; set { var next = Math.Clamp(value, 0, 100); if (_value == next) return; _value = next; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); } }
    internal bool IsDragging => _dragging;
    internal event EventHandler? ValueChanged;
    internal event EventHandler? ValueCommitted;

    public OwnerDrawnSlider()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable |
                 ControlStyles.SupportsTransparentBackColor, true);
        TabStop = true;
        Height = 28;
        AccessibleRole = AccessibleRole.Slider;
        AccessibleName = "Speaker volume";
        AccessibleDescription = "Use arrow keys to adjust volume.";
    }

    public void ApplyTheme(ThemePalette palette) { _palette = palette; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); _dragging = true; Capture = true; SetFromX(e.X); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_dragging) SetFromX(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { if (_dragging) { SetFromX(e.X); _dragging = false; Capture = false; Invalidate(); ValueCommitted?.Invoke(this, EventArgs.Empty); } base.OnMouseUp(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var old = Value;
        Value = e.KeyCode switch { Keys.Left or Keys.Down => Value - 2, Keys.Right or Keys.Up => Value + 2, Keys.PageDown => Value - 10, Keys.PageUp => Value + 10, Keys.Home => 0, Keys.End => 100, _ => Value };
        if (old != Value) { e.Handled = true; ValueCommitted?.Invoke(this, EventArgs.Empty); }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_palette.IsHighContrast) { base.OnPaint(e); ControlPaint.DrawBorder(e.Graphics, ClientRectangle, SystemColors.WindowText, ButtonBorderStyle.Solid); return; }
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var pad = UiGeometry.Scale(this, HorizontalInset);
        var trackHeight = UiGeometry.Scale(this, TrackThickness);
        var track = new Rectangle(pad, (Height - trackHeight) / 2, Math.Max(1, Width - pad * 2), trackHeight);
        using var trackPath = UiGeometry.Rounded(track, trackHeight / 2);
        using var trackBrush = new SolidBrush(Dimmed ? _palette.SliderTrackDimmed : _palette.SliderTrack);
        e.Graphics.FillPath(trackBrush, trackPath);
        var fillWidth = Math.Max(trackHeight, (int)(track.Width * Value / 100f));
        var fill = new Rectangle(track.X, track.Y, fillWidth, track.Height);
        using var fillPath = UiGeometry.Rounded(fill, trackHeight / 2);
        using var fillBrush = new SolidBrush(Dimmed ? _palette.SliderFillDimmed : AccentFill ? _palette.Accent : _palette.SecondaryText);
        e.Graphics.FillPath(fillBrush, fillPath);
        var thumbSize = UiGeometry.Scale(this, _dragging ? 14 : _hovered || Focused ? 12 : 10);
        var x = track.X + (int)(track.Width * Value / 100f);
        var thumb = new Rectangle(x - thumbSize / 2, Height / 2 - thumbSize / 2, thumbSize, thumbSize);
        if (ShowThumbAtRest || _hovered || _dragging)
        {
            using var thumbBrush = new SolidBrush(Dimmed ? _palette.ThumbDimmed : _palette.Thumb);
            e.Graphics.FillEllipse(thumbBrush, thumb);
            using var thumbBorder = new Pen(_palette.Border);
            e.Graphics.DrawEllipse(thumbBorder, thumb);
        }
        if (_dragging)
        {
            var label = new Rectangle(Math.Clamp(x - UiGeometry.Scale(this, 20), 0, Width - UiGeometry.Scale(this, 40)), 0, UiGeometry.Scale(this, 40), UiGeometry.Scale(this, 18));
            e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var labelFont = UiGeometry.UiFont(8F);
            using var labelBrush = new SolidBrush(_palette.Text);
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            e.Graphics.DrawString($"{Value}%", labelFont, labelBrush, label, centered);
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2), _palette.Focus, Color.Transparent);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        base.OnPaintBackground(pevent);
    }

    private void SetFromX(int x)
    {
        var pad = UiGeometry.Scale(this, HorizontalInset);
        Value = (int)Math.Round(Math.Clamp((x - pad) / (double)Math.Max(1, Width - pad * 2), 0, 1) * 100);
        AccessibleDescription = $"Volume {Value} percent. Use arrow keys to adjust volume.";
    }
}

internal static class WindowEffects
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    public static void TryEnableRoundedCorners(Form form)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        try { var value = DwmwcpRound; _ = DwmSetWindowAttribute(form.Handle, DwmwaWindowCornerPreference, ref value, sizeof(int)); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
