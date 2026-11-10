using System.Text.Json;
using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class DiscoveryProgressTests
{
    private static readonly ReceiverInfo Kitchen = new("kitchen", "Kitchen", "192.0.2.10", false, DateTimeOffset.UtcNow);
    private static readonly ReceiverInfo Office = new("office", "Office", "192.0.2.11", false, DateTimeOffset.UtcNow);

    private sealed class ProgressRaop : IRaopClient
    {
        private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();
        public TaskCompletionSource FinishScan { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<IReadOnlyList<ReceiverInfo>>? LateReport { get; private set; }
        public event EventHandler<(string? ReceiverId, StreamState State, string? Error)>? StateChanged { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ReceiverInfo>> DiscoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReceiverInfo>>([Kitchen, Office]);
        public async Task<IReadOnlyList<ReceiverInfo>> DiscoverAsync(Action<IReadOnlyList<ReceiverInfo>>? onPartial, CancellationToken cancellationToken = default)
        {
            onPartial?.Invoke([Kitchen]);
            await FinishScan.Task;
            LateReport = onPartial;
            return [Kitchen, Office];
        }
        public Task<JsonElement> StartStreamAsync(ReceiverInfo target, string pipeName, int initialVolume = 30, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
        public Task<JsonElement> StopStreamAsync(string receiverId, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
        public Task<JsonElement> StopAllStreamsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Empty);
        public Task<JsonElement> SetVolumeAsync(string receiverId, int percent, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void ForceTerminate() { }
    }

    [Fact]
    public async Task EarlyReceiversAreConnectableBeforeTheScanFinishesAndLateReportsAreIgnored()
    {
        var raop = new ProgressRaop();
        await using var controller = new AirBridgeController(raop, new FakeCaptureService());
        await controller.InitializeAsync();
        var partials = new List<string[]>();

        var discovery = controller.DiscoverAsync(partial => partials.Add(partial.Select(item => item.Id).ToArray()));

        Assert.Equal(["kitchen"], Assert.Single(partials));
        Assert.Equal(["kitchen"], controller.Receivers.Select(item => item.Id));
        Assert.False(discovery.IsCompleted);

        raop.FinishScan.SetResult();
        Assert.Equal(["kitchen", "office"], (await discovery).Select(item => item.Id));
        raop.LateReport!([Office]);
        Assert.Single(partials);
        Assert.Equal(["kitchen", "office"], controller.Receivers.Select(item => item.Id));
    }
}
