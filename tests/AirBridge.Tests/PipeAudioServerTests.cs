using System.Diagnostics;
using System.IO.Pipes;
using AirBridge.App;

namespace AirBridge.Tests;

public sealed class PipeAudioServerTests
{
    [Fact]
    public async Task NonReadingClientFillsBoundedQueueWithoutBlockingPumpCaller()
    {
        await using var server = new PipeAudioServer();
        server.Start();
        await using var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.In, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        Assert.True(SpinWait.SpinUntil(() => server.IsConnected, TimeSpan.FromSeconds(2)));
        var block = new byte[SharedAudioPump.BlockBytes];
        var stopwatch = Stopwatch.StartNew();
        var accepted = true;
        for (var index = 0; index < 100 && accepted; index++) accepted = server.TryWrite(block);

        stopwatch.Stop();
        Assert.False(accepted);
        Assert.True(server.IsConnected);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Bounded enqueue took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task ClientSideDisconnectDoesNotPermanentlyRemovePipeServer()
    {
        await using var server = new PipeAudioServer();
        server.Start();
        var first = await ConnectAsync(server);
        await first.DisposeAsync();

        var block = new byte[SharedAudioPump.BlockBytes];
        for (var index = 0; index < 100 && server.IsConnected; index++)
        {
            server.TryWrite(block);
            await Task.Delay(5);
        }

        Assert.True(SpinWait.SpinUntil(() => !server.IsConnected, TimeSpan.FromSeconds(2)));
        await using var second = await ConnectAsync(server);
        var fresh = Enumerable.Repeat((byte)0x5C, SharedAudioPump.BlockBytes).ToArray();
        Assert.True(server.TryWrite(fresh));
        Assert.Equal(fresh, await ReadBlockAsync(second));
    }

    [Fact]
    public async Task NonReadingPreRecordClientDropsExcessSilenceWithoutDisconnecting()
    {
        await using var server = new PipeAudioServer();
        server.Start();
        await using var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.In, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        Assert.True(SpinWait.SpinUntil(() => server.IsConnected, TimeSpan.FromSeconds(2)));
        var silence = new byte[SharedAudioPump.BlockBytes];

        var observedDrop = false;
        for (var index = 0; index < 500; index++)
        {
            var result = server.Write(silence, tolerateBackpressure: true);
            Assert.NotEqual(AudioPipeWriteResult.Unavailable, result);
            observedDrop |= result == AudioPipeWriteResult.Dropped;
        }

        Assert.True(server.IsConnected);
        Assert.True(observedDrop);
    }

    [Fact]
    public async Task FreshClientCanConnectAndReceiveAfterStandbyStyleDisconnect()
    {
        await using var server = new PipeAudioServer();
        server.Start();

        await using (var first = await ConnectAsync(server))
        {
            var firstBlock = Enumerable.Repeat((byte)0x11, SharedAudioPump.BlockBytes).ToArray();
            Assert.True(server.TryWrite(firstBlock));
            Assert.Equal(firstBlock, await ReadBlockAsync(first));
            server.DisconnectClient();
        }

        Assert.True(SpinWait.SpinUntil(() => !server.IsConnected, TimeSpan.FromSeconds(2)));
        await using var second = await ConnectAsync(server);
        var freshBlock = Enumerable.Repeat((byte)0x22, SharedAudioPump.BlockBytes).ToArray();
        Assert.True(server.TryWrite(freshBlock));
        Assert.Equal(freshBlock, await ReadBlockAsync(second));
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(PipeAudioServer server)
    {
        var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.In, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(2000);
            Assert.True(SpinWait.SpinUntil(() => server.IsConnected, TimeSpan.FromSeconds(2)));
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static async Task<byte[]> ReadBlockAsync(NamedPipeClientStream client)
    {
        var block = new byte[SharedAudioPump.BlockBytes];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await client.ReadExactlyAsync(block, timeout.Token);
        return block;
    }
}
