using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using MinerGuru.Core.Models;

namespace MinerGuru.Core.Services;

public enum LogLevel { Info, Warning, Error, Other }

public sealed record LogLine(DateTime Time, LogLevel Level, string Text);

/// <summary>
/// The miner's live log over the WebSocket <c>/api/ws</c>, while a miner page is open. Without a running log it
/// connects once a minute for ten seconds, so the share chart fills up without a permanent connection to the miner.
/// </summary>
public sealed partial class MinerLogStream(string host) : IDisposable
{
    public const int MaxLines = 400;
    public const int MaxSamples = 600;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SampleWindow = TimeSpan.FromSeconds(10);

    /// <summary>Pause before a running log connects again.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _life = new();
    private CancellationTokenSource? _live;
    private string _partial = "";

    public List<LogLine> Lines { get; } = [];
    public List<ShareSample> Samples { get; } = [];
    public bool IsLive { get; private set; }
    public bool IsSampling { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Raised on the caller's context after new data or a state change.</summary>
    public event Action? Changed;

    /// <summary>Starts the once-a-minute sample windows for the share chart.</summary>
    public async void StartSampling()
    {
        try
        {
            while (!_life.IsCancellationRequested)
            {
                if (!IsLive)
                {
                    IsSampling = true;
                    Changed?.Invoke();
                    using var window = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
                    window.CancelAfter(SampleWindow);
                    await ReceiveAsync(keepLines: false, window.Token);
                    IsSampling = false;
                    if (_life.IsCancellationRequested) return;
                    Changed?.Invoke();
                }
                await Task.Delay(SampleInterval, _life.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Report(e); }
    }

    public async void StartLive()
    {
        if (IsLive) return;
        IsLive = true;
        Error = null;
        _live = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        var token = _live.Token;
        Changed?.Invoke();
        try
        {
            // The miner restarts or drops off the network now and then; the log comes back without another click.
            while (true)
            {
                await ReceiveAsync(keepLines: true, token);
                token.ThrowIfCancellationRequested();
                Error = Strings.ConnectionToTheMinerLost;
                Changed?.Invoke();
                await Task.Delay(ReconnectDelay, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Report(e); }
        IsLive = false;
        Error = null;
        if (!_life.IsCancellationRequested) Changed?.Invoke();
    }

    public void StopLive() => _live?.Cancel();

    public void Clear()
    {
        Lines.Clear();
        Changed?.Invoke();
    }

    /// <summary>Only cancels: the running loops still read the token while they wind down.</summary>
    public void Dispose() => _life.Cancel();

    /// <summary>An exception leaving an async void method ends the app, so it stops at these two.</summary>
    private static void Report(Exception e) => DiagnosticLog.Shared.Write("log", $"stream stopped: {e.GetType().Name}");

    private async Task ReceiveAsync(bool keepLines, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        var buffer = new byte[8192];
        try
        {
            await socket.ConnectAsync(new Uri($"ws://{MinerApiClient.NormalizeHost(host)}/api/ws"), ct);
            if (keepLines)
            {
                _partial = "";
                Error = null;
                Changed?.Invoke();
            }
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                Ingest(Encoding.UTF8.GetString(buffer, 0, result.Count), keepLines);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is WebSocketException or UriFormatException or InvalidOperationException)
        {
            DiagnosticLog.Shared.Write("log", $"connection lost: {e.GetType().Name}");
        }
    }

    private void Ingest(string chunk, bool keepLines)
    {
        var now = DateTime.Now;
        var parts = (_partial + chunk).Split('\n');
        _partial = parts[^1];
        var changed = false;
        foreach (var raw in parts[..^1])
        {
            var text = AnsiPattern().Replace(raw, "").TrimEnd();
            if (text.Length == 0) continue;
            changed = true;
            if (ShareSample.Parse(text, now) is { } sample) Samples.Add(sample);
            if (keepLines) Lines.Add(new LogLine(now, LevelOf(text), text));
        }
        if (Samples.Count > MaxSamples) Samples.RemoveRange(0, Samples.Count - MaxSamples);
        if (Lines.Count > MaxLines) Lines.RemoveRange(0, Lines.Count - MaxLines);
        if (changed) Changed?.Invoke();
    }

    /// <summary>ESP-IDF log lines start with <c>I (1234) tag: …</c>.</summary>
    public static LogLevel LevelOf(string line) =>
        line.Length > 2 && line[1] == ' ' && line[2] == '('
            ? line[0] switch { 'E' => LogLevel.Error, 'W' => LogLevel.Warning, 'I' => LogLevel.Info, _ => LogLevel.Other }
            : LogLevel.Other;

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiPattern();
}
