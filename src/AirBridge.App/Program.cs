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
            new AirBridge.Core.ReceiverInfo("kitchen", "Kitchen", "small-speaker", false, now),
            new AirBridge.Core.ReceiverInfo("living", "Living room", "smart-speaker", false, now),
            new AirBridge.Core.ReceiverInfo("bedroom", "Bedroom", "speaker", false, now),
            new AirBridge.Core.ReceiverInfo("office", "Office HomePod", "smart-speaker", false, now),
            new AirBridge.Core.ReceiverInfo("patio", "Patio TV", "media-box", false, now)
        };
        flyout.SetReceivers(receivers, new HashSet<string>(["kitchen", "living", "bedroom"]), new Dictionary<string, int>
        {
            ["kitchen"] = 30, ["living"] = 42, ["bedroom"] = 36, ["office"] = 55, ["patio"] = 24
        }, new Dictionary<string, int> { ["kitchen"] = 60, ["living"] = 0, ["bedroom"] = 20 });
        flyout.UpdateReceiver("kitchen", AirBridge.Core.StreamState.Streaming, 30, true, alignmentTrimMilliseconds: 60);
        flyout.UpdateReceiver("living", AirBridge.Core.StreamState.Reconnecting, 42, true, "Retrying");
        flyout.UpdateReceiver("bedroom", AirBridge.Core.StreamState.Failed, 36, true, "Password required");
        flyout.UpdateReceiver("office", AirBridge.Core.StreamState.Idle, 55, false);
        flyout.UpdateReceiver("patio", AirBridge.Core.StreamState.Idle, 24, false);
        flyout.UpdateStatus(AirBridge.Core.StreamState.Degraded, "Streaming to 2 speakers · 12:48", string.Empty);
    }
}
