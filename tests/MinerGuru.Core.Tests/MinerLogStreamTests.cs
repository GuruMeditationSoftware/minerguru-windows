using System.Net;
using System.Net.Sockets;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class MinerLogStreamTests
{
    /// <summary>What an async void method throws lands here; in the app it would end the process.</summary>
    private sealed class RecordingContext : SynchronizationContext
    {
        public List<Exception> Errors { get; } = [];

        public override void Post(SendOrPostCallback callback, object? state) => ThreadPool.QueueUserWorkItem(_ =>
        {
            SetSynchronizationContext(this);
            try { callback(state); }
            catch (Exception e) { lock (Errors) Errors.Add(e); }
        });
    }

    [Fact]
    public async Task LeavingDuringASampleWindowThrowsNothing()
    {
        // Accepts the connection and never answers, so the sample window stays open.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var context = new RecordingContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        var stream = new MinerLogStream($"127.0.0.1:{port}");
        var after = 0;
        stream.StartSampling();
        SynchronizationContext.SetSynchronizationContext(previous);

        Assert.True(stream.IsSampling);
        await Task.Delay(100);
        stream.Changed += () => after++;
        stream.Dispose();
        await Task.Delay(500);

        Assert.Empty(context.Errors);
        Assert.Equal(0, after);
    }
}

public class MinerLogReconnectTests
{
    /// <summary>Accepts WebSocket upgrades on a free port; each connection sends one line and ends.</summary>
    private static (HttpListener Listener, string Host) StartServer(Func<int, bool> accept)
    {
        var port = 0;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            var count = 0;
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync();
                count++;
                if (!accept(count)) { context.Response.StatusCode = 503; context.Response.Close(); continue; }
                var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes($"I (1) test: line {count}\n"), System.Net.WebSockets.WebSocketMessageType.Text, true, default);
                await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", default);
            }
        });
        return (listener, $"127.0.0.1:{port}");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task ALostConnectionComesBackWithoutAClick()
    {
        var (listener, host) = StartServer(_ => true);
        using var stream = new MinerLogStream(host) { ReconnectDelay = TimeSpan.FromMilliseconds(20) };
        stream.StartLive();
        await WaitFor(() => stream.Lines.ToArray().Length >= 2);
        Assert.True(stream.IsLive);
        stream.StopLive();
        await WaitFor(() => !stream.IsLive);
        Assert.Null(stream.Error);
        listener.Stop();
    }

    [Fact]
    public async Task WhileTheMinerIsGoneTheErrorIsReadableAndClearsAfterwards()
    {
        var (listener, host) = StartServer(count => count >= 3);
        using var stream = new MinerLogStream(host) { ReconnectDelay = TimeSpan.FromMilliseconds(100) };
        var seen = new List<string>();
        stream.Changed += () => { if (stream.Error is { } e) lock (seen) seen.Add(e); };
        stream.StartLive();
        await WaitFor(() => stream.Lines.ToArray().Length >= 1);
        lock (seen)
        {
            Assert.NotEmpty(seen);
            Assert.All(seen, e => Assert.DoesNotContain("WebSocket", e));
        }
        stream.StopLive();
        await WaitFor(() => !stream.IsLive);
        listener.Stop();
    }
}
