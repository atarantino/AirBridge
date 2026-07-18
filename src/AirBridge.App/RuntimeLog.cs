using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AirBridge.App;

internal sealed partial class RuntimeLog : IDisposable
{
    private const long DefaultMaximumBytes = 2 * 1024 * 1024;
    private const int DefaultRetainedFiles = 4;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maximumBytes;
    private readonly int _retainedFiles;
    private bool _disposed;

    internal RuntimeLog(string directory, long maximumBytes = DefaultMaximumBytes, int retainedFiles = DefaultRetainedFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maximumBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (retainedFiles is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(retainedFiles));
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, "airbridge.log");
        _maximumBytes = maximumBytes;
        _retainedFiles = retainedFiles;
    }

    internal string Path => _path;

    internal void Write(string level, string area, string message, Exception? exception = null)
    {
        var detail = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        var line = $"{DateTimeOffset.Now:O} [{Normalize(level)}] [{Normalize(area)}] {Redact(detail)}{Environment.NewLine}";
        lock (_gate)
        {
            if (_disposed) return;
            RotateIfNeeded(Encoding.UTF8.GetByteCount(line));
            File.AppendAllText(_path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    internal static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var redacted = IpCandidateRegex().Replace(value, match =>
            IPAddress.TryParse(match.Value.Trim('[', ']'), out _) ? "[network-address]" : match.Value);
        redacted = MacAddressRegex().Replace(redacted, "[hardware-address]");
        redacted = PipeNameRegex().Replace(redacted, "AirBridge.[pipe]");
        return redacted;
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        var length = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        if (length + incomingBytes <= _maximumBytes) return;
        var oldest = $"{_path}.{_retainedFiles}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = _retainedFiles - 1; index >= 1; index--)
        {
            var source = $"{_path}.{index}";
            if (File.Exists(source)) File.Move(source, $"{_path}.{index + 1}");
        }
        if (File.Exists(_path)) File.Move(_path, $"{_path}.1");
    }

    private static string Normalize(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }

    [GeneratedRegex(@"(?<![\w:])(?:\[[0-9A-Fa-f:.%]+\]|(?:\d{1,3}\.){3}\d{1,3}|[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7})(?![\w:])", RegexOptions.CultureInvariant)]
    private static partial Regex IpCandidateRegex();

    [GeneratedRegex(@"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex MacAddressRegex();

    [GeneratedRegex(@"airbridge(?:\.pcm\.\d+\.[0-9a-f]{16,}|-[0-9a-f]{16,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PipeNameRegex();
}

internal static class AppLog
{
    private static RuntimeLog? _current;
    internal static string DirectoryPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirBridge", "logs");
    internal static string? CurrentPath => Volatile.Read(ref _current)?.Path;

    internal static void Initialize()
    {
        if (Volatile.Read(ref _current) is not null) return;
        var created = new RuntimeLog(DirectoryPath);
        if (Interlocked.CompareExchange(ref _current, created, null) is not null) created.Dispose();
    }

    internal static void Info(string area, string message) => Volatile.Read(ref _current)?.Write("INFO", area, message);
    internal static void Warning(string area, string message) => Volatile.Read(ref _current)?.Write("WARN", area, message);
    internal static void Error(string area, string message, Exception? exception = null) => Volatile.Read(ref _current)?.Write("ERROR", area, message, exception);

    internal static void Shutdown()
    {
        var current = Interlocked.Exchange(ref _current, null);
        current?.Dispose();
    }
}
