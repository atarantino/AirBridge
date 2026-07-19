using System.Runtime.InteropServices;
using AirBridge.Core;

namespace AirBridge.App;

public sealed class MainForm : Form
{
    private const int HotkeyId = 0xA17B;
    private const int WmHotkey = 0x0312;
    private const int WmSettingChange = 0x001A;
    private readonly AirBridgeController _controller = new();
    private readonly PushToTalkRecorder _recorder = new();
    private readonly AgentActivityStore _activityStore;
    private readonly ToolConfirmationStore _agentConfirmations = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly IOpenAiCredentialStore _openAiCredentials = new WindowsOpenAiCredentialStore();
    private readonly SegmentedControl _sourceMode = new() { Width = 156, Height = 36 };
    private readonly ComboBox _sessions = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly ComboBox _groups = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly Label _sourceCaption = new() { Text = "Source", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 10, 8, 0) };
    private readonly PillButton _groupsButton = new() { Text = "Groups  ▾", Width = 92, Height = 34, Quiet = true };
    private readonly PillButton _refreshButton = new() { Text = "Refresh", Width = 92, Height = 34 };
    private readonly PillButton _startButton = new() { Text = "Select a speaker", Width = 168, Height = 36, Primary = true, Enabled = false };
    private readonly PillButton _stopButton = new() { Text = "Stop all", Width = 92, Height = 34, Visible = false };
    private readonly PillButton _settingsButton = new() { IconGlyph = "\uE713", Quiet = true, Width = 38, Height = 38 };
    private readonly ContextMenuStrip _groupsMenu = new();
    private readonly ContextMenuStrip _diagnosticsMenu = new();
    private readonly FlowLayoutPanel _receiverPanel = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 4, 4, 4) };
    private readonly LoadingIndicator _receiverLoading = new();
    private readonly TableLayoutPanel _root = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
    private readonly Dictionary<string, ReceiverRowControl> _receiverRows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectedReceiverIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _receiverVolumes = new(StringComparer.Ordinal);
    private readonly Label _state = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _route = new() { AutoSize = true };
    private readonly Label _duration = new() { AutoSize = true };
    private readonly Label _format = new() { AutoSize = true };
    private readonly Label _latency = new() { AutoSize = true };
    private readonly Label _buffer = new() { AutoSize = true };
    private readonly Label _transport = new() { AutoSize = true };
    private readonly Label _metricsSummary = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _idleTelemetryHint = new() { Dock = DockStyle.Fill, Text = "Select one or more speakers, then start system audio or an application.", TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label _recommendation = new() { AutoSize = true, MaximumSize = new Size(780, 0) };
    private readonly TextBox _command = new() { Dock = DockStyle.Fill, PlaceholderText = "Ask AirBridge to route or diagnose…" };
    private readonly RichTextBox _conversation = new() { ReadOnly = true, ScrollBars = RichTextBoxScrollBars.None, Dock = DockStyle.Fill, Height = 120, BorderStyle = BorderStyle.None, Visible = false };
    private readonly PillButton _pushToTalk = new() { Text = "Hold to talk", IconGlyph = "\uE720", Width = 128, Height = 36, Quiet = true };
    private readonly NotifyIcon _tray = new() { Text = "AirBridge — idle" };
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly TrayFlyoutForm _trayFlyout;
    private readonly VoiceHudForm _voiceHud;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _hotkeyPollTimer = new() { Interval = 30 };
    private readonly ShutdownCoordinator _shutdown;
    private AirBridgeSettings _settings;
    private ThemePalette _palette;
    private OpenAiAgent? _agent;
    private ActivityInspectorForm? _activityInspector;
    private Icon? _ownedTrayIcon;
    private StreamState? _trayIconState;
    private StreamState _lastDisplayedState = StreamState.Idle;
    private bool _allowClose;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;
    private bool _uiResourcesDisposed;
    private bool _refreshingReceivers;
    private bool _previewStreamActive;
    private Control? _telemetryDetails;
    private System.Threading.Timer? _shutdownWatchdog;
    private readonly bool _previewMode;
    private HotkeyGesture _hotkeyGesture;
    private long _hotkeyPressedAt;
    private bool _hotkeyWaitingForRelease;
    private CancellationTokenSource? _transcriptionCancellation;

    public MainForm(bool previewMode = false, AppThemeMode? previewTheme = null, bool previewStreaming = true)
    {
        _previewMode = previewMode;
        _activityStore = new(persistToDisk: !previewMode);
        _previewStreamActive = previewMode && previewStreaming;
        _settings = _settingsStore.Load();
        _controller.ConfigureSettings(_settings);
        foreach (var id in _settings.SelectedReceiverIds) _selectedReceiverIds.Add(id);
        foreach (var pair in _settings.ReceiverVolumes) _receiverVolumes[pair.Key] = pair.Value;
        var themeMode = previewTheme ?? ParseTheme(_settings.ThemeMode);
        _palette = ThemePalette.Current(themeMode);
        _trayFlyout = new(themeMode);
        _voiceHud = new(_palette);
        if (!_voiceHud.SetInitialPosition(_settings.VoiceHudX, _settings.VoiceHudY))
            _settings = _settings with { VoiceHudX = null, VoiceHudY = null };
        _hotkeyGesture = HotkeyGesture.TryParse(_settings.PushToTalkShortcut, out var gesture) ? gesture : HotkeyGesture.Default;
        _shutdown = new(_controller.ShutdownAsync, _controller.ForceCleanup, TimeSpan.FromSeconds(8));

        Text = "AirBridge for Windows";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiGeometry.UiFont(9F);
        ClientSize = new Size(920, 860);
        MinimumSize = new Size(720, 650);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = previewMode;
        Opacity = previewMode ? 1D : 0D;
        BuildLayout();
        UiGeometry.ScaleInitialTextLayout(this);
        BuildTraySurface();
        _tray.Visible = !previewMode;
        WireEvents();
        ApplyTheme();
        UpdateTelemetry();
        if (previewMode) Load += (_, _) => ApplyInitialPreviewBounds();
        if (previewMode) PopulatePreviewData(previewStreaming);
        if (!previewMode) Shown += async (_, _) =>
        {
            Hide();
            _trayFlyout.ShowNearTrayIcon();
            await InitializeAsync();
        };
        FormClosing += OnFormClosing;
        SystemTextScale.Changed += OnTextScaleChanged;
        if (!previewMode)
        {
            var (modifiers, virtualKey) = _hotkeyGesture.ToRegisterHotKeyArgs();
            if (!RegisterHotKey(Handle, HotkeyId, modifiers, virtualKey))
                AppendConversation("System", $"Could not register push-to-talk shortcut {_hotkeyGesture}; another app may be using it.");
        }
    }

    private void WireEvents()
    {
        _controller.Coordinator.RouteChanged += (_, _) => SafeBeginInvoke(UpdateTelemetry);
        _controller.PlaybackChanged += (_, _) => SafeBeginInvoke(UpdateTelemetry);
        _controller.ReceiverAlignmentChanged += (_, args) => SafeBeginInvoke(() => PersistAlignmentTrim(args.ReceiverId, args.Milliseconds));
        _controller.SilenceStandbyChanged += (_, args) => SafeBeginInvoke(() => PersistSilenceStandby(args.Enabled, args.Seconds));
        _controller.TelemetryEvent += (_, message) => SafeBeginInvoke(() => AppendConversation("Timing", message));
        _activityStore.ActivityPublished += OnAgentActivityPublished;
        _timer.Tick += (_, _) => UpdateTelemetry();
        _timer.Start();
        _hotkeyPollTimer.Tick += (_, _) => PollHotkeyGesture();
        _tray.MouseClick += (_, args) => { if (args.Button == MouseButtons.Left) ToggleTrayFlyout(); };
        _trayMenu.Opening += (_, _) => RebuildTrayMenu();
        _pushToTalk.MouseDown += (_, _) => StartRecording(true);
        _pushToTalk.MouseUp += async (_, _) => await StopRecordingAndAskAsync();
        _voiceHud.CancelRequested += (_, _) => CancelVoiceCommand();
        _voiceHud.PositionCommitted += (_, _) => PersistVoiceHudPosition();
        _receiverPanel.ClientSizeChanged += (_, _) => ResizeReceiverRows();
        _receiverPanel.Layout += (_, _) => ResizeReceiverRows();

        _trayFlyout.StartSystemRequested += async (_, _) => await StartSelectedAsync();
        _trayFlyout.StopRequested += async (_, _) => await RunUiActionAsync(() => _controller.StopAsync());
        _trayFlyout.RefreshRequested += async (_, _) => await RunUiActionAsync(RefreshReceiversAsync);
        _trayFlyout.GroupsRequested += (_, _) => ShowGroupsMenu();
        _trayFlyout.SettingsRequested += (_, _) => ShowSettingsDialog();
        _trayFlyout.QuitRequested += (_, _) => RequestQuit();
        _trayFlyout.ReceiverSelectionChanged += (_, args) => SetReceiverSelected(args.ReceiverId, args.Selected);
        _trayFlyout.ReceiverVolumeCommitted += async (_, args) => await ChangeReceiverVolumeAsync(args.ReceiverId, args.Volume);
        _trayFlyout.ReceiverAlignmentTrimChanged += (_, args) => _controller.SetReceiverAlignmentTrim(args.ReceiverId, args.Milliseconds);
        _trayFlyout.ReceiverConnectRequested += async (_, args) => await ConnectReceiverAsync(args.ReceiverId);
        _trayFlyout.ReceiverDisconnectRequested += async (_, args) =>
        {
            await RunUiActionAsync(() => _controller.StopReceiverAsync(args.ReceiverId));
            SetReceiverSelected(args.ReceiverId, false);
        };
        _trayFlyout.ReceiverSleepRequested += async (_, args) => await SleepAppleTvAsync(args.ReceiverId);
    }

    private void BuildLayout()
    {
        _sourceMode.SelectedIndex = _settings.DefaultCaptureMode == CaptureMode.ProcessTreeInclude ? 1 : 0;
        _sourceMode.SelectedIndexChanged += (_, _) => _sessions.Visible = _sourceMode.SelectedIndex == 1;
        _sessions.Visible = _sourceMode.SelectedIndex == 1;
        _sessions.AccessibleName = "Application audio source";
        _sessions.AccessibleDescription = "Choose the application whose audio should be streamed.";

        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 134));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 154));

        _root.Controls.Add(BuildHeader(), 0, 0);
        _root.Controls.Add(BuildRoutingBar(), 0, 1);
        _root.Controls.Add(BuildReceiverSection(), 0, 2);
        _root.Controls.Add(BuildDiagnosticsSection(), 0, 3);
        _root.Controls.Add(BuildAssistantSection(), 0, 4);
        Controls.Add(_root);
    }

    private Control BuildHeader()
    {
        var app = new Label { Text = "AIRBRIDGE", Dock = DockStyle.Fill, Font = UiGeometry.UiFont(8F, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft };
        app.ForeColor = _palette.SecondaryText;
        app.AccessibleName = "AirBridge";
        _state.Font = UiGeometry.UiFont(11F, FontStyle.Bold);
        _settingsButton.AccessibleName = "Open settings";
        _settingsButton.AccessibleDescription = "Change AirBridge appearance, defaults, and standby behavior.";
        _settingsButton.Click += (_, _) => ShowSettingsDialog();
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 8) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(app, 0, 0);
        header.Controls.Add(_state, 0, 1);
        header.Controls.Add(_settingsButton, 1, 0);
        header.SetRowSpan(_settingsButton, 2);
        _settingsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _state.AccessibleName = "Aggregate stream status";
        return header;
    }

    private Control BuildRoutingBar()
    {
        _startButton.AccessibleName = "Select a speaker";
        _stopButton.AccessibleName = "Stop all speakers";
        _startButton.Click += async (_, _) => await StartSelectedAsync();
        _stopButton.Click += async (_, _) => await RunUiActionAsync(() => _controller.StopAsync());

        _sourceCaption.ForeColor = _palette.SecondaryText;
        var source = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        source.Controls.Add(_sourceCaption);
        source.Controls.Add(_sourceMode);
        source.Controls.Add(_sessions);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        actions.Controls.Add(_startButton);
        actions.Controls.Add(_stopButton);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12, 10, 12, 8), Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.Controls.Add(source, 0, 0);
        content.Controls.Add(actions, 2, 0);
        return Card(content, new Padding(0, 0, 0, 12));
    }

    private Control BuildReceiverSection()
    {
        _refreshButton.AccessibleName = "Refresh speakers";
        _refreshButton.AccessibleDescription = "Refresh the available AirPlay speakers.";
        _refreshButton.Click += async (_, _) => await RunUiActionAsync(RefreshReceiversAsync);
        _groupsButton.Click += (_, _) => ShowGroupsMenu(_groupsButton);
        _groupsButton.AccessibleName = "Speaker groups";
        _groupsButton.AccessibleDescription = "Choose a saved speaker group or manage groups in Settings.";

        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 1, Padding = new Padding(12, 9, 12, 6) };
        var titleRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleRow.Controls.Add(new Label { Text = "Speakers", AutoSize = true, Font = UiGeometry.UiFont(10F, FontStyle.Bold), Margin = new Padding(0, 7, 0, 0) }, 0, 0);
        var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        tools.Controls.AddRange([_groupsButton, _refreshButton]);
        titleRow.Controls.Add(tools, 1, 0);
        heading.Controls.Add(titleRow, 0, 0);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(heading, 0, 0);
        content.Controls.Add(_receiverPanel, 0, 1);
        return Card(content, new Padding(0, 0, 0, 12));
    }

    private Control BuildDiagnosticsSection()
    {
        var overflow = SecondaryButton("•••");
        overflow.Width = 42;
        overflow.AccessibleName = "Diagnostics options";
        overflow.AccessibleDescription = "Open diagnostics actions including delay measurement.";
        _diagnosticsMenu.Items.Add("Measure delay", null, async (_, _) => await MeasureSelectedDelayAsync());
        _diagnosticsMenu.Items.Add("Align selected group", null, async (_, _) => await AlignSelectedGroupAsync());
        _diagnosticsMenu.Items.Add(BuildSilenceStandbyMenu());
        _diagnosticsMenu.Items.Add(new ToolStripSeparator());
        _diagnosticsMenu.Items.Add("AI activity inspector", null, (_, _) => ShowActivityInspector());
        _diagnosticsMenu.Items.Add("Open logs folder", null, (_, _) => OpenLogsFolder());
        overflow.Click += (_, _) => _diagnosticsMenu.Show(overflow, new Point(0, overflow.Height));
        var metricsRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        metricsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        metricsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        _metricsSummary.Margin = new Padding(12, 4, 4, 0);
        _metricsSummary.Font = UiGeometry.UiFont(8.5F);
        _metricsSummary.AccessibleName = "Stream metrics";
        metricsRow.Controls.Add(_metricsSummary, 0, 0);
        metricsRow.Controls.Add(overflow, 1, 0);
        overflow.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(metricsRow, 0, 0);
        _buffer.Margin = new Padding(12, 4, 12, 0);
        _buffer.Font = UiGeometry.UiFont(8.5F);
        panel.Controls.Add(_buffer, 0, 1);
        _recommendation.Margin = new Padding(12, 2, 52, 8);
        _recommendation.Font = UiGeometry.UiFont(8.5F);
        _recommendation.AutoSize = false;
        _recommendation.Dock = DockStyle.Fill;
        _recommendation.AutoEllipsis = true;
        panel.Controls.Add(_recommendation, 0, 2);
        _telemetryDetails = panel;
        _idleTelemetryHint.ForeColor = _palette.SecondaryText;
        _idleTelemetryHint.AccessibleName = "Getting started";
        var host = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        host.Controls.Add(panel);
        host.Controls.Add(_idleTelemetryHint);
        _idleTelemetryHint.BringToFront();
        return Card(host, new Padding(0, 0, 0, 12));
    }

    private Control BuildAssistantSection()
    {
        var ask = PrimaryButton("Ask");
        ask.Width = 58;
        ask.Click += async (_, _) => await AskAsync(_command.Text);
        _command.KeyDown += async (_, args) => { if (args.KeyCode == Keys.Enter) { args.SuppressKeyPress = true; await AskAsync(_command.Text); } };
        _command.BorderStyle = BorderStyle.None;
        _command.AccessibleName = "Ask AirBridge";
        _command.AccessibleDescription = "Enter a routing or diagnostics request.";
        _pushToTalk.AccessibleName = "Hold to talk";
        _pushToTalk.AccessibleDescription = "Press and hold while speaking, then release to transcribe.";
        var commandRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, ColumnCount = 3, Padding = new Padding(9, 5, 5, 5), Margin = new Padding(12, 6, 12, 4) };
        commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        commandRow.Controls.Add(_command, 0, 0);
        commandRow.Controls.Add(ask, 1, 0);
        commandRow.Controls.Add(_pushToTalk, 2, 0);
        var search = new RoundedPanel { Dock = DockStyle.Top, Height = 48, Radius = 12, Margin = new Padding(12, 5, 12, 4), Padding = Padding.Empty };
        search.Controls.Add(commandRow);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = "Assistant", AutoSize = true, Font = UiGeometry.UiFont(10F, FontStyle.Bold), Margin = new Padding(12, 7, 0, 0) }, 0, 0);
        panel.Controls.Add(search, 0, 1);
        _conversation.Margin = new Padding(16, 0, 16, 10);
        _conversation.AccessibleName = "Assistant conversation";
        panel.Controls.Add(_conversation, 0, 2);
        return Card(panel, Padding.Empty);
    }

    private void BuildTraySurface()
    {
        // NotifyIcon may not display a ContextMenuStrip that is empty when the
        // first right-click begins. Seed it before attaching; Opening still
        // rebuilds it so subsequent invocations reflect the current state.
        RebuildTrayMenu();
        _tray.ContextMenuStrip = _trayMenu;
        ReplaceTrayIcon(StreamState.Idle);
    }

    private async Task InitializeAsync()
    {
        await RunUiActionAsync(async () =>
        {
            await _controller.InitializeAsync();
            await RefreshReceiversAsync();
            RefreshSessions();
            RefreshGroups();
            var apiKey = ResolveOpenAiApiKey();
            if (!string.IsNullOrWhiteSpace(apiKey) && _settings.AiEnabled) _agent = new OpenAiAgent(apiKey, new AgentPolicy(), _controller,
                activity: _activityStore, confirmationStore: _agentConfirmations, confirmationPrompt: ConfirmAgentToolAsync);
            else AppendConversation("System", "Save an OpenAI API key in Settings to enable GPT-5.6 and push-to-talk. Streaming stays fully available.");
        });
    }

    private async Task RefreshReceiversAsync()
    {
        if (_refreshingReceivers) return;
        _refreshingReceivers = true;
        SetReceiverLoading(true);
        try
        {
            var discovered = await _controller.DiscoverAsync();
            if (_selectedReceiverIds.Count == 0)
            {
                var preferred = discovered.FirstOrDefault(item => item.Id == _settings.DefaultReceiverId)
                    ?? discovered.FirstOrDefault(item => item.Name.Equals(_settings.DefaultReceiverName, StringComparison.OrdinalIgnoreCase));
                if (preferred?.CanConnect == true) _selectedReceiverIds.Add(preferred.Id);
            }
            foreach (var unavailable in discovered.Where(item => !item.CanConnect))
                _selectedReceiverIds.Remove(unavailable.Id);
            RebuildReceiverRows(discovered);
            if (discovered.Count == 0) AppendConversation("System", "No AirPlay receivers found. Confirm Windows and the speakers share the same private local network.");
            PersistUiSettings();
        }
        finally
        {
            _refreshingReceivers = false;
            SetReceiverLoading(false);
        }
    }

    private void SetReceiverLoading(bool loading)
    {
        _refreshButton.Enabled = !loading;
        _refreshButton.AccessibleDescription = loading
            ? "Refreshing the available AirPlay speakers."
            : "Refresh the available AirPlay speakers.";
        _trayFlyout.SetReceiverLoading(loading);

        if (loading)
        {
            _receiverLoading.Width = Math.Max(360, _receiverPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 10);
            _receiverLoading.ApplyTheme(_palette);
            if (!_receiverPanel.Controls.Contains(_receiverLoading)) _receiverPanel.Controls.Add(_receiverLoading);
            _receiverPanel.Controls.SetChildIndex(_receiverLoading, 0);
            _receiverLoading.SetActive(true);
        }
        else
        {
            _receiverLoading.SetActive(false);
            if (_receiverPanel.Controls.Contains(_receiverLoading)) _receiverPanel.Controls.Remove(_receiverLoading);
        }
    }

    private void RebuildReceiverRows(IReadOnlyList<ReceiverInfo> receivers)
    {
        _receiverPanel.SuspendLayout();
        try
        {
            _receiverPanel.Controls.Clear();
            foreach (var row in _receiverRows.Values) row.Dispose();
            _receiverRows.Clear();
            var playback = _controller.ReceiverPlayback.ToDictionary(item => item.Receiver.Id, StringComparer.Ordinal);
            foreach (var receiver in receivers)
            {
                var row = new ReceiverRowControl();
                var volume = _receiverVolumes.TryGetValue(receiver.Id, out var saved) ? saved : 30;
                var state = playback.TryGetValue(receiver.Id, out var active) ? active.State : StreamState.Idle;
                var trim = _controller.GetReceiverAlignmentTrim(receiver.Id);
                row.Bind(receiver, _selectedReceiverIds.Contains(receiver.Id), volume, state,
                    receiver.ConnectionIssue ?? (receiver.RequiresPairing ? "Pairing required" : receiver.RequiresPassword ? "Password required" : null), trim);
                row.SetDashboardStreamActive(IsDashboardStreamActive);
                row.ApplyTheme(_palette);
                row.SelectionChanged += (_, args) => SetReceiverSelected(args.ReceiverId, args.Selected);
                row.VolumeCommitted += async (_, args) => await ChangeReceiverVolumeAsync(args.ReceiverId, args.Volume);
                row.AlignmentTrimChanged += (_, args) => _controller.SetReceiverAlignmentTrim(args.ReceiverId, args.Milliseconds);
                row.ConnectRequested += async (_, args) => await ConnectReceiverAsync(args.ReceiverId);
                row.DisconnectRequested += async (_, args) => await RunUiActionAsync(() => _controller.StopReceiverAsync(args.ReceiverId));
                row.SleepRequested += async (_, args) => await SleepAppleTvAsync(args.ReceiverId);
                _receiverRows.Add(receiver.Id, row);
                _receiverPanel.Controls.Add(row);
            }
            ResizeReceiverRows();
            _trayFlyout.SetReceivers(receivers, _selectedReceiverIds, _receiverVolumes, _settings.ReceiverAlignmentTrimMs);
            UpdateDashboardPresentation(IsDashboardStreamActive);
        }
        finally { _receiverPanel.ResumeLayout(true); }
    }

    private void ResizeReceiverRows()
    {
        var width = Math.Max(360, _receiverPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 10);
        if (_receiverLoading.Width != width) _receiverLoading.Width = width;
        foreach (var row in _receiverRows.Values) if (row.Width != width) row.Width = width;
    }

    private void RefreshSessions()
    {
        try
        {
            var sessions = WasapiCaptureService.ListSessions();
            _sessions.DataSource = sessions.ToList();
            _sessions.DisplayMember = nameof(AudioSessionInfo.Application);
        }
        catch (Exception ex) { AppLog.Error("audio-sessions", "Could not enumerate audio sessions.", ex); AppendConversation("System", ex.Message); }
    }

    private void RefreshGroups()
    {
        _groups.DataSource = null;
        _groups.DataSource = _settings.SpeakerGroups.ToList();
        _groups.DisplayMember = nameof(SpeakerGroup.Name);
        var selected = _settings.SpeakerGroups.FirstOrDefault(group =>
            group.ReceiverIds.ToHashSet(StringComparer.Ordinal).SetEquals(_selectedReceiverIds));
        _groupsButton.Text = selected is null ? "Groups  ▾" : $"{selected.Name}  ▾";
        _trayFlyout.UpdateGroups(selected?.Name, _settings.SpeakerGroups.Count);
    }

    private void ShowGroupsMenu(Control? anchor = null)
    {
        _groupsMenu.Items.Clear();
        if (_settings.SpeakerGroups.Count == 0) _groupsMenu.Items.Add(new ToolStripMenuItem("No saved groups") { Enabled = false });
        foreach (var group in _settings.SpeakerGroups)
        {
            var item = new ToolStripMenuItem(group.Name)
            {
                Checked = group.ReceiverIds.ToHashSet(StringComparer.Ordinal).SetEquals(_selectedReceiverIds)
            };
            item.Click += (_, _) => { _groups.SelectedItem = group; ApplySelectedGroup(); };
            _groupsMenu.Items.Add(item);
        }
        _groupsMenu.Items.Add(new ToolStripSeparator());
        _groupsMenu.Items.Add("Manage groups…", null, (_, _) => ShowSettingsDialog("Groups"));
        if (anchor is null) _groupsMenu.Show(Cursor.Position);
        else _groupsMenu.Show(anchor, new Point(0, anchor.Height));
    }

    private async Task StartSelectedAsync()
    {
        var receivers = _controller.Receivers.Where(item => _selectedReceiverIds.Contains(item.Id)).ToArray();
        if (receivers.Length == 0) { AppendConversation("System", "Select at least one available speaker."); return; }
        var unavailable = receivers.FirstOrDefault(item => !item.CanConnect);
        if (unavailable is not null) { ShowConnectionIssue(unavailable); return; }
        foreach (var receiver in receivers)
            if (!await EnsureReceiverPairedAsync(receiver)) return;
        await RunUiActionAsync(async () =>
        {
            var startVolumes = receivers.ToDictionary(
                receiver => receiver.Id,
                receiver => _receiverVolumes.TryGetValue(receiver.Id, out var volume) ? volume : ReceiverVolumePlan.SafeDefault,
                StringComparer.Ordinal);
            if (_sourceMode.SelectedIndex == 1)
            {
                if (_sessions.SelectedItem is not AudioSessionInfo session) throw new InvalidOperationException("Select an application first.");
                await _controller.StartApplicationAsync(session.ProcessId, receivers, startVolumes);
            }
            else await _controller.StartSystemAsync(receivers, startVolumes);
        });
    }

    private async Task ConnectReceiverAsync(string receiverId)
    {
        var receiver = _controller.Receivers.SingleOrDefault(item => item.Id == receiverId);
        if (receiver is null) return;
        if (!receiver.CanConnect) { ShowConnectionIssue(receiver); SetReceiverSelected(receiverId, false); return; }
        if (!await EnsureReceiverPairedAsync(receiver)) return;
        SetReceiverSelected(receiverId, true);
        await RunUiActionAsync(async () =>
        {
            var volume = _receiverVolumes.TryGetValue(receiverId, out var value) ? value : 30;
            if (_controller.Coordinator.Route.StreamId is null)
            {
                if (_sourceMode.SelectedIndex == 1 && _sessions.SelectedItem is AudioSessionInfo session)
                    await _controller.StartApplicationAsync(session.ProcessId, [receiver], new Dictionary<string, int> { [receiverId] = volume });
                else await _controller.StartSystemAsync([receiver], new Dictionary<string, int> { [receiverId] = volume });
            }
            else await _controller.AddReceiverAsync(receiver, volume);
        });
    }

    private async Task<bool> EnsureReceiverPairedAsync(ReceiverInfo receiver)
    {
        if (!receiver.RequiresPairing) return true;
        try
        {
            await _controller.BeginPairingAsync(receiver.Id);
            using var dialog = new PairingCodeDialog(receiver.Name, _palette);
            var result = Visible ? dialog.ShowDialog(this) : dialog.ShowDialog();
            if (result != DialogResult.OK)
            {
                await _controller.CancelPairingAsync(receiver.Id);
                return false;
            }
            await _controller.PairReceiverAsync(receiver.Id, dialog.PairingCode);
            RebuildReceiverRows(_controller.Receivers);
            return true;
        }
        catch (Exception ex)
        {
            try { await _controller.CancelPairingAsync(receiver.Id); } catch { }
            AppLog.Error("pairing", $"Could not pair receiver {receiver.Id}.", ex);
            MessageBox.Show(this, "AirBridge could not complete pairing. Check the code shown on the TV and try again.",
                "AirBridge pairing", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private async Task<bool> EnsureControlPairedAsync(ReceiverInfo receiver)
    {
        if (!receiver.RequiresControlPairing) return true;
        try
        {
            await _controller.BeginPairingAsync(receiver.Id, controlPairing: true);
            using var dialog = new PairingCodeDialog(receiver.Name, _palette);
            var result = Visible ? dialog.ShowDialog(this) : dialog.ShowDialog();
            if (result != DialogResult.OK)
            {
                await _controller.CancelPairingAsync(receiver.Id);
                return false;
            }
            await _controller.PairReceiverAsync(receiver.Id, dialog.PairingCode);
            RebuildReceiverRows(_controller.Receivers);
            return true;
        }
        catch (Exception ex)
        {
            try { await _controller.CancelPairingAsync(receiver.Id); } catch { }
            AppLog.Error("control-pairing", $"Could not pair Apple TV control for receiver {receiver.Id}.", ex);
            MessageBox.Show(this, "AirBridge could not authorize Apple TV controls. Check the code shown on the TV and try again.",
                "AirBridge pairing", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private async Task SleepAppleTvAsync(string receiverId)
    {
        var receiver = _controller.Receivers.SingleOrDefault(item => item.Id == receiverId);
        if (receiver is null || !receiver.SupportsPowerControl) return;
        var confirmation = MessageBox.Show(this,
            $"Put {receiver.Name} to sleep?\n\nAirBridge will stop streaming to it. HDMI-CEC may also turn off the connected TV or receiver.",
            "Sleep Apple TV", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes) return;
        if (!await EnsureControlPairedAsync(receiver)) return;
        await RunUiActionAsync(async () =>
        {
            if (_controller.ReceiverPlayback.Any(item => item.Receiver.Id == receiverId))
                await _controller.StopReceiverAsync(receiverId);
            await _controller.SleepReceiverAsync(receiverId);
            SetReceiverSelected(receiverId, false);
        });
    }

    private void SetReceiverSelected(string receiverId, bool selected)
    {
        var receiver = _controller.Receivers.SingleOrDefault(item => item.Id == receiverId);
        if (selected && receiver is { CanConnect: false })
        {
            if (_receiverRows.TryGetValue(receiverId, out var unavailableRow)) unavailableRow.SetSelected(false);
            _trayFlyout.UpdateReceiver(receiverId, GetReceiverState(receiverId), selected: false);
            ShowConnectionIssue(receiver);
            return;
        }
        if (selected) _selectedReceiverIds.Add(receiverId); else _selectedReceiverIds.Remove(receiverId);
        if (_receiverRows.TryGetValue(receiverId, out var row)) row.SetSelected(selected);
        _trayFlyout.UpdateReceiver(receiverId, GetReceiverState(receiverId), selected: selected);
        UpdateDashboardPresentation(IsDashboardStreamActive);
        PersistUiSettings();
        RefreshGroups();
    }

    private async Task ChangeReceiverVolumeAsync(string receiverId, int volume)
    {
        _receiverVolumes[receiverId] = volume;
        if (_receiverRows.TryGetValue(receiverId, out var row)) row.SetVolume(volume);
        _trayFlyout.UpdateReceiver(receiverId, GetReceiverState(receiverId), volume: volume);
        PersistUiSettings();
        if (_controller.ReceiverPlayback.Any(item => item.Receiver.Id == receiverId))
            await RunUiActionAsync(() => _controller.SetReceiverVolumeAsync(receiverId, volume));
    }

    private void ApplySelectedGroup()
    {
        if (_groups.SelectedItem is not SpeakerGroup group) return;
        _selectedReceiverIds.Clear();
        var connectableIds = _controller.Receivers.Where(item => item.CanConnect).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in group.ReceiverIds.Where(connectableIds.Contains)) _selectedReceiverIds.Add(id);
        foreach (var pair in _receiverRows) pair.Value.SetSelected(_selectedReceiverIds.Contains(pair.Key));
        foreach (var receiver in _controller.Receivers) _trayFlyout.UpdateReceiver(receiver.Id, GetReceiverState(receiver.Id), selected: _selectedReceiverIds.Contains(receiver.Id));
        UpdateDashboardPresentation(IsDashboardStreamActive);
        PersistUiSettings();
        RefreshGroups();
    }

    private void ShowConnectionIssue(ReceiverInfo receiver)
    {
        AppLog.Warning("receiver", $"Receiver {receiver.Id} is not connectable with its advertised AirPlay access control.");
        MessageBox.Show(this,
            $"{receiver.Name} is set to allow AirPlay for Current User only. A Windows sender cannot use that Apple Account-only mode.\n\nOn the Mac, open System Settings → General → AirDrop & Continuity, then change Allow AirPlay for to Anyone on the Same Network. Refresh AirBridge afterward.",
            "Mac AirPlay access", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void RebuildTrayMenu()
    {
        _trayMenu.Items.Clear();
        var route = _controller.Coordinator.Route;
        var summary = new ToolStripMenuItem(route.StreamId is null ? "AirBridge — idle" : $"● {route.State}: {route.ReceiverName}") { Enabled = false };
        _trayMenu.Items.Add(summary);
        _trayMenu.Items.Add(new ToolStripSeparator());
        if (route.StreamId is null) _trayMenu.Items.Add("Start selected speakers", null, async (_, _) => await StartSelectedAsync());
        else _trayMenu.Items.Add("Stop all speakers", null, async (_, _) => await RunUiActionAsync(() => _controller.StopAsync()));
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Settings", null, (_, _) => ShowSettingsDialog());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Quit", null, (_, _) => RequestQuit());
    }

    private void UpdateTelemetry()
    {
        if (IsDisposed) return;
        var route = _controller.Coordinator.Route;
        var health = _controller.Coordinator.Health();
        var elapsed = route.StartedUtc is { } headerStarted ? $" · {FormatDuration(DateTimeOffset.UtcNow - headerStarted)}" : string.Empty;
        var sourceLabel = route.Mode == CaptureMode.ProcessTreeInclude ? "app audio" : "system audio";
        var active = route.StreamId is not null;
        var activeSpeakerCount = _controller.ReceiverPlayback.Count(item => item.State is not StreamState.Idle and not StreamState.Failed);
        _state.Text = !active
            ? "Ready to stream"
            : _controller.IsResumingFromStandby
                ? $"Resuming to {Math.Max(1, activeSpeakerCount)} speaker{(activeSpeakerCount == 1 ? "" : "s")} · 44.1 kHz"
                : $"{route.State} to {Math.Max(1, activeSpeakerCount)} speaker{(activeSpeakerCount == 1 ? "" : "s")} · 44.1 kHz";
        _state.ForeColor = _palette.StateColor(route.State);
        _state.BackColor = _palette.Window;
        _route.Text = route.StreamId is null ? "Route: Idle" : $"Route: {route.ReceiverName}";
        _duration.Text = route.StartedUtc is { } started ? $"Time: {FormatDuration(DateTimeOffset.UtcNow - started)}" : "Time: —";
        _format.Text = "Format: 44.1k";
        _latency.Text = _controller.LastGroupAlignment is { } aligned
            ? $"Align: {string.Join(" · ", aligned.Measurements.Select(item => $"{item.ReceiverName} {item.MedianMilliseconds}ms → +{aligned.ProposedTrimMilliseconds[item.ReceiverId]}ms"))}"
            : _controller.LastAcousticDelay is { } measured ? $"Delay: {measured.MedianMilliseconds}ms" : $"Delay: ≈{_settings.EstimatedAudioDelayMilliseconds}ms";
        _buffer.Text = $"Health · {health.Buffer.FillPercent}% buffer · {health.Buffer.Underruns} underruns · {health.Buffer.Overruns} overruns · {health.Buffer.StarvedWhileActivePaddingMilliseconds} ms starved";
        var playback = _controller.ReceiverPlayback;
        _transport.Text = playback.Count == 0 ? "Speakers: 0" : $"Speakers: {playback.Count(item => item.State == StreamState.Streaming)}/{playback.Count}";
        UpdateMetricsSummary();
        _recommendation.Text = route.State switch
        {
            StreamState.Degraded => "Recommended: open the affected speaker row and retry it; healthy speakers will keep playing.",
            StreamState.Failed => "Recommended: refresh discovery, confirm the network is Private, then retry the receiver.",
            StreamState.Streaming when health.Buffer.StarvedWhileActivePaddingBytes > 0 => "Recommended: use Analyze stream; the active producer has starved the receiver buffer.",
            StreamState.Streaming => "All selected speakers are healthy.",
            StreamState.Standby => "Standby — capture stays ready and streaming will resume at the live edge when audio returns.",
            _ => "Select one or more speakers, then start system audio or an application."
        };

        foreach (var receiver in _controller.Receivers)
        {
            var value = playback.FirstOrDefault(item => item.Receiver.Id == receiver.Id);
            var state = value?.State ?? StreamState.Idle;
            var volume = value?.Volume ?? (_receiverVolumes.TryGetValue(receiver.Id, out var saved) ? saved : 30);
            var trim = value?.AlignmentTrimMilliseconds ?? _controller.GetReceiverAlignmentTrim(receiver.Id);
            if (_receiverRows.TryGetValue(receiver.Id, out var row)) { row.SetPlaybackState(state, value?.LastError); row.SetDashboardStreamActive(active); row.SetVolume(volume); row.SetAlignmentTrim(trim); }
            _trayFlyout.UpdateReceiver(receiver.Id, state, volume, _selectedReceiverIds.Contains(receiver.Id), value?.LastError, trim);
        }
        var traySource = route.Mode == CaptureMode.ProcessTreeInclude ? "Application audio" : "System audio";
        var trayDuration = route.StartedUtc is { } trayStarted ? $" · {FormatDuration(DateTimeOffset.UtcNow - trayStarted)}" : string.Empty;
        var traySpeakerCount = $"{playback.Count} speaker{(playback.Count == 1 ? "" : "s")}";
        var traySummary = route.StreamId is null
            ? "Not streaming"
            : route.State == StreamState.Standby
                ? $"Standby · {traySpeakerCount} will auto-resume{trayDuration}"
                : _controller.IsResumingFromStandby
                    ? $"Resuming {traySpeakerCount} at the live edge{trayDuration}"
                    : route.State == StreamState.Failed
                        ? $"Connection failed · {traySpeakerCount}{trayDuration}"
                        : $"{route.State} to {traySpeakerCount}{trayDuration}";
        _trayFlyout.UpdateStatus(route.State, traySummary, route.StreamId is null ? traySource : string.Empty);
        _tray.Text = route.StreamId is null ? "AirBridge — idle" : TruncateTooltip($"AirBridge — {route.State} · {route.ReceiverName}");
        ReplaceTrayIcon(route.State);
        _lastDisplayedState = route.State;
        UpdateDashboardPresentation(active);
    }

    private async Task AskAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        AppendConversation("You", text);
        _command.Clear();
        if (_agent is null) { AppendConversation("AirBridge", "GPT-5.6 is disabled because an OpenAI API key is not configured."); return; }
        await RunUiActionAsync(async () =>
        {
            var diagnostic = text.Contains("why", StringComparison.OrdinalIgnoreCase) || text.Contains("fix", StringComparison.OrdinalIgnoreCase) || text.Contains("analy", StringComparison.OrdinalIgnoreCase);
            AppendConversation("AirBridge", await _agent.AskAsync(text, diagnostic, cancellationToken));
        });
    }

    private bool StartRecording(bool holdHint)
    {
        if (_transcriptionCancellation is not null) return false;
        try
        {
            _recorder.Start();
            _pushToTalk.Text = "Listening…";
            if (!_previewMode) _voiceHud.ShowListening(() => _recorder.PeakLevel, holdHint);
            return true;
        }
        catch (Exception ex)
        {
            AppendConversation("System", $"Microphone unavailable: {ex.Message}");
            if (!_previewMode) _voiceHud.ShowError(ex.Message);
            return false;
        }
    }

    private async Task StopRecordingAndAskAsync()
    {
        if (!_recorder.IsRecording) return;
        StopHotkeyPolling();
        _pushToTalk.Text = "Transcribing…";
        var wav = _recorder.Stop();
        var apiKey = ResolveOpenAiApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _pushToTalk.Text = "Hold to talk";
            AppendConversation("System", "An OpenAI API key is required for transcription.");
            if (!_previewMode) _voiceHud.ShowError("An OpenAI API key is required.");
            return;
        }

        _transcriptionCancellation = new CancellationTokenSource();
        var cancellation = _transcriptionCancellation;
        if (!_previewMode) _voiceHud.ShowTranscribing();
        try
        {
            var text = await PushToTalkRecorder.TranscribeAsync(wav, apiKey, cancellation.Token, _activityStore);
            if (!_previewMode) _voiceHud.ShowThinking();
            await AskAsync(text, cancellation.Token);
            if (!_previewMode) _voiceHud.HideHud();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_previewMode) _voiceHud.HideHud();
        }
        catch (Exception ex)
        {
            AppLog.Error("push-to-talk", "Voice transcription failed.", ex);
            AppendConversation("System", ex.Message);
            if (!_previewMode) _voiceHud.ShowError(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_transcriptionCancellation, cancellation)) _transcriptionCancellation = null;
            cancellation.Dispose();
            _pushToTalk.Text = "Hold to talk";
        }
    }

    private void BeginHotkeyGesture()
    {
        if (_recorder.IsRecording)
        {
            _ = StopRecordingAndAskAsync();
            return;
        }
        if (!StartRecording(true)) return;
        _hotkeyPressedAt = Environment.TickCount64;
        _hotkeyWaitingForRelease = true;
        _hotkeyPollTimer.Start();
    }

    private void PollHotkeyGesture()
    {
        if (!_recorder.IsRecording)
        {
            StopHotkeyPolling();
            return;
        }

        // Escape is observed, not swallowed; the foreground application still receives it.
        if ((GetAsyncKeyState((int)Keys.Escape) & 0x8000) != 0)
        {
            CancelVoiceCommand();
            return;
        }
        if (!_hotkeyWaitingForRelease || (GetAsyncKeyState(_hotkeyGesture.VirtualKey) & 0x8000) != 0) return;

        _hotkeyWaitingForRelease = false;
        var elapsed = Environment.TickCount64 - _hotkeyPressedAt;
        if (elapsed < _settings.PushToTalkHoldThresholdMs)
        {
            _voiceHud.SetToggleHint();
            return;
        }
        _ = StopRecordingAndAskAsync();
    }

    private void StopHotkeyPolling()
    {
        _hotkeyPollTimer.Stop();
        _hotkeyWaitingForRelease = false;
    }

    private void CancelVoiceCommand()
    {
        StopHotkeyPolling();
        if (_recorder.IsRecording) _recorder.Cancel();
        _transcriptionCancellation?.Cancel();
        _pushToTalk.Text = "Hold to talk";
        if (!_previewMode) _voiceHud.HideHud();
    }

    private async Task MeasureSelectedDelayAsync()
    {
        var active = _controller.ReceiverPlayback.Where(item => _selectedReceiverIds.Contains(item.Receiver.Id)).ToArray();
        if (active.Length != 1)
        {
            AppendConversation("System", "Select exactly one currently streaming speaker before measuring delay.");
            return;
        }
        var microphone = _settings.CalibrationMicrophoneName ?? AcousticDelayMeasurer.GetAvailableMicrophones().FirstOrDefault()?.Name ?? "the default recording device";
        AppendConversation("System", $"Measuring {active[0].Receiver.Name} with {microphone}; five short chirps will play and microphone audio stays in memory.");
        await RunUiActionAsync(async () =>
        {
            var result = await _controller.MeasureAcousticDelayAsync(active[0].Receiver.Id);
            _settings = _settings with { EstimatedAudioDelayMilliseconds = result.MedianMilliseconds };
            _settingsStore.Save(_settings);
            _latency.Text = $"Delay: {result.MedianMilliseconds}ms";
            UpdateMetricsSummary();
            _recommendation.Text = $"Measured {result.MedianMilliseconds} ms. Use this value in the browser extension.";
            AppendConversation("Delay", $"Median {result.MedianMilliseconds} ms ({string.Join(", ", result.DelaysMilliseconds)} ms). Use this value in the browser extension.");
            MessageBox.Show($"Measured delay for {active[0].Receiver.Name}: {result.MedianMilliseconds} ms.\n\nUse this value in the browser extension.", "AirBridge delay measurement", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    private async Task AlignSelectedGroupAsync()
    {
        var active = _controller.ReceiverPlayback.Where(item => _selectedReceiverIds.Contains(item.Receiver.Id)).ToArray();
        if (active.Length < 2)
        {
            AppendConversation("System", "Select at least two currently streaming speakers before aligning the group.");
            return;
        }
        var microphone = _settings.CalibrationMicrophoneName ?? AcousticDelayMeasurer.GetAvailableMicrophones().FirstOrDefault()?.Name ?? "the default recording device";
        AppendConversation("System", $"Measuring speakers one at a time with {microphone}; non-target speakers will be temporarily muted and all volumes will be restored.");
        await RunUiActionAsync(async () =>
        {
            var result = await _controller.AlignGroupAsync(active.Select(item => item.Receiver.Id).ToArray(), apply: false);
            var names = active.ToDictionary(item => item.Receiver.Id, item => item.Receiver.Name, StringComparer.Ordinal);
            var medians = string.Join(Environment.NewLine, result.Measurements.Select(item => $"{item.ReceiverName}: {item.MedianMilliseconds} ms"));
            var proposal = string.Join(Environment.NewLine, result.ProposedTrimMilliseconds.Select(pair => $"{names[pair.Key]}: +{pair.Value} ms"));
            var skew = string.Join(", ", result.PairwiseSkews.Select(pair => $"{pair.SkewMilliseconds} ms"));
            AppendConversation("Alignment", $"Measured medians: {medians.Replace(Environment.NewLine, "; ")}. Pairwise skew: {skew}. Proposed trims: {proposal.Replace(Environment.NewLine, "; ")}");
            if (MessageBox.Show(this, $"Measured receiver medians:\n{medians}\n\nPairwise skew: {skew}\n\nApply these trims?\n{proposal}", "Align speaker group", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _controller.ApplyGroupAlignment(result);
            AppendConversation("Alignment", "Applied the proposed per-speaker trims. Re-run alignment after a group restart or receiver move.");
        });
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        try { UseWaitCursor = true; await action(); }
        catch (Exception ex) { AppLog.Error("ui-action", "UI action failed.", ex); AppendConversation("System", ex.Message); }
        finally { UseWaitCursor = false; UpdateTelemetry(); }
    }

    private void PersistUiSettings()
    {
        try
        {
            _settings = _settings with
            {
                SelectedReceiverIds = _selectedReceiverIds.ToArray(),
                ReceiverVolumes = new(_receiverVolumes, StringComparer.Ordinal),
                DefaultCaptureMode = _sourceMode.SelectedIndex == 1 ? CaptureMode.ProcessTreeInclude : CaptureMode.SystemMix
            };
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) { AppLog.Error("settings", "Could not save settings.", ex); AppendConversation("System", $"Could not save settings: {ex.Message}"); }
    }

    private void PersistVoiceHudPosition()
    {
        if (_previewMode) return;
        try
        {
            _settings = _settings with { VoiceHudX = _voiceHud.Left, VoiceHudY = _voiceHud.Top };
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            AppLog.Error("settings", "Could not save the voice HUD position.", ex);
            AppendConversation("System", $"Could not save the voice HUD position: {ex.Message}");
        }
    }

    private void PersistAlignmentTrim(string receiverId, int milliseconds)
    {
        try
        {
            var trims = new Dictionary<string, int>(_settings.ReceiverAlignmentTrimMs, StringComparer.Ordinal)
            {
                [receiverId] = milliseconds
            };
            _settings = _settings with { ReceiverAlignmentTrimMs = trims };
            _settingsStore.Save(_settings);
            UpdateTelemetry();
        }
        catch (Exception ex) { AppLog.Error("settings", "Could not save speaker alignment.", ex); AppendConversation("System", $"Could not save speaker alignment: {ex.Message}"); }
    }

    private void PersistSilenceStandby(bool enabled, int seconds)
    {
        try
        {
            _settings = _settings with { SilenceStandbyEnabled = enabled, SilenceStandbySeconds = seconds };
            _settingsStore.Save(_settings);
            UpdateTelemetry();
        }
        catch (Exception ex) { AppLog.Error("settings", "Could not save silence standby.", ex); AppendConversation("System", $"Could not save silence standby: {ex.Message}"); }
    }

    private ToolStripMenuItem BuildSilenceStandbyMenu()
    {
        var menu = new ToolStripMenuItem("Silence standby");
        foreach (var seconds in new[] { 10, 30, 60, 120, 300, 600 })
        {
            var item = new ToolStripMenuItem($"After {seconds} seconds") { Checked = _settings.SilenceStandbyEnabled && _settings.SilenceStandbySeconds == seconds };
            item.Click += (_, _) => _controller.SetSilenceStandbySettings(true, seconds);
            menu.DropDownItems.Add(item);
        }
        var off = new ToolStripMenuItem("Off") { Checked = !_settings.SilenceStandbyEnabled };
        off.Click += (_, _) => _controller.SetSilenceStandbySettings(false, _settings.SilenceStandbySeconds);
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(off);
        menu.DropDownOpening += (_, _) =>
        {
            var current = _controller.GetSilenceStandbySettings();
            foreach (ToolStripItem child in menu.DropDownItems)
                if (child is ToolStripMenuItem choice)
                    choice.Checked = choice.Text == "Off" ? !current.Enabled : current.Enabled && choice.Text == $"After {current.Seconds} seconds";
        };
        return menu;
    }

    private void ApplyTheme()
    {
        _palette.Apply(this);
        _root.BackColor = _palette.Window;
        ApplyThemedControls(_root);
        _trayFlyout.ApplyTheme(_palette);
        _voiceHud.ApplyTheme(_palette);
        _activityInspector?.ApplyTheme(_palette);
        foreach (var row in _receiverRows.Values) row.ApplyTheme(_palette);
        _conversation.BackColor = _palette.Surface;
        _conversation.ForeColor = _palette.Text;
        _command.BackColor = _palette.Surface;
        _command.ForeColor = _palette.Text;
        _sourceCaption.ForeColor = _palette.IsHighContrast ? SystemColors.WindowText : _palette.SecondaryText;
        _idleTelemetryHint.ForeColor = _palette.IsHighContrast ? SystemColors.WindowText : _palette.SecondaryText;
    }

    private void ShowSettingsDialog(string? initialTab = null)
    {
        _trayFlyout.Hide();
        var environmentApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        bool storedApiKeyConfigured;
        try { storedApiKeyConfigured = _openAiCredentials.IsConfigured; }
        catch (Exception ex)
        {
            AppLog.Error("credentials", "Could not check OpenAI credential status.", ex);
            MessageBox.Show(this, ex.Message, "AirBridge settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        using var dialog = new SettingsForm(_settings, _palette, storedApiKeyConfigured, !string.IsNullOrWhiteSpace(environmentApiKey), _controller.Receivers, initialTab);
        dialog.OpenLogsRequested += (_, _) => OpenLogsFolder();
        dialog.OpenActivityInspectorRequested += (_, _) => ShowActivityInspector();
        dialog.SaveRequested += (_, _) => ApplySettings(dialog);
        if (Visible) dialog.StartPosition = FormStartPosition.CenterParent;
        if (Visible) dialog.ShowDialog(this); else dialog.ShowDialog();
    }

    private void OnAgentActivityPublished(AgentActivityEvent activity)
    {
        if (activity.Kind != AgentActivityKind.Error) return;
        SafeBeginInvoke(() =>
        {
            var message = string.IsNullOrWhiteSpace(activity.Details) ? activity.Summary : activity.Details;
            var title = activity.Title switch
            {
                "align_group" => "Speaker alignment failed",
                "measure_acoustic_delay" => "Delay measurement failed",
                _ => activity.Title
            };
            AppendConversation("System", $"{title}: {message}");
            _recommendation.Text = $"{title}: {message}";
            if (!_previewMode && _tray.Visible)
                _tray.ShowBalloonTip(8000, title, message, ToolTipIcon.Error);
        });
    }

    private Task<bool> ConfirmAgentToolAsync(string toolName, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void ShowPrompt()
        {
            if (cancellationToken.IsCancellationRequested || IsDisposed)
            {
                completion.TrySetResult(false);
                return;
            }
            var (title, message) = toolName switch
            {
                "align_group" => ("Allow speaker alignment?",
                    "AirBridge will play calibration chirps, briefly mute non-target speakers, capture the selected microphone in memory, and apply bounded timing trims. Microphone audio is discarded locally."),
                "measure_acoustic_delay" => ("Allow delay measurement?",
                    "AirBridge will play five calibration chirps and capture the selected microphone in memory. Microphone audio is discarded locally."),
                "save_routing_rule" => ("Save routing rule?", "Allow AirBridge to save this routing rule?"),
                "change_startup_behavior" => ("Change startup behavior?", "Allow AirBridge to change its startup behavior?"),
                "enable_microphone_calibration" => ("Enable microphone calibration?", "Allow AirBridge to enable microphone calibration?"),
                _ => ("Allow AirBridge action?", $"Allow the requested {toolName.Replace('_', ' ')} action once?")
            };
            CompleteFromHudAsync();

            async void CompleteFromHudAsync()
            {
                try { completion.TrySetResult(await _voiceHud.ShowConfirmation(title, message, cancellationToken)); }
                catch (Exception ex)
                {
                    AppLog.Error("agent-confirmation", "Could not show the confirmation prompt.", ex);
                    completion.TrySetResult(false);
                }
            }
        }

        if (InvokeRequired) BeginInvoke(ShowPrompt); else ShowPrompt();
        return completion.Task;
    }

    private void ApplySettings(SettingsForm dialog)
    {
        try
        {
            if (dialog.ReplacementApiKey is { } replacement) _openAiCredentials.Write(replacement);
            else if (dialog.RemoveApiKeyRequested) _openAiCredentials.Delete();
        }
        catch (Exception ex)
        {
            AppLog.Error("credentials", "Could not update the saved OpenAI credential.", ex);
            MessageBox.Show(this, ex.Message, "AirBridge settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var alignmentTrims = new Dictionary<string, int>(_settings.ReceiverAlignmentTrimMs, StringComparer.Ordinal);
        foreach (var pair in dialog.ReceiverAlignmentTrims) alignmentTrims[pair.Key] = pair.Value;
        var next = _settings with
        {
            ThemeMode = dialog.ThemeMode.ToString().ToLowerInvariant(),
            DefaultCaptureMode = dialog.DefaultCaptureMode,
            SilenceStandbyEnabled = dialog.SilenceStandbyEnabled,
            SilenceStandbySeconds = dialog.SilenceStandbySeconds,
            PushToTalkShortcut = dialog.PushToTalkShortcut,
            PushToTalkHoldThresholdMs = dialog.PushToTalkHoldThresholdMs,
            CalibrationMicrophoneName = dialog.CalibrationMicrophoneName,
            ReceiverAlignmentTrimMs = alignmentTrims,
            SpeakerGroups = dialog.SpeakerGroups,
            AiEnabled = dialog.AiEnabled
        };
        var previousGesture = _hotkeyGesture;
        var candidateGesture = HotkeyGesture.TryParse(next.PushToTalkShortcut, out var parsedGesture) ? parsedGesture : HotkeyGesture.Default;
        if (!_previewMode)
        {
            UnregisterHotKey(Handle, HotkeyId);
            var (modifiers, virtualKey) = candidateGesture.ToRegisterHotKeyArgs();
            if (RegisterHotKey(Handle, HotkeyId, modifiers, virtualKey))
            {
                _hotkeyGesture = candidateGesture;
            }
            else
            {
                var (previousModifiers, previousVirtualKey) = previousGesture.ToRegisterHotKeyArgs();
                var restored = RegisterHotKey(Handle, HotkeyId, previousModifiers, previousVirtualKey);
                next = next with { PushToTalkShortcut = previousGesture.ToString() };
                AppendConversation("System", $"Could not use push-to-talk shortcut {candidateGesture} because another app is using it. The previous shortcut was restored{(restored ? "." : ", but Windows could not re-register it.")}");
            }
        }
        else
        {
            _hotkeyGesture = candidateGesture;
        }
        _settings = next;
        _settingsStore.Save(_settings);
        _controller.ConfigureSettings(_settings);
        RefreshGroups();
        _sourceMode.SelectedIndex = _settings.DefaultCaptureMode == CaptureMode.ProcessTreeInclude ? 1 : 0;

        var themeMode = ParseTheme(_settings.ThemeMode);
        _palette = ThemePalette.Current(themeMode);
        _trayFlyout.SetThemeMode(themeMode);
        ApplyTheme();

        var apiKey = ResolveOpenAiApiKey();
        _agent = _settings.AiEnabled && !string.IsNullOrWhiteSpace(apiKey)
            ? new OpenAiAgent(apiKey, new AgentPolicy(), _controller, activity: _activityStore,
                confirmationStore: _agentConfirmations, confirmationPrompt: ConfirmAgentToolAsync)
            : null;
        UpdateTelemetry();
        dialog.MarkSaved();
    }

    private string? ResolveOpenAiApiKey()
    {
        var environmentApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        return !string.IsNullOrWhiteSpace(environmentApiKey) ? environmentApiKey.Trim() : _openAiCredentials.Read();
    }

    private void ToggleTrayFlyout()
    {
        if (_trayFlyout.Visible) _trayFlyout.Hide(); else _trayFlyout.ShowNearTrayIcon();
    }

    private static void OpenLogsFolder()
    {
        Directory.CreateDirectory(AppLog.DirectoryPath);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppLog.DirectoryPath}\"")
        {
            UseShellExecute = true
        });
    }

    private void ShowActivityInspector()
    {
        if (_activityInspector is { IsDisposed: false })
        {
            if (_activityInspector.WindowState == FormWindowState.Minimized) _activityInspector.WindowState = FormWindowState.Normal;
            _activityInspector.Show();
            _activityInspector.BringToFront();
            _activityInspector.Activate();
            return;
        }
        _activityInspector = new ActivityInspectorForm(_activityStore, _palette);
        _activityInspector.FormClosed += (_, _) => _activityInspector = null;
        _activityInspector.Show();
    }

    private void ReplaceTrayIcon(StreamState state)
    {
        if (_trayIconState == state && _ownedTrayIcon is not null) return;
        var next = TrayIconFactory.Create(state, palette: _palette);
        var old = _ownedTrayIcon;
        _ownedTrayIcon = next;
        _tray.Icon = next;
        _trayIconState = state;
        old?.Dispose();
    }

    private StreamState GetReceiverState(string receiverId) => _controller.ReceiverPlayback.FirstOrDefault(item => item.Receiver.Id == receiverId)?.State ?? StreamState.Idle;
    private void AppendConversation(string speaker, string text)
    {
        _conversation.Visible = true;
        _conversation.SelectionStart = _conversation.TextLength;
        _conversation.SelectionLength = 0;
        using var speakerFont = UiGeometry.UiFont(8.5F, FontStyle.Bold);
        using var messageFont = UiGeometry.UiFont(9F);
        _conversation.SelectionFont = speakerFont;
        _conversation.SelectionColor = speaker == "You" ? _palette.Accent : _palette.SecondaryText;
        _conversation.AppendText(speaker.ToUpperInvariant() + Environment.NewLine);
        _conversation.SelectionFont = messageFont;
        _conversation.SelectionColor = _palette.Text;
        _conversation.AppendText(text + Environment.NewLine);
        _conversation.ScrollToCaret();
    }
    private void ApplyInitialPreviewBounds()
    {
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var margin = UiGeometry.Scale(this, 24);
        var availableWidth = Math.Max(1, workingArea.Width - margin * 2);
        var availableHeight = Math.Max(1, workingArea.Height - margin * 2);
        var width = Math.Min(UiGeometry.Scale(this, 960), availableWidth);
        var height = Math.Min(UiGeometry.Scale(this, 900), availableHeight);
        MinimumSize = new Size(
            Math.Min(UiGeometry.Scale(this, 720), width),
            Math.Min(UiGeometry.Scale(this, 650), height));
        Bounds = new Rectangle(
            workingArea.Left + (workingArea.Width - width) / 2,
            workingArea.Top + (workingArea.Height - height) / 2,
            width,
            height);
    }
    private void SafeBeginInvoke(Action action) { if (!IsDisposed && IsHandleCreated) BeginInvoke(action); }

    private static Control Card(Control content, Padding margin)
    {
        var panel = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = margin, AutoSize = content.AutoSize, Radius = 12, Tag = "card" };
        content.Dock = DockStyle.Fill;
        panel.Controls.Add(content);
        return panel;
    }

    private static Control Metric(string title, Label value)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 6, 2) };
        value.AutoSize = false;
        value.Dock = DockStyle.Fill;
        value.AutoEllipsis = true;
        value.Font = UiGeometry.UiFont(8.5F);
        value.TextAlign = ContentAlignment.MiddleCenter;
        value.AccessibleName = title;
        panel.Controls.Add(value);
        return panel;
    }

    private void UpdateMetricsSummary()
    {
        _metricsSummary.Text = string.Join("   ·   ", _route.Text, _duration.Text, _format.Text, _latency.Text, _transport.Text);
        _metricsSummary.AccessibleDescription = _metricsSummary.Text;
    }

    private bool IsDashboardStreamActive => _previewMode ? _previewStreamActive : _controller.Coordinator.Route.StreamId is not null;

    private void UpdateDashboardPresentation(bool streamActive)
    {
        var selectedCount = _receiverRows.Count == 0
            ? _selectedReceiverIds.Count
            : _receiverRows.Count(pair => _selectedReceiverIds.Contains(pair.Key));
        _startButton.Text = selectedCount == 0
            ? "Select a speaker"
            : $"Stream to {selectedCount} speaker{(selectedCount == 1 ? "" : "s")}";
        _startButton.AccessibleName = _startButton.Text;
        _startButton.Enabled = selectedCount > 0 && !streamActive;
        _startButton.Visible = !streamActive;
        _stopButton.Visible = streamActive;
        if (_telemetryDetails is not null) _telemetryDetails.Visible = streamActive;
        _idleTelemetryHint.Visible = !streamActive;
        if (streamActive) _telemetryDetails?.BringToFront(); else _idleTelemetryHint.BringToFront();
        foreach (var row in _receiverRows.Values) row.SetDashboardStreamActive(streamActive);
    }

    private PillButton PrimaryButton(string text) => new() { Text = text, Width = 104, Height = 36, Primary = true, AccessibleName = text };
    private PillButton SecondaryButton(string text) => new() { Text = text, Width = 92, Height = 34, AccessibleName = text };
    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
    private static string TruncateTooltip(string text) => text.Length <= 63 ? text : text[..60] + "…";
    private static AppThemeMode ParseTheme(string value) => Enum.TryParse<AppThemeMode>(value, true, out var parsed) ? parsed : AppThemeMode.System;

    private void ApplyThemedControls(Control root)
    {
        if (root is IThemeAware themed) themed.ApplyTheme(_palette);
        foreach (Control child in root.Controls) ApplyThemedControls(child);
    }

    private void PopulatePreviewData(bool streaming)
    {
        var now = DateTimeOffset.UtcNow;
        var preview = new[]
        {
            new ReceiverInfo("preview-a", "Desk Speaker", "local-network-receiver", false, now),
            new ReceiverInfo("preview-b", "Media Room", "local-network-receiver", false, now),
            new ReceiverInfo("preview-c", "Upstairs Speaker", "local-network-receiver", false, now)
        };
        _selectedReceiverIds.Add(preview[0].Id);
        if (streaming) _selectedReceiverIds.Add(preview[1].Id);
        _receiverVolumes[preview[0].Id] = 30;
        _receiverVolumes[preview[1].Id] = 42;
        _receiverVolumes[preview[2].Id] = 55;
        RebuildReceiverRows(preview);
        if (streaming)
        {
            _receiverRows[preview[0].Id].SetPlaybackState(StreamState.Streaming);
            _receiverRows[preview[0].Id].SetAlignmentTrim(60);
            _receiverRows[preview[1].Id].SetPlaybackState(StreamState.Reconnecting, "Retrying");
            _trayFlyout.UpdateReceiver(preview[0].Id, StreamState.Streaming, 30, true, alignmentTrimMilliseconds: 60);
            _trayFlyout.UpdateReceiver(preview[1].Id, StreamState.Reconnecting, 42, true, "Retrying");
            _trayFlyout.UpdateStatus(StreamState.Degraded, "Streaming to 2 speakers · 12:48", string.Empty);
            _state.Text = "Streaming to 2 speakers · 44.1 kHz";
            _state.ForeColor = _palette.Warning;
            _route.Text = "Route: Desk Speaker +1";
            _duration.Text = "Time: 12:48";
            _transport.Text = "Speakers: 1/2";
            UpdateMetricsSummary();
            _recommendation.Text = "Recommended: retry Media Room; Desk Speaker will keep playing.";
            AppendConversation("AirBridge", "Desk Speaker is still streaming. Retry Media Room when it becomes available.");
        }
        else
        {
            foreach (var row in _receiverRows.Values) row.SetPlaybackState(StreamState.Idle);
            _trayFlyout.UpdateStatus(StreamState.Idle, "Not streaming", "System audio");
            _state.Text = "Ready to stream";
            _state.ForeColor = _palette.Available;
            _conversation.Clear();
            _conversation.Visible = false;
        }
        _trayFlyout.UpdateReceiver(preview[2].Id, StreamState.Idle, 55, false);
        UpdateDashboardPresentation(streaming);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
        {
            BeginHotkeyGesture();
        }
        base.WndProc(ref m);
        if (m.Msg == WmSettingChange && IsHandleCreated && !IsDisposed)
            BeginInvoke(SystemTextScale.Refresh);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs args)
    {
        if (!_allowClose && args.CloseReason == CloseReason.UserClosing) { args.Cancel = true; Hide(); return; }
        if (_previewMode)
        {
            _controller.ForceCleanup();
            DisposeUiResources();
            return;
        }
        if (!_shutdownCompleted)
        {
            args.Cancel = true;
            BeginShutdown();
            return;
        }
        DisposeUiResources();
    }

    private void RequestQuit()
    {
        _allowClose = true;
        Close();
    }

    private void BeginShutdown()
    {
        if (_shutdownStarted) return;
        AppLog.Info("lifecycle", "Quit requested; beginning bounded shutdown.");
        _shutdownStarted = true;
        UnregisterHotKey(Handle, HotkeyId);
        CancelVoiceCommand();
        _timer.Stop();
        _tray.Visible = false;
        _trayFlyout.Hide();
        _voiceHud.HideHud();
        _shutdownWatchdog = new System.Threading.Timer(_ =>
        {
            AppLog.Warning("lifecycle", "Shutdown watchdog expired; forcing cleanup.");
            _controller.ForceCleanup();
            AppLog.Shutdown();
            Environment.Exit(0);
        }, null, TimeSpan.FromSeconds(12), Timeout.InfiniteTimeSpan);
        _ = CompleteShutdownAsync();
    }

    private async Task CompleteShutdownAsync()
    {
        try { await _shutdown.ShutdownAsync(); }
        catch (Exception ex) { AppLog.Error("lifecycle", "Shutdown coordinator failed.", ex); }
        _shutdownCompleted = true;
        if (!IsDisposed) Close();
    }

    private void DisposeUiResources()
    {
        if (_uiResourcesDisposed) return;
        _uiResourcesDisposed = true;
        SystemTextScale.Changed -= OnTextScaleChanged;
        _shutdownWatchdog?.Dispose();
        _shutdownWatchdog = null;
        _timer.Stop();
        _hotkeyPollTimer.Stop();
        _tray.Visible = false;
        _trayFlyout.Close();
        _trayFlyout.Dispose();
        _activityInspector?.Close();
        _activityInspector?.Dispose();
        _activityInspector = null;
        _voiceHud.Close();
        _voiceHud.Dispose();
        _ownedTrayIcon?.Dispose();
        _ownedTrayIcon = null;
        _tray.Dispose();
        _trayMenu.Dispose();
        _groupsMenu.Dispose();
        _diagnosticsMenu.Dispose();
        _receiverLoading.Dispose();
        _recorder.Dispose();
        _hotkeyPollTimer.Dispose();
        _transcriptionCancellation?.Cancel();
        _transcriptionCancellation?.Dispose();
        _transcriptionCancellation = null;
    }

    private void OnTextScaleChanged(object? sender, TextScaleChangedEventArgs args)
    {
        UiGeometry.RescaleText(this, args.Previous, args.Current);
        foreach (var row in _receiverRows.Values) row.ApplyTextScale();
        ResizeReceiverRows();
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
}
