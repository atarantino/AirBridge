using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace AirBridge.App;

internal sealed class VoiceHudForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private readonly RoundedPanel _surface = new() { Dock = DockStyle.Fill, Radius = 16, Padding = new Padding(14, 9, 10, 9) };
    private readonly Label _mic = new() { Text = "\uE720", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.BottomLeft };
    private readonly Label _hint = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.TopLeft };
    private readonly VoiceLevelMeter _level = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 4) };
    private readonly PillButton _cancel = new() { Text = "×", Quiet = true, Width = 30, Height = 30 };
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _errorTimer = new() { Interval = 2500 };
    private ThemePalette _palette;
    private Func<float>? _levelProvider;
    private Point _dragOrigin;
    private Point _windowOrigin;
    private bool _dragging;
    private float _pulsePhase;

    public VoiceHudForm(ThemePalette palette)
    {
        _palette = palette;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(300, 86);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "AirBridge voice controls";
        AccessibleName = "AirBridge voice controls";

        _mic.Font = UiGeometry.IconFont(19F);
        _mic.AccessibleName = "Microphone";
        _status.Font = UiGeometry.UiFont(9.5F, FontStyle.Bold);
        _hint.Font = UiGeometry.UiFont(8F);
        _cancel.Font = UiGeometry.UiFont(13F);
        _cancel.AccessibleName = "Cancel voice command";

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        var copy = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Margin = Padding.Empty };
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        copy.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        copy.Controls.Add(_status, 0, 0);
        copy.Controls.Add(_level, 0, 1);
        copy.Controls.Add(_hint, 0, 2);
        root.Controls.Add(_mic, 0, 0);
        root.Controls.Add(copy, 1, 0);
        root.Controls.Add(_cancel, 2, 0);
        _surface.Controls.Add(root);
        Controls.Add(_surface);

        _cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        _updateTimer.Tick += (_, _) => UpdateAnimation();
        _errorTimer.Tick += (_, _) => { _errorTimer.Stop(); HideHud(); };
        WireDrag(_surface);
        WireDrag(root);
        WireDrag(copy);
        WireDrag(_mic);
        WireDrag(_status);
        WireDrag(_hint);
        WireDrag(_level);
        Shown += (_, _) => WindowEffects.TryEnableRoundedCorners(this);
        ApplyTheme(palette);
    }

    public event EventHandler? CancelRequested;
    public event EventHandler? PositionCommitted;

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow;
            return parameters;
        }
    }

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        BackColor = palette.Window;
        _surface.ApplyTheme(palette);
        _cancel.ApplyTheme(palette);
        _level.ApplyTheme(palette);
        _status.ForeColor = palette.Text;
        _hint.ForeColor = palette.SecondaryText;
        _mic.ForeColor = palette.Accent;
        Invalidate(true);
    }

    public bool SetInitialPosition(int? x, int? y)
    {
        var requested = x is { } left && y is { } top ? new Rectangle(left, top, Width, Height) : Rectangle.Empty;
        if (!requested.IsEmpty && Screen.AllScreens.Any(screen => screen.WorkingArea.Contains(requested)))
        {
            Location = requested.Location;
            return true;
        }
        var area = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - 24);
        return x is null && y is null;
    }

    public void ShowListening(Func<float> levelProvider, bool holdHint)
    {
        _levelProvider = levelProvider;
        _status.Text = "Listening";
        _hint.Text = holdHint ? "release to send · Esc to cancel" : "tap shortcut to send · Esc to cancel";
        _level.Visible = true;
        _cancel.Visible = true;
        _errorTimer.Stop();
        ShowHud();
    }

    public void SetToggleHint() => _hint.Text = "tap shortcut to send · Esc to cancel";

    public void ShowTranscribing()
    {
        _levelProvider = null;
        _level.Level = 0;
        _status.Text = "Transcribing";
        _hint.Text = "Turning speech into text…";
        _level.Visible = true;
        _cancel.Visible = true;
        _errorTimer.Stop();
        ShowHud();
    }

    public void ShowError(string message)
    {
        _levelProvider = null;
        _status.Text = "Voice command failed";
        _hint.Text = message;
        _level.Visible = false;
        _cancel.Visible = false;
        _mic.ForeColor = _palette.Error;
        ShowHud();
        _updateTimer.Stop();
        _errorTimer.Stop();
        _errorTimer.Start();
    }

    public void HideHud()
    {
        _updateTimer.Stop();
        _errorTimer.Stop();
        Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _updateTimer.Dispose();
            _errorTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ShowHud()
    {
        if (!Visible) Show();
        _updateTimer.Start();
    }

    private void UpdateAnimation()
    {
        _level.Level = Math.Clamp(_levelProvider?.Invoke() ?? 0, 0, 1);
        _pulsePhase += 0.18f;
        var amount = (MathF.Sin(_pulsePhase) + 1) / 2;
        _mic.ForeColor = Blend(_palette.Accent, _palette.Text, amount * 0.28f);
    }

    private void WireDrag(Control control)
    {
        control.MouseDown += (_, args) =>
        {
            if (args.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragOrigin = Cursor.Position;
            _windowOrigin = Location;
        };
        control.MouseMove += (_, _) =>
        {
            if (!_dragging || Control.MouseButtons != MouseButtons.Left) return;
            var current = Cursor.Position;
            Location = new Point(_windowOrigin.X + current.X - _dragOrigin.X, _windowOrigin.Y + current.Y - _dragOrigin.Y);
        };
        control.MouseUp += (_, args) =>
        {
            if (args.Button != MouseButtons.Left || !_dragging) return;
            _dragging = false;
            PositionCommitted?.Invoke(this, EventArgs.Empty);
        };
    }

    private static Color Blend(Color first, Color second, float amount) => Color.FromArgb(
        (int)(first.R + (second.R - first.R) * amount),
        (int)(first.G + (second.G - first.G) * amount),
        (int)(first.B + (second.B - first.B) * amount));
}

internal sealed class VoiceLevelMeter : Control, IThemeAware
{
    private ThemePalette _palette = ThemePalette.Current();
    private float _level;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal float Level
    {
        get => _level;
        set { _level = value; Invalidate(); }
    }

    public VoiceLevelMeter() => SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

    public void ApplyTheme(ThemePalette palette) { _palette = palette; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = Rectangle.Inflate(ClientRectangle, 0, -2);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var trackPath = UiGeometry.Rounded(bounds, bounds.Height / 2);
        using var track = new SolidBrush(_palette.SliderTrack);
        e.Graphics.FillPath(track, trackPath);
        var fillBounds = new Rectangle(bounds.X, bounds.Y, Math.Max(bounds.Height, (int)(bounds.Width * MathF.Sqrt(Level))), bounds.Height);
        using var fillPath = UiGeometry.Rounded(fillBounds, fillBounds.Height / 2);
        using var fill = new SolidBrush(_palette.Accent);
        e.Graphics.FillPath(fill, fillPath);
    }
}
