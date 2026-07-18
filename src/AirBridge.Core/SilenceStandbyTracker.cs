namespace AirBridge.Core;

public enum SilenceStandbyAction { None, EnterStandby, Resume }

public static class PcmSignalDetector
{
    public const int DefaultPeakThreshold = 10;

    public static bool ContainsSignal(ReadOnlySpan<byte> s16LePcm, int peakThreshold = DefaultPeakThreshold)
    {
        if (peakThreshold < 0) throw new ArgumentOutOfRangeException(nameof(peakThreshold));
        for (var index = 0; index + 1 < s16LePcm.Length; index += sizeof(short))
            if (Math.Abs((int)BitConverter.ToInt16(s16LePcm.Slice(index, sizeof(short)))) >= peakThreshold) return true;
        return false;
    }
}

/// <summary>Pure 20 ms silence timer used by the live shared pump.</summary>
public sealed class SilenceStandbyTracker
{
    private const int TicksPerSecond = 1000 / 20;
    private readonly object _gate = new();
    private int _silentTicks;
    private bool _triggered;
    private bool _enabled = true;
    private int _seconds = 60;

    public bool Enabled { get { lock (_gate) return _enabled; } }
    public int Seconds { get { lock (_gate) return _seconds; } }

    public void Configure(bool enabled, int seconds)
    {
        if (seconds is < 10 or > 600) throw new ArgumentOutOfRangeException(nameof(seconds));
        lock (_gate)
        {
            _enabled = enabled;
            _seconds = seconds;
            ResetCore();
        }
    }

    public SilenceStandbyAction Observe(bool containsSignal, bool routeActive, bool isStandby, bool suppressed = false)
    {
        lock (_gate)
        {
            if (!_enabled || !routeActive || suppressed)
            {
                ResetCore();
                return SilenceStandbyAction.None;
            }
            if (isStandby)
            {
                if (containsSignal)
                {
                    ResetCore();
                    return SilenceStandbyAction.Resume;
                }
                return SilenceStandbyAction.None;
            }
            if (containsSignal)
            {
                ResetCore();
                return SilenceStandbyAction.None;
            }
            if (_triggered) return SilenceStandbyAction.None;
            _silentTicks++;
            if (_silentTicks < _seconds * TicksPerSecond) return SilenceStandbyAction.None;
            _triggered = true;
            return SilenceStandbyAction.EnterStandby;
        }
    }

    public void Reset()
    {
        lock (_gate) ResetCore();
    }

    private void ResetCore()
    {
        _silentTicks = 0;
        _triggered = false;
    }
}
