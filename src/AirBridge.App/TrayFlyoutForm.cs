using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AirBridge.Core;

namespace AirBridge.App;

/// <summary>Compact tray-first control surface. It contains no controller dependencies.</summary>
public sealed class TrayFlyoutForm : Form
{
    private const int WmSettingChange = 0x001A;
    private const int WmSetRedraw = 0x000B;
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwUpdatenow = 0x0100;
    private const uint RdwAllchildren = 0x0080;
    private readonly AntiAliasedLabel _status = new() { Dock = DockStyle.Fill };
    private readonly LetterSpacedLabel _wordmark = new() { Text = "AIRBRIDGE", Dock = DockStyle.Fill, Font = UiGeometry.UiFont(8F, FontStyle.Bold), Tracking = 1.5f };
    private readonly LetterSpacedLabel _outputLabel = new() { Text = "OUTPUT", Dock = DockStyle.Fill, Font = UiGeometry.UiFont(8.5F, FontStyle.Regular), Tracking = 1.2f, Padding = new Padding(0, 2, 0, 0) };
    private readonly AntiAliasedLabel _browserDelay = new() { Text = "Browser extension: delay picture 2,000 ms  ›", Dock = DockStyle.Fill, Cursor = Cursors.Hand };
    private readonly FlowLayoutPanel _receivers = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 4, 0, 0) };
    private readonly LoadingIndicator _receiverLoading = new();
    private readonly PillButton _toggle = new() { Text = "Start", Primary = true, Width = 92, Height = 36 };
    private readonly PillButton _groups = new() { Text = "Groups", Quiet = true, Width = 96, Height = 36 };
    private readonly PillButton _refresh = new() { IconGlyph = "\uE72C", Quiet = true, Width = 36, Height = 36 };
    private readonly PillButton _settings = new() { IconGlyph = "\uE713", Quiet = true, Width = 36, Height = 36 };
    private readonly PillButton _quit = new() { IconGlyph = "\uE7E8", Quiet = true, Width = 36, Height = 36 };
    private readonly ToolTip _toolTip = new();
    private readonly Dictionary<string, ReceiverRowControl> _rows = new(StringComparer.Ordinal);
    private AppThemeMode _themeMode;
    private readonly TableLayoutPanel _root;
    private ThemePalette _palette;
    private StreamState _streamState;
    private Rectangle? _trayWorkingArea;
    private bool _receiverLoadingActive;
    private bool _receiverRowsPending;

    public TrayFlyoutForm(AppThemeMode themeMode = AppThemeMode.System)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _themeMode = themeMode;
        var requested = ThemePalette.Current(themeMode);
        _palette = requested;
        var textWidthScale = TextWidthScale(SystemTextScale.Current);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size((int)Math.Ceiling(420 * textWidthScale), 206);
        MinimumSize = new Size((int)Math.Ceiling(380 * textWidthScale), 206);
        MaximumSize = new Size((int)Math.Ceiling(480 * textWidthScale), 600);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Padding = new Padding(12);
        Text = "AirBridge quick controls";
        AccessibleName = "AirBridge quick controls";
        AccessibleDescription = "Select speakers, adjust volume, and start or stop streaming.";
        KeyPreview = true;
        Font = UiGeometry.UiFont(9F);

        _wordmark.AccessibleName = "AirBridge";
        _status.Font = UiGeometry.UiFont(10.5F, FontStyle.Bold);
        _status.AccessibleName = "Streaming status";
        _toggle.AccessibleName = "Start selected speakers";
        _groups.AccessibleName = "Choose a speaker group";
        _groups.AccessibleDescription = "Choose a saved speaker group or manage groups in Settings.";
        _refresh.AccessibleName = "Refresh speakers";
        _settings.AccessibleName = "Open settings";
        _quit.AccessibleName = "Quit AirBridge";
        _quit.AccessibleDescription = "Stop AirBridge and close the application.";
        _toolTip.SetToolTip(_refresh, "Refresh speakers");
        _toolTip.SetToolTip(_groups, "Speaker groups");
        _toolTip.SetToolTip(_settings, "Settings");
        _toolTip.SetToolTip(_quit, "Quit AirBridge");
        _outputLabel.AccessibleName = "Audio outputs";
        _browserDelay.AccessibleName = "Browser extension picture delay";
        _browserDelay.AccessibleDescription = "Open Browser sync settings to measure or review the recommended picture delay.";
        _toolTip.SetToolTip(_browserDelay, "Open Browser sync settings");

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
        quiet.Controls.AddRange([_groups, _refresh, _settings, _quit]);
        actions.Controls.Add(_toggle, 0, 0);
        actions.Controls.Add(quiet, 2, 0);

        _root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        _root.Controls.Add(heading, 0, 0);
        _root.Controls.Add(_outputLabel, 0, 1);
        _root.Controls.Add(_receivers, 0, 2);
        _root.Controls.Add(_browserDelay, 0, 3);
        _root.Controls.Add(actions, 0, 4);
        Controls.Add(_root);

        _toggle.Click += (_, _) =>
        {
            if (_streamState is StreamState.Idle or StreamState.Failed) StartSystemRequested?.Invoke(this, EventArgs.Empty);
            else StopRequested?.Invoke(this, EventArgs.Empty);
        };
        _refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        _groups.Click += (_, _) => GroupsRequested?.Invoke(this, EventArgs.Empty);
        _settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _browserDelay.Click += (_, _) => BrowserSyncRequested?.Invoke(this, EventArgs.Empty);
        _quit.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);
        KeyDown += (_, args) => { if (args.KeyCode == Keys.Escape) Hide(); };
        Deactivate += (_, _) => { if (AutoHide) Hide(); };
        _receivers.Layout += (_, _) => ResizeRows();
        _receivers.ClientSizeChanged += (_, _) => ResizeRows();
        Shown += (_, _) => WindowEffects.ConfigureBorderlessPopup(this);
        SystemTextScale.Changed += OnTextScaleChanged;
        UiGeometry.ScaleInitialTextLayout(this);
        UpdateTextSizedControls();
        ApplyTheme(_palette);
        UpdateStatus(StreamState.Idle, "Not streaming", "System audio");
    }

    public event EventHandler? StartSystemRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? GroupsRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? BrowserSyncRequested;
    public event EventHandler? QuitRequested;
    public event EventHandler<ReceiverSelectionChangedEventArgs>? ReceiverSelectionChanged;
    public event EventHandler<ReceiverVolumeChangedEventArgs>? ReceiverVolumeCommitted;
    public event EventHandler<ReceiverAlignmentTrimChangedEventArgs>? ReceiverAlignmentTrimChanged;
    public event EventHandler<ReceiverActionEventArgs>? ReceiverConnectRequested;
    public event EventHandler<ReceiverActionEventArgs>? ReceiverDisconnectRequested;
    public event EventHandler<ReceiverActionEventArgs>? ReceiverSleepRequested;

    internal bool AutoHide = true;
    public IReadOnlyCollection<string> ReceiverIds => _rows.Keys;

    public void SetReceivers(
        IEnumerable<ReceiverInfo> receivers,
        IReadOnlySet<string>? selectedIds = null,
        IReadOnlyDictionary<string, int>? volumes = null,
        IReadOnlyDictionary<string, int>? alignmentTrims = null)
    {
        var redrawSuspended = SuspendFlyoutRedraw();
        _receivers.SuspendLayout();
        try
        {
            _receiverRowsPending = _receiverLoadingActive;
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
                row.SleepRequested += (_, args) => ReceiverSleepRequested?.Invoke(this, args);
                row.Visible = !_receiverRowsPending;
                _rows.Add(receiver.Id, row);
                _receivers.Controls.Add(row);
            }

            if (_receiverLoadingActive)
            {
                _receivers.Controls.Add(_receiverLoading);
                _receivers.Controls.SetChildIndex(_receiverLoading, 0);
                _receiverLoading.SetActive(true);
            }
        }
        finally
        {
            _receivers.ResumeLayout(true);
            ResizeRows();
            UpdateContentHeight();
            ResumeFlyoutRedraw(redrawSuspended);
        }
    }

    public void SetReceiverLoading(bool loading)
    {
        var redrawSuspended = SuspendFlyoutRedraw();
        _receivers.SuspendLayout();
        try
        {
            _receiverLoadingActive = loading;
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
                _receiverLoading.SetActive(true);
            }
            else
            {
                _receiverRowsPending = false;
                _receiverLoading.SetActive(false);
                if (_receivers.Controls.Contains(_receiverLoading)) _receivers.Controls.Remove(_receiverLoading);
                foreach (var row in _rows.Values) row.Visible = true;
            }
        }
        finally
        {
            _receivers.ResumeLayout(true);
            ResizeRows();
            UpdateContentHeight();
            ResumeFlyoutRedraw(redrawSuspended);
        }
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

    public void UpdateBrowserDelay(int milliseconds)
    {
        _browserDelay.Text = $"Browser extension: delay picture {milliseconds:N0} ms  ›";
        _browserDelay.AccessibleDescription = $"Recommended browser picture delay is {milliseconds} milliseconds. Open Browser sync settings to measure or review it.";
    }

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        _palette.Apply(this);
        _toggle.ApplyTheme(_palette);
        _groups.ApplyTheme(_palette);
        _refresh.ApplyTheme(_palette);
        _settings.ApplyTheme(_palette);
        _quit.ApplyTheme(_palette);
        _wordmark.ApplyTheme(_palette);
        _outputLabel.ApplyTheme(_palette);
        _browserDelay.ForeColor = _palette.Accent;
        foreach (var row in _rows.Values) row.ApplyTheme(_palette);
        _status.ForeColor = _palette.StateColor(_streamState);
        UpdateRoundedRegion();
    }

    public void ShowNearTrayIcon()
    {
        _trayWorkingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        RepositionToTrayAnchor();
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
        e.Graphics.Clear(_palette.Surface);
        var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
        using var sidePen = new Pen(_palette.Border);
        if (WindowEffects.UseRoundedPopupCorners)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = UiGeometry.Rounded(bounds, UiGeometry.Scale(this, 14));
            e.Graphics.DrawPath(sidePen, path);
        }
        else
        {
            e.Graphics.SmoothingMode = SmoothingMode.None;
            e.Graphics.DrawRectangle(sidePen, bounds);
        }
        var separatorY = ClientSize.Height - Padding.Bottom - UiGeometry.Scale(this, 49);
        e.Graphics.DrawLine(sidePen, Padding.Left, separatorY, ClientSize.Width - Padding.Right, separatorY);
    }

    protected override bool ShowWithoutActivation => false;

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == WmSettingChange && IsHandleCreated && !IsDisposed)
            BeginInvoke(() =>
            {
                SystemTextScale.Refresh();
                ApplyTheme(ThemePalette.Current(_themeMode));
            });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemTextScale.Changed -= OnTextScaleChanged;
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
        // Windows 10's binary rounded regions produce visibly jagged outer
        // edges. Keep the popup rectangular there and let DWM round Windows 11.
        var previousRegion = Region;
        Region = null;
        previousRegion?.Dispose();
    }

    private bool IsGlobalStreamActive => _streamState is not StreamState.Idle and not StreamState.Failed;

    private void OnTextScaleChanged(object? sender, TextScaleChangedEventArgs args)
    {
        var widthRatio = TextWidthScale(args.Current) / TextWidthScale(args.Previous);
        MinimumSize = new((int)Math.Ceiling(MinimumSize.Width * widthRatio), MinimumSize.Height);
        MaximumSize = new((int)Math.Ceiling(MaximumSize.Width * widthRatio), MaximumSize.Height);
        ClientSize = new((int)Math.Ceiling(ClientSize.Width * widthRatio), ClientSize.Height);
        UiGeometry.RescaleText(this, args.Previous, args.Current);
        UpdateTextSizedControls();
        foreach (var row in _rows.Values) row.ApplyTextScale();
        UpdateContentHeight();
        RepositionToTrayAnchor();
    }

    private void UpdateTextSizedControls()
    {
        var maximum = Math.Max(UiGeometry.Scale(this, 110), ClientSize.Width / 3);
        _toggle.Width = Math.Min(maximum, Math.Max(UiGeometry.Scale(this, 92), TextRenderer.MeasureText(_toggle.Text, _toggle.Font).Width + UiGeometry.Scale(this, 24)));
        _groups.Width = Math.Min(maximum, Math.Max(UiGeometry.Scale(this, 96), TextRenderer.MeasureText(_groups.Text, _groups.Font).Width + UiGeometry.Scale(this, 24)));
    }

    private static float TextWidthScale(float textScale) => 1f + (Math.Min(2.25f, Math.Max(1f, textScale)) - 1f) * 0.4f;

    public void UpdateGroups(string? selectedName, int count)
    {
        _groups.Text = string.IsNullOrWhiteSpace(selectedName) ? "Groups" : selectedName;
        _groups.AccessibleDescription = selectedName is null
            ? $"Choose from {count} saved speaker groups or manage groups in Settings."
            : $"{selectedName} is selected. Choose another saved group or manage groups in Settings.";
        _toolTip.SetToolTip(_groups, selectedName is null ? $"Speaker groups ({count})" : $"Selected group: {selectedName}");
        UpdateTextSizedControls();
    }

    private void UpdateContentHeight()
    {
        _receivers.AutoScroll = _rows.Count > 6;
        var loadingHeight = _receiverLoadingActive ? _receiverLoading.Height + _receiverLoading.Margin.Vertical : 0;
        var rowsHeight = _receiverRowsPending ? 0 : _rows.Values.Take(6).Sum(row => row.Height + row.Margin.Vertical);
        var chromeHeight = _root.RowStyles.Cast<RowStyle>()
            .Where(style => style.SizeType == SizeType.Absolute)
            .Sum(style => (int)Math.Ceiling(style.Height));
        var desired = Padding.Vertical + chromeHeight + _receivers.Padding.Vertical + loadingHeight + rowsHeight;
        SetAnchoredClientHeight(Math.Min(MaximumSize.Height, Math.Max(MinimumSize.Height, desired)));
    }

    private void SetAnchoredClientHeight(int clientHeight)
    {
        if (!Visible)
        {
            ClientSize = new Size(ClientSize.Width, clientHeight);
            return;
        }

        var targetSize = SizeFromClientSize(new Size(ClientSize.Width, clientHeight));
        var workingArea = _trayWorkingArea ?? Screen.FromRectangle(Bounds).WorkingArea;
        var margin = UiGeometry.Scale(this, 8);
        var x = Math.Clamp(workingArea.Right - targetSize.Width - margin, workingArea.Left, workingArea.Right - targetSize.Width);
        var y = Math.Clamp(workingArea.Bottom - targetSize.Height - margin, workingArea.Top, workingArea.Bottom - targetSize.Height);
        SetBounds(x, y, targetSize.Width, targetSize.Height, BoundsSpecified.All);
    }

    private bool SuspendFlyoutRedraw()
    {
        if (!Visible || !IsHandleCreated) return false;
        _ = SendMessage(Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    private void ResumeFlyoutRedraw(bool suspended)
    {
        if (!suspended || !IsHandleCreated) return;
        _ = SendMessage(Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
        _ = RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwUpdatenow | RdwAllchildren);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr window, IntPtr updateRectangle, IntPtr updateRegion, uint flags);

    private void RepositionToTrayAnchor()
    {
        var workingArea = _trayWorkingArea ?? Screen.FromRectangle(Bounds).WorkingArea;
        var margin = UiGeometry.Scale(this, 8);
        var x = Math.Clamp(workingArea.Right - Width - margin, workingArea.Left, workingArea.Right - Width);
        var y = Math.Clamp(workingArea.Bottom - Height - margin, workingArea.Top, workingArea.Bottom - Height);
        Location = new Point(x, y);
    }

}
