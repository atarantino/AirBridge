namespace AirBridge.App;

internal sealed class PairingCodeDialog : Form
{
    private readonly TextBox _code = new()
    {
        Dock = DockStyle.Fill,
        MaxLength = 8,
        TextAlign = HorizontalAlignment.Center,
        Font = UiGeometry.UiFont(18F, FontStyle.Bold),
        AccessibleName = "AirPlay pairing code"
    };
    private readonly Button _pair = new() { Text = "Pair", DialogResult = DialogResult.OK, AutoSize = true, Enabled = false };

    public PairingCodeDialog(string receiverName, ThemePalette palette)
    {
        Text = "Pair AirPlay receiver";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(370, 165);
        BackColor = palette.Window;
        ForeColor = palette.Text;

        var instructions = new Label
        {
            Text = $"Enter the code shown on {receiverName}.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true
        };
        buttons.Controls.Add(_pair);
        buttons.Controls.Add(cancel);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(instructions, 0, 0);
        layout.Controls.Add(_code, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        AcceptButton = _pair;
        CancelButton = cancel;
        _code.TextChanged += (_, _) =>
        {
            var digitsOnly = _code.Text.All(char.IsDigit);
            _pair.Enabled = digitsOnly && _code.Text.Length >= 4;
        };
        Shown += (_, _) =>
        {
            BringToFront();
            Activate();
            _code.Focus();
        };
    }

    public string PairingCode => _code.Text;
}
