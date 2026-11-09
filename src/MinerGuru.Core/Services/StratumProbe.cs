using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Services;

/// <summary>The first job a pool hands out, as far as the block template is concerned.</summary>
public sealed record StratumJob(string? Extranonce1, int? Extranonce2Size, string PrevHash, string Coinb1, string Coinb2,
    int MerkleBranchCount, string Version, string Nbits, string Ntime)
{
    public DecodedCoinbase? Coinbase => CoinbaseDecoder.Decode(Coinb1, Coinb2, Extranonce1, Extranonce2Size);
}

public enum StratumFailure { InvalidEndpoint, ConnectionFailed, Rejected, Timeout, Closed }

/// <summary>Why a probe ended without a job.</summary>
public sealed class StratumProbeException(StratumFailure failure, string? detail = null) : Exception(Text(failure, detail))
{
    public StratumFailure Failure { get; } = failure;
    public string? Detail { get; } = detail;

    private static string Text(StratumFailure failure, string? detail) => failure switch
    {
        StratumFailure.InvalidEndpoint => L("Pool-Adresse ist unvollständig.", "The pool address is incomplete."),
        StratumFailure.ConnectionFailed => L($"Keine Verbindung zum Pool: {detail}", $"No connection to the pool: {detail}"),
        StratumFailure.Rejected => L($"Der Pool lehnt den User ab: {detail}", $"The pool rejects the user: {detail}"),
        StratumFailure.Timeout => L("Der Pool hat keinen Auftrag geschickt.", "The pool sent no job."),
        _ => L("Der Pool hat die Verbindung getrennt.", "The pool closed the connection."),
    };
}

/// <summary>
/// Stratum V1 conversation up to the first <c>mining.notify</c>, without the network: subscribe, authorize,
/// read the job. Never submits a share.
/// </summary>
public sealed class StratumProbeSession(string user, string userAgent, string password = "x")
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private string? _extranonce1;
    private int? _extranonce2Size;
    private string? _authorizeError;

    /// <summary>Lines to send right after connecting.</summary>
    public IReadOnlyList<string> OpeningLines =>
    [
        Request(1, "mining.subscribe", [userAgent]),
        Request(2, "mining.authorize", [user, password]),
    ];

    /// <summary>Feeds one line from the pool; returns the job once there is one. Throws when the pool refuses.</summary>
    public StratumJob? Receive(string line)
    {
        JsonObject? message;
        try { message = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
        if (message == null) return null;

        if (Text(message["method"]) is { } method)
        {
            var parameters = message["params"] as JsonArray ?? [];
            switch (method)
            {
                case "mining.set_extranonce":
                    if (parameters.Count >= 2) ReadExtranonce(parameters[0], parameters[1]);
                    break;
                case "mining.notify":
                    // Ocean sends the job before the authorize result, so a job is taken whenever it comes.
                    if (_authorizeError != null) throw new StratumProbeException(StratumFailure.Rejected, _authorizeError);
                    return Job(parameters, _extranonce1, _extranonce2Size);
            }
            return null;
        }

        switch (Number(message["id"]))
        {
            case 1:
                // [[subscriptions], extranonce1, extranonce2_size]; some pools refuse unknown agents and still send work.
                if (message["result"] is JsonArray { Count: >= 3 } result) ReadExtranonce(result[1], result[2]);
                break;
            case 2:
                if (!IsTrue(message["result"]))
                {
                    _authorizeError = ErrorText(message["error"]) ?? L("ohne Begründung", "no reason given");
                    throw new StratumProbeException(StratumFailure.Rejected, _authorizeError);
                }
                break;
        }
        return null;
    }

    private void ReadExtranonce(JsonNode? nonce1, JsonNode? size)
    {
        if (Text(nonce1) is not { } text || Number(size) is not { } value) return;
        _extranonce1 = text;
        _extranonce2Size = value;
    }

    /// <summary><c>[job_id, prevhash, coinb1, coinb2, merkle_branch, version, nbits, ntime, clean]</c></summary>
    private static StratumJob? Job(JsonArray p, string? extranonce1, int? extranonce2Size)
    {
        if (p.Count < 8 || Text(p[1]) is not { } prevHash || Text(p[2]) is not { } coinb1 || Text(p[3]) is not { } coinb2 ||
            p[4] is not JsonArray branches || Text(p[5]) is not { } version || Text(p[6]) is not { } nbits || Text(p[7]) is not { } ntime) return null;
        return new StratumJob(extranonce1, extranonce2Size, prevHash, coinb1, coinb2, branches.Count, version, nbits, ntime);
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static bool IsTrue(JsonNode? node) =>
        node is JsonValue value && ((value.TryGetValue<bool>(out var flag) && flag) || (value.TryGetValue<int>(out var number) && number != 0));

    private static string? ErrorText(JsonNode? error) => error switch
    {
        JsonArray list when list.Count >= 2 => Text(list[1]),
        JsonArray { Count: 1 } list => list[0]?.ToString(),
        JsonObject o => Text(o["message"]),
        _ => Text(error),
    };

    private static string Request(int id, string method, string[] parameters) =>
        JsonSerializer.Serialize(new { id, method, @params = parameters }, Options);
}

/// <summary>Opens a short Stratum connection from the PC to read the pool's current block template.</summary>
public static class StratumProbe
{
    public static string UserAgent =>
        $"MinerGuru/{Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "dev"} (block view, no mining)";

    public static async Task<StratumJob> FetchJobAsync(string rawHost, int port, bool tls, string user, TimeSpan? timeout = null, CancellationToken cancellation = default)
    {
        var host = StratumHost.Clean(rawHost);
        if (host.Length == 0 || port is < 1 or > 65535) throw new StratumProbeException(StratumFailure.InvalidEndpoint);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        var session = new StratumProbeSession(user, UserAgent);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, deadline.Token);
            Stream stream = client.GetStream();
            if (tls)
            {
                // Only public data travels here (address in, block template out); pools often use self-signed certificates.
                var secure = new SslStream(stream, false, (_, _, _, _) => true);
                await secure.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, deadline.Token);
                stream = secure;
            }
            await using var _ = stream;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(session.OpeningLines.Select(l => l + "\n"))), deadline.Token);
            return await ReadJobAsync(stream, session, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new StratumProbeException(StratumFailure.Timeout);
        }
        catch (Exception e) when (e is SocketException or IOException or AuthenticationException)
        {
            throw new StratumProbeException(StratumFailure.ConnectionFailed, e.Message);
        }
    }

    private static async Task<StratumJob> ReadJobAsync(Stream stream, StratumProbeSession session, CancellationToken cancellation)
    {
        var buffer = new byte[64 * 1024];
        var pending = new List<byte>();
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellation);
            if (count == 0 || pending.Count + count >= 1_000_000) throw new StratumProbeException(StratumFailure.Closed);
            pending.AddRange(buffer.AsSpan(0, count));
            int newline;
            while ((newline = pending.IndexOf((byte)'\n')) >= 0)
            {
                var line = Encoding.UTF8.GetString(pending.GetRange(0, newline).ToArray());
                pending.RemoveRange(0, newline + 1);
                if (session.Receive(line) is { } job) return job;
            }
        }
    }
}
