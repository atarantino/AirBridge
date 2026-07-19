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
        if (!ContainsAudibleAudio(wav))
        {
            activity?.Publish(new(DateTimeOffset.Now, AgentActivityKind.Transcription, "No speech detected",
                "The microphone capture was silent", Tone: AgentActivityTone.Warning));
            return string.Empty;
        }
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
        var root = document.RootElement;
        var apiUsage = OpenAiCostEstimator.FromTranscriptionResponse(root);
        var text = root.GetProperty("text").GetString() ?? string.Empty;
        if (!ContainsTranscript(text))
        {
            activity?.Publish(new(DateTimeOffset.Now, AgentActivityKind.Transcription, "No speech detected",
                TranscriptionSummary("The transcription was empty", apiUsage), DurationMilliseconds: timer.ElapsedMilliseconds,
                Tone: AgentActivityTone.Warning, ApiUsage: apiUsage));
            return string.Empty;
        }
        activity?.Publish(new(DateTimeOffset.Now, AgentActivityKind.Transcription, "Transcript ready",
            TranscriptionSummary("Text returned to the local AirBridge assistant", apiUsage), AgentActivitySanitizer.Sanitize(text),
            timer.ElapsedMilliseconds, AgentActivityTone.Success, apiUsage));
        return text;
    }

    private static string TranscriptionSummary(string summary, OpenAiApiUsage? usage) => usage is null
        ? summary
        : $"{summary} · est. {OpenAiCostEstimator.FormatUsd(usage.EstimatedCostUsd)}";

    internal static bool ContainsTranscript(string? text) => !string.IsNullOrWhiteSpace(text);

    internal static bool ContainsAudibleAudio(byte[] wav)
    {
        var chunkOffset = 12;
        while (chunkOffset + 8 <= wav.Length)
        {
            var chunkSize = BitConverter.ToInt32(wav, chunkOffset + 4);
            if (chunkSize < 0) return false;
            var dataOffset = chunkOffset + 8;
            if (wav.AsSpan(chunkOffset, 4).SequenceEqual("data"u8))
            {
                var dataEnd = Math.Min(wav.Length, dataOffset + chunkSize);
                for (var offset = dataOffset; offset + 1 < dataEnd; offset += 2)
                    if (Math.Abs((int)BitConverter.ToInt16(wav, offset)) > 16) return true;
                return false;
            }
            var nextOffset = (long)dataOffset + chunkSize + (chunkSize & 1);
            if (nextOffset > wav.Length || nextOffset <= chunkOffset) return false;
            chunkOffset = (int)nextOffset;
        }
        return false;
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
