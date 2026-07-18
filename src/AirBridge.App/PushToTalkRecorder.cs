using System.Net.Http.Headers;
using System.Text.Json;
using NAudio.Wave;

namespace AirBridge.App;

public sealed class PushToTalkRecorder : IDisposable
{
    private readonly object _gate = new();
    private WaveIn? _input;
    private WaveFileWriter? _writer;
    private MemoryStream? _audio;

    public bool IsRecording => _input is not null;

    public void Start()
    {
        lock (_gate)
        {
            if (_input is not null) return;
            _audio = new MemoryStream();
            _input = new WaveIn { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50 };
            _writer = new WaveFileWriter(new NonClosingStream(_audio), _input.WaveFormat);
            _input.DataAvailable += (_, args) => _writer?.Write(args.Buffer, 0, args.BytesRecorded);
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
            _writer?.Dispose();
            _writer = null;
            var bytes = _audio.ToArray();
            _audio.Dispose();
            _audio = null;
            return bytes;
        }
    }

    public static async Task<string> TranscribeAsync(byte[] wav, string apiKey, CancellationToken cancellationToken = default)
    {
        if (wav.Length < 100) throw new InvalidOperationException("No microphone audio was captured.");
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
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Transcription failed ({(int)response.StatusCode}).");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.GetProperty("text").GetString() ?? string.Empty;
    }

    public void Dispose()
    {
        if (IsRecording) Stop();
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
