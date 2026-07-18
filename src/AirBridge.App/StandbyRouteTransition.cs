using AirBridge.Core;

namespace AirBridge.App;

/// <summary>
/// Defines the ordered, externally visible parts of standby transitions.
/// Keeping this seam deterministic makes it possible to prove that receivers
/// are marked released only after RAOP stop succeeds and that resume restores
/// trims before opening a new group gate and starting each leg at its volume.
/// </summary>
public static class StandbyRouteTransition
{
    public static async Task EnterAsync(
        Func<CancellationToken, Task> stopAll,
        Action markReleased,
        CancellationToken cancellationToken = default)
    {
        await stopAll(cancellationToken).ConfigureAwait(false);
        markReleased();
    }

    public static async Task ResumeAsync(
        IReadOnlyList<ReceiverResumeSetting> plan,
        Action<string, int> restoreTrim,
        Action<IReadOnlyCollection<string>> beginGroupGate,
        Func<string, int, CancellationToken, Task> startLeg,
        CancellationToken cancellationToken = default)
    {
        foreach (var setting in plan)
            restoreTrim(setting.ReceiverId, setting.AlignmentTrimMilliseconds);

        beginGroupGate(plan.Select(setting => setting.ReceiverId).ToArray());

        await Task.WhenAll(plan.Select(setting =>
            startLeg(setting.ReceiverId, setting.Volume, cancellationToken))).ConfigureAwait(false);
    }
}
