using System.Text.Json;
using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class DiscoveryProgressTests
{
    private static readonly ReceiverInfo Kitchen = new("kitchen", "Kitchen", "192.0.2.10", false, DateTimeOffset.UtcNow);
    private static readonly ReceiverInfo Office = new("office", "Office", "192.0.2.11", false, DateTimeOffset.UtcNow);

    private sealed class ProgressRaop() : FakeRaopClient(Kitchen, Office), IRaopClient
    {
        public TaskCompletionSource FinishScan { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<IReadOnlyList<ReceiverInfo>>? LateReport { get; private set; }
        public async Task<IReadOnlyList<ReceiverInfo>> DiscoverAsync(Action<IReadOnlyList<ReceiverInfo>>? onPartial, CancellationToken cancellationToken = default)
        {
            onPartial?.Invoke([Kitchen]);
            await FinishScan.Task;
            LateReport = onPartial;
            return [Kitchen, Office];
        }
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
