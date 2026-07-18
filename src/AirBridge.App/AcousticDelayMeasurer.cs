using System.Diagnostics;
using AirBridge.Core;
using NAudio.Wave;

namespace AirBridge.App;

public sealed class AcousticDelayMeasurer
{
    public async Task<AcousticDelayResult> MeasureAsync(
        PcmBroadcastHub capturePath,
        string receiverId,
        string receiverName,
        CancellationToken cancellationToken = default)
    {
        using var microphone = new InMemoryMicrophoneCapture();
        var recordingStart = microphone.Start();
        await Task.Delay(300, cancellationToken);
        var emissionTicks = await capturePath.PlayCalibrationAsync(CalibrationChirp.CreateSequence(), cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(5500), cancellationToken);
        var samples = await microphone.StopAsync();
        var emissions = emissionTicks.Select(ticks => (int)Math.Round((ticks - recordingStart) * 1000.0 / Stopwatch.Frequency)).ToArray();
        var detected = AcousticDelayDetector.Detect(samples, InMemoryMicrophoneCapture.SampleRate, emissions);
        return new(detected.MedianMilliseconds, detected.DelaysMilliseconds, detected.OnsetsMilliseconds, receiverId, receiverName);
    }

    private sealed class InMemoryMicrophoneCapture : IDisposable
    {
        public const int SampleRate = 16000;
        private readonly object _gate = new();
        private readonly MemoryStream _pcm = new();
        private WaveIn? _input;
        private TaskCompletionSource? _stopped;

        public long Start()
        {
            _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _input = new WaveIn { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 40 };
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
