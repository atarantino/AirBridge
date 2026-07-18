using System.Text.Json.Serialization;

namespace AirBridge.Core;

[JsonConverter(typeof(JsonStringEnumConverter<StreamState>))]
public enum StreamState { Idle, Discovering, Connecting, Negotiating, Buffering, Streaming, Standby, Degraded, Reconnecting, Failed }

[JsonConverter(typeof(JsonStringEnumConverter<CaptureMode>))]
public enum CaptureMode { SystemMix, ProcessTreeInclude, ProcessTreeExclude, EndpointLoopback }

[JsonConverter(typeof(JsonStringEnumConverter<QualityProfile>))]
public enum QualityProfile { Balanced, Stable, LowLatency }

public sealed record ReceiverInfo(string Id, string Name, string Address, bool RequiresPassword, DateTimeOffset LastSeenUtc);

public sealed record ReceiverPlaybackInfo(
    ReceiverInfo Receiver,
    StreamState State,
    int Volume,
    DateTimeOffset? StartedUtc,
    string? LastError,
    int AlignmentTrimMilliseconds = 0);

public sealed record SpeakerGroup(string Id, string Name, IReadOnlyList<string> ReceiverIds);

public sealed record ReceiverDelayMeasurement(
    string ReceiverId,
    string ReceiverName,
    int MedianMilliseconds,
    IReadOnlyList<int> SamplesMilliseconds);

public sealed record PairwiseReceiverSkew(
    string FirstReceiverId,
    string SecondReceiverId,
    int SkewMilliseconds,
    string EarlyReceiverId);

public sealed record GroupAlignmentResult(
    IReadOnlyList<ReceiverDelayMeasurement> Measurements,
    IReadOnlyList<PairwiseReceiverSkew> PairwiseSkews,
    IReadOnlyDictionary<string, int> ProposedTrimMilliseconds,
    bool Applied)
{
    public string? RouteStreamId { get; init; }
    public IReadOnlyList<string> RouteReceiverIds { get; init; } = [];
    public IReadOnlyDictionary<string, int> BaselineTrimMilliseconds { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
}

public static class ReceiverVolumePlan
{
    public const int SafeDefault = 30;

    public static int Resolve(string receiverId, IReadOnlyDictionary<string, int>? volumes) =>
        volumes is not null && volumes.TryGetValue(receiverId, out var value)
            ? Math.Clamp(value, 0, 100)
            : SafeDefault;
}

public sealed record ReceiverResumeSetting(string ReceiverId, int Volume, int AlignmentTrimMilliseconds);

public static class ReceiverResumePlan
{
    public static IReadOnlyList<ReceiverResumeSetting> Create(IEnumerable<ReceiverPlaybackInfo> playback) =>
        playback.Select(item => new ReceiverResumeSetting(
            item.Receiver.Id,
            Math.Clamp(item.Volume, 0, 100),
            Math.Clamp(item.AlignmentTrimMilliseconds, 0, 500))).ToArray();
}

public sealed record AudioSessionInfo(int ProcessId, string Application, string Executable, bool IsPlaying, float Volume);

public sealed record RouteInfo(string? StreamId, CaptureMode Mode, int? ProcessId, string? ReceiverId, string? ReceiverName, StreamState State, DateTimeOffset? StartedUtc)
{
    public IReadOnlyList<ReceiverPlaybackInfo> Destinations { get; init; } = [];
}

public sealed record BufferSnapshot(
    int CapacityBytes,
    int FillBytes,
    int TargetMilliseconds,
    long BytesWritten,
    long BytesRead,
    long Overruns,
    long Underruns,
    long Epoch,
    long ProducerIdlePaddingBytes = 0,
    long StarvedWhileActivePaddingBytes = 0)
{
    public int FillMilliseconds => (int)Math.Round(FillBytes / 176.4);
    public int FillPercent => CapacityBytes == 0 ? 0 : (int)Math.Round(FillBytes * 100.0 / CapacityBytes);
    public int ProducerIdlePaddingMilliseconds => (int)Math.Round(ProducerIdlePaddingBytes / 176.4);
    public int StarvedWhileActivePaddingMilliseconds => (int)Math.Round(StarvedWhileActivePaddingBytes / 176.4);
}

public sealed record StreamHealth(
    string? StreamId,
    StreamState State,
    string Rating,
    BufferSnapshot Buffer,
    long CaptureDiscontinuities,
    double AverageRttMs,
    double JitterMs,
    double PacketLossPercent,
    DateTimeOffset CapturedUtc,
    bool? LastFixVerified,
    string? LastError);

public sealed record AirBridgeSettings
{
    public string DefaultReceiverName { get; init; } = string.Empty;
    public string? DefaultReceiverId { get; init; }
    public CaptureMode DefaultCaptureMode { get; init; } = CaptureMode.SystemMix;
    public QualityProfile QualityProfile { get; init; } = QualityProfile.Balanced;
    public int BufferTargetMilliseconds { get; init; } = 500;
    public bool AiEnabled { get; init; } = true;
    public bool RestorePreviousRoute { get; init; }
    public string PushToTalkShortcut { get; init; } = "Ctrl+Alt+Space";
    public int EstimatedAudioDelayMilliseconds { get; init; } = 2000;
    public IReadOnlyList<string> SelectedReceiverIds { get; init; } = [];
    public Dictionary<string, int> ReceiverVolumes { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ReceiverAlignmentTrimMs { get; init; } = new(StringComparer.Ordinal);
    public bool SilenceStandbyEnabled { get; init; } = true;
    public int SilenceStandbySeconds { get; init; } = 60;
    public IReadOnlyList<SpeakerGroup> SpeakerGroups { get; init; } = [];
    public string ThemeMode { get; init; } = "system";
}
