using AirBridge.Core;
using System.Runtime.InteropServices;

namespace AirBridge.App;

internal sealed class SettingsForm : Form
{
    private const int WmSettingChange = 0x001A;
    private const string ControlFrameTag = "AirBridge.ControlFrame";
    private const string SecondaryTextTag = "AirBridge.SecondaryText";
    private const string ErrorTextTag = "AirBridge.ErrorText";
    private sealed record GroupReceiverChoice(string ReceiverId, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record MicrophoneChoice(string? DeviceName, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _capture = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _standby = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _calibrationMicrophone = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _pushToTalkShortcut = new() { Dock = DockStyle.Fill, ReadOnly = true, ShortcutsEnabled = false };
    private readonly Label _pushToTalkError = new() { AutoSize = true };
    private readonly NumericUpDown _holdThreshold = new() { Minimum = 100, Maximum = 1000, Increment = 50, Dock = DockStyle.Left, Width = 110 };
    private readonly CheckBox _aiEnabled = new() { Text = "Enable the AirBridge assistant when an API key is available", AutoSize = true };
    private readonly TextBox _apiKey = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly Button _removeApiKey = new() { Text = "Remove", AutoSize = true };
    private readonly Label _apiKeyStatus = new() { AutoSize = true };
    private readonly FlowLayoutPanel _buttonBar = new();
    private readonly Dictionary<string, NumericUpDown> _alignmentTrims = new(StringComparer.Ordinal);
    private readonly List<SpeakerGroup> _speakerGroups;
    private readonly List<GroupReceiverChoice> _groupReceiverChoices = [];
    private readonly ListBox _speakerGroupList = new() { Dock = DockStyle.Fill, DisplayMember = nameof(SpeakerGroup.Name), IntegralHeight = false };
    private readonly TextBox _speakerGroupName = new() { Dock = DockStyle.Top, PlaceholderText = "Group name" };
    private readonly CheckedListBox _speakerGroupMembers = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private TabControl? _tabs;
    private TabPage? _groupsPage;
    private bool _updatingSpeakerGroup;
    private bool _removeApiKeyRequested;
    private string _shortcutBeforeCapture = HotkeyGesture.Default.ToString();
    private HotkeyGesture _capturedShortcut = HotkeyGesture.Default;

    public SettingsForm(
        AirBridgeSettings settings,
        ThemePalette palette,
        bool storedApiKeyConfigured,
        bool apiKeyManagedByEnvironment,
        IReadOnlyList<ReceiverInfo>? receivers = null,
        string? initialTab = null)
    {
        _speakerGroups = settings.SpeakerGroups.ToList();
        Text = "AirBridge Settings";
        AccessibleName = "AirBridge settings";
        AutoScaleMode = AutoScaleMode.Dpi;
        var textWidthScale = TextWidthScale(SystemTextScale.Current);
        var textHeightScale = TextHeightScale(SystemTextScale.Current);
        ClientSize = new Size((int)Math.Ceiling(700 * textWidthScale), (int)Math.Ceiling(570 * textHeightScale));
        MinimumSize = new Size((int)Math.Ceiling(620 * textWidthScale), (int)Math.Ceiling(500 * textHeightScale));
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = UiGeometry.UiFont(9F);

        InitializeValues(settings, palette, storedApiKeyConfigured, apiKeyManagedByEnvironment);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 7),
            AccessibleName = "Settings categories",
            DrawMode = TabDrawMode.OwnerDrawFixed,
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(112, 32)
        };
        tabs.DrawItem += (_, args) => DrawTab(tabs, args, palette);
        tabs.TabPages.Add(BuildGeneralPage(palette));
        _groupsPage = BuildGroupsPage(receivers ?? [], palette);
        tabs.TabPages.Add(_groupsPage);
        tabs.TabPages.Add(BuildSyncPage(receivers ?? [], settings.ReceiverAlignmentTrimMs, palette));
        tabs.TabPages.Add(BuildAssistantPage(palette));
        tabs.TabPages.Add(BuildAdvancedPage(palette));
        _tabs = tabs;
        if (!string.IsNullOrWhiteSpace(initialTab))
        {
            var requested = tabs.TabPages.Cast<TabPage>().FirstOrDefault(page => page.Text.Equals(initialTab, StringComparison.OrdinalIgnoreCase));
            if (requested is not null) tabs.SelectedTab = requested;
        }

        var save = new Button { Text = "Save", AutoSize = true, Padding = new Padding(14, 4, 14, 4) };
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(14, 4, 14, 4) };
        save.Click += (_, _) =>
        {
            if (!_capturedShortcut.IsValid)
            {
                _pushToTalkShortcut.Focus();
                return;
            }
            if (!ValidateSpeakerGroups()) return;
            SaveRequested?.Invoke(this, EventArgs.Empty);
        };
        AcceptButton = save;
        CancelButton = close;

        _buttonBar.Dock = DockStyle.Bottom;
        _buttonBar.Height = UiGeometry.TextLogical(64);
        _buttonBar.FlowDirection = FlowDirection.RightToLeft;
        _buttonBar.Padding = new Padding(16, 10, 16, 8);
        _buttonBar.WrapContents = false;
        _buttonBar.Controls.AddRange([save, close]);
        Controls.Add(tabs);
        Controls.Add(_buttonBar);
        SystemTextScale.Changed += OnTextScaleChanged;
        UiGeometry.ScaleInitialTextLayout(this);
        palette.Apply(this);
        ApplyColors(this, palette);
        WindowEffects.ApplyTheme(this, palette);
        Shown += (_, _) =>
        {
            WindowEffects.ApplyTheme(this, palette);
            EnsureFitsWorkingArea();
        };
    }

    public event EventHandler? OpenLogsRequested;
    public event EventHandler? OpenActivityInspectorRequested;
    public event EventHandler? SaveRequested;

    public AppThemeMode ThemeMode => _theme.SelectedIndex switch { 1 => AppThemeMode.Light, 2 => AppThemeMode.Dark, _ => AppThemeMode.System };
    public CaptureMode DefaultCaptureMode => _capture.SelectedIndex == 1 ? CaptureMode.ProcessTreeInclude : CaptureMode.SystemMix;
    public bool SilenceStandbyEnabled => _standby.SelectedIndex > 0;
    public int SilenceStandbySeconds => _standby.SelectedIndex switch { 1 => 10, 2 => 30, 3 => 60, 4 => 120, 5 => 300, 6 => 600, _ => 60 };
    public string PushToTalkShortcut => _capturedShortcut.ToString();
    public int PushToTalkHoldThresholdMs => (int)_holdThreshold.Value;
    public string? CalibrationMicrophoneName => (_calibrationMicrophone.SelectedItem as MicrophoneChoice)?.DeviceName;
    public bool AiEnabled => _aiEnabled.Checked;
    public string? ReplacementApiKey => string.IsNullOrWhiteSpace(_apiKey.Text) ? null : _apiKey.Text.Trim();
    public bool RemoveApiKeyRequested => _removeApiKeyRequested && ReplacementApiKey is null;
    public IReadOnlyDictionary<string, int> ReceiverAlignmentTrims => _alignmentTrims.ToDictionary(pair => pair.Key, pair => (int)pair.Value.Value, StringComparer.Ordinal);
    public IReadOnlyList<SpeakerGroup> SpeakerGroups => _speakerGroups.Select(item => item with
    {
        Name = item.Name.Trim(),
        ReceiverIds = item.ReceiverIds.Distinct(StringComparer.Ordinal).ToArray()
    }).ToArray();

    public void MarkSaved()
    {
        if (ReplacementApiKey is not null)
        {
            _apiKey.Clear();
            _apiKey.PlaceholderText = "Paste a new key to replace the saved key";
            _apiKeyStatus.Text = "A key is securely saved for this Windows user";
            _removeApiKey.Enabled = true;
        }
        else if (_removeApiKeyRequested)
        {
            _apiKeyStatus.Text = "No key is saved";
            _removeApiKey.Enabled = false;
        }
        _removeApiKeyRequested = false;
    }

    private void InitializeValues(AirBridgeSettings settings, ThemePalette palette, bool storedApiKeyConfigured, bool apiKeyManagedByEnvironment)
    {
        _theme.Items.AddRange(["Use Windows setting", "Light", "Dark"]);
        _theme.SelectedIndex = ParseTheme(settings.ThemeMode) switch { AppThemeMode.Light => 1, AppThemeMode.Dark => 2, _ => 0 };
        _theme.AccessibleName = "App theme";

        _capture.Items.AddRange(["System audio", "Application audio"]);
        _capture.SelectedIndex = settings.DefaultCaptureMode == CaptureMode.ProcessTreeInclude ? 1 : 0;
        _capture.AccessibleName = "Default audio source";

        _standby.Items.AddRange(["Off", "After 10 seconds", "After 30 seconds", "After 60 seconds", "After 2 minutes", "After 5 minutes", "After 10 minutes"]);
        _standby.SelectedIndex = settings.SilenceStandbyEnabled ? SecondsToIndex(settings.SilenceStandbySeconds) : 0;
        _standby.AccessibleName = "Silence standby";

        var microphones = AcousticDelayMeasurer.GetAvailableMicrophones();
        foreach (var microphone in microphones) _calibrationMicrophone.Items.Add(new MicrophoneChoice(microphone.Name, microphone.Name));
        if (!string.IsNullOrWhiteSpace(settings.CalibrationMicrophoneName) &&
            microphones.All(item => !item.Name.Equals(settings.CalibrationMicrophoneName, StringComparison.OrdinalIgnoreCase)))
            _calibrationMicrophone.Items.Add(new MicrophoneChoice(settings.CalibrationMicrophoneName, $"Unavailable: {settings.CalibrationMicrophoneName}"));
        if (_calibrationMicrophone.Items.Count == 0)
        {
            _calibrationMicrophone.Items.Add(new MicrophoneChoice(null, "No recording devices available"));
            _calibrationMicrophone.Enabled = false;
        }
        else
        {
            _calibrationMicrophone.SelectedIndex = Enumerable.Range(0, _calibrationMicrophone.Items.Count)
                .FirstOrDefault(index => _calibrationMicrophone.Items[index] is MicrophoneChoice choice &&
                    string.Equals(choice.DeviceName, settings.CalibrationMicrophoneName, StringComparison.OrdinalIgnoreCase));
        }
        _calibrationMicrophone.AccessibleName = "Speaker calibration microphone";

        _capturedShortcut = HotkeyGesture.TryParse(settings.PushToTalkShortcut, out var shortcut) ? shortcut : HotkeyGesture.Default;
        _pushToTalkShortcut.Text = _capturedShortcut.ToString();
        _shortcutBeforeCapture = _pushToTalkShortcut.Text;
        _pushToTalkShortcut.AccessibleName = "Push-to-talk shortcut";
        _pushToTalkShortcut.AccessibleDescription = "Focus this field and press a key combination to change the global shortcut.";
        _pushToTalkShortcut.Enter += (_, _) => _shortcutBeforeCapture = _pushToTalkShortcut.Text;
        _pushToTalkShortcut.KeyDown += CapturePushToTalkShortcut;
        _pushToTalkError.ForeColor = palette.Error;
        _pushToTalkError.Tag = ErrorTextTag;
        _holdThreshold.Value = Math.Clamp(settings.PushToTalkHoldThresholdMs, 100, 1000);
        _holdThreshold.AccessibleName = "Push-to-talk hold threshold in milliseconds";

        _aiEnabled.Checked = settings.AiEnabled;
        _aiEnabled.AccessibleDescription = "The assistant also requires a saved or environment-provided OpenAI API key.";
        _apiKey.PlaceholderText = apiKeyManagedByEnvironment
            ? "Provided by OPENAI_API_KEY"
            : storedApiKeyConfigured ? "Paste a new key to replace the saved key" : "Paste an OpenAI API key";
        _apiKey.AccessibleName = "OpenAI API key";
        _apiKey.AccessibleDescription = "Write-only field. A saved key is never displayed.";
        _apiKey.Enabled = !apiKeyManagedByEnvironment;
        _removeApiKey.Enabled = storedApiKeyConfigured && !apiKeyManagedByEnvironment;
        _apiKeyStatus.Text = apiKeyManagedByEnvironment
            ? "Managed by the OPENAI_API_KEY environment variable"
            : storedApiKeyConfigured ? "A key is securely saved for this Windows user" : "No key is saved";
        _apiKeyStatus.ForeColor = palette.SecondaryText;
        _apiKeyStatus.Tag = SecondaryTextTag;
        _removeApiKey.Click += (_, _) =>
        {
            _removeApiKeyRequested = true;
            _apiKey.Clear();
            _apiKey.PlaceholderText = "Paste a new OpenAI API key";
            _apiKeyStatus.Text = "The saved key will be removed when you save";
            _removeApiKey.Enabled = false;
        };
    }

    private TabPage BuildGeneralPage(ThemePalette palette)
    {
        var page = CreatePage("General");
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(24, 22, 24, 8) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 3; row++) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        AddField(fields, 0, "Appearance", _theme);
        AddField(fields, 1, "Default audio source", _capture);
        AddField(fields, 2, "Silence standby", _standby);

        var shortcutArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        shortcutArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shortcutArea.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        shortcutArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        shortcutArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var resetShortcut = new Button { Text = "Reset", AutoSize = true, Margin = new Padding(8, 4, 0, 4) };
        resetShortcut.Click += (_, _) => SetCapturedShortcut(HotkeyGesture.Default);
        shortcutArea.Controls.Add(_pushToTalkShortcut, 0, 0);
        shortcutArea.Controls.Add(resetShortcut, 1, 0);
        shortcutArea.Controls.Add(_pushToTalkError, 0, 1);
        shortcutArea.SetColumnSpan(_pushToTalkError, 2);
        AddField(fields, 3, "Push-to-talk shortcut", shortcutArea);

        var thresholdArea = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        thresholdArea.Controls.Add(_holdThreshold);
        thresholdArea.Controls.Add(new Label { Text = "ms", AutoSize = true, Margin = new Padding(5, 10, 0, 0) });
        AddField(fields, 4, "Hold threshold (ms)", thresholdArea);
        fields.Controls.Add(SecondaryText("Daily speaker selection and volume controls live in the tray flyout.", palette), 0, 5);
        fields.SetColumnSpan(fields.GetControlFromPosition(0, 5)!, 2);
        page.Controls.Add(fields);
        return page;
    }

    private TabPage BuildSyncPage(IReadOnlyList<ReceiverInfo> receivers, IReadOnlyDictionary<string, int> trims, ThemePalette palette)
    {
        var page = CreatePage("Speaker sync");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(24, 20, 24, 16) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(SecondaryText("Add a small delay to faster speakers so a group sounds aligned. These values apply the next time you save.", palette), 0, 0);

        var microphoneRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
        microphoneRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        microphoneRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        microphoneRow.Controls.Add(new Label { Text = "Calibration microphone", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        microphoneRow.Controls.Add(_calibrationMicrophone, 1, 0);
        root.Controls.Add(microphoneRow, 0, 1);

        var list = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 16, 8, 8) };
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        if (receivers.Count == 0)
        {
            list.Controls.Add(SecondaryText("No speakers have been discovered yet. Refresh the flyout and reopen Settings.", palette), 0, 0);
            list.SetColumnSpan(list.GetControlFromPosition(0, 0)!, 2);
        }
        else
        {
            var row = 0;
            foreach (var receiver in receivers.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var value = new NumericUpDown
                {
                    Minimum = 0,
                    Maximum = ReceiverAlignmentPlan.MaximumTrimMilliseconds,
                    Increment = 10,
                    Value = trims.TryGetValue(receiver.Id, out var trim)
                        ? Math.Clamp(trim, ReceiverAlignmentPlan.MinimumTrimMilliseconds, ReceiverAlignmentPlan.MaximumTrimMilliseconds)
                        : ReceiverAlignmentPlan.MinimumTrimMilliseconds,
                    Width = 82,
                    Margin = new Padding(0, 7, 0, 0),
                    AccessibleName = $"{receiver.Name} sync delay in milliseconds"
                };
                _alignmentTrims[receiver.Id] = value;
                list.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
                list.Controls.Add(new Label { Text = receiver.Name, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
                var valueRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
                valueRow.Controls.Add(value);
                valueRow.Controls.Add(new Label { Text = "ms", AutoSize = true, Margin = new Padding(4, 9, 0, 0) });
                list.Controls.Add(valueRow, 1, row++);
            }
        }

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(list);
        var reset = new Button { Text = "Reset all to 0 ms", AutoSize = true };
        reset.Click += (_, _) => { foreach (var value in _alignmentTrims.Values) value.Value = 0; };
        root.Controls.Add(scroll, 0, 2);
        root.Controls.Add(reset, 0, 3);
        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildGroupsPage(IReadOnlyList<ReceiverInfo> receivers, ThemePalette palette)
    {
        var page = CreatePage("Groups");
        var knownIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var receiver in receivers.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            knownIds.Add(receiver.Id);
            _groupReceiverChoices.Add(new(receiver.Id, receiver.Name));
        }
        var unavailableIndex = 1;
        foreach (var receiverId in _speakerGroups.SelectMany(item => item.ReceiverIds).Distinct(StringComparer.Ordinal).Where(id => !knownIds.Contains(id)))
            _groupReceiverChoices.Add(new(receiverId, $"Unavailable saved speaker {unavailableIndex++}"));
        foreach (var choice in _groupReceiverChoices) _speakerGroupMembers.Items.Add(choice);

        var add = new Button { Text = "New group", AutoSize = true };
        var remove = new Button { Text = "Remove group", AutoSize = true, Enabled = false };
        add.Click += (_, _) =>
        {
            var number = 1;
            var name = "New group";
            while (_speakerGroups.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = $"New group {++number}";
            var group = new SpeakerGroup($"group-{Guid.NewGuid():N}", name, []);
            _speakerGroups.Add(group);
            _speakerGroupList.Items.Add(group);
            _speakerGroupList.SelectedIndex = _speakerGroupList.Items.Count - 1;
            _speakerGroupName.Focus();
            _speakerGroupName.SelectAll();
        };
        remove.Click += (_, _) =>
        {
            var index = _speakerGroupList.SelectedIndex;
            if (index < 0) return;
            _speakerGroups.RemoveAt(index);
            _speakerGroupList.Items.RemoveAt(index);
            if (_speakerGroupList.Items.Count > 0) _speakerGroupList.SelectedIndex = Math.Min(index, _speakerGroupList.Items.Count - 1);
            else LoadSelectedSpeakerGroup();
        };
        _speakerGroupList.SelectedIndexChanged += (_, _) =>
        {
            remove.Enabled = _speakerGroupList.SelectedIndex >= 0;
            LoadSelectedSpeakerGroup();
        };
        _speakerGroupName.TextChanged += (_, _) => UpdateSelectedSpeakerGroupName();
        _speakerGroupName.Leave += (_, _) => RefreshSelectedSpeakerGroupListItem();
        _speakerGroupMembers.ItemCheck += (_, args) => UpdateSelectedSpeakerGroupMembers(args);
        foreach (var group in _speakerGroups) _speakerGroupList.Items.Add(group);

        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        leftButtons.Controls.AddRange([add, remove]);
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 0, 18, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(new Label { Text = "Saved groups", AutoSize = true, Font = UiGeometry.UiFont(9F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 7) }, 0, 0);
        left.Controls.Add(FrameControl(_speakerGroupList, palette), 0, 1);
        left.Controls.Add(leftButtons, 0, 2);

        var details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        details.Controls.Add(new Label { Text = "Group name", AutoSize = true, Margin = new Padding(0, 0, 0, 5) }, 0, 0);
        details.Controls.Add(FrameControl(_speakerGroupName, palette), 0, 1);
        details.Controls.Add(new Label { Text = "Speakers — check to add, uncheck to remove", AutoSize = true, Margin = new Padding(0, 8, 0, 5) }, 0, 2);
        details.Controls.Add(FrameControl(_speakerGroupMembers, palette), 0, 3);

        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(24, 20, 24, 16) };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        columns.Controls.Add(left, 0, 0);
        columns.Controls.Add(details, 1, 0);
        page.Controls.Add(columns);
        if (_speakerGroupList.Items.Count > 0) _speakerGroupList.SelectedIndex = 0;
        else LoadSelectedSpeakerGroup();
        return page;
    }

    private void LoadSelectedSpeakerGroup()
    {
        if (_updatingSpeakerGroup) return;
        _updatingSpeakerGroup = true;
        try
        {
            var selected = _speakerGroupList.SelectedIndex >= 0 ? _speakerGroups[_speakerGroupList.SelectedIndex] : null;
            _speakerGroupName.Enabled = _speakerGroupMembers.Enabled = selected is not null;
            _speakerGroupName.Text = selected?.Name ?? string.Empty;
            var ids = selected?.ReceiverIds.ToHashSet(StringComparer.Ordinal) ?? [];
            for (var index = 0; index < _speakerGroupMembers.Items.Count; index++)
                _speakerGroupMembers.SetItemChecked(index, ids.Contains(((GroupReceiverChoice)_speakerGroupMembers.Items[index]).ReceiverId));
        }
        finally { _updatingSpeakerGroup = false; }
    }

    private void UpdateSelectedSpeakerGroupName()
    {
        if (_updatingSpeakerGroup || _speakerGroupList.SelectedIndex < 0) return;
        var index = _speakerGroupList.SelectedIndex;
        var updated = _speakerGroups[index] with { Name = _speakerGroupName.Text };
        _speakerGroups[index] = updated;
    }

    private void RefreshSelectedSpeakerGroupListItem()
    {
        var index = _speakerGroupList.SelectedIndex;
        if (index < 0) return;
        _updatingSpeakerGroup = true;
        try
        {
            _speakerGroupList.Items[index] = _speakerGroups[index];
            _speakerGroupList.SelectedIndex = index;
        }
        finally { _updatingSpeakerGroup = false; }
    }

    private void UpdateSelectedSpeakerGroupMembers(ItemCheckEventArgs args)
    {
        if (_updatingSpeakerGroup || _speakerGroupList.SelectedIndex < 0) return;
        var receiverIds = _groupReceiverChoices.Where((_, index) =>
            index == args.Index ? args.NewValue == CheckState.Checked : _speakerGroupMembers.GetItemChecked(index))
            .Select(item => item.ReceiverId)
            .ToArray();
        var index = _speakerGroupList.SelectedIndex;
        _speakerGroups[index] = _speakerGroups[index] with { ReceiverIds = receiverIds };
    }

    private bool ValidateSpeakerGroups()
    {
        var invalidIndex = _speakerGroups.FindIndex(item => string.IsNullOrWhiteSpace(item.Name) || item.ReceiverIds.Count == 0);
        var duplicate = _speakerGroups.GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (invalidIndex < 0 && duplicate is null) return true;
        if (duplicate is not null) invalidIndex = _speakerGroups.FindIndex(item => item.Name.Equals(duplicate.Key, StringComparison.OrdinalIgnoreCase));
        if (_tabs is not null && _groupsPage is not null) _tabs.SelectedTab = _groupsPage;
        if (invalidIndex >= 0) _speakerGroupList.SelectedIndex = invalidIndex;
        var message = duplicate is not null
            ? "Give every speaker group a unique name."
            : string.IsNullOrWhiteSpace(_speakerGroups[invalidIndex].Name)
                ? "Enter a name for every speaker group."
                : "Choose at least one speaker for every group.";
        MessageBox.Show(this, message, "Speaker groups", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private TabPage BuildAssistantPage(ThemePalette palette)
    {
        var page = CreatePage("Assistant");
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(24, 22, 24, 8) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var keyInput = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        keyInput.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        keyInput.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _apiKey.Margin = new Padding(0, 7, 8, 7);
        _removeApiKey.Margin = new Padding(0, 5, 0, 5);
        keyInput.Controls.Add(_apiKey, 0, 0);
        keyInput.Controls.Add(_removeApiKey, 1, 0);
        AddField(fields, 0, "OpenAI API key", keyInput);
        fields.Controls.Add(_apiKeyStatus, 1, 1);
        fields.Controls.Add(_aiEnabled, 0, 2);
        fields.SetColumnSpan(_aiEnabled, 2);
        fields.Controls.Add(SecondaryText("The key is stored in Windows Credential Manager and is never displayed after saving. Streaming works without it.", palette), 0, 3);
        fields.SetColumnSpan(fields.GetControlFromPosition(0, 3)!, 2);
        page.Controls.Add(fields);
        return page;
    }

    private TabPage BuildAdvancedPage(ThemePalette palette)
    {
        var page = CreatePage("Advanced");
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24, 22, 24, 16) };
        content.Controls.Add(new Label { Text = "Troubleshooting", AutoSize = true, Font = UiGeometry.UiFont(10F, FontStyle.Bold) });
        content.Controls.Add(SecondaryText("Runtime logs include receiver state and transport errors. Audio is never logged.", palette));
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 26) };
        var activity = new Button { Text = "Open AI Activity Inspector", AutoSize = true };
        activity.AccessibleDescription = "View sanitized, in-memory transcription, OpenAI API, policy, and tool activity.";
        activity.Click += (_, _) => OpenActivityInspectorRequested?.Invoke(this, EventArgs.Empty);
        var logs = new Button { Text = "Open logs folder", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        logs.Click += (_, _) => OpenLogsRequested?.Invoke(this, EventArgs.Empty);
        actions.Controls.AddRange([activity, logs]);
        content.Controls.Add(actions);
        content.Controls.Add(new Label { Text = "About", AutoSize = true, Font = UiGeometry.UiFont(10F, FontStyle.Bold) });
        content.Controls.Add(SecondaryText($"AirBridge for Windows  ·  {Application.ProductVersion}\nWindows 10 19045 or newer", palette));
        page.Controls.Add(content);
        return page;
    }

    private static TabPage CreatePage(string text) => new(text) { Padding = Padding.Empty, UseVisualStyleBackColor = false, AutoScroll = true };

    private static void DrawTab(TabControl tabs, DrawItemEventArgs args, ThemePalette palette)
    {
        var bounds = tabs.GetTabRect(args.Index);
        var selected = args.Index == tabs.SelectedIndex;
        using var fill = new SolidBrush(selected ? palette.Window : palette.Surface);
        using var border = new Pen(palette.Border);
        args.Graphics.FillRectangle(fill, bounds);
        args.Graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        if (selected)
        {
            using var accent = new SolidBrush(palette.Accent);
            args.Graphics.FillRectangle(accent, bounds.Left + 1, bounds.Bottom - 3, bounds.Width - 2, 3);
        }
        TextRenderer.DrawText(
            args.Graphics,
            tabs.TabPages[args.Index].Text,
            tabs.Font,
            bounds,
            palette.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if ((args.State & DrawItemState.Focus) != 0) args.DrawFocusRectangle();
    }

    private static Label SecondaryText(string text, ThemePalette palette) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(580, 0),
        ForeColor = palette.SecondaryText,
        Margin = new Padding(0, 6, 0, 0),
        Tag = SecondaryTextTag
    };

    private static Panel FrameControl(Control control, ThemePalette palette)
    {
        control.Dock = DockStyle.Fill;
        control.Margin = Padding.Empty;
        if (control is TextBoxBase textBox) textBox.BorderStyle = BorderStyle.None;
        if (control is ListBox listBox) listBox.BorderStyle = BorderStyle.None;
        var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = Padding.Empty, Tag = ControlFrameTag };
        frame.Paint += (_, args) =>
        {
            using var border = new Pen(palette.Border);
            args.Graphics.DrawRectangle(border, 0, 0, frame.ClientSize.Width - 1, frame.ClientSize.Height - 1);
        };
        frame.Controls.Add(control);
        return frame;
    }

    private static void AddField(TableLayoutPanel panel, int row, string label, Control control)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 0) }, 0, row);
        control.Margin = new Padding(0, 5, 0, 5);
        panel.Controls.Add(control, 1, row);
    }

    private static int SecondsToIndex(int seconds) => seconds switch { <= 10 => 1, <= 30 => 2, <= 60 => 3, <= 120 => 4, <= 300 => 5, _ => 6 };
    private static AppThemeMode ParseTheme(string value) => Enum.TryParse<AppThemeMode>(value, true, out var parsed) ? parsed : AppThemeMode.System;

    private static void ApplyColors(Control root, ThemePalette palette)
    {
        root.ForeColor = root.Tag switch
        {
            SecondaryTextTag => palette.SecondaryText,
            ErrorTextTag => palette.Error,
            _ => palette.Text
        };
        if (root is ComboBox or NumericUpDown or TextBox or ListBox or CheckedListBox) root.BackColor = palette.Surface;
        if (root is ComboBox combo)
        {
            combo.FlatStyle = FlatStyle.Flat;
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.DrawItem += (_, args) => DrawComboItem(combo, args, palette);
        }
        if (root is TabPage) root.BackColor = palette.Window;
        if (root is Panel { Tag: ControlFrameTag }) root.BackColor = palette.Surface;
        foreach (Control child in root.Controls) ApplyColors(child, palette);
    }

    private static void DrawComboItem(ComboBox combo, DrawItemEventArgs args, ThemePalette palette)
    {
        if (args.Index < 0) return;
        var selected = (args.State & DrawItemState.Selected) != 0;
        using var fill = new SolidBrush(selected ? palette.SurfaceSelected : palette.Surface);
        args.Graphics.FillRectangle(fill, args.Bounds);
        TextRenderer.DrawText(
            args.Graphics,
            combo.GetItemText(combo.Items[args.Index]),
            combo.Font,
            Rectangle.Inflate(args.Bounds, -6, 0),
            palette.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((args.State & DrawItemState.Focus) != 0)
            ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(args.Bounds, -1, -1), palette.Focus, selected ? palette.SurfaceSelected : palette.Surface);
    }

    private void CapturePushToTalkShortcut(object? sender, KeyEventArgs args)
    {
        args.SuppressKeyPress = true;
        args.Handled = true;
        if (args.KeyCode == Keys.Escape)
        {
            if (HotkeyGesture.TryParse(_shortcutBeforeCapture, out var previous)) SetCapturedShortcut(previous);
            return;
        }
        if (args.KeyCode is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or Keys.Menu or Keys.LMenu or Keys.RMenu or
            Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.LWin or Keys.RWin) return;

        var win = (GetKeyState((int)Keys.LWin) & 0x8000) != 0 || (GetKeyState((int)Keys.RWin) & 0x8000) != 0;
        var gesture = new HotkeyGesture(
            args.Control,
            args.Alt,
            args.Shift,
            win,
            (int)args.KeyCode);
        _capturedShortcut = gesture;
        _pushToTalkShortcut.Text = gesture.ToString();
        _pushToTalkError.Text = gesture.IsValid ? string.Empty : "Use at least one modifier, or choose F13–F24.";
    }

    private void SetCapturedShortcut(HotkeyGesture gesture)
    {
        _capturedShortcut = gesture;
        _pushToTalkShortcut.Text = gesture.ToString();
        _pushToTalkError.Text = string.Empty;
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == WmSettingChange && IsHandleCreated && !IsDisposed)
            BeginInvoke(SystemTextScale.Refresh);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) SystemTextScale.Changed -= OnTextScaleChanged;
        base.Dispose(disposing);
    }

    private void OnTextScaleChanged(object? sender, TextScaleChangedEventArgs args)
    {
        var widthRatio = TextWidthScale(args.Current) / TextWidthScale(args.Previous);
        var heightRatio = TextHeightScale(args.Current) / TextHeightScale(args.Previous);
        MinimumSize = new((int)Math.Ceiling(MinimumSize.Width * widthRatio), (int)Math.Ceiling(MinimumSize.Height * heightRatio));
        ClientSize = new((int)Math.Ceiling(ClientSize.Width * widthRatio), (int)Math.Ceiling(ClientSize.Height * heightRatio));
        UiGeometry.RescaleText(this, args.Previous, args.Current);
        _buttonBar.Height = UiGeometry.TextLogical(64);
        EnsureFitsWorkingArea();
    }

    private void EnsureFitsWorkingArea()
    {
        var area = Screen.FromControl(this).WorkingArea;
        var maxWidth = Math.Max(420, area.Width - UiGeometry.Scale(this, 32));
        var maxHeight = Math.Max(360, area.Height - UiGeometry.Scale(this, 32));
        MinimumSize = new(Math.Min(MinimumSize.Width, maxWidth), Math.Min(MinimumSize.Height, maxHeight));
        Size = new(Math.Min(Width, maxWidth), Math.Min(Height, maxHeight));
    }

    private static float TextWidthScale(float textScale) => 1f + (Math.Clamp(textScale, 1f, 2.25f) - 1f) * 0.4f;
    private static float TextHeightScale(float textScale) => 1f + (Math.Clamp(textScale, 1f, 2.25f) - 1f) * 0.25f;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
}
