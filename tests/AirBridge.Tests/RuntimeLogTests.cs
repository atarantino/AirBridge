using AirBridge.App;

namespace AirBridge.Tests;

public sealed class RuntimeLogTests : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"airbridge-log-test-{Guid.NewGuid():N}");

    [Fact]
    public void Write_RedactsNetworkAndTransportIdentifiers_ButKeepsExceptionDetails()
    {
        using var log = new RuntimeLog(_directory);

        log.Write("ERROR", "test", "peer 192.168.1.44 [fe80::1234] AA:BB:CC:DD:EE:FF pipes airbridge-0123456789abcdef and AirBridge.Pcm.1234.abcdef0123456789", new InvalidOperationException("inner detail"));

        var content = File.ReadAllText(log.Path);
        Assert.DoesNotContain("192.168.1.44", content);
        Assert.DoesNotContain("fe80::1234", content);
        Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", content);
        Assert.DoesNotContain("0123456789abcdef", content);
        Assert.Contains("[network-address]", content);
        Assert.Contains("[hardware-address]", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("inner detail", content);
    }

    [Fact]
    public void Write_RotatesAtConfiguredBound()
    {
        using var log = new RuntimeLog(_directory, maximumBytes: 1024, retainedFiles: 2);
        for (var index = 0; index < 30; index++) log.Write("INFO", "rotation", new string('x', 100));

        Assert.True(File.Exists(log.Path));
        Assert.True(File.Exists(log.Path + ".1"));
        Assert.True(Directory.GetFiles(_directory, "airbridge.log*").Length <= 3);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
