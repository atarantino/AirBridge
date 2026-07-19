using System.Diagnostics;
using AirBridge.Core;
using NAudio.Wave;

namespace AirBridge.App;

public sealed record MicrophoneDeviceInfo(int DeviceNumber, string Name)
{
    public override string ToString() => Name;
}

public sealed class AcousticDelayMeasurer
{
    // Field captures below 2% peak produced false matches; genuine chirps measured at least 3.5%.
    internal const double MinimumDetectionPeakPercent = 2.0;
    private string? _preferredMicrophoneName;

    public static IReadOnlyList<MicrophoneDeviceInfo> GetAvailableMicrophones()
    {
        try
        {
            var devices = new List<MicrophoneDeviceInfo>(WaveIn.DeviceCount);
            for (var deviceNumber = 0; deviceNumber < WaveIn.DeviceCount; deviceNumber++)
            {
                var capabilities = WaveIn.GetCapabilities(deviceNumber);
                devices.Add(new(deviceNumber, capabilities.ProductName));
            }
            return devices;
        }
        catch (Exception ex)
        {
            AppLog.Warning("acoustic-measurement", $"Could not enumerate calibration microphones: {ex.Message}");
            return [];
        }
    }

    public void Configure(string? preferredMicrophoneName) =>
        _preferredMicrophoneName = string.IsNullOrWhiteSpace(preferredMicrophoneName) ? null : preferredMicrophoneName.Trim();

    public async Task<AcousticDelayResult> MeasureAsync(
        PcmBroadcastHub capturePath,
        string receiverId,
        string receiverName,
        CancellationToken cancellationToken = default)
    {
        var selectedMicrophone = ResolveMicrophone();
        AppLog.Info("acoustic-measurement", $"Using calibration microphone '{selectedMicrophone.Name}' (device {selectedMicrophone.DeviceNumber}) for {receiverName}.");
        using var microphone = new InMemoryMicrophoneCapture(selectedMicrophone.DeviceNumber);
        var recordingStart = microphone.Start();
        await Task.Delay(300, cancellationToken);
        var emissionTicks = await capturePath.PlayCalibrationAsync(CalibrationChirp.CreateSequence(), cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(5500), cancellationToken);
        var samples = await microphone.StopAsync();
        var emissions = emissionTicks.Select(ticks => (int)Math.Round((ticks - recordingStart) * 1000.0 / Stopwatch.Frequency)).ToArray();
        var signal = AnalyzeSignal(samples);
        AppLog.Info("acoustic-measurement",
            $"Captured {signal.DurationMilliseconds} ms from '{selectedMicrophone.Name}'; samples={samples.Length}; " +
            $"peak={signal.PeakPercent:F2}%; rms={signal.RmsPercent:F2}%; calibration emissions={emissions.Length}.");
        if (emissions.Length < 5)
            throw new InvalidOperationException(
                $"Calibration playback did not emit all five chirps while measuring {receiverName}; only {emissions.Length} reached the shared audio stream. " +
                "This is an AirBridge playback pipeline failure, not a microphone failure.");
        if (!IsCaptureEligibleForDetection(signal.IsEffectivelySilent, signal.PeakPercent))
            throw new InvalidOperationException(
                $"Calibration microphone '{selectedMicrophone.Name}' captured {receiverName} at only {signal.PeakPercent:F2}% peak, which is too quiet for a reliable delay measurement. " +
                $"Move the microphone closer to {receiverName} or raise its volume and retry.");
        (int MedianMilliseconds, IReadOnlyList<int> DelaysMilliseconds, IReadOnlyList<int> OnsetsMilliseconds) detected;
        try { detected = AcousticDelayDetector.Detect(samples, InMemoryMicrophoneCapture.SampleRate, emissions); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("usable chirp returns", StringComparison.Ordinal))
        {
            var explanation = samples.Length == 0
                ? $"'{selectedMicrophone.Name}' returned no audio samples."
                : signal.IsEffectivelySilent
                    ? $"'{selectedMicrophone.Name}' captured {signal.DurationMilliseconds} ms, but its signal was effectively silent (peak {signal.PeakPercent:F2}%)."
                    : $"'{selectedMicrophone.Name}' captured live audio (peak {signal.PeakPercent:F2}%, RMS {signal.RmsPercent:F2}%), but none of the five chirps matched.";
            throw new InvalidOperationException(
                $"{explanation} AirBridge queued all five chirps while measuring {receiverName}; check calibration playback, routing, and detector timing before blaming the microphone.", ex);
        }
        return new(detected.MedianMilliseconds, detected.DelaysMilliseconds, detected.OnsetsMilliseconds, receiverId, receiverName);
    }

    internal static bool IsCaptureEligibleForDetection(bool isEffectivelySilent, double peakPercent) =>
        !isEffectivelySilent && peakPercent >= MinimumDetectionPeakPercent;

    private MicrophoneDeviceInfo ResolveMicrophone()
    {
        var devices = GetAvailableMicrophones();
        if (devices.Count == 0) throw new InvalidOperationException("No recording devices are available for speaker calibration.");
        if (_preferredMicrophoneName is null) return devices[0];
        return devices.FirstOrDefault(device => device.Name.Equals(_preferredMicrophoneName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"The selected calibration microphone '{_preferredMicrophoneName}' is unavailable. Choose an available microphone in Settings > Speaker sync.");
    }

    private static (int DurationMilliseconds, double PeakPercent, double RmsPercent, bool IsEffectivelySilent) AnalyzeSignal(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return (0, 0, 0, true);
        var peak = 0;
        double sumSquares = 0;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs((int)sample);
            peak = Math.Max(peak, magnitude);
            sumSquares += (double)sample * sample;
        }
        var rms = Math.Sqrt(sumSquares / samples.Length);
        return (
            (int)Math.Round(samples.Length * 1000.0 / InMemoryMicrophoneCapture.SampleRate),
            peak * 100.0 / short.MaxValue,
            rms * 100.0 / short.MaxValue,
            peak < 128 && rms < 32);
    }

    private sealed class InMemoryMicrophoneCapture : IDisposable
    {
        public const int SampleRate = 16000;
        private readonly object _gate = new();
        private readonly MemoryStream _pcm = new();
        private readonly int _deviceNumber;
        private WaveIn? _input;
        private TaskCompletionSource? _stopped;

        public InMemoryMicrophoneCapture(int deviceNumber) => _deviceNumber = deviceNumber;

        public long Start()
        {
            _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _input = new WaveIn { DeviceNumber = _deviceNumber, WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 40 };
            _input.DataAvailable += OnDataAvailable;
            _input.RecordingStopped += OnRecordingStopped;
            _input.StartRecording();
            return Stopwatch.GetTimestamp();
        }

        public async Task<short[]> StopAsync()
        {
            if (_input is null) return [];
            _input.StopRecording();
            if (_stopped is not null) await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            byte[] bytes;
            lock (_gate) bytes = _pcm.ToArray();
            var samples = new short[bytes.Length / sizeof(short)];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(short));
            DisposeInput();
            return samples;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs args)
        {
            lock (_gate) _pcm.Write(args.Buffer, 0, args.BytesRecorded);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs args)
        {
            if (args.Exception is null) _stopped?.TrySetResult();
            else _stopped?.TrySetException(args.Exception);
        }

        private void DisposeInput()
        {
            if (_input is null) return;
            _input.DataAvailable -= OnDataAvailable;
            _input.RecordingStopped -= OnRecordingStopped;
            _input.Dispose();
            _input = null;
        }

        public void Dispose()
        {
            try { _input?.StopRecording(); } catch { }
            DisposeInput();
            _pcm.Dispose();
        }
    }
}
