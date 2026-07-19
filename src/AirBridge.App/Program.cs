namespace AirBridge.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppLog.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) => AppLog.Error("ui", "Unhandled UI-thread exception.", eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            AppLog.Error("runtime", $"Unhandled process exception (terminating={eventArgs.IsTerminating}).", eventArgs.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLog.Error("runtime", "Unobserved task exception.", eventArgs.Exception);
            eventArgs.SetObserved();
        };
        AppLog.Info("lifecycle", $"AirBridge starting; pid={Environment.ProcessId}.");
        ApplicationConfiguration.Initialize();
        if (args is ["--preview"])
        {
            Application.Run(new MainForm(previewMode: true));
            return;
        }
        if (args.Length >= 2 && args[0] == "--snapshot")
        {
            var path = args[1];
            var theme = args.Length >= 3 && Enum.TryParse<AppThemeMode>(args[2], true, out var parsedTheme) ? parsedTheme : AppThemeMode.System;
            var scale = args.Length >= 4 && float.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedScale)
                ? Math.Clamp(parsedScale, 1f, 2f) : 1f;
            var streaming = args.Length < 5 || !args[4].Equals("idle", StringComparison.OrdinalIgnoreCase);
            using var form = new MainForm(previewMode: true, previewTheme: theme, previewStreaming: streaming);
            form.Show();
            Application.DoEvents();
            if (scale != 1f)
            {
                form.Scale(new SizeF(scale, scale));
                form.PerformLayout();
                Application.DoEvents();
            }
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            form.Close();
            return;
        }
        if (args.Length >= 2 && args[0] == "--snapshot-flyout")
        {
            var flyoutPath = args[1];
            var theme = AppThemeMode.Dark;
            var scaleArgument = 2;
            if (args.Length >= 3 && Enum.TryParse<AppThemeMode>(args[2], true, out var parsedTheme))
            {
                theme = parsedTheme;
                scaleArgument = 3;
            }
            var scale = args.Length > scaleArgument && float.TryParse(args[scaleArgument], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedScale)
                ? Math.Clamp(parsedScale, 1f, 2f) : 1f;
            using var flyout = new TrayFlyoutForm(theme);
            flyout.AutoHide = false;
            PopulatePreviewFlyout(flyout);
            if (scale != 1f) flyout.Scale(new SizeF(scale, scale));
            flyout.Show();
            flyout.ReflowReceiverRows();
            Application.DoEvents();
            using var bitmap = new Bitmap(flyout.Width, flyout.Height);
            flyout.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(flyoutPath))!);
            bitmap.Save(flyoutPath, System.Drawing.Imaging.ImageFormat.Png);
            flyout.Close();
            return;
        }
        if (args.Length >= 2 && args[0] == "--snapshot-settings")
        {
            var settingsPath = args[1];
            var theme = args.Length >= 3 && Enum.TryParse<AppThemeMode>(args[2], true, out var parsedTheme) ? parsedTheme : AppThemeMode.System;
            var now = DateTimeOffset.UtcNow;
            var previewReceivers = new[]
            {
                new AirBridge.Core.ReceiverInfo("output-a", "Desk Speaker", "small-speaker", false, now),
                new AirBridge.Core.ReceiverInfo("output-b", "Media Room", "smart-speaker", false, now),
                new AirBridge.Core.ReceiverInfo("output-c", "Upstairs Speaker", "smart-speaker", false, now)
            };
            var previewSettings = new AirBridge.Core.AirBridgeSettings
            {
                ReceiverAlignmentTrimMs = new(StringComparer.Ordinal) { ["output-a"] = 60, ["output-b"] = 20 },
                SpeakerGroups =
                [
                    new("group-work", "Workday", ["output-a", "output-c"]),
                    new("group-home", "Whole home", ["output-a", "output-b", "output-c"])
                ]
            };
            using var settings = new SettingsForm(previewSettings, ThemePalette.Current(theme), storedApiKeyConfigured: true, apiKeyManagedByEnvironment: false, previewReceivers);
            settings.Show();
            Application.DoEvents();
            if (args.Length >= 4 && int.TryParse(args[3], out var tabIndex) && settings.Controls.OfType<TabControl>().FirstOrDefault() is { } tabs)
                tabs.SelectedIndex = Math.Clamp(tabIndex, 0, tabs.TabCount - 1);
            using var bitmap = new Bitmap(settings.Width, settings.Height);
            settings.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!);
            bitmap.Save(settingsPath, System.Drawing.Imaging.ImageFormat.Png);
            settings.Close();
            return;
        }
        if (args.Length >= 2 && args[0] == "--snapshot-hud")
        {
            var hudPath = args[1];
            var theme = args.Length >= 3 && Enum.TryParse<AppThemeMode>(args[2], true, out var parsedTheme) ? parsedTheme : AppThemeMode.Dark;
            using var hud = new VoiceHudForm(ThemePalette.Current(theme));
            hud.SetInitialPosition(null, null);
            if (args.Length >= 4 && args[3].Equals("confirmation", StringComparison.OrdinalIgnoreCase))
                _ = hud.ShowConfirmation("Allow speaker alignment?", "AirBridge will play calibration chirps, briefly mute non-target speakers, capture the selected microphone in memory, and apply bounded timing trims. Microphone audio is discarded locally.", CancellationToken.None);
            else if (args.Length >= 4 && args[3].Equals("thinking", StringComparison.OrdinalIgnoreCase))
                hud.ShowThinking();
            else
                hud.ShowListening(() => 0.42f, holdHint: false);
            for (var frame = 0; frame < 4; frame++) Application.DoEvents();
            using var bitmap = new Bitmap(hud.Width, hud.Height);
            hud.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(hudPath))!);
            bitmap.Save(hudPath, System.Drawing.Imaging.ImageFormat.Png);
            hud.Close();
            return;
        }
        if (args.Length >= 2 && args[0] == "--snapshot-activity")
        {
            var activityPath = args[1];
            var theme = args.Length >= 3 && Enum.TryParse<AppThemeMode>(args[2], true, out var parsedTheme) ? parsedTheme : AppThemeMode.Dark;
            var store = new AgentActivityStore();
            var now = DateTimeOffset.Now;
            store.Publish(new(now, AirBridge.Core.AgentActivityKind.Transcription, "Audio transcription", "gpt-4o-transcribe · 84 KB in memory", "Audio is never logged."));
            store.Publish(new(now.AddMilliseconds(842), AirBridge.Core.AgentActivityKind.Transcription, "Transcript ready", "Text returned to the local AirBridge assistant", "Why does the media room speaker keep dropping out?", 842, AirBridge.Core.AgentActivityTone.Success));
            store.Publish(new(now.AddMilliseconds(850), AirBridge.Core.AgentActivityKind.ApiRequest, "Responses API", "gpt-5.6 · high reasoning · turn 1", "New conversation response"));
            store.Publish(new(now.AddSeconds(2.2), AirBridge.Core.AgentActivityKind.ApiResponse, "Responses API", "Completed · 1,284 input / 96 output tokens", "Response resp_demo_123456…", 1350, AirBridge.Core.AgentActivityTone.Success));
            store.Publish(new(now.AddSeconds(2.3), AirBridge.Core.AgentActivityKind.ToolCall, "get_stream_health", "Tool requested by GPT-5.6", "{}"));
            store.Publish(new(now.AddSeconds(2.31), AirBridge.Core.AgentActivityKind.Policy, "get_stream_health", "Allowed by local policy", "Allowed.", Tone: AirBridge.Core.AgentActivityTone.Success));
            store.Publish(new(now.AddSeconds(2.32), AirBridge.Core.AgentActivityKind.ToolResult, "get_stream_health", "Local tool completed", "{\"state\":\"Streaming\",\"buffer_fill_percent\":72,\"new_underruns\":0}", 8, AirBridge.Core.AgentActivityTone.Success));
            store.Publish(new(now.AddSeconds(3), AirBridge.Core.AgentActivityKind.AssistantResponse, "Assistant response", "Returned to AirBridge", "The stream is healthy and no new underruns were observed.", Tone: AirBridge.Core.AgentActivityTone.Success));
            using var activity = new ActivityInspectorForm(store, ThemePalette.Current(theme));
            activity.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(activity.Width, activity.Height);
            activity.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(activityPath))!);
            bitmap.Save(activityPath, System.Drawing.Imaging.ImageFormat.Png);
            activity.Close();
            return;
        }
        if (args.Length >= 1 && args[0] == "--stress-flyout")
        {
            var cycles = args.Length >= 2 && int.TryParse(args[1], out var parsedCycles)
                ? Math.Clamp(parsedCycles, 1, 100)
                : 20;
            using var flyout = new TrayFlyoutForm(AppThemeMode.Dark) { AutoHide = false };
            PopulatePreviewFlyout(flyout);
            for (var cycle = 0; cycle < cycles; cycle++)
            {
                flyout.Show();
                flyout.ReflowReceiverRows();
                var visibleUntil = Environment.TickCount64 + 45;
                while (Environment.TickCount64 < visibleUntil) Application.DoEvents();
                flyout.Hide();
                Application.DoEvents();
            }
            flyout.Close();
            return;
        }
        Application.Run(new MainForm());

        // Returning from Main can leave the process alive when a native audio
        // library owns a foreground thread. MainForm has already completed its
        // bounded graceful cleanup before the message loop is allowed to end.
        AppLog.Info("lifecycle", "AirBridge message loop ended after graceful cleanup.");
        AppLog.Shutdown();
        Environment.Exit(0);
    }

    private static void PopulatePreviewFlyout(TrayFlyoutForm flyout)
    {
        var now = DateTimeOffset.UtcNow;
        var receivers = new[]
        {
            new AirBridge.Core.ReceiverInfo("output-a", "Desk Speaker", "small-speaker", false, now),
            new AirBridge.Core.ReceiverInfo("output-b", "Media Room", "smart-speaker", false, now),
            new AirBridge.Core.ReceiverInfo("output-c", "Portable Speaker", "speaker", false, now),
            new AirBridge.Core.ReceiverInfo("output-d", "Upstairs Speaker", "smart-speaker", false, now),
            new AirBridge.Core.ReceiverInfo("output-e", "TV", "media-box", false, now,
                DeviceType: "apple-tv", SupportsPowerControl: true, RequiresControlPairing: true)
        };
        flyout.SetReceivers(receivers, new HashSet<string>(["output-a", "output-b", "output-c"]), new Dictionary<string, int>
        {
            ["output-a"] = 30, ["output-b"] = 42, ["output-c"] = 36, ["output-d"] = 55, ["output-e"] = 24
        }, new Dictionary<string, int> { ["output-a"] = 60, ["output-b"] = 0, ["output-c"] = 20 });
        flyout.UpdateReceiver("output-a", AirBridge.Core.StreamState.Streaming, 30, true, alignmentTrimMilliseconds: 60);
        flyout.UpdateReceiver("output-b", AirBridge.Core.StreamState.Reconnecting, 42, true, "Retrying");
        flyout.UpdateReceiver("output-c", AirBridge.Core.StreamState.Failed, 36, true, "Password required");
        flyout.UpdateReceiver("output-d", AirBridge.Core.StreamState.Idle, 55, false);
        flyout.UpdateReceiver("output-e", AirBridge.Core.StreamState.Idle, 24, false);
        flyout.UpdateStatus(AirBridge.Core.StreamState.Degraded, "Streaming to 2 speakers · 12:48", string.Empty);
    }
}
