using System.Text.Json;
using AirBridge.App;
using AirBridge.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

RuntimeProfile.Configure(null, requireIsolatedProfile: true);
AppLog.Initialize();
var command = args.FirstOrDefault() ?? "--full-pipeline";
var report = new Dictionary<string, object?>
{
    ["command"] = command, ["status"] = "failed", ["acoustic_output_proven"] = false,
    ["started_utc"] = DateTimeOffset.UtcNow
};
AirBridgeController? controller = null;
PythonRaopClient? host = null;
FileStream? hardwareLock = null;
var exitCode = 1;
try
{
    if (command == "--ping")
    {
        host = new PythonRaopClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        var response = await host.PingAsync(deadline.Token);
        var acknowledged = response.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        report["criteria"] = new { host_ping_acknowledged = acknowledged, receiver_transport_tested = false };
        if (!acknowledged) throw new InvalidOperationException("The RAOP host did not acknowledge ping.");
    }
    else
    {
        if (Environment.GetEnvironmentVariable("AIRBRIDGE_RUN_HARDWARE_TESTS") != "1")
            throw new InvalidOperationException("Set AIRBRIDGE_RUN_HARDWARE_TESTS=1 to explicitly enable audio-device and receiver diagnostics.");
        try
        {
            hardwareLock = new FileStream(Path.Combine(Path.GetTempPath(), "AirBridge.HardwareDiagnostics.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { throw new InvalidOperationException("Another worktree owns the hardware diagnostic lease."); }

        if (command == "--devices")
        {
            using var enumerator = new MMDeviceEnumerator();
            using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            report["devices"] = devices.Select(device => device.FriendlyName).ToArray();
            report["criteria"] = new { devices_enumerated = true, microphone_recorded = false };
        }
        else
        {
            var namedCommand = command.StartsWith("--", StringComparison.Ordinal);
            if (namedCommand && command is not ("--full-pipeline" or "--start-volume" or "--measure-delay" or "--volume-live"))
                throw new ArgumentException("Unknown diagnostic command.");
            var target = namedCommand ? args.ElementAtOrDefault(1) : command;
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Specify one exact discovered receiver name after the command.");
            using var operationDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var token = operationDeadline.Token;
            controller = new AirBridgeController();
            controller.ConfigureSettings(new AirBridgeSettings { SilenceStandbyEnabled = false });
            await controller.InitializeAsync(token);
            var receivers = await controller.DiscoverAsync(token);
            var receiver = receivers.SingleOrDefault(item => item.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The exact target receiver was not discovered.");
            report["target"] = receiver.Name;

            if (command == "--start-volume")
            {
                var count = ParseBounded(args.ElementAtOrDefault(2), 10, 1, 20);
                var attempts = new List<object>();
                for (var attempt = 1; attempt <= count; attempt++)
                {
                    await controller.StartSystemAsync(receiver, token);
                    await WaitForStreamingAsync(controller, receiver.Id, token);
                    var configured = CurrentVolume(controller, receiver.Id);
                    if (configured != ReceiverVolumePlan.SafeDefault) throw new InvalidOperationException("Unexpected initial volume configuration.");
                    attempts.Add(new { attempt, state = "Streaming", configured_volume = configured });
                    await controller.StopAsync(token);
                }
                report["attempts"] = attempts;
                report["criteria"] = new { every_start_reached_streaming = true, initial_volume_configuration = ReceiverVolumePlan.SafeDefault };
            }
            else if (command == "--measure-delay")
            {
                await controller.StartSystemAsync(receiver, token);
                await WaitForStreamingAsync(controller, receiver.Id, token);
                var result = await controller.MeasureAcousticDelayAsync(receiver.Id, token);
                report["measurement"] = new { median_delay_ms = result.MedianMilliseconds, samples_ms = result.DelaysMilliseconds };
                report["criteria"] = new { acoustic_chirps_detected = result.DelaysMilliseconds.Count > 0, clean_program_audio_measured = false };
                if (result.DelaysMilliseconds.Count == 0) throw new InvalidOperationException("No acoustic chirps were detected.");
            }
            else if (command == "--volume-live")
            {
                await controller.StartSystemAsync([receiver], new Dictionary<string, int> { [receiver.Id] = 14 }, token);
                await WaitForStreamingAsync(controller, receiver.Id, token);
                var values = new List<int>();
                foreach (var volume in new[] { 10, 18, 12 })
                {
                    await controller.SetReceiverVolumeAsync(receiver.Id, volume, token);
                    if (CurrentVolume(controller, receiver.Id) != volume) throw new InvalidOperationException("Controller volume did not update after host acknowledgement.");
                    values.Add(volume);
                }
                report["criteria"] = new { host_volume_commands_acknowledged = true, configured_volumes = values, audible_loudness_tested = false };
            }
            else
            {
                var seconds = ParseBounded(args.ElementAtOrDefault(namedCommand ? 2 : 1), 8, 2, 30);
                var volume = ParseBounded(args.ElementAtOrDefault(namedCommand ? 3 : 2), ReceiverVolumePlan.SafeDefault, 0, 100);
                await controller.StartSystemAsync([receiver], new Dictionary<string, int> { [receiver.Id] = volume }, token);
                await WaitForStreamingAsync(controller, receiver.Id, token);
                using var output = new WaveOut();
                var signal = new SignalGenerator(48000, 2) { Gain = 0.15, Frequency = 523.25, Type = SignalGeneratorType.Sin };
                output.Init(signal.Take(TimeSpan.FromSeconds(seconds + 1)).ToWaveProvider());
                var observationGate = new object();
                var window = new StreamVerificationWindow(controller.ObserveStreamVerification(), window: TimeSpan.FromSeconds(seconds));
                void ObserveRoute(object? sender, RouteInfo route) { lock (observationGate) window.Observe(controller.ObserveStreamVerification()); }
                controller.Coordinator.RouteChanged += ObserveRoute;
                try
                {
                    output.Play();
                    while (!window.Complete)
                    {
                        await Task.Delay(250, token);
                        lock (observationGate) window.Observe(controller.ObserveStreamVerification());
                    }
                    StreamVerificationResult result;
                    lock (observationGate) result = window.Result();
                    report["verification"] = result with { Receivers = result.Receivers.ToDictionary(_ => "receiver-1", item => item.Value) };
                    report["criteria"] = new { state_continuity_checked = true, pcm_progress_checked = true,
                        per_receiver_active_starvation_checked = true, bounded_ring_checked = true, microphone_recorded = false };
                    if (result.Status != StreamVerificationStatus.Verified)
                    {
                        report["status"] = result.Status == StreamVerificationStatus.Inconclusive ? "inconclusive" : "failed";
                        throw new InvalidOperationException(result.Note);
                    }
                }
                finally
                {
                    controller.Coordinator.RouteChanged -= ObserveRoute;
                    output.Stop();
                }
            }
        }
    }
    report["status"] = "passed";
    exitCode = 0;
}
catch (Exception exception)
{
    report["error"] = AgentActivitySanitizer.Sanitize(exception.Message);
    exitCode = report["status"] as string == "inconclusive" ? 2 : 1;
}
finally
{
    try
    {
        using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        if (controller is not null) await Task.Run(() => controller.ShutdownAsync(cleanupDeadline.Token)).WaitAsync(cleanupDeadline.Token);
        if (host is not null) await host.ShutdownAsync(cleanupDeadline.Token).WaitAsync(cleanupDeadline.Token);
        report["cleanup_completed"] = true;
    }
    catch (Exception exception)
    {
        controller?.ForceCleanup();
        host?.ForceTerminate();
        report["cleanup_completed"] = false;
        report["cleanup_error"] = AgentActivitySanitizer.Sanitize(exception.Message);
        report["status"] = "failed";
        exitCode = 1;
    }
    hardwareLock?.Dispose();
    AppLog.Shutdown();
    report["finished_utc"] = DateTimeOffset.UtcNow;
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
}
return exitCode;

static int ParseBounded(string? value, int fallback, int minimum, int maximum) =>
    int.TryParse(value, out var parsed) ? Math.Clamp(parsed, minimum, maximum) : fallback;

static async Task WaitForStreamingAsync(AirBridgeController controller, string receiverId, CancellationToken cancellationToken)
{
    using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    startup.CancelAfter(TimeSpan.FromSeconds(20));
    while (true)
    {
        var playback = controller.ReceiverPlayback.FirstOrDefault(item => item.Receiver.Id == receiverId);
        if (playback?.State == StreamState.Streaming) return;
        if (playback?.State == StreamState.Failed) throw new InvalidOperationException(playback.LastError ?? "Receiver failed to start.");
        await Task.Delay(100, startup.Token);
    }
}

static int CurrentVolume(AirBridgeController controller, string receiverId) =>
    controller.ReceiverPlayback.Single(item => item.Receiver.Id == receiverId).Volume;
