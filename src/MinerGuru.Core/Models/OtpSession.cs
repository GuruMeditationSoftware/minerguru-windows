using System.Text.Json.Nodes;

namespace MinerGuru.Core.Models;

/// <summary>A session the miner granted for a one-time code (NerdOS): sent as <c>X-OTP-Session</c> when writing settings.</summary>
public sealed record OtpSession(string Token, DateTime ExpiresUtc)
{
    /// <summary>A session this close to its end could run out on the way to the miner.</summary>
    public static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(60);

    public bool IsUsable(DateTime nowUtc) => ExpiresUtc - nowUtc > SafetyMargin;

    /// <summary>The miner answers <c>{token, ttlMs, expiresAt}</c>. Its clock may be wrong, so only the duration counts.</summary>
    public static OtpSession Parse(string body, DateTime nowUtc)
    {
        var json = JsonNode.Parse(body) as JsonObject ?? throw new FormatException();
        var token = json["token"]?.GetValue<string>() ?? throw new FormatException();
        var ttl = json["ttlMs"]?.GetValue<double>() ?? throw new FormatException();
        return new OtpSession(token, nowUtc.AddMilliseconds(ttl));
    }
}
