using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MinerGuru.Core.Models;

namespace MinerGuru.Core.Services;

public class MinerApiException(string message, int? status = null, string detail = "") : Exception(message)
{
    private readonly string? _unauthorized = status == 401 ? detail.ToLowerInvariant() : null;

    public int? Status { get; } = status;

    /// <summary>AxeOS also answers 401 (requests from outside the local network), so the text decides.</summary>
    public bool IsOtpRefused => _unauthorized?.Contains("otp") == true && !IsOtpBlocked;
    public bool IsOtpBlocked => _unauthorized?.Contains("blocked") == true;
    public bool IsOtpWrongCode => _unauthorized?.Contains("invalid") == true;

    public static MinerApiException FromResponse(int status, string body)
    {
        var plain = new MinerApiException("", status, body);
        var message = plain.IsOtpBlocked ? Strings.TooManyWrongOneTime
            : plain.IsOtpRefused ? Strings.TheMinerDemandsAValid
            : string.Format(Strings.HTTPError, status) + (string.IsNullOrWhiteSpace(body) ? "" : $": {body.Trim()}");
        return new MinerApiException(message, status, body);
    }
}

/// <summary>Payload of <c>PATCH /api/system</c>; fields left <c>null</c> are not sent.</summary>
public sealed class MinerSystemUpdatePayload
{
    [JsonPropertyName("hostname")] public string? Hostname { get; set; }
    [JsonPropertyName("frequency")] public int? Frequency { get; set; }
    [JsonPropertyName("coreVoltage")] public int? CoreVoltage { get; set; }
    [JsonPropertyName("overclockEnabled")] public int? OverclockEnabled { get; set; }
    [JsonPropertyName("autofanspeed")] public int? AutoFanSpeed { get; set; }
    [JsonPropertyName("fanspeed")] public int? FanSpeed { get; set; }
    [JsonPropertyName("manualFanSpeed")] public int? ManualFanSpeed { get; set; }
    [JsonPropertyName("temptarget")] public int? TempTarget { get; set; }
    [JsonPropertyName("pidTargetTemp")] public int? PidTargetTemp { get; set; }
    [JsonPropertyName("poolMode")] public int? PoolMode { get; set; }
    [JsonPropertyName("poolBalance")] public int? PoolBalance { get; set; }
    [JsonPropertyName("stratumURL")] public string? StratumUrl { get; set; }
    [JsonPropertyName("stratumPort")] public int? StratumPort { get; set; }
    [JsonPropertyName("stratumUser")] public string? StratumUser { get; set; }
    [JsonPropertyName("stratumPassword")] public string? StratumPassword { get; set; }
    [JsonPropertyName("fallbackStratumURL")] public string? FallbackStratumUrl { get; set; }
    [JsonPropertyName("fallbackStratumPort")] public int? FallbackStratumPort { get; set; }
    [JsonPropertyName("fallbackStratumUser")] public string? FallbackStratumUser { get; set; }
    [JsonPropertyName("fallbackStratumPassword")] public string? FallbackStratumPassword { get; set; }
    /// <summary>AxeOS 2.15+: all filled slots, with the primary and fallback slot.</summary>
    [JsonPropertyName("pools")] public List<JsonObject>? Pools { get; set; }
    [JsonPropertyName("primaryPoolIndex")] public int? PrimaryPoolIndex { get; set; }
    [JsonPropertyName("secondaryPoolIndex")] public int? SecondaryPoolIndex { get; set; }
}

/// <summary>HTTP API of AxeOS and NerdOS. Hosts may carry a port (<c>192.168.1.20</c>, <c>127.0.0.1:48001</c>).</summary>
/// <summary>The user declined a write: nothing happened.</summary>
public sealed class WriteDeclinedException() : MinerApiException(Strings.Cancelled);

public sealed class MinerApiClient
{
    public static MinerApiClient Shared { get; } = new();

    private static readonly JsonSerializerOptions PayloadOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    private readonly HttpClient _http;

    /// <param name="handler">Tests pass their own, to see what would be sent.</param>
    public MinerApiClient(HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(6);
    }

    /// <summary>Strips <c>http://</c>, paths and whitespace from what the user typed.</summary>
    public static string NormalizeHost(string raw)
    {
        var s = raw.Trim();
        foreach (var prefix in new[] { "http://", "https://" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        var slash = s.IndexOf('/');
        return slash >= 0 ? s[..slash] : s;
    }

    private static Uri BuildUri(string host, string path)
    {
        var normalized = NormalizeHost(host);
        if (normalized.Length == 0 || !Uri.TryCreate($"http://{normalized}{path}", UriKind.Absolute, out var uri))
            throw new MinerApiException(string.Format(Strings.InvalidAddress, host));
        return uri;
    }

    /// <summary><c>GET /api/system/info</c>: the telemetry and the reply as indented JSON.</summary>
    public async Task<(MinerSystemInfo Info, string RawJson)> FetchSystemInfoAsync(string host, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3.5));
        string body;
        try
        {
            using var response = await _http.GetAsync(BuildUri(host, "/api/system/info"), cts.Token);
            body = await response.Content.ReadAsStringAsync(cts.Token);
            if (!response.IsSuccessStatusCode) throw MinerApiException.FromResponse((int)response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MinerApiException(Strings.TimedOut);
        }
        catch (HttpRequestException e)
        {
            throw new MinerApiException(e.Message);
        }

        try
        {
            var node = JsonNode.Parse(body) as JsonObject ?? throw new JsonException(Strings.NotAJSONObject);
            return (MinerSystemInfo.Parse(node), node.ToJsonString(PrettyOptions));
        }
        catch (JsonException e)
        {
            throw new MinerApiException(string.Format(Strings.InvalidReplyFromTheMiner, e.Message));
        }
    }

    /// <summary><c>PATCH /api/system</c>. AxeOS uses clock changes only after a restart, NerdOS at once.</summary>
    public static MinerApiException ReadOnlyError() =>
        new(Strings.ReadOnlyModeIsOn);

    public static string PayloadJson(MinerSystemUpdatePayload payload) => JsonSerializer.Serialize(payload, PayloadOptions);

    /// <summary>Whether a TCP connection to the pool succeeds; only says the pool answers.</summary>
    public static async Task<bool> CanReachAsync(string rawHost, int port)
    {
        var host = StratumHost.Clean(rawHost);
        if (host.Length == 0 || port is < 1 or > 65535) return false;
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    public async Task UpdateSystemSettingsAsync(string host, MinerSystemUpdatePayload payload, string? otpSession = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, BuildUri(host, "/api/system"))
        {
            // A string body, not JsonContent: that one goes out chunked without Content-Length, and the miners' ESP-IDF server then reads nothing.
            Content = new StringContent(JsonSerializer.Serialize(payload, PayloadOptions), Encoding.UTF8, "application/json"),
        };
        if (otpSession != null) request.Headers.Add("X-OTP-Session", otpSession);
        await SendAsync(request, ct);
    }

    /// <summary>Exchanges a one-time code for a session via <c>POST /api/otp/session</c> (NerdOS).</summary>
    public async Task<OtpSession> CreateOtpSessionAsync(string host, string code)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(host, "/api/otp/session"))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-TOTP", code);
        var body = await SendAsync(request, default);
        try
        {
            return OtpSession.Parse(body, DateTime.UtcNow);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            throw new MinerApiException(string.Format(Strings.InvalidReplyFromTheMiner, e.Message));
        }
    }

    /// <summary><c>DELETE /api/system/pools/{index}</c> (AxeOS 2.15+); refused for the primary and fallback slot.</summary>
    public Task DeletePoolSlotAsync(string host, int index, CancellationToken ct = default) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Delete, BuildUri(host, $"/api/system/pools/{index}")), ct);

    public Task RestartAsync(string host, CancellationToken ct = default) => PostCommandAsync(host, "/api/system/restart", ct);

    /// <summary>Stops hashing without a reboot (AxeOS 2.15+).</summary>
    public Task PauseAsync(string host, CancellationToken ct = default) => PostCommandAsync(host, "/api/system/pause", ct);

    public Task ResumeAsync(string host, CancellationToken ct = default) => PostCommandAsync(host, "/api/system/resume", ct);

    private Task PostCommandAsync(string host, string path, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, BuildUri(host, path))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        }, ct);

    /// <summary>
    /// Sends something that changes a miner and notes the outcome in the diagnostic log: the names of
    /// the fields sent, never their values or the miner's answer, which can hold passwords.
    /// </summary>
    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (ChangeLog.Shared.IsReadOnly) throw ReadOnlyError();
        var sent = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
        var fields = sent == null ? "" : DiagnosticLog.FieldNames(sent);
        var host = request.RequestUri is { } uri ? (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}") : "?";
        var path = request.RequestUri?.AbsolutePath ?? "?";
        var method = request.Method.Method;
        var target = $"{request.RequestUri?.Host}:{request.RequestUri?.Port} {request.Method} {request.RequestUri?.AbsolutePath}{(fields == "" ? "" : $" fields={fields}")}";
        try
        {
            using (request)
            using (var response = await _http.SendAsync(request, ct))
            {
                DiagnosticLog.Shared.Write("write", $"{target} status={(int)response.StatusCode}");
                ChangeLog.Shared.RecordWrite(host, method, path, sent, response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode) throw MinerApiException.FromResponse((int)response.StatusCode, body);
                return body;
            }
        }
        catch (HttpRequestException e)
        {
            DiagnosticLog.Shared.Write("write", $"{target} failed");
            ChangeLog.Shared.RecordWrite(host, method, path, sent, e.Message);
            throw new MinerApiException(e.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            DiagnosticLog.Shared.Write("write", $"{target} timed out");
            ChangeLog.Shared.RecordWrite(host, method, path, sent, Strings.TimedOut);
            throw new MinerApiException(Strings.TimedOut);
        }
    }
}
