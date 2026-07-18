using AirBridge.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AirBridge.App;

public sealed class WasapiCaptureService : IAudioCaptureService
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");
    private readonly IPcmSink _sink;
    private readonly StreamCoordinator _coordinator;
    private WasapiRecorder? _capture;
    private PcmNormalizer? _normalizer;

    public WasapiCaptureService(IPcmSink sink, StreamCoordinator coordinator)
    {
        _sink = sink;
        _coordinator = coordinator;
    }

    public WaveFormat? SourceFormat => _capture?.WaveFormat;

    public async Task StartSystemAsync(CancellationToken cancellationToken = default, TimeSpan? activationTimeout = null)
    {
        Stop();
        var builder = new WasapiRecorderBuilder()
            .WithLoopbackCapture()
            .WithBufferLength(20)
            .WithMmcssThreadPriority("Pro Audio");
        var build = Task.Run(() => builder.BuildAsync(), cancellationToken);
        _capture = await AwaitRecorderAsync(build, activationTimeout ?? TimeSpan.FromSeconds(10), cancellationToken);
        _normalizer = new PcmNormalizer(_capture.WaveFormat.SampleRate, _capture.WaveFormat.Channels);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
    }

    public async Task StartProcessTreeAsync(int processId, bool exclude = false, CancellationToken cancellationToken = default, TimeSpan? activationTimeout = null)
    {
        Stop();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("Application loopback requires Windows 10 2004 or later.");
        var builder = new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)processId, exclude ? ProcessLoopbackMode.ExcludeTargetProcessTree : ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithBufferLength(20)
            .WithMmcssThreadPriority("Pro Audio");
        // Some drivers block inside BuildAsync before returning its Task. Keep
        // that COM activation off the UI/test thread so our timeout can fire.
        var build = Task.Run(() => builder.BuildAsync(), cancellationToken);
        _capture = await AwaitRecorderAsync(build, activationTimeout ?? TimeSpan.FromSeconds(10), cancellationToken);
        _normalizer = new PcmNormalizer(_capture.WaveFormat.SampleRate, _capture.WaveFormat.Channels);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
    }

    private void OnDataAvailable(ReadOnlySpan<byte> captured, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (_capture is null || _normalizer is null) return;
        try
        {
            var silent = (flags & AudioClientBufferFlags.Silent) != 0;
            var silence = silent ? new byte[captured.Length] : null;
            ReadOnlySpan<byte> data = silent ? silence! : captured;
            var format = _capture.WaveFormat;
            var subFormat = (format as WaveFormatExtensible)?.SubFormat;
            byte[] normalized;
            if ((format.Encoding == WaveFormatEncoding.IeeeFloat || subFormat == FloatSubFormat) && format.BitsPerSample == 32)
                normalized = _normalizer.ConvertFloat32(data);
            else if ((format.Encoding == WaveFormatEncoding.Pcm || subFormat == PcmSubFormat) && format.BitsPerSample == 16)
                normalized = _normalizer.ConvertInt16(data);
            else
                throw new NotSupportedException($"Unsupported capture format: {format.Encoding}, {format.BitsPerSample} bit, subtype {subFormat}");
            _sink.Write(normalized, producerActive: !silent && PcmSignalDetector.ContainsSignal(normalized));
        }
        catch (Exception ex)
        {
            _coordinator.Transition(StreamState.Failed, ex.Message);
            Stop();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) _coordinator.Transition(StreamState.Failed, "The Windows capture endpoint stopped.");
    }

    public void Stop()
    {
        if (_capture is null) return;
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        try { _capture.StopRecording(); } catch (InvalidOperationException) { }
        _capture.Dispose();
        _capture = null;
        _normalizer = null;
    }

    private static async Task<WasapiRecorder> AwaitRecorderAsync(Task<WasapiRecorder> build, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await build.WaitAsync(timeout, cancellationToken);
        }
        catch
        {
            _ = build.ContinueWith(
                completed => completed.Result.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    public static IReadOnlyList<AudioSessionInfo> ListSessions()
    {
        var sessions = new List<AudioSessionInfo>();
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        for (var index = 0; index < device.AudioSessionManager.Sessions.Count; index++)
        {
            using var session = device.AudioSessionManager.Sessions[index];
            var pid = (int)session.GetProcessID;
            if (pid <= 0) continue;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                sessions.Add(new(pid, process.ProcessName, $"{process.ProcessName}.exe", session.State.ToString().Contains("Active", StringComparison.Ordinal), session.SimpleAudioVolume.Volume));
            }
            catch (ArgumentException) { }
        }
        return sessions.GroupBy(item => item.ProcessId).Select(group => group.First()).OrderBy(item => item.Application).ToArray();
    }

    public void Dispose() => Stop();
}
