using AirBridge.App;
using AirBridge.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

if (args.Length > 0 && args[0] == "--devices")
{
    using var enumerator = new MMDeviceEnumerator();
    using var defaultCapture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    Console.WriteLine($"Default communications microphone: {defaultCapture.FriendlyName} | {defaultCapture.ID}");
    using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
    foreach (var device in devices) Console.WriteLine($"Capture: {device.FriendlyName} | {device.ID}");
    return;
}

var command = args.FirstOrDefault() ?? "--full-pipeline";
var target = command.StartsWith("--", StringComparison.Ordinal) ? args.ElementAtOrDefault(1) ?? "Kitchen" : command;
await using var controller = new AirBridgeController();
await controller.InitializeAsync();
var receivers = await controller.DiscoverAsync();
var receiver = receivers.SingleOrDefault(item => item.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
    ?? throw new InvalidOperationException($"Receiver {target} was not discovered.");

if (command == "--start-volume")
{
    var count = int.TryParse(args.ElementAtOrDefault(2), out var parsedCount) ? Math.Clamp(parsedCount, 1, 20) : 10;
    for (var attempt = 1; attempt <= count; attempt++)
    {
        await controller.StartSystemAsync(receiver);
        await WaitForStreamingAsync(controller, receiver.Id, TimeSpan.FromSeconds(20));
        var playback = controller.ReceiverPlayback.Single(item => item.Receiver.Id == receiver.Id);
        Console.WriteLine($"attempt={attempt}/{count} state={playback.State} configured_volume={playback.Volume}%");
        await Task.Delay(500);
        await controller.StopAsync();
        await Task.Delay(500);
    }
    Console.WriteLine($"Completed {count} post-RECORD starts to {receiver.Name} at 30% default volume.");
    return;
}

if (command == "--measure-delay")
{
    await controller.StartSystemAsync(receiver);
    await WaitForStreamingAsync(controller, receiver.Id, TimeSpan.FromSeconds(20));
    var result = await controller.MeasureAcousticDelayAsync(receiver.Id);
    Console.WriteLine($"receiver={result.ReceiverName} median_delay_ms={result.MedianMilliseconds} samples_ms=[{string.Join(",", result.DelaysMilliseconds)}]");
    Console.WriteLine("Use this value in the browser extension.");
    await controller.StopAsync();
    return;
}

if (command == "--volume-live")
{
    const int initialVolume = 14;
    await controller.StartSystemAsync(
        [receiver],
        new Dictionary<string, int> { [receiver.Id] = initialVolume });
    await WaitForStreamingAsync(controller, receiver.Id, TimeSpan.FromSeconds(20));

    Console.WriteLine($"receiver={receiver.Name} streaming initial_volume={CurrentVolume(controller, receiver.Id)}%");
    foreach (var volume in new[] { 10, 18, 12 })
    {
        await controller.SetReceiverVolumeAsync(receiver.Id, volume);
        Console.WriteLine($"set_volume={volume}% controller_volume={CurrentVolume(controller, receiver.Id)}%");
        await Task.Delay(750);
    }

    await controller.StopAsync();
    Console.WriteLine("Live volume diagnostic completed.");
    return;
}

var seconds = int.TryParse(args.ElementAtOrDefault(command.StartsWith("--", StringComparison.Ordinal) ? 2 : 1), out var parsed) ? Math.Clamp(parsed, 2, 30) : 8;
var pipelineVolume = int.TryParse(args.ElementAtOrDefault(command.StartsWith("--", StringComparison.Ordinal) ? 3 : 2), out var parsedVolume)
    ? Math.Clamp(parsedVolume, 0, 100)
    : ReceiverVolumePlan.SafeDefault;
Console.WriteLine($"Starting full Windows loopback pipeline to {receiver.Name} ({receiver.Id})");
await controller.StartSystemAsync(
    [receiver],
    new Dictionary<string, int> { [receiver.Id] = pipelineVolume });

using var output = new WaveOut();
var signal = new SignalGenerator(48000, 2)
{
    Gain = 0.15,
    Frequency = 523.25,
    Type = SignalGeneratorType.Sin
};
output.Init(signal.Take(TimeSpan.FromSeconds(seconds)).ToWaveProvider());
output.Play();

for (var index = 0; index < seconds + 3; index++)
{
    await Task.Delay(1000);
    var health = controller.Coordinator.Health();
    Console.WriteLine($"t={index + 1,2}s state={health.State,-12} fill={health.Buffer.FillPercent,3}% underruns={health.Buffer.Underruns} overruns={health.Buffer.Overruns} idlePad={health.Buffer.ProducerIdlePaddingMilliseconds}ms starvedPad={health.Buffer.StarvedWhileActivePaddingMilliseconds}ms");
}

await controller.StopAsync();
Console.WriteLine("Full-pipeline diagnostic completed.");

static async Task WaitForStreamingAsync(AirBridgeController controller, string receiverId, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        var playback = controller.ReceiverPlayback.FirstOrDefault(item => item.Receiver.Id == receiverId);
        if (playback?.State == StreamState.Streaming) return;
        if (playback?.State == StreamState.Failed) throw new InvalidOperationException(playback.LastError ?? "Receiver failed to start.");
        await Task.Delay(100);
    }
    throw new TimeoutException("Receiver did not reach Streaming within the hardware acceptance window.");
}

static int CurrentVolume(AirBridgeController controller, string receiverId) =>
    controller.ReceiverPlayback.Single(item => item.Receiver.Id == receiverId).Volume;
