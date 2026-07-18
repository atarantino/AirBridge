using AirBridge.Core;
using System.Runtime.InteropServices;

namespace AirBridge.App;

internal sealed class SettingsForm : Form
{
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _capture = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _standby = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _pushToTalkShortcut = new() { Dock = DockStyle.Fill, ReadOnly = true, ShortcutsEnabled = false };
    private readonly Label _pushToTalkError = new() { AutoSize = true };
    private readonly NumericUpDown _holdThreshold = new() { Minimum = 100, Maximum = 1000, Increment = 50, Dock = DockStyle.Left, Width = 110 };
    private readonly CheckBox _aiEnabled = new() { Text = "Enable the AirBridge assistant when an API key is available", AutoSize = true };
    private readonly TextBox _apiKey = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly Button _removeApiKey = new() { Text = "Remove", AutoSize = true };
    private readonly Label _apiKeyStatus = new() { AutoSize = true };
    private readonly Dictionary<string, NumericUpDown> _alignmentTrims = new(StringComparer.Ordinal);
    private bool _removeApiKeyRequested;
    private string _shortcutBeforeCapture = HotkeyGesture.Default.ToString();
    private HotkeyGesture _capturedShortcut = HotkeyGesture.Default;

    public SettingsForm(
        AirBridgeSettings settings,
        ThemePalette palette,
        bool storedApiKeyConfigured,
        bool apiKeyManagedByEnvironment,
        IReadOnlyList<ReceiverInfo>? receivers = null)
    {
        Text = "AirBridge Settings";
        AccessibleName = "AirBridge settings";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(700, 570);
        MinimumSize = new Size(620, 500);
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
        tabs.TabPages.Add(BuildSyncPage(receivers ?? [], settings.ReceiverAlignmentTrimMs, palette));
        tabs.TabPages.Add(BuildAssistantPage(palette));
        tabs.TabPages.Add(BuildAdvancedPage(palette));

        var save = new Button { Text = "Save", AutoSize = true, Padding = new Padding(14, 4, 14, 4) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(14, 4, 14, 4) };
        save.Click += (_, _) =>
        {
            if (!_capturedShortcut.IsValid)
            {
                _pushToTalkShortcut.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
        };
        AcceptButton = save;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(16, 10, 16, 8),
            WrapContents = false
        };
        buttons.Controls.AddRange([save, cancel]);
        Controls.Add(tabs);
        Controls.Add(buttons);
        palette.Apply(this);
        ApplyColors(this, palette);
    }

    public event EventHandler? OpenLogsRequested;

    public AppThemeMode ThemeMode => _theme.SelectedIndex switch { 1 => AppThemeMode.Light, 2 => AppThemeMode.Dark, _ => AppThemeMode.System };
    public CaptureMode DefaultCaptureMode => _capture.SelectedIndex == 1 ? CaptureMode.ProcessTreeInclude : CaptureMode.SystemMix;
    public bool SilenceStandbyEnabled => _standby.SelectedIndex > 0;
    public int SilenceStandbySeconds => _standby.SelectedIndex switch { 1 => 10, 2 => 30, 3 => 60, 4 => 120, 5 => 300, 6 => 600, _ => 60 };
    public string PushToTalkShortcut => _capturedShortcut.ToString();
    public int PushToTalkHoldThresholdMs => (int)_holdThreshold.Value;
    public bool AiEnabled => _aiEnabled.Checked;
    public string? ReplacementApiKey => string.IsNullOrWhiteSpace(_apiKey.Text) ? null : _apiKey.Text.Trim();
    public bool RemoveApiKeyRequested => _removeApiKeyRequested && ReplacementApiKey is null;
    public IReadOnlyDictionary<string, int> ReceiverAlignmentTrims => _alignmentTrims.ToDictionary(pair => pair.Key, pair => (int)pair.Value.Value, StringComparer.Ordinal);

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

        _capturedShortcut = HotkeyGesture.TryParse(settings.PushToTalkShortcut, out var shortcut) ? shortcut : HotkeyGesture.Default;
        _pushToTalkShortcut.Text = _capturedShortcut.ToString();
        _shortcutBeforeCapture = _pushToTalkShortcut.Text;
        _pushToTalkShortcut.AccessibleName = "Push-to-talk shortcut";
        _pushToTalkShortcut.AccessibleDescription = "Focus this field and press a key combination to change the global shortcut.";
        _pushToTalkShortcut.Enter += (_, _) => _shortcutBeforeCapture = _pushToTalkShortcut.Text;
        _pushToTalkShortcut.KeyDown += CapturePushToTalkShortcut;
        _pushToTalkError.ForeColor = palette.Error;
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
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(24, 20, 24, 16) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(SecondaryText("Add a small delay to faster speakers so a group sounds aligned. These values apply the next time you save.", palette), 0, 0);

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
                    Maximum = 500,
                    Increment = 10,
                    Value = trims.TryGetValue(receiver.Id, out var trim) ? Math.Clamp(trim, 0, 500) : 0,
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
        root.Controls.Add(scroll, 0, 1);
        root.Controls.Add(reset, 0, 2);
        page.Controls.Add(root);
        return page;
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
        var logs = new Button { Text = "Open logs folder", AutoSize = true, Margin = new Padding(0, 12, 0, 26) };
        logs.Click += (_, _) => OpenLogsRequested?.Invoke(this, EventArgs.Empty);
        content.Controls.Add(logs);
        content.Controls.Add(new Label { Text = "About", AutoSize = true, Font = UiGeometry.UiFont(10F, FontStyle.Bold) });
        content.Controls.Add(SecondaryText($"AirBridge for Windows  ·  {Application.ProductVersion}\nWindows 10 19045 or newer", palette));
        page.Controls.Add(content);
        return page;
    }

    private static TabPage CreatePage(string text) => new(text) { Padding = Padding.Empty, UseVisualStyleBackColor = false };

    private static void DrawTab(TabControl tabs, DrawItemEventArgs args, ThemePalette palette)
    {
        var bounds = tabs.GetTabRect(args.Index);
        var selected = args.Index == tabs.SelectedIndex;
        using var fill = new SolidBrush(selected ? palette.Window : palette.Surface);
        using var border = new Pen(palette.Border);
        args.Graphics.FillRectangle(fill, bounds);
        args.Graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
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
        Margin = new Padding(0, 6, 0, 0)
    };

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
        if (root is ComboBox or CheckBox or Label or NumericUpDown) root.ForeColor = palette.Text;
        if (root is ComboBox or NumericUpDown or TextBox) root.BackColor = palette.Surface;
        if (root is TabPage) root.BackColor = palette.Window;
        foreach (Control child in root.Controls) ApplyColors(child, palette);
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

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
}
