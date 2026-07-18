using System.Drawing.Drawing2D;
using AirBridge.Core;

namespace AirBridge.App;

/// <summary>Compact tray-first control surface. It contains no controller dependencies.</summary>
public sealed class TrayFlyoutForm : Form
{
    private const int WmSettingChange = 0x001A;
    private readonly AntiAliasedLabel _status = new() { Dock = DockStyle.Fill };
    private readonly LetterSpacedLabel _wordmark = new() { Text = "AIRBRIDGE", Dock = DockStyle.Fill, Font = UiGeometry.UiFont(8F, FontStyle.Bold), Tracking = 1.5f };
    private readonly LetterSpacedLabel _outputLabel = new() { Text = "OUTPUT", Dock = DockStyle.Fill, Font = UiGeometry.UiFont(8.5F, FontStyle.Regular), Tracking = 1.2f, Padding = new Padding(0, 2, 0, 0) };
    private readonly FlowLayoutPanel _receivers = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 4, 0, 0) };
    private readonly LoadingIndicator _receiverLoading = new();
    private readonly PillButton _toggle = new() { Text = "Start", Primary = true, Width = 92, Height = 36 };
    private readonly PillButton _refresh = new() { IconGlyph = "\uE72C", Quiet = true, Width = 36, Height = 36 };
    private readonly PillButton _settings = new() { IconGlyph = "\uE713", Quiet = true, Width = 36, Height = 36 };
    private readonly PillButton _dashboard = new() { IconGlyph = "\uE740", Quiet = true, Width = 36, Height = 36 };
    private readonly PillButton _quit = new() { IconGlyph = "\uE7E8", Quiet = true, Width = 36, Height = 36 };
    private readonly ToolTip _toolTip = new();
    private readonly Dictionary<string, ReceiverRowControl> _rows = new(StringComparer.Ordinal);
    private AppThemeMode _themeMode;
    private readonly TableLayoutPanel _root;
    private ThemePalette _palette;
    private StreamState _streamState;

    public TrayFlyoutForm(AppThemeMode themeMode = AppThemeMode.System)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _themeMode = themeMode;
        var requested = ThemePalette.Current(themeMode);
        _palette = requested;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(420, 344);
        MinimumSize = new Size(380, 180);
        MaximumSize = new Size(480, 600);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Padding = new Padding(12);
        Text = "AirBridge quick controls";
        AccessibleName = "AirBridge quick controls";
        AccessibleDescription = "Select speakers, adjust volume, and start or stop streaming.";
        KeyPreview = true;

        _wordmark.AccessibleName = "AirBridge";
        _status.Font = UiGeometry.UiFont(10F, FontStyle.Bold);
        _status.AccessibleName = "Streaming status";
        _toggle.AccessibleName = "Start selected speakers";
        _refresh.AccessibleName = "Refresh speakers";
        _settings.AccessibleName = "Open settings";
        _dashboard.AccessibleName = "Open dashboard";
        _quit.AccessibleName = "Quit AirBridge";
        _quit.AccessibleDescription = "Stop AirBridge and close the application.";
        _toolTip.SetToolTip(_refresh, "Refresh speakers");
        _toolTip.SetToolTip(_settings, "Settings");
        _toolTip.SetToolTip(_dashboard, "Open dashboard");
        _toolTip.SetToolTip(_quit, "Quit AirBridge");
        _toggle.GrayscaleText = true;
        _refresh.GrayscaleText = true;
        _settings.GrayscaleText = true;
        _dashboard.GrayscaleText = true;
        _quit.GrayscaleText = true;
        _outputLabel.AccessibleName = "Audio outputs";

        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        heading.Controls.Add(_wordmark, 0, 0);
        heading.Controls.Add(_status, 0, 1);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty, Padding = new Padding(0, 7, 0, 0) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var quiet = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        quiet.Controls.AddRange([_refresh, _settings, _dashboard, _quit]);
        actions.Controls.Add(_toggle, 0, 0);
        actions.Controls.Add(quiet, 2, 0);

        _root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        _root.Controls.Add(heading, 0, 0);
        _root.Controls.Add(_outputLabel, 0, 1);
        _root.Controls.Add(_receivers, 0, 2);
        _root.Controls.Add(actions, 0, 3);
        Controls.Add(_root);

        _toggle.Click += (_, _) =>
        {
            if (_streamState is StreamState.Idle or StreamState.Failed) StartSystemRequested?.Invoke(this, EventArgs.Empty);
            else StopRequested?.Invoke(this, EventArgs.Empty);
        };
        _refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        _settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _dashboard.Click += (_, _) => OpenDashboardRequested?.Invoke(this, EventArgs.Empty);
        _quit.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);
        KeyDown += (_, args) => { if (args.KeyCode == Keys.Escape) Hide(); };
        Deactivate += (_, _) => { if (AutoHide) Hide(); };
        _receivers.Layout += (_, _) => ResizeRows();
        _receivers.ClientSizeChanged += (_, _) => ResizeRows();
        Shown += (_, _) => WindowEffects.TryEnableRoundedCorners(this);
        ApplyTheme(_palette);
        UpdateStatus(StreamState.Idle, "Not streaming", "System audio");
    }

    public event EventHandler? StartSystemRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? OpenDashboardRequested;
    public event EventHandler? QuitRequested;
    public event EventHandler<ReceiverSelectionChangedEventArgs>? ReceiverSelectionChanged;
    public event EventHandler<ReceiverVolumeChangedEventArgs>? ReceiverVolumeCommitted;
    public event EventHandler<ReceiverAlignmentTrimChangedEventArgs>? ReceiverAlignmentTrimChanged;
    public event EventHandler<ReceiverActionEventArgs>? ReceiverConnectRequested;
    public event EventHandler<ReceiverActionEventArgs>? ReceiverDisconnectRequested;

    internal bool AutoHide = true;
    public IReadOnlyCollection<string> ReceiverIds => _rows.Keys;

    public void SetReceivers(
        IEnumerable<ReceiverInfo> receivers,
        IReadOnlySet<string>? selectedIds = null,
        IReadOnlyDictionary<string, int>? volumes = null,
        IReadOnlyDictionary<string, int>? alignmentTrims = null)
    {
        _receivers.SuspendLayout();
        try
        {
            _receivers.Controls.Clear();
            foreach (var row in _rows.Values) row.Dispose();
            _rows.Clear();
            foreach (var receiver in receivers)
            {
                var row = new ReceiverRowControl { Width = Math.Max(340, _receivers.ClientSize.Width - SystemInformation.VerticalScrollBarWidth) };
                row.UseCompactLayout();
                row.SetCompactStreamActive(IsGlobalStreamActive);
                var selected = selectedIds?.Contains(receiver.Id) == true;
                var volume = volumes?.TryGetValue(receiver.Id, out var savedVolume) == true ? savedVolume : 30;
                var alignmentTrim = alignmentTrims?.TryGetValue(receiver.Id, out var savedTrim) == true ? savedTrim : 0;
                row.Bind(receiver, selected, volume, alignmentTrimMilliseconds: alignmentTrim);
                row.ApplyTheme(_palette);
                row.SizeChanged += (_, _) => { if (!IsDisposed) UpdateContentHeight(); };
                row.SelectionChanged += (_, args) => ReceiverSelectionChanged?.Invoke(this, args);
                row.VolumeCommitted += (_, args) => ReceiverVolumeCommitted?.Invoke(this, args);
                row.AlignmentTrimChanged += (_, args) => ReceiverAlignmentTrimChanged?.Invoke(this, args);
                row.ConnectRequested += (_, args) => ReceiverConnectRequested?.Invoke(this, args);
                row.DisconnectRequested += (_, args) => ReceiverDisconnectRequested?.Invoke(this, args);
                _rows.Add(receiver.Id, row);
                _receivers.Controls.Add(row);
            }
        }
        finally { _receivers.ResumeLayout(true); ResizeRows(); UpdateContentHeight(); }
    }

    public void SetReceiverLoading(bool loading)
    {
        _refresh.Enabled = !loading;
        _refresh.AccessibleDescription = loading
            ? "Refreshing the available AirPlay speakers."
            : "Refresh the available AirPlay speakers.";
        _toolTip.SetToolTip(_refresh, loading ? "Refreshing speakers…" : "Refresh speakers");

        if (loading)
        {
            _receiverLoading.ApplyTheme(_palette);
            if (!_receivers.Controls.Contains(_receiverLoading)) _receivers.Controls.Add(_receiverLoading);
            _receivers.Controls.SetChildIndex(_receiverLoading, 0);
        }
        else if (_receivers.Controls.Contains(_receiverLoading))
        {
            _receivers.Controls.Remove(_receiverLoading);
        }
        ResizeRows();
    }

    public void UpdateReceiver(
        string receiverId,
        StreamState state,
        int? volume = null,
        bool? selected = null,
        string? detail = null,
        int? alignmentTrimMilliseconds = null)
    {
        if (!_rows.TryGetValue(receiverId, out var row)) return;
        if (volume is int value) row.SetVolume(value);
        if (alignmentTrimMilliseconds is int trim) row.SetAlignmentTrim(trim);
        if (selected is bool isSelected) row.SetSelected(isSelected);
        if (!(state == StreamState.Idle && selected == true && row.IsPendingVisual)) row.SetPlaybackState(state, detail);
    }

    public void UpdateStatus(StreamState state, string summary, string source)
    {
        _streamState = state;
        _status.Text = IsGlobalStreamActive || string.IsNullOrWhiteSpace(source) ? summary : $"{summary} · {source}";
        _status.ForeColor = _palette.StateColor(state);
        _status.AccessibleDescription = $"{state}: {_status.Text}";
        _toggle.Text = state is StreamState.Idle or StreamState.Failed ? "Start" : "Stop";
        _toggle.AccessibleName = state is StreamState.Idle or StreamState.Failed ? "Start selected speakers" : "Stop all speakers";
        foreach (var row in _rows.Values) row.SetCompactStreamActive(IsGlobalStreamActive);
    }

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        _palette.Apply(this);
        _toggle.ApplyTheme(_palette);
        _refresh.ApplyTheme(_palette);
        _settings.ApplyTheme(_palette);
        _dashboard.ApplyTheme(_palette);
        _quit.ApplyTheme(_palette);
        _wordmark.ApplyTheme(_palette);
        _outputLabel.ApplyTheme(_palette);
        foreach (var row in _rows.Values) row.ApplyTheme(_palette);
        _status.ForeColor = _palette.StateColor(_streamState);
        UpdateRoundedRegion();
    }

    public void ShowNearTrayIcon()
    {
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = Math.Clamp(workingArea.Right - Width - 8, workingArea.Left, workingArea.Right - Width);
        var y = Math.Clamp(workingArea.Bottom - Height - 8, workingArea.Top, workingArea.Bottom - Height);
        Location = new Point(x, y);
        Show();
        ReflowReceiverRows();
        Activate();
        BringToFront();
    }

    public void SetThemeMode(AppThemeMode themeMode)
    {
        _themeMode = themeMode;
        ApplyTheme(ThemePalette.Current(themeMode));
    }

    public void ReflowReceiverRows() { PerformLayout(); ResizeRows(); }

    protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateRoundedRegion(); }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_palette.IsHighContrast) { e.Graphics.Clear(SystemColors.Window); return; }
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(_palette.Surface);
        var radius = UiGeometry.Scale(this, 14);
        var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
        using var path = UiGeometry.Rounded(bounds, radius);
        using var sidePen = new Pen(_palette.Border);
        e.Graphics.DrawPath(sidePen, path);
        var separatorY = ClientSize.Height - Padding.Bottom - UiGeometry.Scale(this, 49);
        e.Graphics.DrawLine(sidePen, Padding.Left, separatorY, ClientSize.Width - Padding.Right, separatorY);
    }

    protected override bool ShowWithoutActivation => false;

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == WmSettingChange && IsHandleCreated && !IsDisposed)
            BeginInvoke(() => ApplyTheme(ThemePalette.Current(_themeMode)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _receiverLoading.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ResizeRows()
    {
        var width = Math.Max(340, _receivers.ClientSize.Width - (_receivers.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
        if (_receiverLoading.Width != width) _receiverLoading.Width = width;
        foreach (var row in _rows.Values) if (row.Width != width) row.Width = width;
    }

    private void UpdateRoundedRegion()
    {
        if (_palette.IsHighContrast || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) { Region = null; return; }
        using var path = UiGeometry.Rounded(ClientRectangle, UiGeometry.Scale(this, 14));
        Region = new Region(path);
    }

    private bool IsGlobalStreamActive => _streamState is not StreamState.Idle and not StreamState.Failed;

    private void UpdateContentHeight()
    {
        _receivers.AutoScroll = _rows.Count > 6;
        var rowsHeight = _rows.Values.Take(6).Sum(row => row.Height + row.Margin.Vertical);
        var chromeHeight = _root.RowStyles.Cast<RowStyle>()
            .Where(style => style.SizeType == SizeType.Absolute)
            .Sum(style => (int)Math.Ceiling(style.Height));
        var desired = Padding.Vertical + chromeHeight + _receivers.Padding.Vertical + rowsHeight;
        ClientSize = new Size(ClientSize.Width, Math.Min(MaximumSize.Height, Math.Max(MinimumSize.Height, desired)));
    }

}
