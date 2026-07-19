using System.Text.Json;
using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class ToolPrivacyTests
{
    private sealed class FakeRaop(ReceiverInfo receiver) : IRaopClient
    {
        private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();
        public event EventHandler<(string? ReceiverId, StreamState State, string? Error)>? StateChanged;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ReceiverInfo>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ReceiverInfo>>([receiver]);
        public Task<JsonElement> StartStreamAsync(ReceiverInfo target, string pipeName, int initialVolume = 30, CancellationToken cancellationToken = default)
        {
            StateChanged?.Invoke(this, (target.Id, StreamState.Streaming, null));
            return Task.FromResult(Empty);
        }
        public Task<JsonElement> StopStreamAsync(string receiverId, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
        public Task<JsonElement> StopAllStreamsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Empty);
        public Task<JsonElement> SetVolumeAsync(string receiverId, int percent, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
        public Task<JsonElement> PlayDiagnosticToneAsync(string receiverName, double seconds, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void ForceTerminate() { }
    }

    private sealed class FakeCapture : IAudioCaptureService
    {
        public Task StartSystemAsync(CancellationToken cancellationToken = default, TimeSpan? activationTimeout = null) => Task.CompletedTask;
        public Task StartProcessTreeAsync(int processId, bool exclude = false, CancellationToken cancellationToken = default, TimeSpan? activationTimeout = null) => Task.CompletedTask;
        public void Stop() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task AlignmentToolsUseOpaqueAliasesAndNeverExposeStoredRaopIdentifier()
    {
        const string privateReceiverId = "E2EF679FB205";
        await using var controller = new AirBridgeController();
        controller.ConfigureSettings(new AirBridgeSettings
        {
            ReceiverAlignmentTrimMs = new(StringComparer.Ordinal) { [privateReceiverId] = 60 }
        });
        using var empty = JsonDocument.Parse("{}");

        var read = await controller.ExecuteAsync("get_alignment", empty.RootElement, CancellationToken.None);
        var json = JsonSerializer.Serialize(read);
        Assert.DoesNotContain(privateReceiverId, json, StringComparison.Ordinal);
        Assert.Contains("receiver-1", json, StringComparison.Ordinal);

        using var update = JsonDocument.Parse("""{"receiver_id":"receiver-1","trim_ms":70}""");
        var written = await controller.ExecuteAsync("set_alignment_trim", update.RootElement, CancellationToken.None);
        Assert.DoesNotContain(privateReceiverId, JsonSerializer.Serialize(written), StringComparison.Ordinal);
        Assert.Equal(70, controller.GetReceiverAlignmentTrim(privateReceiverId));
    }

    [Fact]
    public async Task ReceiverListRouteBufferStartAndReconnectResultsNeverExposeRaopIdentifier()
    {
        const string privateReceiverId = "48A6B845CCEE";
        var receiver = new ReceiverInfo(privateReceiverId, "Family Room", "192.0.2.10", false, DateTimeOffset.UtcNow);
        await using var controller = new AirBridgeController(new FakeRaop(receiver), new FakeCapture());
        await controller.InitializeAsync();
        await controller.DiscoverAsync();
        using var empty = JsonDocument.Parse("{}");
        using var start = JsonDocument.Parse("""{"receiver_ids":["receiver-1"],"quality_profile":"balanced"}""");
        using var reconnect = JsonDocument.Parse("""{"receiver_id":"receiver-1","all":false}""");

        object?[] results =
        [
            await controller.ExecuteAsync("list_airplay_devices", empty.RootElement, CancellationToken.None),
            await controller.ExecuteAsync("start_system_stream", start.RootElement, CancellationToken.None),
            await controller.ExecuteAsync("get_current_routes", empty.RootElement, CancellationToken.None),
            await controller.ExecuteAsync("get_buffer_metrics", empty.RootElement, CancellationToken.None),
            await controller.ExecuteAsync("reconnect_stream", reconnect.RootElement, CancellationToken.None)
        ];
        var json = JsonSerializer.Serialize(results);

        Assert.DoesNotContain(privateReceiverId, json, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.10", json, StringComparison.Ordinal);
        Assert.Contains("receiver-1", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairingReceiverProducesSpecificAgentConfirmationRequest()
    {
        var receiver = new ReceiverInfo("private-id", "Bedroom", "local", false, DateTimeOffset.UtcNow,
            RequiresPairing: true, SupportsPairing: true);
        await using var controller = new AirBridgeController(new FakeRaop(receiver), new FakeCapture());
        await controller.InitializeAsync();
        await controller.DiscoverAsync();
        using var start = JsonDocument.Parse("""{"receiver_ids":["receiver-1"],"quality_profile":"balanced"}""");

        var confirmation = controller.GetConfirmationRequest("start_system_stream", start.RootElement);

        Assert.Equal("Pair with Bedroom?", confirmation?.Title);
        Assert.Contains("enter the code", confirmation?.Message, StringComparison.Ordinal);
    }
}
