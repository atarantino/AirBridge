using System.Diagnostics;
using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class PythonRaopClientProcessTests
{
    private sealed class HostFixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "AirBridge-process-tests", Guid.NewGuid().ToString("N"));
        public string HostPath { get; }
        public string PythonPath { get; }

        public HostFixture(string source)
        {
            Directory.CreateDirectory(DirectoryPath);
            HostPath = Path.Combine(DirectoryPath, "fixture_host.py");
            File.WriteAllText(HostPath, source);
            var candidate = PythonRaopClient.FindRuntime(AppContext.BaseDirectory).Runtime;
            if (File.Exists(candidate)) PythonPath = candidate;
            else
            {
                PythonPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                    .Select(directory => Path.Combine(directory, "python.exe")).FirstOrDefault(File.Exists)
                    ?? throw new InvalidOperationException("Python 3.12 is required for the real process-boundary tests. Run scripts/bootstrap.ps1.");
            }
        }

        public PythonRaopClient CreateClient() => new(PythonPath, HostPath);
        public void Dispose()
        {
            File.Delete(HostPath);
            File.Delete(Path.Combine(DirectoryPath, "starts.txt"));
            Directory.Delete(DirectoryPath);
        }
    }

    private const string PersistentHost = """
        import json, os, sys
        with open(os.path.join(os.path.dirname(__file__), "starts.txt"), "a") as starts:
            starts.write(str(os.getpid()) + "\n")
        for line in sys.stdin:
            request = json.loads(line)
            result = {"ok": True, "pid": os.getpid(), "profile": os.getenv("AIRBRIDGE_DATA_DIR"), "run_id": os.getenv("AIRBRIDGE_RUN_ID"),
                      "sensitive_inherited": any(os.getenv(name) for name in ["OPENAI_API_KEY", "AIRBRIDGE_MODEL_EVAL_KEY", "AIRBRIDGE_RUN_HARDWARE_TESTS", "AIRBRIDGE_MODEL_EVALS", "AIRBRIDGE_RUN_MODEL_EVALS"])}
            print(json.dumps({"request_id": request["request_id"], "ok": True, "result": result}), flush=True)
        """;

    [Fact]
    public async Task ConcurrentStartsShareOneOwnedHostProcessAndPreserveProfileIdentity()
    {
        using var fixture = new HostFixture(PersistentHost);
        await using var client = fixture.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.StartAsync(deadline.Token)));
        var first = await client.PingAsync(deadline.Token);
        var second = await client.PingAsync(deadline.Token);
        Assert.Equal(first.GetProperty("pid").GetInt32(), second.GetProperty("pid").GetInt32());
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.DirectoryPath, "starts.txt")));
        Assert.Equal(RuntimeProfile.DataDirectory, first.GetProperty("profile").GetString());
        Assert.Equal(RuntimeProfile.RunId, first.GetProperty("run_id").GetString());
        Assert.False(first.GetProperty("sensitive_inherited").GetBoolean());
    }

    [Fact]
    public void HostEnvironmentExcludesAiCredentialsAndUnrelatedPaidHardwareOptIns()
    {
        var startInfo = new ProcessStartInfo();
        var excluded = new[] { "OPENAI_API_KEY", "AIRBRIDGE_MODEL_EVAL_KEY", "AIRBRIDGE_RUN_HARDWARE_TESTS", "AIRBRIDGE_MODEL_EVALS", "AIRBRIDGE_RUN_MODEL_EVALS" };
        foreach (var name in excluded) startInfo.Environment[name] = "fixture-only-sentinel";
        PythonRaopClient.ConfigureHostEnvironment(startInfo);
        foreach (var name in excluded) Assert.False(startInfo.Environment.ContainsKey(name));
        Assert.Equal(RuntimeProfile.DataDirectory, startInfo.Environment[RuntimeProfile.DataDirectoryVariable]);
        Assert.Equal(RuntimeProfile.RunId, startInfo.Environment["AIRBRIDGE_RUN_ID"]);
    }

    [Fact]
    public async Task AFinalResponseIsDrainedBeforeProcessExitFailsRemainingRequests()
    {
        const string source = """
            import json, sys
            for index, line in enumerate(sys.stdin):
                request = json.loads(line)
                print(json.dumps({"request_id": request["request_id"], "ok": True, "result": {"ok": True}}), flush=True)
                if index == 1:
                    break
            """;
        for (var iteration = 0; iteration < 4; iteration++)
        {
            using var fixture = new HostFixture(source);
            await using var client = fixture.CreateClient();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.StartAsync(deadline.Token);
            Assert.True((await client.PingAsync(deadline.Token)).GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    public async Task StructurallyMalformedResponsesDoNotDisableTheReaderOrLoseTheValidResponse()
    {
        const string source = """
            import json, sys
            for line in sys.stdin:
                request = json.loads(line)
                print(json.dumps({"request_id": request["request_id"], "ok": True}), flush=True)
                print(json.dumps({"request_id": request["request_id"], "ok": "wrong-type", "result": {}}), flush=True)
                print("[]", flush=True)
                print(json.dumps({"request_id": request["request_id"], "ok": True, "result": {"ok": True}}), flush=True)
            """;
        using var fixture = new HostFixture(source);
        await using var client = fixture.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.StartAsync(deadline.Token);
        Assert.True((await client.PingAsync(deadline.Token)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task EofResolvesAnOutstandingRequestBeforeTheFifteenSecondCommandDeadline()
    {
        const string source = """
            import json, sys
            for index, line in enumerate(sys.stdin):
                request = json.loads(line)
                if index == 1:
                    break
                print(json.dumps({"request_id": request["request_id"], "ok": True, "result": {"ok": True}}), flush=True)
            """;
        using var fixture = new HostFixture(source);
        await using var client = fixture.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.StartAsync(deadline.Token);
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync(deadline.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"EOF completion took {watch.Elapsed}.");
    }

    [Fact]
    public async Task StartupFailureCleansUpTheOwnedHostWithoutLeavingAPendingCommand()
    {
        using var fixture = new HostFixture("import sys\nsys.exit(0)\n");
        await using var client = fixture.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(() => client.StartAsync(deadline.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync(deadline.Token));
    }
}
