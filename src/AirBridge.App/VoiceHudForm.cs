using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace AirBridge.App;

internal sealed class VoiceHudForm : Form
{
    private const int CornerRadius = 14;
    private const int WsExNoActivate = 0x08000000;
    private const int CsDropShadow = 0x00020000;
    private const int WmSettingChange = 0x001A;
    private readonly RoundedPanel _surface = new() { Dock = DockStyle.Fill, Radius = WindowEffects.UseRoundedPopupCorners ? CornerRadius : 0, ClipToRoundedRegion = false, Padding = new Padding(14, 10, 10, 10) };
    private readonly VoiceMicIndicator _mic = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 8, 4) };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.BottomLeft };
    private readonly Label _hint = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.TopLeft };
    private readonly VoiceLevelMeter _level = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 3) };
    private readonly PillButton _cancel = new() { Text = string.Empty, IconGlyph = "\uE711", Quiet = true, TransparentQuiet = true, Width = 34, Height = 34, Margin = new Padding(2, 0, 0, 0) };
    private readonly PillButton _deny = new() { Text = "Don’t allow", Quiet = true, Width = 112, Height = 34, Margin = new Padding(0, 0, 8, 0) };
    private readonly PillButton _approve = new() { Text = "Approve", Primary = true, Width = 96, Height = 34, Margin = Padding.Empty };
    private readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Visible = false, Margin = Padding.Empty };
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _errorTimer = new() { Interval = 2500 };
    private readonly System.Windows.Forms.Timer _responseTimer = new();
    private ThemePalette _palette;
    private Func<float>? _levelProvider;
    private Point _dragOrigin;
    private Point _windowOrigin;
    private bool _dragging;
    private float _pulsePhase;
    private TaskCompletionSource<bool>? _confirmation;
    private CancellationTokenRegistration _confirmationCancellation;

    public VoiceHudForm(ThemePalette palette)
    {
        _palette = palette;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(360, 92);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "AirBridge voice controls";
        AccessibleName = "AirBridge voice controls";

        _mic.AccessibleName = "Microphone";
        _status.Font = UiGeometry.UiFont(10F, FontStyle.Bold);
        _hint.Font = UiGeometry.UiFont(8.5F);
        _cancel.AccessibleName = "Cancel voice command";
        _cancel.AccessibleDescription = "Discard the current recording or transcription.";
        _deny.AccessibleName = "Don’t allow action";
        _approve.AccessibleName = "Approve action";

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        var copy = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, Margin = Padding.Empty };
        copy.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        copy.RowStyles.Add(new RowStyle(SizeType.Absolute, 11));
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        copy.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        copy.Controls.Add(_status, 0, 0);
        copy.Controls.Add(_level, 0, 1);
        copy.Controls.Add(_hint, 0, 2);
        _actions.Controls.Add(_approve);
        _actions.Controls.Add(_deny);
        copy.Controls.Add(_actions, 0, 3);
        root.Controls.Add(_mic, 0, 0);
        root.Controls.Add(copy, 1, 0);
        root.Controls.Add(_cancel, 2, 0);
        _surface.Controls.Add(root);
        Controls.Add(_surface);
        SystemTextScale.Changed += OnTextScaleChanged;
        UiGeometry.ScaleInitialTextLayout(this);

        _cancel.Click += (_, _) =>
        {
            if (_confirmation is not null) CompleteConfirmation(false);
            else CancelRequested?.Invoke(this, EventArgs.Empty);
        };
        _deny.Click += (_, _) => CompleteConfirmation(false);
        _approve.Click += (_, _) => CompleteConfirmation(true);
        _updateTimer.Tick += (_, _) => UpdateAnimation();
        _errorTimer.Tick += (_, _) => { _errorTimer.Stop(); HideHud(); };
        _responseTimer.Tick += (_, _) => { _responseTimer.Stop(); HideHud(); };
        WireDrag(_surface);
        WireDrag(root);
        WireDrag(copy);
        WireDrag(_mic);
        WireDrag(_status);
        WireDrag(_hint);
        WireDrag(_level);
        Shown += (_, _) =>
        {
            WindowEffects.ApplyTheme(this, _palette);
            WindowEffects.ConfigureBorderlessPopup(this);
        };
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
            parameters.ExStyle |= WsExNoActivate;
            // CS_DROPSHADOW follows the binary Win32 region on Windows 10 and
            // exposes a bright, stair-stepped fringe around rounded corners.
            parameters.ClassStyle &= ~CsDropShadow;
            return parameters;
        }
    }

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        BackColor = palette.Window;
        _surface.ApplyTheme(palette);
        _cancel.ApplyTheme(palette);
        _deny.ApplyTheme(palette);
        _approve.ApplyTheme(palette);
        _level.ApplyTheme(palette);
        _mic.ApplyTheme(palette);
        _status.ForeColor = palette.Text;
        _hint.ForeColor = palette.SecondaryText;
        UpdateWindowRegion();
        if (IsHandleCreated)
        {
            WindowEffects.ApplyTheme(this, palette);
            WindowEffects.ConfigureBorderlessPopup(this);
        }
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
        SetCompactLayout();
        _responseTimer.Stop();
        _levelProvider = levelProvider;
        _status.Text = "Listening";
        _hint.Text = holdHint ? "release to send · Esc to cancel" : "tap shortcut to send · Esc to cancel";
        _mic.IsError = false;
        _mic.IsWarning = false;
        _mic.IsResponse = false;
        _level.Indeterminate = false;
        _level.Visible = true;
        _cancel.Visible = true;
        _errorTimer.Stop();
        ShowHud();
    }

    public void SetToggleHint() => _hint.Text = "tap shortcut to send · Esc to cancel";

    public void ShowTranscribing()
    {
        SetCompactLayout();
        _responseTimer.Stop();
        _levelProvider = null;
        _level.Level = 0;
        _status.Text = "Transcribing";
        _hint.Text = "Turning speech into text…";
        _mic.IsError = false;
        _mic.IsWarning = false;
        _mic.IsResponse = false;
        _level.Indeterminate = true;
        _level.Visible = true;
        _cancel.Visible = true;
        _errorTimer.Stop();
        ShowHud();
    }

    public void ShowThinking()
    {
        SetCompactLayout();
        _responseTimer.Stop();
        _levelProvider = null;
        _level.Level = 0;
        _status.Text = "Thinking";
        _hint.Text = "Working out what to do…";
        _mic.IsError = false;
        _mic.IsWarning = false;
        _mic.IsResponse = false;
        _level.Indeterminate = true;
        _level.Visible = true;
        _cancel.Visible = true;
        _errorTimer.Stop();
        ShowHud();
    }

    public void ShowAssistantResponse(string message)
    {
        var displayMessage = PlainTextResponse(message);
        SetResponseLayout(displayMessage);
        _levelProvider = null;
        _status.Text = "AirBridge";
        _hint.Text = displayMessage;
        _hint.AccessibleName = $"AirBridge response: {displayMessage}";
        _mic.IsError = false;
        _mic.IsWarning = false;
        _mic.IsResponse = true;
        _mic.Pulse = 0;
        _level.Indeterminate = false;
        _level.Visible = false;
        _cancel.Visible = false;
        _errorTimer.Stop();
        _responseTimer.Stop();
        _responseTimer.Interval = Math.Clamp(3000 + message.Length * 35, 4000, 10000);
        ShowHud();
        _updateTimer.Stop();
        _responseTimer.Start();
    }

    public void ShowNoSpeech()
    {
        const string message = "Check that your microphone isn’t muted, then try again.";
        SetResponseLayout(message);
        _levelProvider = null;
        _status.Text = "Nothing heard";
        _hint.Text = message;
        _hint.AccessibleName = $"No speech detected. {message}";
        _mic.IsError = false;
        _mic.IsWarning = true;
        _mic.IsResponse = false;
        _mic.Pulse = 0;
        _level.Indeterminate = false;
        _level.Visible = false;
        _cancel.Visible = false;
        _errorTimer.Stop();
        _responseTimer.Stop();
        _responseTimer.Interval = 5000;
        ShowHud();
        _updateTimer.Stop();
        _responseTimer.Start();
    }

    public Task<bool> ShowConfirmation(string title, string message, CancellationToken cancellationToken, bool useMicrophoneIcon = true)
    {
        CompleteConfirmation(false, showThinking: false);
        SetConfirmationLayout();
        _responseTimer.Stop();
        _levelProvider = null;
        _status.Text = title;
        _hint.Text = message;
        _hint.AccessibleName = message;
        _mic.IsError = false;
        _mic.IsWarning = false;
        _mic.IsResponse = !useMicrophoneIcon;
        _mic.Pulse = 0;
        _cancel.Visible = true;
        _cancel.AccessibleName = "Don’t allow action";
        _cancel.AccessibleDescription = "Close this prompt without approving the requested action.";
        _confirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirmationCancellation = cancellationToken.Register(() =>
        {
            if (IsDisposed)
            {
                var completion = _confirmation;
                _confirmation = null;
                completion?.TrySetResult(false);
                return;
            }
            try { BeginInvoke(() => CompleteConfirmation(false, showThinking: false)); }
            catch (InvalidOperationException)
            {
                var completion = _confirmation;
                _confirmation = null;
                completion?.TrySetResult(false);
            }
        });
        ShowHud();
        return _confirmation.Task;
    }

    public void ShowError(string message)
    {
        SetCompactLayout();
        _responseTimer.Stop();
        _levelProvider = null;
        _status.Text = "Voice command failed";
        _hint.Text = message;
        _level.Indeterminate = false;
        _level.Visible = false;
        _cancel.Visible = false;
        _mic.IsError = true;
        _mic.IsWarning = false;
        _mic.IsResponse = false;
        _mic.Pulse = 0;
        ShowHud();
        _updateTimer.Stop();
        _errorTimer.Stop();
        _errorTimer.Start();
    }

    public void HideHud()
    {
        CompleteConfirmation(false, showThinking: false);
        _updateTimer.Stop();
        _errorTimer.Stop();
        _responseTimer.Stop();
        Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CompleteConfirmation(false, showThinking: false);
            SystemTextScale.Changed -= OnTextScaleChanged;
            _updateTimer.Dispose();
            _errorTimer.Dispose();
            _responseTimer.Dispose();
            _confirmationCancellation.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == WmSettingChange && IsHandleCreated && !IsDisposed)
            BeginInvoke(SystemTextScale.Refresh);
    }

    private void OnTextScaleChanged(object? sender, TextScaleChangedEventArgs args)
    {
        var ratio = args.Current / args.Previous;
        UiGeometry.RescaleText(this, args.Previous, args.Current);
        ClientSize = new(Math.Max(1, (int)Math.Ceiling(ClientSize.Width * ratio)), Math.Max(1, (int)Math.Ceiling(ClientSize.Height * ratio)));
        UpdateWindowRegion();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
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
        _mic.Pulse = amount;
        _level.AnimationPhase = _pulsePhase;
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

    private void UpdateWindowRegion()
    {
        // Windows 10's binary rounded regions produce visibly jagged outer
        // edges. Keep the HUD rectangular there and let DWM round Windows 11.
        var previousRegion = Region;
        Region = null;
        previousRegion?.Dispose();
    }

    private void SetCompactLayout()
    {
        _actions.Visible = false;
        _level.Visible = true;
        _hint.AutoEllipsis = true;
        _hint.AccessibleName = null;
        _cancel.AccessibleName = "Cancel voice command";
        _cancel.AccessibleDescription = "Discard the current recording or transcription.";
        if (_actions.Parent is TableLayoutPanel copy)
        {
            copy.RowStyles[1].Height = 11;
            copy.RowStyles[3].Height = 0;
        }
        ResizeHud(new Size(360, 92));
    }

    private void SetConfirmationLayout()
    {
        _actions.Visible = true;
        _level.Visible = false;
        _hint.AutoEllipsis = false;
        if (_actions.Parent is TableLayoutPanel copy)
        {
            copy.RowStyles[1].Height = 0;
            copy.RowStyles[3].Height = 42;
        }
        ResizeHud(new Size(480, 164));
    }

    private void SetResponseLayout(string message)
    {
        _actions.Visible = false;
        _level.Visible = false;
        _hint.AutoEllipsis = true;
        _hint.AccessibleName = null;
        if (_actions.Parent is TableLayoutPanel copy)
        {
            copy.RowStyles[1].Height = 0;
            copy.RowStyles[3].Height = 0;
        }
        var estimatedLines = message.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / 52d)));
        var height = Math.Clamp(60 + estimatedLines * 18, 96, 164);
        ResizeHud(new Size(420, height));
    }

    internal static string PlainTextResponse(string message)
    {
        var plain = message.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Trim();
        return string.Join(Environment.NewLine, plain.Split('\n').Select(line =>
            line.TrimStart().StartsWith("- ", StringComparison.Ordinal)
                ? $"• {line.TrimStart()[2..]}"
                : line.TrimEnd()));
    }

    private void ResizeHud(Size logicalSize)
    {
        var scale = DeviceDpi / 96f;
        var next = new Size((int)Math.Ceiling(logicalSize.Width * scale), (int)Math.Ceiling(logicalSize.Height * scale));
        if (ClientSize == next) return;
        var bottom = Bottom;
        ClientSize = next;
        Top = bottom - Height;
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    private void CompleteConfirmation(bool approved, bool showThinking = true)
    {
        var completion = _confirmation;
        if (completion is null) return;
        _confirmation = null;
        _confirmationCancellation.Dispose();
        if (showThinking) ShowThinking();
        completion.TrySetResult(approved);
    }
}

internal sealed class VoiceMicIndicator : Control, IThemeAware
{
    private ThemePalette _palette = ThemePalette.Current();
    private float _pulse;
    private bool _isError;
    private bool _isWarning;
    private bool _isResponse;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal float Pulse { get => _pulse; set { _pulse = Math.Clamp(value, 0, 1); Invalidate(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IsError { get => _isError; set { _isError = value; Invalidate(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IsWarning { get => _isWarning; set { _isWarning = value; Invalidate(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IsResponse { get => _isResponse; set { _isResponse = value; Invalidate(); } }

    public VoiceMicIndicator()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    public void ApplyTheme(ThemePalette palette) { _palette = palette; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var size = Math.Min(Width, Height) - 2;
        if (size <= 0) return;
        var bounds = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
        var color = IsError ? _palette.Error : IsWarning ? _palette.Warning : _palette.Accent;
        using var halo = new SolidBrush(Color.FromArgb(IsError ? 28 : 24 + (int)(Pulse * 20), color));
        e.Graphics.FillEllipse(halo, bounds);
        var ring = Rectangle.Inflate(bounds, -1, -1);
        using var ringPen = new Pen(Color.FromArgb(IsError ? 95 : 80 + (int)(Pulse * 60), color), 1.2f);
        e.Graphics.DrawEllipse(ringPen, ring);
        using var iconFont = UiGeometry.IconFont(17F);
        using var iconPath = new GraphicsPath();
        using var format = new StringFormat(StringFormat.GenericTypographic);
        var emSize = iconFont.SizeInPoints * e.Graphics.DpiY / 72f;
        iconPath.AddString(IsResponse ? "\uE8BD" : "\uE720", iconFont.FontFamily, (int)iconFont.Style, emSize, PointF.Empty, format);
        var glyphBounds = iconPath.GetBounds();
        using var transform = new Matrix();
        transform.Translate(
            bounds.Left + (bounds.Width - glyphBounds.Width) / 2f - glyphBounds.Left,
            bounds.Top + (bounds.Height - glyphBounds.Height) / 2f - glyphBounds.Top);
        iconPath.Transform(transform);
        using var iconBrush = new SolidBrush(color);
        e.Graphics.FillPath(iconBrush, iconPath);
    }
}

internal sealed class VoiceLevelMeter : Control, IThemeAware
{
    private ThemePalette _palette = ThemePalette.Current();
    private float _level;
    private bool _indeterminate;
    private float _animationPhase;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal float Level
    {
        get => _level;
        set { _level = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Indeterminate { get => _indeterminate; set { _indeterminate = value; Invalidate(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal float AnimationPhase { get => _animationPhase; set { _animationPhase = value; Invalidate(); } }

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
        var fillBounds = Indeterminate
            ? new Rectangle(bounds.X + (int)((bounds.Width - bounds.Width * 0.3f) * ((MathF.Sin(AnimationPhase) + 1) / 2)), bounds.Y, Math.Max(bounds.Height, (int)(bounds.Width * 0.3f)), bounds.Height)
            : new Rectangle(bounds.X, bounds.Y, Math.Max(bounds.Height, (int)(bounds.Width * MathF.Sqrt(Level))), bounds.Height);
        using var fillPath = UiGeometry.Rounded(fillBounds, fillBounds.Height / 2);
        using var fill = new SolidBrush(_palette.Accent);
        e.Graphics.FillPath(fill, fillPath);
    }
}
