using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class MultiReceiverTests
{
    private static readonly ReceiverInfo SpeakerA = new("speakerA", "Speaker A", "local", false, DateTimeOffset.UtcNow);
    private static readonly ReceiverInfo Office = new("office", "Office", "local", false, DateTimeOffset.UtcNow);

    private sealed class PumpEndpoint(string name) : IAudioPipeEndpoint
    {
        public string PipeName { get; } = name;
        public bool IsConnected { get; set; } = true;
        public bool CanAcceptWrite { get; set; } = true;
        public int DropsRemaining { get; set; }
        public List<byte[]> Writes { get; } = [];

        public AudioPipeWriteResult Write(byte[] pcm, bool tolerateBackpressure = false)
        {
            if (!IsConnected) return AudioPipeWriteResult.Unavailable;
            if (DropsRemaining > 0)
            {
                DropsRemaining--;
                return AudioPipeWriteResult.Dropped;
            }
            Writes.Add(pcm.ToArray());
            return AudioPipeWriteResult.Accepted;
        }
    }

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
    public async Task GoLivePrefillsEveryLegWithBaseTargetPlusTrimAndKeepsZeroTrimCushion()
    {
        var pump = new SharedAudioPump();
        var trimmedBuffer = PumpBuffer(0x41, 40);
        var zeroTrimBuffer = PumpBuffer(0x52, 40);
        var trimmed = new PumpEndpoint("trimmed");
        var zeroTrim = new PumpEndpoint("zero");
        pump.AddLeg("trimmed", trimmedBuffer, trimmed, 10);
        pump.AddLeg("zero", zeroTrimBuffer, zeroTrim, 0);
        pump.BeginGroup(["trimmed", "zero"]);
        pump.MarkReady("trimmed");
        pump.MarkReady("zero");

        await pump.PumpOnceAsync();
        trimmedBuffer.Write(Block(0x41));
        zeroTrimBuffer.Write(Block(0x52));
        await pump.PumpOnceAsync();
        trimmedBuffer.Write(Block(0x41));
        zeroTrimBuffer.Write(Block(0x52));
        await pump.PumpOnceAsync();

        Assert.All(trimmed.Writes[0], value => Assert.Equal(0, value));
        Assert.All(trimmed.Writes[1], value => Assert.Equal(0, value));
        Assert.All(trimmed.Writes[2][..1764], value => Assert.Equal(0, value));
        Assert.All(trimmed.Writes[2][1764..], value => Assert.Equal(0x41, value));
        Assert.All(zeroTrim.Writes[0], value => Assert.Equal(0, value));
        Assert.All(zeroTrim.Writes[1], value => Assert.Equal(0, value));
        Assert.All(zeroTrim.Writes[2], value => Assert.Equal(0x52, value));
        Assert.Equal(ReceiverAlignmentPlan.ToPcmByteCount(50), trimmedBuffer.Snapshot().FillBytes);
        Assert.Equal(ReceiverAlignmentPlan.ToPcmByteCount(40), zeroTrimBuffer.Snapshot().FillBytes);

        await pump.PumpOnceAsync();

        Assert.Equal(0, zeroTrimBuffer.Snapshot().Underruns);
    }

    [Fact]
    public async Task ActiveStarvationDropsExactlyThePaddedBytesWhenDataResumes()
    {
        var pump = new SharedAudioPump();
        var delayedBuffer = PumpBuffer(0x11, 0);
        var siblingBuffer = PumpBuffer(0x11, 0);
        var delayed = new PumpEndpoint("delayed");
        var sibling = new PumpEndpoint("sibling");
        pump.AddLeg("delayed", delayedBuffer, delayed, 0);
        pump.AddLeg("sibling", siblingBuffer, sibling, 0);
        pump.BeginGroup(["delayed", "sibling"]);
        pump.MarkReady("delayed");
        pump.MarkReady("sibling");
        await pump.PumpOnceAsync();

        delayedBuffer.Write([], producerActive: true);
        siblingBuffer.Write(Block(0x22));
        await pump.PumpOnceAsync();

        delayedBuffer.Write(Block(0x22));
        delayedBuffer.Write(Block(0x33));
        siblingBuffer.Write(Block(0x33));
        await pump.PumpOnceAsync();

        Assert.All(delayed.Writes[1], value => Assert.Equal(0, value));
        Assert.All(delayed.Writes[2], value => Assert.Equal(0x33, value));
        Assert.Equal(SharedAudioPump.BlockBytes, delayedBuffer.Snapshot().StarvedWhileActivePaddingBytes);
        Assert.Equal(0, delayedBuffer.Snapshot().FillBytes);
    }

    [Fact]
    public async Task ProducerIdlePaddingDoesNotDropAudioAfterResume()
    {
        var pump = new SharedAudioPump();
        var pausedBuffer = PumpBuffer(0x18, 0);
        var siblingBuffer = PumpBuffer(0x18, 0);
        var paused = new PumpEndpoint("paused");
        pump.AddLeg("paused", pausedBuffer, paused, 0);
        pump.AddLeg("sibling", siblingBuffer, new PumpEndpoint("sibling"), 0);
        pump.BeginGroup(["paused", "sibling"]);
        pump.MarkReady("paused");
        pump.MarkReady("sibling");
        await pump.PumpOnceAsync();

        pausedBuffer.Write([], producerActive: false);
        siblingBuffer.Write([], producerActive: false);
        await pump.PumpOnceAsync();
        pausedBuffer.Write(Block(0x29));
        siblingBuffer.Write(Block(0x29));
        await pump.PumpOnceAsync();

        Assert.All(paused.Writes[1], value => Assert.Equal(0, value));
        Assert.All(paused.Writes[2], value => Assert.Equal(0x29, value));
        Assert.Equal(SharedAudioPump.BlockBytes, pausedBuffer.Snapshot().ProducerIdlePaddingBytes);
        Assert.Equal(0, pausedBuffer.Snapshot().StarvedWhileActivePaddingBytes);
    }

    [Fact]
    public async Task BackpressureDropInsertsEquivalentSilenceBeforeContinuingAudio()
    {
        var pump = new SharedAudioPump();
        var delayedBuffer = PumpBuffer(0x14, 0);
        var siblingBuffer = PumpBuffer(0x14, 0);
        var delayed = new PumpEndpoint("delayed") { DropsRemaining = 1 };
        var sibling = new PumpEndpoint("sibling");
        pump.AddLeg("delayed", delayedBuffer, delayed, 0);
        pump.AddLeg("sibling", siblingBuffer, sibling, 0);
        pump.BeginGroup(["delayed", "sibling"]);
        pump.MarkReady("delayed");
        pump.MarkReady("sibling");
        await pump.PumpOnceAsync();

        delayedBuffer.Write(Block(0x25));
        siblingBuffer.Write(Block(0x25));
        await pump.PumpOnceAsync();
        delayedBuffer.Write(Block(0x36));
        siblingBuffer.Write(Block(0x36));
        await pump.PumpOnceAsync();

        Assert.All(delayed.Writes[0], value => Assert.Equal(0, value));
        Assert.All(delayed.Writes[1], value => Assert.Equal(0x25, value));
        Assert.All(sibling.Writes[0], value => Assert.Equal(0x14, value));
        Assert.All(sibling.Writes[1], value => Assert.Equal(0x25, value));
        Assert.All(sibling.Writes[2], value => Assert.Equal(0x36, value));
    }

    [Fact]
    public async Task ExcessCorrectionDebtResyncsEveryLegAndReappliesPrefillAndTrim()
    {
        var pump = new SharedAudioPump();
        var starvedBuffer = PumpBuffer(0x47, 20, 64);
        var siblingBuffer = PumpBuffer(0x47, 20, 64);
        var starved = new PumpEndpoint("starved");
        var sibling = new PumpEndpoint("sibling");
        pump.AddLeg("starved", starvedBuffer, starved, 0);
        pump.AddLeg("sibling", siblingBuffer, sibling, 10);
        pump.BeginGroup(["starved", "sibling"]);
        pump.MarkReady("starved");
        pump.MarkReady("sibling");
        await pump.PumpOnceAsync();
        starvedBuffer.Write(Block(0x47));
        siblingBuffer.Write(Block(0x47));
        await pump.PumpOnceAsync();

        starvedBuffer.Clear(advanceEpoch: false);
        starvedBuffer.Write([], producerActive: true);
        for (var index = 0; index < 13; index++)
        {
            siblingBuffer.Write(Block(0x71));
            await pump.PumpOnceAsync();
        }

        Assert.Equal(1, pump.GroupResyncCount);
        Assert.Equal(0, pump.GetAlignmentTrim("starved"));
        Assert.Equal(10, pump.GetAlignmentTrim("sibling"));

        starvedBuffer.Write(Block(0x71));
        starvedBuffer.Write(Block(0x71));
        siblingBuffer.Write(Block(0x71));
        siblingBuffer.Write(Block(0x71));
        await pump.PumpOnceAsync();
        starvedBuffer.Write(Block(0x71));
        siblingBuffer.Write(Block(0x71));
        await pump.PumpOnceAsync();

        Assert.All(starved.Writes[^2], value => Assert.Equal(0, value));
        Assert.All(starved.Writes[^1], value => Assert.Equal(0x71, value));
        Assert.All(sibling.Writes[^2], value => Assert.Equal(0, value));
        Assert.All(sibling.Writes[^1][..1764], value => Assert.Equal(0, value));
        Assert.All(sibling.Writes[^1][1764..], value => Assert.Equal(0x71, value));
    }

    [Fact]
    public async Task ReadyLateJoinResyncsLiveGroupOnceWithoutChangingConfiguredTrims()
    {
        var pump = new SharedAudioPump();
        var firstBuffer = PumpBuffer(0x12, 0);
        var secondBuffer = PumpBuffer(0x23, 0);
        var first = new PumpEndpoint("first");
        var second = new PumpEndpoint("second");
        pump.AddLeg("first", firstBuffer, first, 10);
        pump.AddLeg("second", secondBuffer, second, 0);
        pump.BeginGroup(["first", "second"]);
        pump.MarkReady("first");
        pump.MarkReady("second");
        await pump.PumpOnceAsync();

        firstBuffer.Write(Block(0x12));
        secondBuffer.Write(Block(0x23));
        var thirdBuffer = PumpBuffer(0x34, 0);
        var third = new PumpEndpoint("third");
        pump.AddLeg("third", thirdBuffer, third, 20);
        pump.MarkReady("third", resyncGroup: true);
        await pump.PumpOnceAsync();

        Assert.Equal(1, pump.GroupResyncCount);
        Assert.Equal(10, pump.GetAlignmentTrim("first"));
        Assert.Equal(0, pump.GetAlignmentTrim("second"));
        Assert.Equal(20, pump.GetAlignmentTrim("third"));
        Assert.All(first.Writes[^1][..1764], value => Assert.Equal(0, value));
        Assert.All(first.Writes[^1][1764..], value => Assert.Equal(0x12, value));
        Assert.All(second.Writes[^1], value => Assert.Equal(0x23, value));
        Assert.All(third.Writes[^1], value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task CalibrationModeSuppressesPrefillAndAllCorrectionDebt()
    {
        var pump = new SharedAudioPump();
        var measuredBuffer = PumpBuffer(0x45, 40);
        var siblingBuffer = PumpBuffer(0x45, 40);
        var measured = new PumpEndpoint("measured") { DropsRemaining = 1 };
        pump.AddLeg("measured", measuredBuffer, measured, 10);
        pump.AddLeg("sibling", siblingBuffer, new PumpEndpoint("sibling"), 0);
        pump.SetCalibrationMode(true);
        pump.BeginGroup(["measured", "sibling"]);
        pump.MarkReady("measured");
        pump.MarkReady("sibling");
        await pump.PumpOnceAsync();

        measuredBuffer.Write(Block(0x56));
        siblingBuffer.Write(Block(0x56));
        await pump.PumpOnceAsync();
        measuredBuffer.Write([], producerActive: true);
        siblingBuffer.Write(Block(0x67));
        await pump.PumpOnceAsync();
        measuredBuffer.Write(Block(0x78));
        siblingBuffer.Write(Block(0x78));
        await pump.PumpOnceAsync();

        Assert.All(measured.Writes[0], value => Assert.Equal(0x56, value));
        Assert.All(measured.Writes[1], value => Assert.Equal(0, value));
        Assert.All(measured.Writes[2], value => Assert.Equal(0x78, value));
        Assert.Equal(0, pump.GroupResyncCount);
        Assert.Equal(10, pump.GetAlignmentTrim("measured"));
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
                CalibrationMicrophoneName = "Desk microphone",
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
            Assert.Equal("Desk microphone", restored.CalibrationMicrophoneName);
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

    private static BoundedPcmBuffer PumpBuffer(byte value, int targetMilliseconds, int capacityBlocks = 8)
    {
        var buffer = new BoundedPcmBuffer(SharedAudioPump.BlockBytes * capacityBlocks, targetMilliseconds);
        buffer.Write(Block(value));
        return buffer;
    }

    private static byte[] Block(byte value) => Enumerable.Repeat(value, SharedAudioPump.BlockBytes).ToArray();
}
