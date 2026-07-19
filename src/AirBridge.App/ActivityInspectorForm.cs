using System.Text.Json;
using System.Runtime.InteropServices;
using AirBridge.Core;

namespace AirBridge.App;

internal sealed class AgentActivityStore : IAgentActivitySink
{
    private const int DefaultCapacity = 250;
    private readonly object _gate = new();
    private readonly Queue<AgentActivityEvent> _events = new();
    private readonly int _capacity;
    private readonly PersistentAgentActivityLog? _persistentLog;

    internal AgentActivityStore(int capacity = DefaultCapacity, bool persistToDisk = false, string? logPath = null)
    {
        if (capacity is < 10 or > 5000) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        if (persistToDisk)
            _persistentLog = new(logPath ?? System.IO.Path.Combine(AppLog.DirectoryPath, "ai-activity.jsonl"));
    }

    internal event Action<AgentActivityEvent>? ActivityPublished;
    internal event EventHandler? Cleared;

    public void Publish(AgentActivityEvent activity)
    {
        var sanitized = activity with
        {
            Title = AgentActivitySanitizer.Sanitize(activity.Title),
            Summary = AgentActivitySanitizer.Sanitize(activity.Summary),
            Details = AgentActivitySanitizer.Sanitize(activity.Details)
        };
        lock (_gate)
        {
            _events.Enqueue(sanitized);
            while (_events.Count > _capacity) _events.Dequeue();
        }
        if (ShouldPersist(sanitized.Kind))
        {
            try { _persistentLog?.Write(sanitized); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warning("ai-activity", $"Could not persist sanitized AI activity: {ex.Message}");
            }
        }
        ActivityPublished?.Invoke(sanitized);
    }

    private static bool ShouldPersist(AgentActivityKind kind) =>
        kind is AgentActivityKind.ApiRequest or AgentActivityKind.ApiResponse or AgentActivityKind.ToolCall or
            AgentActivityKind.Policy or AgentActivityKind.ToolResult or AgentActivityKind.Error;

    internal IReadOnlyList<AgentActivityEvent> Snapshot()
    {
        lock (_gate) return _events.ToArray();
    }

    internal void Clear()
    {
        lock (_gate) _events.Clear();
        Cleared?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class PersistentAgentActivityLog
{
    private const long MaximumBytes = 2 * 1024 * 1024;
    private const int RetainedFiles = 4;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly object _gate = new();
    private readonly string _path;

    internal PersistentAgentActivityLog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
    }

    internal void Write(AgentActivityEvent activity)
    {
        var payload = new
        {
            timestamp = activity.Timestamp,
            type = activity.Kind.ToString(),
            activity.Title,
            activity.Summary,
            activity.Details,
            duration_ms = activity.DurationMilliseconds,
            tone = activity.Tone.ToString()
        };
        var line = JsonSerializer.Serialize(payload, JsonOptions) + Environment.NewLine;
        lock (_gate)
        {
            RotateIfNeeded(System.Text.Encoding.UTF8.GetByteCount(line));
            File.AppendAllText(_path, line, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        var length = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        if (length + incomingBytes <= MaximumBytes) return;
        var oldest = $"{_path}.{RetainedFiles}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = RetainedFiles - 1; index >= 1; index--)
        {
            var source = $"{_path}.{index}";
            if (File.Exists(source)) File.Move(source, $"{_path}.{index + 1}");
        }
        if (File.Exists(_path)) File.Move(_path, $"{_path}.1");
    }
}

internal sealed class ActivityInspectorForm : Form
{
    private const int WmSettingChange = 0x001A;
    private readonly AgentActivityStore _store;
    private readonly ListView _events = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        OwnerDraw = true,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        GridLines = false,
        BorderStyle = BorderStyle.None
    };
    private readonly TextBox _details = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 9F),
        BorderStyle = BorderStyle.None,
        Margin = Padding.Empty
    };
    private readonly CheckBox _paused = new() { Text = "Pause", AutoSize = true, Margin = new Padding(8, 8, 10, 0) };
    private readonly CheckBox _autoScroll = new() { Text = "Auto-scroll", AutoSize = true, Checked = true, Margin = new Padding(0, 8, 10, 0) };
    private readonly CheckBox _showTranscripts = new() { Text = "Show transcript text", AutoSize = true, Margin = new Padding(0, 8, 10, 0) };
    private readonly Button _copy = new() { Text = "Copy sanitized JSON", AutoSize = true, Enabled = false };
    private readonly Button _clear = new() { Text = "Clear", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(10, 9, 0, 0) };
    private readonly Label _privacy;
    private ThemePalette _palette;

    internal ActivityInspectorForm(AgentActivityStore store, ThemePalette palette)
    {
        _store = store;
        _palette = palette;
        Text = "AirBridge AI Activity Inspector";
        AccessibleName = "AI Activity Inspector";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiGeometry.UiFont(9F);
        _details.Font = new Font("Consolas", 9F * SystemTextScale.Current);
        ClientSize = new Size(980, 640);
        MinimumSize = new Size(720, 460);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        _events.Columns.Add("Time", 96);
        _events.Columns.Add("Type", 112);
        _events.Columns.Add("Activity", 190);
        _events.Columns.Add("Summary", 440);
        _events.Columns.Add("Duration", 90);
        _events.AccessibleName = "AI activity timeline";
        _events.SelectedIndexChanged += (_, _) => ShowSelectedActivity();
        _events.Resize += (_, _) => ResizeSummaryColumn();
        _events.DrawColumnHeader += DrawColumnHeader;
        _events.DrawItem += (_, args) => args.DrawDefault = false;
        _events.DrawSubItem += DrawEventCell;

        _paused.CheckedChanged += (_, _) =>
        {
            if (!_paused.Checked) RebuildTimeline();
            UpdateStatus();
        };
        _showTranscripts.CheckedChanged += (_, _) => ShowSelectedActivity();
        _copy.Click += (_, _) => CopySelectedActivity();
        _clear.Click += (_, _) => _store.Clear();

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(12, 8, 12, 7),
            Margin = Padding.Empty
        };
        toolbar.Controls.AddRange([_paused, _autoScroll, _showTranscripts, _copy, _clear, _status]);

        _privacy = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Memory only · bounded to 250 events · credentials, network addresses, hardware IDs, and pipe identifiers are redacted · audio is never logged",
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 8, 0)
        };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 385,
            Panel1MinSize = 180,
            Panel2MinSize = 110,
            SplitterWidth = 1,
            IsSplitterFixed = false,
            Margin = Padding.Empty
        };
        var timelineFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1, 1, 1, 0), Margin = Padding.Empty };
        var detailsFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = Padding.Empty };
        timelineFrame.Controls.Add(_events);
        detailsFrame.Controls.Add(_details);
        split.Panel1.Controls.Add(timelineFrame);
        split.Panel2.Controls.Add(detailsFrame);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(split, 0, 1);
        root.Controls.Add(_privacy, 0, 2);
        Controls.Add(root);
        SystemTextScale.Changed += OnTextScaleChanged;
        UiGeometry.ScaleInitialTextLayout(this);

        _store.ActivityPublished += OnActivityPublished;
        _store.Cleared += OnStoreCleared;
        ApplyTheme(palette);
        Shown += (_, _) => WindowEffects.ApplyTheme(this, _palette);
        RebuildTimeline();
    }

    internal void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        palette.Apply(this);
        _events.BackColor = palette.Window;
        _events.ForeColor = palette.Text;
        _details.BackColor = palette.Window;
        _details.ForeColor = palette.Text;
        _privacy.ForeColor = palette.SecondaryText;
        if (_events.Parent is { } timelineFrame) timelineFrame.BackColor = palette.Border;
        if (_details.Parent is { } detailsFrame) detailsFrame.BackColor = palette.Border;
        if (_events.Parent?.Parent?.Parent is SplitContainer split)
        {
            split.BackColor = palette.Border;
            split.Panel1.BackColor = palette.Window;
            split.Panel2.BackColor = palette.Window;
        }
        WindowEffects.ApplyTheme(this, palette);
        RecolorItems();
        _events.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemTextScale.Changed -= OnTextScaleChanged;
            _store.ActivityPublished -= OnActivityPublished;
            _store.Cleared -= OnStoreCleared;
        }
        base.Dispose(disposing);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == WmSettingChange && IsHandleCreated && !IsDisposed)
            BeginInvoke(SystemTextScale.Refresh);
    }

    private void OnTextScaleChanged(object? sender, TextScaleChangedEventArgs args) =>
        UiGeometry.RescaleText(this, args.Previous, args.Current);

    private void OnActivityPublished(AgentActivityEvent activity)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(() => OnActivityPublished(activity));
        else if (!_paused.Checked)
        {
            AppendActivity(activity);
            UpdateStatus();
        }
    }

    private void OnStoreCleared(object? sender, EventArgs args)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(() => OnStoreCleared(sender, args));
        else
        {
            _events.Items.Clear();
            _details.Clear();
            _copy.Enabled = false;
            UpdateStatus();
        }
    }

    private void RebuildTimeline()
    {
        _events.BeginUpdate();
        _events.Items.Clear();
        foreach (var activity in _store.Snapshot()) AppendActivity(activity, ensureVisible: false);
        _events.EndUpdate();
        if (_events.Items.Count > 0)
        {
            var latest = _events.Items[^1];
            latest.Selected = true;
            latest.Focused = true;
            if (_autoScroll.Checked) latest.EnsureVisible();
        }
        UpdateStatus();
    }

    private void AppendActivity(AgentActivityEvent activity, bool ensureVisible = true)
    {
        var item = new ListViewItem(activity.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff")) { Tag = activity };
        item.SubItems.Add(FormatKind(activity.Kind));
        item.SubItems.Add(activity.Title);
        item.SubItems.Add(activity.Summary);
        item.SubItems.Add(activity.DurationMilliseconds is { } duration ? $"{duration:N0} ms" : string.Empty);
        item.ForeColor = ToneColor(activity.Tone);
        _events.Items.Add(item);
        if (ensureVisible && _autoScroll.Checked) item.EnsureVisible();
    }

    private void ShowSelectedActivity()
    {
        var activity = SelectedActivity();
        _copy.Enabled = activity is not null;
        if (activity is null)
        {
            _details.Clear();
            return;
        }
        var details = VisibleDetails(activity);
        _details.Text = $"{activity.Timestamp.ToLocalTime():O}\r\n{FormatKind(activity.Kind)} · {activity.Title}\r\n{activity.Summary}" +
            (activity.DurationMilliseconds is { } duration ? $" · {duration:N0} ms" : string.Empty) +
            (string.IsNullOrWhiteSpace(details) ? string.Empty : $"\r\n\r\n{details}");
    }

    private void CopySelectedActivity()
    {
        var activity = SelectedActivity();
        if (activity is null) return;
        var payload = new
        {
            timestamp = activity.Timestamp,
            type = FormatKind(activity.Kind),
            activity.Title,
            activity.Summary,
            details = VisibleDetails(activity),
            duration_ms = activity.DurationMilliseconds,
            tone = activity.Tone.ToString().ToLowerInvariant()
        };
        try { Clipboard.SetText(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })); }
        catch (ExternalException ex) { MessageBox.Show(this, ex.Message, "Could not copy activity", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private AgentActivityEvent? SelectedActivity() =>
        _events.SelectedItems.Count == 1 ? _events.SelectedItems[0].Tag as AgentActivityEvent : null;

    private string? VisibleDetails(AgentActivityEvent activity) =>
        activity.Kind == AgentActivityKind.Transcription && !_showTranscripts.Checked
            ? "Transcript hidden. Enable “Show transcript text” to reveal it for this in-memory session."
            : activity.Details;

    private void UpdateStatus()
    {
        var count = _store.Snapshot().Count;
        _status.Text = $"{(_paused.Checked ? "Paused" : "Live")} · {count} event{(count == 1 ? string.Empty : "s")}";
        _status.ForeColor = _paused.Checked ? _palette.Warning : _palette.Success;
    }

    private void ResizeSummaryColumn()
    {
        if (_events.Columns.Count < 5) return;
        _events.Columns[3].Width = Math.Max(180, _events.ClientSize.Width - 96 - 112 - 190 - 90 - 24);
    }

    private void DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs args)
    {
        using var fill = new SolidBrush(_palette.Surface);
        using var border = new Pen(_palette.Border);
        args.Graphics.FillRectangle(fill, args.Bounds);
        args.Graphics.DrawLine(border, args.Bounds.Left, args.Bounds.Bottom - 1, args.Bounds.Right, args.Bounds.Bottom - 1);
        if (args.ColumnIndex > 0)
            args.Graphics.DrawLine(border, args.Bounds.Left, args.Bounds.Top + 7, args.Bounds.Left, args.Bounds.Bottom - 7);
        TextRenderer.DrawText(
            args.Graphics,
            args.Header?.Text ?? string.Empty,
            _events.Font,
            Rectangle.Inflate(args.Bounds, -8, 0),
            _palette.SecondaryText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void DrawEventCell(object? sender, DrawListViewSubItemEventArgs args)
    {
        if (args.Item is null || args.SubItem is null) return;
        var selected = args.Item.Selected;
        using var fill = new SolidBrush(selected ? _palette.SurfaceSelected : _palette.Window);
        args.Graphics.FillRectangle(fill, args.Bounds);
        var textColor = args.ColumnIndex switch
        {
            0 or 3 or 4 => _palette.SecondaryText,
            1 => args.Item.ForeColor,
            _ => _palette.Text
        };
        TextRenderer.DrawText(
            args.Graphics,
            args.SubItem.Text,
            _events.Font,
            Rectangle.Inflate(args.Bounds, -8, 0),
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (args.ColumnIndex == _events.Columns.Count - 1 && selected && args.Item.Focused && _events.Focused)
            ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(args.Item.Bounds, -1, -1), _palette.Focus, _palette.SurfaceSelected);
    }

    private void RecolorItems()
    {
        foreach (ListViewItem item in _events.Items)
            if (item.Tag is AgentActivityEvent activity) item.ForeColor = ToneColor(activity.Tone);
    }

    private Color ToneColor(AgentActivityTone tone) => tone switch
    {
        AgentActivityTone.Success => _palette.Success,
        AgentActivityTone.Warning => _palette.Warning,
        AgentActivityTone.Error => _palette.Error,
        _ => _palette.Text
    };

    private static string FormatKind(AgentActivityKind kind) => kind switch
    {
        AgentActivityKind.ApiRequest => "API request",
        AgentActivityKind.ApiResponse => "API response",
        AgentActivityKind.ToolCall => "Tool call",
        AgentActivityKind.ToolResult => "Tool result",
        AgentActivityKind.AssistantResponse => "Assistant",
        _ => kind.ToString()
    };
}
