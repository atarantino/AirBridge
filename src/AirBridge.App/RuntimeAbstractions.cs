using System.Text.Json;
using AirBridge.Core;

namespace AirBridge.App;

public interface IRaopClient
{
    event EventHandler<(string? ReceiverId, StreamState State, string? Error)>? StateChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReceiverInfo>> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> BeginPairingAsync(string receiverId, bool controlPairing = false, CancellationToken cancellationToken = default) =>
        Task.FromException<JsonElement>(new NotSupportedException("Pairing is not supported by this RAOP client."));
    Task<JsonElement> FinishPairingAsync(string receiverId, string pin, CancellationToken cancellationToken = default) =>
        Task.FromException<JsonElement>(new NotSupportedException("Pairing is not supported by this RAOP client."));
    Task<JsonElement> CancelPairingAsync(string receiverId, CancellationToken cancellationToken = default) =>
        Task.FromResult(default(JsonElement));
    Task<JsonElement> SleepAsync(string receiverId, CancellationToken cancellationToken = default) =>
        Task.FromException<JsonElement>(new NotSupportedException("Power control is not supported by this RAOP client."));
    Task<JsonElement> StartStreamAsync(ReceiverInfo receiver, string pipeName, int initialVolume = 30, CancellationToken cancellationToken = default);
    Task<JsonElement> StopStreamAsync(string receiverId, CancellationToken cancellationToken = default);
    Task<JsonElement> StopAllStreamsAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> SetVolumeAsync(string receiverId, int percent, CancellationToken cancellationToken = default);
    Task<JsonElement> PlayDiagnosticToneAsync(string receiverName, double seconds, CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken);
    void ForceTerminate();
}

public interface IAudioCaptureService : IDisposable
{
    Task StartSystemAsync(CancellationToken cancellationToken = default, TimeSpan? activationTimeout = null);
    Task StartProcessTreeAsync(int processId, bool exclude = false, CancellationToken cancellationToken = default, TimeSpan? activationTimeout = null);
    void Stop();
}
