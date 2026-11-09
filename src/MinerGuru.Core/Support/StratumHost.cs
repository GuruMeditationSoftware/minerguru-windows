namespace MinerGuru.Core;

/// <summary>Normalizes pool addresses as entered in AxeOS / NerdOS (<c>stratum+tcp://pool.example:3333/x</c>).</summary>
public static class StratumHost
{
    private static readonly string[] Schemes = ["stratum+tcp://", "stratum+ssl://", "stratum://", "tcp://", "http://", "https://"];

    /// <summary>
    /// Strips scheme and path. Without <paramref name="keepPort"/> a trailing <c>:port</c> goes too:
    /// <c>stratum+tcp://eusolo.ckpool.org:3333/x</c> → <c>eusolo.ckpool.org</c>.
    /// </summary>
    public static string Clean(string raw, bool keepPort = false)
    {
        var s = raw.Trim();
        var scheme = Schemes.FirstOrDefault(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (scheme != null) s = s[scheme.Length..];
        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        if (!keepPort)
        {
            var colon = s.IndexOf(':');
            if (colon >= 0) s = s[..colon];
        }
        return s;
    }
}
