using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class MultiReceiverTests
{
    private static readonly ReceiverInfo SpeakerA = new("speakerA", "Speaker A", "local", false, DateTimeOffset.UtcNow);
    private static readonly ReceiverInfo Office = new("office", "Office", "local", false, DateTimeOffset.UtcNow);

    [Fact]
    public void FanoutDeliversIdenticalOrderedPcmToEveryReceiver()
    {
        var hub = new PcmBroadcastHub();
        var speakerA = hub.Subscribe(SpeakerA.Id);
        var office = hub.Subscribe(Office.Id);
        var first = Enumerable.Range(0, 3528).Select(index => (byte)(index % 251)).ToArray();
        var second = Enumerable.Range(0, 3528).Select(index => (byte)((index + 37) % 251)).ToArray();

        hub.Write(first);
        hub.Write(second);

        var speakerAResult = new byte[first.Length + second.Length];
        var officeResult = new byte[speakerAResult.Length];
        Assert.Equal(speakerAResult.Length, speakerA.Read(speakerAResult, false));
        Assert.Equal(officeResult.Length, office.Read(officeResult, false));
        Assert.Equal(first.Concat(second), speakerAResult);
        Assert.Equal(speakerAResult, officeResult);
    }

    [Fact]
    public void StalledReceiverOverrunDoesNotAffectHealthyReceiver()
    {
        var hub = new PcmBroadcastHub();
        var stalled = hub.Subscribe(SpeakerA.Id);
        var healthy = hub.Subscribe(Office.Id);
        var block = new byte[3528];
        var healthyRead = new byte[block.Length];

        for (var index = 0; index < 300; index++)
        {
            block.AsSpan().Fill((byte)(index % 255));
            hub.Write(block);
            Assert.Equal(block.Length, healthy.Read(healthyRead, false));
            Assert.Equal(block, healthyRead);
        }

        Assert.True(stalled.Snapshot().Overruns > 0);
        Assert.Equal(0, healthy.Snapshot().Overruns);
        Assert.Equal(0, healthy.Snapshot().Underruns);
    }

    [Fact]
    public async Task CalibrationIsMixedIntoSharedCaptureFanoutForEveryLeg()
    {
        var hub = new PcmBroadcastHub();
        var speakerA = hub.Subscribe(SpeakerA.Id);
        var office = hub.Subscribe(Office.Id);
        short[] samples = [120, -120, 240, -240];
        var pcm = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
        var sequence = new ChirpSequence(pcm, [0, 4], SharedAudioPump.SampleRate, SharedAudioPump.Channels);

        var calibration = hub.PlayCalibrationAsync(sequence);
        hub.AdvanceCalibration(pcm.Length);
        var emissions = await calibration;

        var speakerAResult = new byte[pcm.Length];
        var officeResult = new byte[pcm.Length];
        Assert.Equal(pcm.Length, speakerA.Read(speakerAResult, false));
        Assert.Equal(pcm.Length, office.Read(officeResult, false));
        Assert.Equal(pcm, speakerAResult);
        Assert.Equal(pcm, officeResult);
        Assert.Equal(2, emissions.Count);
        Assert.True(emissions[1] >= emissions[0]);
    }

    [Fact]
    public async Task CalibrationAdvancesFromSharedPumpClockWithoutCaptureCallbacks()
    {
        var hub = new PcmBroadcastHub();
        var receiver = hub.Subscribe(SpeakerA.Id);
        var pcm = Enumerable.Repeat((byte)0x2A, SharedAudioPump.BlockBytes * 2).ToArray();
        var sequence = new ChirpSequence(pcm, [0, SharedAudioPump.BlockBytes], SharedAudioPump.SampleRate, SharedAudioPump.Channels);
        var pump = new SharedAudioPump(
            hub.MonitorBuffer,
            advanceCalibration: () => hub.AdvanceCalibration(SharedAudioPump.BlockBytes));

        var calibration = hub.PlayCalibrationAsync(sequence);
        await pump.PumpOnceAsync();
        await pump.PumpOnceAsync();
        var emissions = await calibration;

        var received = new byte[pcm.Length];
        Assert.Equal(pcm.Length, receiver.Read(received, false));
        Assert.Equal(pcm, received);
        Assert.Equal(2, emissions.Count);
    }

    [Fact]
    public void AggregateRouteIsDegradedWhenOnlyOneReceiverFails()
    {
        var buffer = new BoundedPcmBuffer(176400);
        var coordinator = new StreamCoordinator(buffer);
        coordinator.Begin(SpeakerA.Id, SpeakerA.Name, CaptureMode.SystemMix, null);
        coordinator.UpdateDestinations([
            new(SpeakerA, StreamState.Streaming, 30, DateTimeOffset.UtcNow, null),
            new(Office, StreamState.Streaming, 40, DateTimeOffset.UtcNow, null)
        ]);
        Assert.Equal(StreamState.Streaming, coordinator.Route.State);

        coordinator.UpdateDestinations([
            new(SpeakerA, StreamState.Streaming, 30, DateTimeOffset.UtcNow, null),
            new(Office, StreamState.Failed, 40, DateTimeOffset.UtcNow, "offline")
        ]);

        Assert.Equal(StreamState.Degraded, coordinator.Route.State);
        Assert.Equal(2, coordinator.Route.Destinations.Count);
        Assert.Equal("Speaker A + 1", coordinator.Route.ReceiverName);
    }

    [Fact]
    public void SettingsRoundTripSpeakerGroupsAndVolumes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"AirBridge.Tests.{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AirBridgeSettings
            {
                SelectedReceiverIds = [SpeakerA.Id, Office.Id],
                ReceiverVolumes = new() { [SpeakerA.Id] = 30, [Office.Id] = 42 },
                ReceiverAlignmentTrimMs = new() { [SpeakerA.Id] = 120, [Office.Id] = 0 },
                SilenceStandbyEnabled = true,
                SilenceStandbySeconds = 120,
                SpeakerGroups = [new("downstairs", "Downstairs", [SpeakerA.Id, Office.Id])],
                ThemeMode = "dark"
            });

            var restored = store.Load();
            Assert.Equal([SpeakerA.Id, Office.Id], restored.SelectedReceiverIds);
            Assert.Equal(42, restored.ReceiverVolumes[Office.Id]);
            Assert.Equal(120, restored.ReceiverAlignmentTrimMs[SpeakerA.Id]);
            Assert.Equal(0, restored.ReceiverAlignmentTrimMs[Office.Id]);
            Assert.True(restored.SilenceStandbyEnabled);
            Assert.Equal(120, restored.SilenceStandbySeconds);
            Assert.Equal("Downstairs", Assert.Single(restored.SpeakerGroups).Name);
            Assert.Equal("dark", restored.ThemeMode);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void OlderSettingsLoadWithAlignmentAndStandbyDefaultsWithoutLosingExistingValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"AirBridge.Tests.{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """
                {
                  "defaultReceiverName": "Family Room",
                  "selectedReceiverIds": ["beam"],
                  "receiverVolumes": { "beam": 17 },
                  "themeMode": "dark"
                }
                """);
            var store = new SettingsStore(path);

            var restored = store.Load();
            store.Save(restored);
            var reloaded = store.Load();

            Assert.Equal("Family Room", reloaded.DefaultReceiverName);
            Assert.Equal(["beam"], reloaded.SelectedReceiverIds);
            Assert.Equal(17, reloaded.ReceiverVolumes["beam"]);
            Assert.Empty(reloaded.ReceiverAlignmentTrimMs);
            Assert.True(reloaded.SilenceStandbyEnabled);
            Assert.Equal(60, reloaded.SilenceStandbySeconds);
            Assert.Equal("dark", reloaded.ThemeMode);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void SettingsLoadNormalizesExplicitNullCollectionsAndUnsafeBounds()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"AirBridge.Tests.{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """
                {
                  "selectedReceiverIds": null,
                  "receiverVolumes": { "beam": 140 },
                  "receiverAlignmentTrimMs": null,
                  "speakerGroups": null,
                  "silenceStandbySeconds": 999
                }
                """);

            var restored = new SettingsStore(path).Load();

            Assert.Empty(restored.SelectedReceiverIds);
            Assert.Equal(100, restored.ReceiverVolumes["beam"]);
            Assert.Empty(restored.ReceiverAlignmentTrimMs);
            Assert.Empty(restored.SpeakerGroups);
            Assert.Equal(600, restored.SilenceStandbySeconds);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
