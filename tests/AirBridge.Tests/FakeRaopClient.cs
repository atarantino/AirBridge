using System.Text.Json;
using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

internal class FakeRaopClient(params ReceiverInfo[] receivers) : IRaopClient
{
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();
    public event EventHandler<(string? ReceiverId, StreamState State, string? Error)>? StateChanged;
    public List<(string ReceiverId, int Volume)> Starts { get; } = [];
    public List<(string ReceiverId, int Volume)> VolumeChanges { get; } = [];
    public int StopAllCount { get; private set; }
    public Func<CancellationToken, Task<JsonElement>>? StopAllHandler { get; set; }
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ReceiverInfo>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ReceiverInfo>>(receivers);
    public Task<JsonElement> StartStreamAsync(ReceiverInfo receiver, string pipeName, int initialVolume = 30, CancellationToken cancellationToken = default)
    {
        Starts.Add((receiver.Id, initialVolume));
        StateChanged?.Invoke(this, (receiver.Id, StreamState.Streaming, null));
        return Task.FromResult(Empty);
    }
    public Task<JsonElement> StopStreamAsync(string receiverId, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
    public Task<JsonElement> StopAllStreamsAsync(CancellationToken cancellationToken = default)
    {
        StopAllCount++;
        return StopAllHandler?.Invoke(cancellationToken) ?? Task.FromResult(Empty);
    }
    public Task<JsonElement> SetVolumeAsync(string receiverId, int percent, CancellationToken cancellationToken = default)
    {
        VolumeChanges.Add((receiverId, percent));
        return Task.FromResult(Empty);
    }
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void ForceTerminate() { }
}
