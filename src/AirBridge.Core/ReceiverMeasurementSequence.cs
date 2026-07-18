using System.Runtime.ExceptionServices;

namespace AirBridge.Core;

/// <summary>Runs receiver measurements sequentially and restores every prior volume on all exit paths.</summary>
public static class ReceiverMeasurementSequence
{
    public static async Task<IReadOnlyList<T>> RunAsync<T>(
        IReadOnlyList<string> targetReceiverIds,
        IReadOnlyDictionary<string, int> originalVolumes,
        Func<string, int, CancellationToken, Task> setVolumeAsync,
        Func<string, CancellationToken, Task<T>> measureAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetReceiverIds);
        ArgumentNullException.ThrowIfNull(originalVolumes);
        ArgumentNullException.ThrowIfNull(setVolumeAsync);
        ArgumentNullException.ThrowIfNull(measureAsync);
        var results = new List<T>(targetReceiverIds.Count);
        Exception? operationFailure = null;
        var restorationFailures = new List<Exception>();
        try
        {
            foreach (var target in targetReceiverIds)
            {
                var isolation = ReceiverAlignmentPlan.MeasurementVolumePlan(target, originalVolumes);
                foreach (var pair in isolation)
                    await setVolumeAsync(pair.Key, pair.Value, cancellationToken).ConfigureAwait(false);
                results.Add(await measureAsync(target, cancellationToken).ConfigureAwait(false));
            }
        }
        catch (Exception ex) { operationFailure = ex; }
        finally
        {
            foreach (var pair in originalVolumes)
            {
                try { await setVolumeAsync(pair.Key, pair.Value, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { restorationFailures.Add(ex); }
            }
        }

        if (operationFailure is not null)
        {
            if (restorationFailures.Count > 0)
                throw new AggregateException("Receiver measurement failed and one or more volumes could not be restored.", [operationFailure, .. restorationFailures]);
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }
        if (restorationFailures.Count > 0)
            throw new AggregateException("One or more receiver volumes could not be restored after measurement.", restorationFailures);
        return results;
    }
}
