using AirBridge.Core;

namespace AirBridge.App;

internal sealed class SettingsForm : Form
{
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _capture = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _standby = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox _aiEnabled = new() { Text = "Enable the AirBridge assistant when an API key is available", AutoSize = true };

    public SettingsForm(AirBridgeSettings settings, ThemePalette palette)
    {
        Text = "AirBridge settings";
        AccessibleName = "AirBridge settings";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(500, 350);
        MinimumSize = new Size(460, 340);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = UiGeometry.UiFont(9F);

        _theme.Items.AddRange(["Use Windows setting", "Light", "Dark"]);
        _theme.SelectedIndex = ParseTheme(settings.ThemeMode) switch
        {
            AppThemeMode.Light => 1,
            AppThemeMode.Dark => 2,
            _ => 0
        };
        _theme.AccessibleName = "App theme";

        _capture.Items.AddRange(["System audio", "Application audio"]);
        _capture.SelectedIndex = settings.DefaultCaptureMode == CaptureMode.ProcessTreeInclude ? 1 : 0;
        _capture.AccessibleName = "Default audio source";

        _standby.Items.AddRange(["Off", "After 10 seconds", "After 30 seconds", "After 60 seconds", "After 2 minutes", "After 5 minutes", "After 10 minutes"]);
        _standby.SelectedIndex = settings.SilenceStandbyEnabled ? SecondsToIndex(settings.SilenceStandbySeconds) : 0;
        _standby.AccessibleName = "Silence standby";
        _aiEnabled.Checked = settings.AiEnabled;
        _aiEnabled.AccessibleDescription = "The assistant also requires the OPENAI_API_KEY environment variable.";

        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(14, 4, 14, 4) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(14, 4, 14, 4) };
        AcceptButton = save;
        CancelButton = cancel;

        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(24, 18, 24, 8) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        AddField(fields, 0, "Appearance", _theme);
        AddField(fields, 1, "Default source", _capture);
        AddField(fields, 2, "Silence standby", _standby);
        fields.Controls.Add(_aiEnabled, 0, 3);
        fields.SetColumnSpan(_aiEnabled, 2);
        fields.Controls.Add(new Label
        {
            Text = "The assistant requires OPENAI_API_KEY. Streaming and speaker controls work without it.",
            AutoSize = true,
            ForeColor = palette.SecondaryText,
            Margin = new Padding(0, 5, 0, 0)
        }, 0, 4);
        fields.SetColumnSpan(fields.GetControlFromPosition(0, 4)!, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 10, 16, 8) };
        buttons.Controls.AddRange([save, cancel]);
        Controls.Add(fields);
        Controls.Add(buttons);
        palette.Apply(this);
        ApplyColors(this, palette);
    }

    public AppThemeMode ThemeMode => _theme.SelectedIndex switch { 1 => AppThemeMode.Light, 2 => AppThemeMode.Dark, _ => AppThemeMode.System };
    public CaptureMode DefaultCaptureMode => _capture.SelectedIndex == 1 ? CaptureMode.ProcessTreeInclude : CaptureMode.SystemMix;
    public bool SilenceStandbyEnabled => _standby.SelectedIndex > 0;
    public int SilenceStandbySeconds => _standby.SelectedIndex switch { 1 => 10, 2 => 30, 3 => 60, 4 => 120, 5 => 300, 6 => 600, _ => 60 };
    public bool AiEnabled => _aiEnabled.Checked;

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
        if (root is ComboBox or CheckBox or Label) root.ForeColor = palette.Text;
        if (root is ComboBox) root.BackColor = palette.Surface;
        foreach (Control child in root.Controls) ApplyColors(child, palette);
    }
}
