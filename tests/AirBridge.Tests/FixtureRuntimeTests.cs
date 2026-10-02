using System.Text.Json;
using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class FixtureRuntimeTests
{
    [Fact]
    public async Task HealthyFixtureCarriesGeneratedPcmThroughControllerAndReceiverPipes()
    {
        var fixture = new FixtureRuntime("healthy");
        await using var controller = new AirBridgeController(fixture.Raop, fixture.Capture);
        fixture.Capture.WritePcm = controller.AppendCapturedPcm;
        await controller.InitializeAsync();
        var receivers = await controller.DiscoverAsync();
        await controller.StartSystemAsync(receivers.Take(2).ToArray());
        Assert.Equal(StreamState.Streaming, controller.Coordinator.Route.State);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var snapshot = JsonSerializer.SerializeToElement(fixture.Snapshot());
            if (snapshot.GetProperty("receivers").EnumerateArray().All(item => item.GetProperty("signalBlocks").GetInt64() > 0)) break;
            await Task.Delay(25, deadline.Token);
        }
        await controller.SetReceiverVolumeAsync(receivers[0].Id, 47);
        var state = JsonSerializer.SerializeToElement(fixture.Snapshot());
        Assert.Equal(47, state.GetProperty("receivers")[0].GetProperty("volume").GetInt32());
        Assert.True(state.GetProperty("capture").GetProperty("blocksGenerated").GetInt64() > 0);
        await controller.StopAsync();
        Assert.Equal(StreamState.Idle, controller.Coordinator.Route.State);
        Assert.Empty(fixture.Raop.Snapshot());
    }

    [Fact]
    public async Task FailureFixtureLeavesHealthySiblingStreaming()
    {
        var fixture = new FixtureRuntime("partial-failure");
        await using var controller = new AirBridgeController(fixture.Raop, fixture.Capture);
        fixture.Capture.WritePcm = controller.AppendCapturedPcm;
        await controller.InitializeAsync();
        var receivers = await controller.DiscoverAsync();
        await controller.StartSystemAsync(receivers.Take(2).ToArray());
        Assert.Equal(StreamState.Degraded, controller.Coordinator.Route.State);
        Assert.Equal(StreamState.Streaming, controller.ReceiverPlayback.Single(item => item.Receiver.Id == "fixture-a").State);
        Assert.Equal(StreamState.Failed, controller.ReceiverPlayback.Single(item => item.Receiver.Id == "fixture-b").State);
        await controller.StopAsync();
        Assert.Empty(fixture.Raop.Snapshot());
    }

    [Fact]
    public async Task PairingFixtureRequiresExplicitCorrectCode()
    {
        var fixture = new FixtureRuntime("pairing");
        await fixture.Raop.StartAsync();
        var receivers = await fixture.Raop.DiscoverAsync();
        Assert.True(receivers.Single(item => item.Id == "fixture-b").RequiresPairing);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Raop.FinishPairingAsync("fixture-b", "0000"));
        await fixture.Raop.BeginPairingAsync("fixture-b");
        await fixture.Raop.FinishPairingAsync("fixture-b", "1234");
        Assert.False((await fixture.Raop.DiscoverAsync()).Single(item => item.Id == "fixture-b").RequiresPairing);
        await fixture.Raop.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NoReceiversFixtureDoesNotDiscoverHardware()
    {
        var fixture = new FixtureRuntime("no-receivers");
        await fixture.Raop.StartAsync();
        Assert.Empty(await fixture.Raop.DiscoverAsync());
        Assert.Throws<ArgumentException>(() => new FixtureRuntime("unknown"));
    }
}
