using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class ReceiverMeasurementSequenceTests
{
    [Fact]
    public async Task FloorsEveryNonTargetThenRestoresAllOriginalVolumes()
    {
        var original = new Dictionary<string, int> { ["speakerA"] = 18, ["beam"] = 8, ["office"] = 42 };
        var calls = new List<(string Receiver, int Volume)>();

        var results = await ReceiverMeasurementSequence.RunAsync(
            ["speakerA", "beam"],
            original,
            (id, volume, _) => { calls.Add((id, volume)); return Task.CompletedTask; },
            (id, _) => Task.FromResult(id));

        Assert.Equal(["speakerA", "beam"], results);
        Assert.Contains(("speakerA", 18), calls.Take(3));
        Assert.Contains(("beam", 0), calls.Take(3));
        Assert.Contains(("office", 0), calls.Take(3));
        Assert.Contains(("speakerA", 0), calls.Skip(3).Take(3));
        Assert.Contains(("beam", 8), calls.Skip(3).Take(3));
        Assert.Contains(("office", 0), calls.Skip(3).Take(3));
        Assert.Equal(original.Select(pair => (pair.Key, pair.Value)), calls.TakeLast(3));
    }

    [Fact]
    public async Task MeasurementFailureStillRestoresEveryOriginalVolume()
    {
        var original = new Dictionary<string, int> { ["speakerA"] = 18, ["beam"] = 8 };
        var calls = new List<(string Receiver, int Volume)>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ReceiverMeasurementSequence.RunAsync<string>(
            ["speakerA", "beam"],
            original,
            (id, volume, _) => { calls.Add((id, volume)); return Task.CompletedTask; },
            (id, _) => id == "beam" ? throw new InvalidOperationException("measurement failed") : Task.FromResult(id)));

        Assert.Equal(("speakerA", 18), calls[^2]);
        Assert.Equal(("beam", 8), calls[^1]);
    }

    [Fact]
    public async Task RestoreFailureIsSurfacedAfterAllRestoresAreAttempted()
    {
        var original = new Dictionary<string, int> { ["speakerA"] = 18, ["beam"] = 8 };
        var restorePhase = false;
        var restoreAttempts = new List<string>();

        var error = await Assert.ThrowsAsync<AggregateException>(() => ReceiverMeasurementSequence.RunAsync(
            ["speakerA"],
            original,
            (id, _, _) =>
            {
                if (!restorePhase) return Task.CompletedTask;
                restoreAttempts.Add(id);
                return id == "speakerA" ? Task.FromException(new IOException("restore failed")) : Task.CompletedTask;
            },
            (id, _) => { restorePhase = true; return Task.FromResult(id); }));

        Assert.Contains("could not be restored", error.Message);
        Assert.Equal(["speakerA", "beam"], restoreAttempts);
    }
}
