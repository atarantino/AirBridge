using AirBridge.Core;

namespace AirBridge.App;

public sealed partial class MainForm
{
    private readonly HttpClient _updateHttp = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly CancellationTokenSource _updateCancellation = new();
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    private AppUpdate? _availableUpdate;
    private SettingsForm? _openSettings;
    private bool _updateBusy;
    private string _updateMessage = "Check for a newer version of AirBridge.";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string? PendingUpdateInstaller { get; private set; }
    private bool UpdatesAllowed => !IsTestSession && !RuntimeProfile.IsIsolated;

    private void StartUpdateChecks()
    {
        if (!UpdatesAllowed) return;
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_availableUpdate is not null) ShowSettingsDialog("Advanced");
        };
        _updateTimer.Tick += async (_, _) =>
        {
            if (_settings.AutomaticallyCheckForUpdates) await CheckUpdatesAsync(notify: true);
        };
        _updateTimer.Start();
        if (_settings.AutomaticallyCheckForUpdates) _ = CheckUpdatesAsync(notify: true);
    }

    private void RefreshUpdateStatus() => _openSettings?.SetUpdateStatus(
        UpdatesAllowed ? _updateMessage : "Updates are disabled in development and fixture sessions.",
        _updateBusy || !UpdatesAllowed, _availableUpdate is not null);

    private async Task CheckUpdatesAsync(bool notify = false)
    {
        if (!UpdatesAllowed || _updateBusy || _shutdownStarted) return;
        _updateBusy = true;
        _updateMessage = "Checking for updates…";
        RefreshUpdateStatus();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_updateCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var previous = _availableUpdate?.Version;
            _availableUpdate = await new AppUpdateClient(_updateHttp).CheckAsync(typeof(Program).Assembly.GetName().Version!, timeout.Token);
            _updateMessage = _availableUpdate is { } update
                ? $"AirBridge {update.Version} is available."
                : "You're up to date.";
            if (notify && _settings.AutomaticallyCheckForUpdates && !_shutdownStarted && _availableUpdate is { } newer && newer.Version != previous)
                _tray.ShowBalloonTip(10000, "AirBridge update available",
                    $"Version {newer.Version} is ready. Click to review and install. Your settings will be kept.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _availableUpdate = null;
            if (!_updateCancellation.IsCancellationRequested)
            {
                _updateMessage = ex is InvalidDataException ? ex.Message : "Couldn't check for updates. Check your internet connection and try again, or open Release notes.";
                AppLog.Warning("updates", "Update check failed: " + ex.GetType().Name);
            }
        }
        finally { _updateBusy = false; RefreshUpdateStatus(); }
    }

    private async Task InstallUpdateAsync(SettingsForm dialog)
    {
        if (!UpdatesAllowed || _updateBusy || _availableUpdate is not { } update || _shutdownStarted) return;
        if (MessageBox.Show(dialog,
            $"Download and install AirBridge {update.Version}?\n\nAirBridge will close and streaming will stop. Windows may ask for administrator approval. Reopen AirBridge after setup finishes.\n\nSaved API keys, speaker pairings and preferences will be kept. Choose Save first if you have unsaved changes in Settings.",
            "Update AirBridge", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        _updateBusy = true;
        _updateMessage = "Downloading update…";
        RefreshUpdateStatus();
        using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_updateCancellation.Token);
        // Closing Settings cancels the download without stopping audio or the app.
        FormClosedEventHandler cancelDownload = (_, _) => downloadCancellation.Cancel();
        dialog.FormClosed += cancelDownload;
        try
        {
            downloadCancellation.CancelAfter(TimeSpan.FromMinutes(10));
            var progress = new Progress<int>(percent =>
            {
                if (_shutdownStarted) return;
                _updateMessage = $"Downloading update… {percent}%";
                RefreshUpdateStatus();
            });
            var installer = await new AppUpdateClient(_updateHttp).DownloadAsync(update, RuntimeProfile.DataDirectory, progress, downloadCancellation.Token);
            if (_shutdownStarted || dialog.IsDisposed) return;
            // The normal shutdown path stops audio and the owned host before Program opens setup.
            PendingUpdateInstaller = installer;
            dialog.Close();
            RequestQuit();
        }
        catch (OperationCanceledException) { _updateMessage = "Update download canceled or timed out. You can try again."; }
        catch (Exception ex)
        {
            _updateMessage = ex is InvalidDataException ? ex.Message : "Couldn't download the update. Nothing was installed. Try again or open Release notes.";
            AppLog.Warning("updates", "Update download failed: " + ex.GetType().Name);
        }
        finally
        {
            dialog.FormClosed -= cancelDownload;
            _updateBusy = false;
            RefreshUpdateStatus();
        }
    }
}
