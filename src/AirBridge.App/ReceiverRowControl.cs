using System.Drawing.Drawing2D;
using AirBridge.Core;

namespace AirBridge.App;

public sealed class ReceiverSelectionChangedEventArgs(string receiverId, bool selected) : EventArgs
{
    public string ReceiverId { get; } = receiverId;
    public bool Selected { get; } = selected;
}

public sealed class ReceiverVolumeChangedEventArgs(string receiverId, int volume) : EventArgs
{
    public string ReceiverId { get; } = receiverId;
    public int Volume { get; } = volume;
}

public sealed class ReceiverAlignmentTrimChangedEventArgs(string receiverId, int milliseconds) : EventArgs
{
    public string ReceiverId { get; } = receiverId;
    public int Milliseconds { get; } = milliseconds;
}

public sealed class ReceiverActionEventArgs(string receiverId) : EventArgs
{
    public string ReceiverId { get; } = receiverId;
}

/// <summary>Owner-drawn, keyboard-accessible AirPlay-inspired receiver row.</summary>
public sealed class ReceiverRowControl : UserControl
{
    private readonly OwnerDrawnSlider _volume = new() { Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly PillButton _action = new() { Anchor = AnchorStyles.Top | AnchorStyles.Right, Width = 76, Height = 32 };
    private readonly PillButton _trimDown = new() { Text = "-", Quiet = true, Width = 26, Height = 24 };
    private readonly PillButton _trimUp = new() { Text = "+", Quiet = true, Width = 26, Height = 24 };
    private readonly Label _trimValue = new() { AutoSize = false, Text = "Sync 0 ms", TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent };
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _equalizerTimer = new() { Interval = 90 };
    private readonly System.Windows.Forms.Timer _heightTimer = new() { Interval = 15 };
    private ThemePalette _palette = ThemePalette.Current();
    private ReceiverInfo? _receiver;
    private StreamState _streamState;
    private string? _detail;
    private bool _selected;
    private bool _updating;
    private bool _compact;
    private bool _compactStreamActive;
    private bool _dashboardStreamActive;
    private bool _hovered;
    private float _hoverAlpha;
    private int _equalizerFrame;
    private int _heightFrame;
    private int _heightStart;
    private int _heightTarget;
    private int _alignmentTrimMilliseconds;

    public ReceiverRowControl()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable |
                 ControlStyles.SupportsTransparentBackColor, true);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = false;
        Height = 72;
        MinimumSize = new Size(350, 64);
        Margin = new Padding(0, 0, 0, 8);
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        AccessibleName = "AirPlay speaker";
        AccessibleDescription = "Press Space or Enter to include this speaker.";
        Cursor = Cursors.Hand;

        _volume.TabIndex = 0;
        _trimDown.TabIndex = 1;
        _trimUp.TabIndex = 2;
        _action.TabIndex = 3;
        _action.AccessibleName = "Connect speaker";
        _trimDown.AccessibleName = "Decrease speaker alignment trim by 10 milliseconds";
        _trimDown.AccessibleDescription = "Decreases this speaker's additional delay by 10 milliseconds.";
        _trimUp.AccessibleName = "Increase speaker alignment trim by 10 milliseconds";
        _trimUp.AccessibleDescription = "Increases this speaker's additional delay by 10 milliseconds.";
        _trimValue.AccessibleName = "Speaker alignment trim";
        _trimValue.Font = UiGeometry.UiFont(8F, FontStyle.Bold);
        _trimDown.TransparentQuiet = true;
        _trimUp.TransparentQuiet = true;
        _action.TransparentQuiet = true;
        _action.BackColor = Color.Transparent;
        _trimDown.BackColor = Color.Transparent;
        _trimUp.BackColor = Color.Transparent;
        Controls.AddRange([_volume, _trimDown, _trimValue, _trimUp, _action]);

        _volume.ValueCommitted += (_, _) => CommitVolume();
        _trimDown.Click += (_, _) => AdjustAlignmentTrim(-10);
        _trimUp.Click += (_, _) => AdjustAlignmentTrim(10);
        _action.Click += (_, _) =>
        {
            if (_receiver is null) return;
            if (IsActive) DisconnectRequested?.Invoke(this, new(_receiver.Id));
            else ConnectRequested?.Invoke(this, new(_receiver.Id));
        };
        _hoverTimer.Tick += (_, _) => AnimateHover();
        _equalizerTimer.Tick += (_, _) => { _equalizerFrame = (_equalizerFrame + 1) % 6; Invalidate(); };
        _heightTimer.Tick += (_, _) => AnimateHeight();
        ApplyTheme(_palette);
    }

    /// <summary>Uses denser proportions for the narrow tray flyout without changing dashboard rows.</summary>
    public void UseCompactLayout()
    {
        _compact = true;
        MinimumSize = new Size(350, 44);
        Height = UiGeometry.ScaleText(this, 44);
        Margin = new Padding(0, 0, 0, UiGeometry.Scale(this, 6));
        _action.Visible = false;
        _action.TabStop = false;
        _volume.ShowThumbAtRest = false;
        _volume.TrackThickness = 5;
        _volume.HorizontalInset = 0;
        PerformLayout();
        UpdateAccessibility();
        Invalidate();
    }

    internal void ApplyTextScale()
    {
        _trimValue.Font = UiGeometry.UiFont(8F, FontStyle.Bold);
        if (_compact) UpdateCompactHeight(false);
        else
        {
            Height = UiGeometry.ScaleText(this, 72);
            MinimumSize = new Size(MinimumSize.Width, UiGeometry.ScaleText(this, 64));
        }
        PerformLayout();
        Invalidate(true);
    }

    internal void SetCompactStreamActive(bool active)
    {
        if (_compactStreamActive == active) return;
        _compactStreamActive = active;
        UpdateAccessibility();
        Invalidate();
    }

    internal void SetDashboardStreamActive(bool active)
    {
        if (_dashboardStreamActive == active) return;
        _dashboardStreamActive = active;
        UpdateDashboardPresentation();
    }

    public event EventHandler<ReceiverSelectionChangedEventArgs>? SelectionChanged;
    public event EventHandler<ReceiverVolumeChangedEventArgs>? VolumeCommitted;
    public event EventHandler<ReceiverAlignmentTrimChangedEventArgs>? AlignmentTrimChanged;
    public event EventHandler<ReceiverActionEventArgs>? ConnectRequested;
    public event EventHandler<ReceiverActionEventArgs>? DisconnectRequested;

    public string? ReceiverId => _receiver?.Id;
    public bool IsSelected => _selected;
    public bool IsActive => _streamState is not StreamState.Idle and not StreamState.Failed;
    public int Volume => _volume.Value;
    public int AlignmentTrimMilliseconds => _alignmentTrimMilliseconds;
    internal bool IsPendingVisual => IsPendingState(_streamState);

    public void Bind(ReceiverInfo receiver, bool selected = false, int volume = 30, StreamState state = StreamState.Idle, string? detail = null, int alignmentTrimMilliseconds = 0)
    {
        _receiver = receiver;
        AccessibleName = $"{receiver.Name} AirPlay speaker";
        _volume.AccessibleName = $"{receiver.Name} volume";
        _trimDown.AccessibleName = $"Decrease {receiver.Name} alignment trim by 10 milliseconds";
        _trimUp.AccessibleName = $"Increase {receiver.Name} alignment trim by 10 milliseconds";
        _trimValue.AccessibleName = $"{receiver.Name} alignment trim";
        _action.AccessibleName = $"Connect {receiver.Name}";
        _updating = true;
        _selected = selected;
        _volume.Value = volume;
        SetAlignmentTrimCore(alignmentTrimMilliseconds);
        _updating = false;
        SetPlaybackState(state, detail);
    }

    public void SetPlaybackState(StreamState state, string? detail = null)
    {
        _streamState = state;
        _detail = detail;
        UpdateDashboardPresentation();
        _volume.AccentFill = _selected || state == StreamState.Streaming;
        _volume.Dimmed = !_selected && !IsActive;
        _volume.Invalidate();
        _equalizerTimer.Enabled = IsAnimatedState(state) && Visible;
        UpdateAccessibility();
        UpdateCompactHeight(Visible && IsHandleCreated);
        PerformLayout();
        Invalidate();
    }

    public void SetSelected(bool selected)
    {
        _updating = true;
        _selected = selected;
        _updating = false;
        _volume.AccentFill = selected || _streamState == StreamState.Streaming;
        _volume.Dimmed = !selected && !IsActive;
        _volume.Invalidate();
        UpdateAccessibility();
        UpdateCompactHeight(Visible && IsHandleCreated);
        Invalidate();
    }

    public void SetVolume(int volume)
    {
        if (_volume.IsDragging) return;
        _updating = true;
        _volume.Value = volume;
        _updating = false;
    }

    public void SetAlignmentTrim(int milliseconds) => SetAlignmentTrimCore(milliseconds);

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        BackColor = palette.Surface;
        ForeColor = palette.Text;
        _volume.ApplyTheme(palette);
        _volume.BackColor = Color.Transparent;
        _trimDown.ApplyTheme(palette);
        _trimUp.ApplyTheme(palette);
        _trimDown.BackColor = Color.Transparent;
        _trimUp.BackColor = Color.Transparent;
        _trimValue.ForeColor = palette.SecondaryText;
        _action.ApplyTheme(palette);
        _action.BackColor = Color.Transparent;
        Invalidate(true);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var scale = DeviceDpi / 96f;
        var verticalScale = scale * SystemTextScale.Current;
        var right = (int)(12 * scale);
        if (_compact)
        {
            _volume.Visible = CompactEngaged;
            _trimDown.Visible = false;
            _trimValue.Visible = false;
            _trimUp.Visible = false;
            if (!CompactEngaged) return;
            var nameLeft = (int)(52 * scale);
            var trailingRight = Width - right;
            var showStatus = _streamState is not StreamState.Idle and not StreamState.Streaming;
            var statusWidth = showStatus ? (int)(112 * scale) : 0;
            var sliderLeft = nameLeft + statusWidth;
            _volume.SetBounds(sliderLeft, (int)(34 * verticalScale), Math.Max((int)(80 * scale), trailingRight - sliderLeft), (int)(23 * scale));
            return;
        }
        var showTrim = IsActive;
        _trimDown.Visible = _trimValue.Visible = _trimUp.Visible = showTrim;
        _action.Visible = _dashboardStreamActive;
        var actionWidth = (int)(76 * scale);
        var actionRight = Width - right;
        _action.SetBounds(actionRight - actionWidth, (Height - (int)(32 * verticalScale)) / 2, actionWidth, (int)(32 * verticalScale));
        var sliderWidth = Math.Max((int)(120 * scale), Math.Min((int)(178 * scale), Width / 3));
        var sliderRight = (_action.Visible ? _action.Left : actionRight) - (int)(10 * scale);
        _volume.SetBounds(sliderRight - sliderWidth, (int)(8 * verticalScale), sliderWidth, (int)(26 * scale));
        if (showTrim) LayoutTrimControls(sliderRight, (int)(39 * verticalScale), scale, verticalScale);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; _hoverTimer.Start(); PerformLayout(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e)
    {
        if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) { _hovered = false; _hoverTimer.Start(); PerformLayout(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && !_volume.Bounds.Contains(e.Location) && (!_action.Visible || !_action.Bounds.Contains(e.Location))) ActivateRow();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { ActivateRow(); e.Handled = true; e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }

    protected override void OnEnter(EventArgs e) { base.OnEnter(e); PerformLayout(); Invalidate(); }
    protected override void OnLeave(EventArgs e) { base.OnLeave(e); PerformLayout(); Invalidate(); }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _equalizerTimer.Enabled = Visible && IsAnimatedState(_streamState);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_palette.IsHighContrast) { e.Graphics.Clear(SystemColors.Window); return; }
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? _palette.Surface);
        var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
        using var path = UiGeometry.Rounded(bounds, UiGeometry.Scale(this, 11));
        var baseSurface = _selected ? _palette.SurfaceSelected : _palette.Surface;
        var hovered = Blend(baseSurface, _palette.SurfaceHover, _hoverAlpha);
        using var brush = new SolidBrush(hovered);
        using var border = new Pen(_selected ? _palette.Accent : _palette.Border);
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(border, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_compact)
        {
            PaintCompact(e.Graphics);
            return;
        }
        var s = DeviceDpi / 96f;
        var v = s * SystemTextScale.Current;
        var glyphBox = new Rectangle((int)(14 * s), (Height - (int)(34 * s)) / 2, (int)(34 * s), (int)(34 * s));
        using var iconFont = UiGeometry.IconFont(_compact ? 15F : 17F);
        TextRenderer.DrawText(e.Graphics, "\uE767", iconFont, glyphBox, _palette.IsHighContrast ? SystemColors.WindowText : _palette.SecondaryText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var textLeft = glyphBox.Right + (int)(10 * s);
        var nameRight = _compact ? _action.Left - (int)(34 * s) : _volume.Left - (int)(34 * s);
        var statusRight = _compact ? _volume.Left - (int)(8 * s) : nameRight;
        var nameBounds = new Rectangle(textLeft, _compact ? (int)(7 * v) : (int)(12 * v), Math.Max(40, nameRight - textLeft), (int)(23 * v));
        var statusBounds = new Rectangle(textLeft + (int)(13 * s), nameBounds.Bottom + (_compact ? 0 : (int)(2 * v)), Math.Max(30, statusRight - textLeft - (int)(13 * s)), (int)(20 * v));
        using var nameFont = UiGeometry.UiFont(_compact ? 9.5F : 10F, FontStyle.Bold);
        using var secondaryFont = UiGeometry.UiFont(_compact ? 8.25F : 9F);
        TextRenderer.DrawText(e.Graphics, _receiver?.Name ?? "Speaker", nameFont, nameBounds, _palette.IsHighContrast ? SystemColors.WindowText : _palette.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var dotSize = UiGeometry.Scale(this, 6);
        var dot = new Rectangle(textLeft, statusBounds.Top + (statusBounds.Height - dotSize) / 2, dotSize, dotSize);
        using var stateBrush = new SolidBrush(_palette.IsHighContrast ? SystemColors.WindowText : _palette.StateColor(_streamState));
        e.Graphics.FillEllipse(stateBrush, dot);
        var detail = _compact || string.IsNullOrWhiteSpace(_detail) ? StatusText(_streamState) : $"{StatusText(_streamState)} · {_detail}";
        TextRenderer.DrawText(e.Graphics, detail, secondaryFont, statusBounds, _palette.IsHighContrast ? SystemColors.WindowText : _palette.SecondaryText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        if (_streamState == StreamState.Streaming) DrawEqualizer(e.Graphics, Math.Min(nameRight - (int)(24 * s), nameBounds.Left + TextRenderer.MeasureText(_receiver?.Name ?? "", nameFont).Width + (int)(7 * s)), nameBounds.Top + (int)(5 * s));
        DrawSelection(e.Graphics);
        if (Focused && ShowFocusCues)
        {
            var focus = Rectangle.Inflate(ClientRectangle, -3, -3);
            using var pen = new Pen(_palette.IsHighContrast ? SystemColors.Highlight : _palette.Focus, UiGeometry.Scale(this, 2));
            using var focusPath = UiGeometry.Rounded(focus, UiGeometry.Scale(this, 9));
            e.Graphics.DrawPath(pen, focusPath);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _hoverTimer.Dispose(); _equalizerTimer.Dispose(); _heightTimer.Dispose(); }
        base.Dispose(disposing);
    }

    private void PaintCompact(Graphics graphics)
    {
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var s = DeviceDpi / 96f;
        var v = s * SystemTextScale.Current;
        var left = (int)(12 * s);
        var glyphSize = (int)(30 * s);
        var glyphTop = CompactEngaged ? (int)(5 * v) : (Height - glyphSize) / 2;
        var glyphBox = new Rectangle(left, glyphTop, glyphSize, glyphSize);
        var nameLeft = glyphBox.Right + (int)(10 * s);
        var checkSize = (int)(22 * s);
        var checkTop = CompactEngaged ? (int)(8 * v) : (Height - checkSize) / 2;
        var checkBox = new Rectangle(Width - (int)(12 * s) - checkSize, checkTop, checkSize, checkSize);
        var nameBounds = new Rectangle(nameLeft, CompactEngaged ? (int)(5 * v) : 0, Math.Max(40, checkBox.Left - (int)(8 * s) - nameLeft), CompactEngaged ? (int)(25 * v) : Height);

        using var iconFont = UiGeometry.IconFont(14F);
        using var nameFont = UiGeometry.UiFont(9.5F, FontStyle.Bold);
        using var statusFont = UiGeometry.UiFont(8.25F);
        var tileActive = _streamState == StreamState.Streaming;
        using var tilePath = UiGeometry.Rounded(glyphBox, UiGeometry.Scale(this, 8));
        using var tileBrush = new SolidBrush(tileActive ? _palette.Accent : _palette.SurfaceHover);
        graphics.FillPath(tileBrush, tilePath);
        var iconColor = _palette.IsHighContrast ? SystemColors.WindowText : tileActive ? _palette.OnAccent : _palette.SecondaryText;
        using var centered = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var iconBrush = new SolidBrush(iconColor);
        graphics.DrawString(DeviceGlyph(), iconFont, iconBrush, glyphBox, centered);
        using var nameBrush = new SolidBrush(_palette.IsHighContrast ? SystemColors.WindowText : _palette.Text);
        using var nameFormat = new StringFormat(StringFormat.GenericTypographic)
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        graphics.DrawString(_receiver?.Name ?? "Speaker", nameFont, nameBrush, nameBounds, nameFormat);

        if (_streamState == StreamState.Streaming)
        {
            var measured = graphics.MeasureString(_receiver?.Name ?? string.Empty, nameFont, PointF.Empty, StringFormat.GenericTypographic).Width;
            DrawEqualizer(graphics, (int)Math.Min(checkBox.Left - (int)(19 * s), nameLeft + measured + (int)(6 * s)), (int)(11 * v));
        }

        if (_streamState is not StreamState.Idle and not StreamState.Streaming)
        {
            var dotSize = UiGeometry.Scale(this, 6);
            var dot = new Rectangle(nameLeft, (int)(44 * v) - dotSize / 2, dotSize, dotSize);
            using var stateBrush = new SolidBrush(_palette.IsHighContrast ? SystemColors.WindowText : _palette.StateColor(_streamState));
            graphics.FillEllipse(stateBrush, dot);
            var statusText = _streamState == StreamState.Failed && !string.IsNullOrWhiteSpace(_detail) ? _detail! : StatusText(_streamState);
            var statusBounds = new Rectangle(dot.Right + (int)(5 * s), (int)(34 * v), Math.Max(28, _volume.Left - dot.Right - (int)(8 * s)), (int)(20 * v));
            using var statusBrush = new SolidBrush(_palette.IsHighContrast ? SystemColors.WindowText : _palette.SecondaryText);
            using var statusFormat = new StringFormat(StringFormat.GenericTypographic)
            {
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            graphics.DrawString(statusText, statusFont, statusBrush, statusBounds, statusFormat);
        }

        DrawCompactSelection(graphics, checkBox);
        if (Focused && ShowFocusCues)
        {
            var focus = Rectangle.Inflate(ClientRectangle, -3, -3);
            using var pen = new Pen(_palette.IsHighContrast ? SystemColors.Highlight : _palette.Focus, UiGeometry.Scale(this, 2));
            using var focusPath = UiGeometry.Rounded(focus, UiGeometry.Scale(this, 9));
            graphics.DrawPath(pen, focusPath);
        }
    }

    private void DrawCompactSelection(Graphics graphics, Rectangle bounds)
    {
        if (IsPendingState(_streamState))
        {
            using var spinner = new Pen(_palette.Accent, Math.Max(1.5f, 2f * DeviceDpi / 96f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawArc(spinner, Rectangle.Inflate(bounds, -2, -2), _equalizerFrame * 60, 235);
            return;
        }

        if (_selected || _streamState == StreamState.Streaming)
        {
            using var fill = new SolidBrush(_palette.IsHighContrast ? SystemColors.Highlight : _palette.Accent);
            graphics.FillEllipse(fill, bounds);
            using var checkPen = new Pen(_palette.IsHighContrast ? SystemColors.HighlightText : _palette.OnAccent, Math.Max(1.5f, 2f * DeviceDpi / 96f))
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLines(checkPen,
            [
                new(bounds.Left + bounds.Width / 4, bounds.Top + bounds.Height / 2),
                new(bounds.Left + bounds.Width * 5 / 12, bounds.Bottom - bounds.Height / 4),
                new(bounds.Right - bounds.Width / 5, bounds.Top + bounds.Height / 3)
            ]);
            return;
        }

        if (_hoverAlpha >= .25f || Focused)
        {
            using var outline = new Pen(_palette.IsHighContrast ? SystemColors.WindowText : _palette.SecondaryText, Math.Max(1f, 1.5f * DeviceDpi / 96f));
            graphics.DrawEllipse(outline, Rectangle.Inflate(bounds, -1, -1));
        }
    }

    private void ActivateRow()
    {
        Focus();
        if (!_compact)
        {
            ToggleSelection();
            return;
        }

        if (!_compactStreamActive)
        {
            ToggleSelection();
            return;
        }

        if (_receiver is null) return;
        if (_streamState == StreamState.Failed || !_selected)
        {
            _selected = true;
            SetPlaybackState(StreamState.Connecting, "Connecting…");
            ConnectRequested?.Invoke(this, new(_receiver.Id));
        }
        else
        {
            _selected = false;
            SetPlaybackState(StreamState.Idle);
            DisconnectRequested?.Invoke(this, new(_receiver.Id));
        }
    }

    private void ToggleSelection()
    {
        Focus();
        _selected = !_selected;
        _volume.AccentFill = _selected || _streamState == StreamState.Streaming;
        _volume.Dimmed = !_selected && !IsActive;
        _volume.Invalidate();
        UpdateAccessibility();
        UpdateCompactHeight(Visible && IsHandleCreated);
        Invalidate();
        if (!_updating && _receiver is not null) SelectionChanged?.Invoke(this, new(_receiver.Id, _selected));
    }

    private void UpdateAccessibility()
    {
        var name = _receiver?.Name ?? "Speaker";
        var state = StatusText(_streamState);
        if (_compact)
        {
            AccessibleRole = _compactStreamActive ? AccessibleRole.PushButton : AccessibleRole.CheckButton;
            var action = !_compactStreamActive
                ? _selected ? "unselect" : "select"
                : _streamState == StreamState.Failed ? "retry"
                : _selected ? "disconnect" : "connect";
            AccessibleName = $"{name}, {action}";
            AccessibleDescription = $"{state}, volume {_volume.Value} percent, alignment trim {_alignmentTrimMilliseconds} milliseconds. Press Space or Enter to {action}.";
        }
        else
        {
            AccessibleRole = AccessibleRole.CheckButton;
            AccessibleName = $"{name} AirPlay speaker";
            AccessibleDescription = $"{name}, {state}, volume {_volume.Value} percent, alignment trim {_alignmentTrimMilliseconds} milliseconds. Press Space or Enter to toggle selection.";
        }
    }

    private void CommitVolume()
    {
        if (!_updating && _receiver is not null) VolumeCommitted?.Invoke(this, new(_receiver.Id, _volume.Value));
    }

    private void AdjustAlignmentTrim(int delta)
    {
        var before = _alignmentTrimMilliseconds;
        SetAlignmentTrimCore(before + delta);
        if (_alignmentTrimMilliseconds != before && !_updating && _receiver is not null)
            AlignmentTrimChanged?.Invoke(this, new(_receiver.Id, _alignmentTrimMilliseconds));
    }

    private void SetAlignmentTrimCore(int milliseconds)
    {
        _alignmentTrimMilliseconds = Math.Clamp(milliseconds, 0, 500);
        _trimValue.Text = $"Sync {_alignmentTrimMilliseconds} ms";
        _trimValue.AccessibleDescription = $"Additional alignment delay is {_alignmentTrimMilliseconds} milliseconds.";
        _trimDown.Enabled = _alignmentTrimMilliseconds > 0;
        _trimUp.Enabled = _alignmentTrimMilliseconds < 500;
        UpdateAccessibility();
    }

    private void LayoutTrimControls(int right, int top, float scale, float verticalScale)
    {
        var buttonWidth = (int)(26 * scale);
        var valueWidth = (int)(68 * scale);
        var height = (int)(24 * verticalScale);
        var total = buttonWidth * 2 + valueWidth;
        var left = right - total;
        _trimDown.SetBounds(left, top, buttonWidth, height);
        _trimValue.SetBounds(_trimDown.Right, top, valueWidth, height);
        _trimUp.SetBounds(_trimValue.Right, top, buttonWidth, height);
    }

    private void AnimateHover()
    {
        var target = _hovered ? 1f : 0f;
        _hoverAlpha += Math.Sign(target - _hoverAlpha) * 0.16f;
        if (Math.Abs(target - _hoverAlpha) <= 0.17f) { _hoverAlpha = target; _hoverTimer.Stop(); }
        Invalidate();
    }

    private bool CompactEngaged => _compact && (_selected || _streamState == StreamState.Streaming);

    private void UpdateCompactHeight(bool animate)
    {
        if (!_compact) return;
        var target = UiGeometry.ScaleText(this, CompactEngaged ? 64 : 44);
        if (Height == target) return;
        if (!animate)
        {
            _heightTimer.Stop();
            Height = target;
            PerformLayout();
            return;
        }
        _heightStart = Height;
        _heightTarget = target;
        _heightFrame = 0;
        _heightTimer.Start();
    }

    private void AnimateHeight()
    {
        _heightFrame++;
        var progress = Math.Min(1f, _heightFrame / 10f);
        var eased = 1f - MathF.Pow(1f - progress, 3f);
        Height = (int)Math.Round(_heightStart + (_heightTarget - _heightStart) * eased);
        PerformLayout();
        if (progress >= 1f) _heightTimer.Stop();
    }

    private void DrawSelection(Graphics graphics)
    {
        var size = UiGeometry.Scale(this, 18);
        var box = new Rectangle(_volume.Left - UiGeometry.Scale(this, 27), _volume.Top + (_volume.Height - size) / 2, size, size);
        if (_selected)
        {
            using var fill = new SolidBrush(_palette.IsHighContrast ? SystemColors.Highlight : _palette.Accent);
            graphics.FillEllipse(fill, box);
            using var checkPen = new Pen(_palette.IsHighContrast ? SystemColors.HighlightText : _palette.OnAccent, UiGeometry.Scale(this, 2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLines(checkPen, [new(box.Left + size / 4, box.Top + size / 2), new(box.Left + size * 5 / 12, box.Bottom - size / 4), new(box.Right - size / 5, box.Top + size / 3)]);
            return;
        }
        using var pen = new Pen(_palette.IsHighContrast ? SystemColors.WindowText : _palette.SecondaryText, Math.Max(1f, 1.5f * DeviceDpi / 96f));
        graphics.DrawEllipse(pen, box);
    }

    private void UpdateDashboardPresentation()
    {
        if (_compact) return;
        _action.Visible = _dashboardStreamActive;
        _action.Text = IsActive ? "Leave" : "Join";
        _action.Quiet = IsActive;
        _action.AccessibleName = $"{_action.Text} {_receiver?.Name ?? "speaker"}";
        _volume.Dimmed = !_selected && !IsActive;
        UpdateAccessibility();
        PerformLayout();
        Invalidate(true);
    }


    private void DrawEqualizer(Graphics graphics, int x, int y)
    {
        var unit = UiGeometry.Scale(this, 2);
        var heights = new[] { 2 + (_equalizerFrame % 3), 4 - (_equalizerFrame % 3), 2 + ((_equalizerFrame + 1) % 3) };
        using var brush = new SolidBrush(_palette.Accent);
        for (var i = 0; i < 3; i++) graphics.FillRectangle(brush, x + i * unit * 2, y + unit * (4 - heights[i]), unit, unit * heights[i]);
    }

    private static string StatusText(StreamState state) => state switch
    {
        StreamState.Idle => "Available",
        StreamState.Streaming => "Streaming",
        StreamState.Standby => "Standby — waiting for audio",
        StreamState.Failed => "Connection failed",
        StreamState.Reconnecting => "Reconnecting…",
        _ => state.ToString()
    };

    private static bool IsPendingState(StreamState state) => state is
        StreamState.Discovering or StreamState.Connecting or StreamState.Negotiating or
        StreamState.Buffering or StreamState.Reconnecting;

    private static bool IsAnimatedState(StreamState state) => state == StreamState.Streaming || IsPendingState(state);

    private string DeviceGlyph()
    {
        var identity = $"{_receiver?.Name} {_receiver?.Address}";
        if (identity.Contains("laptop", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("computer", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("mac", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains(" pc", StringComparison.OrdinalIgnoreCase)) return "\uE770";
        if (identity.Contains("tv", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("media", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("roku", StringComparison.OrdinalIgnoreCase)) return "\uE7F4";
        if (identity.Contains("homepod", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("sonos", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("echo", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("smart", StringComparison.OrdinalIgnoreCase)) return "\uE7F5";
        if (identity.Contains("mini", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("small", StringComparison.OrdinalIgnoreCase)) return "\uE767";
        return "\uE767";
    }

    private static Color Blend(Color a, Color b, float amount) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * amount), (int)(a.R + (b.R - a.R) * amount),
        (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
}
