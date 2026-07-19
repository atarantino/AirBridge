using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text.Json;
using AirBridge.Core;
using NAudio.Wave;

namespace AirBridge.App;

public sealed class PushToTalkRecorder : IDisposable
{
    private readonly object _gate = new();
    private WaveIn? _input;
    private WaveFileWriter? _writer;
    private MemoryStream? _audio;
    private volatile float _peakLevel;

    public bool IsRecording => _input is not null;
    public float PeakLevel => _peakLevel;

    public void Start()
    {
        lock (_gate)
        {
            if (_input is not null) return;
            _audio = new MemoryStream();
            _input = new WaveIn { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50 };
            _writer = new WaveFileWriter(new NonClosingStream(_audio), _input.WaveFormat);
            _input.DataAvailable += (_, args) =>
            {
                var peak = 0;
                for (var offset = 0; offset + 1 < args.BytesRecorded; offset += 2)
                    peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(args.Buffer, offset)));
                _peakLevel = Math.Clamp(peak / 32768f, 0f, 1f);
                _writer?.Write(args.Buffer, 0, args.BytesRecorded);
            };
            _peakLevel = 0;
            _input.StartRecording();
        }
    }

    public byte[] Stop()
    {
        lock (_gate)
        {
            if (_input is null || _audio is null) return [];
            _input.StopRecording();
            _input.Dispose();
            _input = null;
            _peakLevel = 0;
            _writer?.Dispose();
            _writer = null;
            var bytes = _audio.ToArray();
            _audio.Dispose();
            _audio = null;
            return bytes;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_input is not null)
            {
                _input.StopRecording();
                _input.Dispose();
                _input = null;
            }
            _writer?.Dispose();
            _writer = null;
            _audio?.Dispose();
            _audio = null;
            _peakLevel = 0;
        }
    }

    public static async Task<string> TranscribeAsync(byte[] wav, string apiKey, CancellationToken cancellationToken = default, IAgentActivitySink? activity = null)
    {
        if (wav.Length < 100) throw new InvalidOperationException("No microphone audio was captured.");
        activity?.Publish(new(DateTimeOffset.Now, AgentActivityKind.Transcription, "Audio transcription",
            $"gpt-4o-transcribe · {Math.Max(1, wav.Length / 1024):N0} KB in memory",
            "Microphone audio is sent only for this user-initiated transcription and is never added to the activity log."));
        var timer = Stopwatch.StartNew();
        using var http = new HttpClient { BaseAddress = new Uri("https://api.openai.com/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("gpt-4o-transcribe"), "model");
        form.Add(new StringContent("Transcribe a short command for the AirBridge Windows audio router."), "prompt");
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "push-to-talk.wav");
        using var response = await http.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        timer.Stop();
        if (!response.IsSuccessStatusCode)
        {
            activity?.Publish(new(DateTimeOffset.Now, AgentActivityKind.Error, "Audio transcription failed",
                $"HTTP {(int)response.StatusCode}", DurationMilliseconds: timer.ElapsedMilliseconds, Tone: AgentActivityTone.Error));
            throw new InvalidOperationException($"Transcription failed ({(int)response.StatusCode}).");
        }
        using var document = JsonDocument.Parse(raw);
        var text = document.RootElement.GetProperty("text").GetString() ?? string.Empty;
        activity?.Publish(new(DateTimeOffset.Now, AgentActivityKind.Transcription, "Transcript ready",
            "Text returned to the local AirBridge assistant", AgentActivitySanitizer.Sanitize(text), timer.ElapsedMilliseconds, AgentActivityTone.Success));
        return text;
    }

    public void Dispose()
    {
        Cancel();
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { }
    }
}
