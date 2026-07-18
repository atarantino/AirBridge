namespace AirBridge.Core;

/// <summary>Pure calculations for delaying the faster legs of a multi-receiver route.</summary>
public static class ReceiverAlignmentPlan
{
    public const int MinimumTrimMilliseconds = 0;
    public const int MaximumTrimMilliseconds = 500;
    public const int ProposalQuantumMilliseconds = 10;
    public const int CanonicalSampleRate = 44100;
    public const int CanonicalChannels = 2;
    public const int CanonicalBytesPerSample = 2;

    public static int Resolve(string receiverId, IReadOnlyDictionary<string, int>? trims) =>
        trims is not null && trims.TryGetValue(receiverId, out var value)
            ? Math.Clamp(value, MinimumTrimMilliseconds, MaximumTrimMilliseconds)
            : MinimumTrimMilliseconds;

    /// <summary>Returns a whole-frame byte count for the requested PCM duration.</summary>
    public static int ToPcmByteCount(
        int trimMilliseconds,
        int sampleRate = CanonicalSampleRate,
        int channels = CanonicalChannels,
        int bytesPerSample = CanonicalBytesPerSample)
    {
        if (trimMilliseconds is < MinimumTrimMilliseconds or > MaximumTrimMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(trimMilliseconds));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (bytesPerSample <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSample));

        var frames = (long)Math.Round(
            trimMilliseconds * sampleRate / 1000.0,
            MidpointRounding.AwayFromZero);
        return checked((int)(frames * channels * bytesPerSample));
    }

    /// <summary>
    /// Proposes added delay for each receiver relative to the slowest measured median.
    /// Results are rounded to 10 ms and bounded to the supported persisted range.
    /// </summary>
    public static IReadOnlyDictionary<string, int> ProposeTrims(
        IReadOnlyDictionary<string, int> medianDelayMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(medianDelayMilliseconds);
        if (medianDelayMilliseconds.Count == 0)
            return new Dictionary<string, int>(StringComparer.Ordinal);

        var maximumMedian = medianDelayMilliseconds.Values.Max();
        return medianDelayMilliseconds.ToDictionary(
            pair => pair.Key,
            pair => Math.Clamp(
                (int)Math.Round(
                    (maximumMedian - pair.Value) / (double)ProposalQuantumMilliseconds,
                    MidpointRounding.AwayFromZero) * ProposalQuantumMilliseconds,
                MinimumTrimMilliseconds,
                MaximumTrimMilliseconds),
            StringComparer.Ordinal);
    }

    public static IReadOnlyDictionary<string, int> MeasurementVolumePlan(
        string targetReceiverId,
        IReadOnlyDictionary<string, int> currentVolumes,
        int isolationFloor = 0)
    {
        ArgumentNullException.ThrowIfNull(currentVolumes);
        if (!currentVolumes.ContainsKey(targetReceiverId))
            throw new ArgumentException("Target receiver is not present in the volume plan.", nameof(targetReceiverId));
        return currentVolumes.ToDictionary(
            pair => pair.Key,
            pair => pair.Key == targetReceiverId ? Math.Clamp(pair.Value, 0, 100) : Math.Clamp(isolationFloor, 0, 100),
            StringComparer.Ordinal);
    }

    public static IReadOnlyDictionary<string, int> RemoveAppliedTrims(
        IReadOnlyDictionary<string, int> measuredMedianMilliseconds,
        IReadOnlyDictionary<string, int> currentTrimMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(measuredMedianMilliseconds);
        ArgumentNullException.ThrowIfNull(currentTrimMilliseconds);
        return measuredMedianMilliseconds.ToDictionary(
            pair => pair.Key,
            pair => pair.Value - (currentTrimMilliseconds.TryGetValue(pair.Key, out var trim) ? Math.Clamp(trim, 0, 500) : 0),
            StringComparer.Ordinal);
    }

    public static IReadOnlyList<PairwiseReceiverSkew> PairwiseSkews(
        IReadOnlyDictionary<string, int> medianDelayMilliseconds)
    {
        var pairs = new List<PairwiseReceiverSkew>();
        var values = medianDelayMilliseconds.ToArray();
        for (var first = 0; first < values.Length; first++)
        for (var second = first + 1; second < values.Length; second++)
        {
            var a = values[first];
            var b = values[second];
            pairs.Add(new(
                a.Key,
                b.Key,
                Math.Abs(a.Value - b.Value),
                a.Value <= b.Value ? a.Key : b.Key));
        }
        return pairs;
    }
}

public static class GroupAlignmentApplicability
{
    public static void Validate(
        GroupAlignmentResult result,
        string? currentRouteStreamId,
        IReadOnlyCollection<string> currentReceiverIds,
        IReadOnlyDictionary<string, int> currentTrimMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (currentRouteStreamId != result.RouteStreamId ||
            !currentReceiverIds.Order(StringComparer.Ordinal).SequenceEqual(result.RouteReceiverIds.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("The active route changed after measurement. Re-run Align group before applying trims.");
        foreach (var pair in result.BaselineTrimMilliseconds)
        {
            var current = currentTrimMilliseconds.TryGetValue(pair.Key, out var value) ? value : 0;
            if (current != pair.Value)
                throw new InvalidOperationException("A receiver trim changed after measurement. Re-run Align group before applying trims.");
        }
    }
}
