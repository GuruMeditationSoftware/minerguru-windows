namespace MinerGuru.Core.Models;

/// <summary>The SHA-256 coin mined on a pool slot, detected from pool host, wallet format and network difficulty.</summary>
public enum MinedCoin { Btc, Bch, Dgb, Xec, Bsv, Ppc, Nmc, Fb, Unknown }

public static class MinedCoinExtensions
{
    public static string Symbol(this MinedCoin coin) => coin switch
    {
        MinedCoin.Unknown => "SHA-256",
        _ => coin.ToString().ToUpperInvariant(),
    };

    public static string FullName(this MinedCoin coin) => coin switch
    {
        MinedCoin.Btc => "Bitcoin",
        MinedCoin.Bch => "Bitcoin Cash",
        MinedCoin.Dgb => "DigiByte",
        MinedCoin.Xec => "eCash",
        MinedCoin.Bsv => "Bitcoin SV",
        MinedCoin.Ppc => "Peercoin",
        MinedCoin.Nmc => "Namecoin",
        MinedCoin.Fb => "Fractal Bitcoin",
        _ => "SHA-256 Coin",
    };
}

public static class MinedCoinDetector
{
    private const string CashAddrCharset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    private static readonly string[] KnownBtcHosts =
    [
        "ckpool.org", "public-pool.io", "ocean.xyz", "braiins.com", "slushpool.com", "kano.is", "vkbit.com",
        "antpool.com", "f2pool.com", "viabtc.com", "foundryusa.com", "luxor.tech", "btc.",
    ];

    /// <summary>
    /// Detects the coin with three signals: keywords in the pool host, the wallet address format and the
    /// network difficulty reported by Stratum (&gt; 20T BTC, 50G–10T BCH, 1M–50G DGB).
    /// </summary>
    public static MinedCoin Detect(string poolHost, string stratumUser, double? networkDifficulty = null)
    {
        var host = poolHost.Trim().ToLowerInvariant();
        var user = stratumUser.Trim();
        if (host.Length == 0 && user.Length == 0) return MinedCoin.Unknown;

        // The wallet before an optional `.worker` suffix.
        var dot = user.IndexOf('.');
        var wallet = dot >= 0 ? user[..dot] : user;
        var walletLower = wallet.ToLowerInvariant();

        if (FromHost(host) is { } byHost) return byHost;
        if (FromAddress(wallet) is { } byAddress) return byAddress;

        // 3. Network difficulty
        if (networkDifficulty is > 0 and var nd)
        {
            if (nd >= 20e12) return MinedCoin.Btc;
            if (nd >= 50e9 && nd < 10e12) return MinedCoin.Bch;
            if (nd >= 1e6 && nd < 50e9) return MinedCoin.Dgb;
        }

        // 4. Known Bitcoin pools and legacy BTC addresses
        if (KnownBtcHosts.Any(host.Contains)) return MinedCoin.Btc;
        if ((wallet.StartsWith('1') || wallet.StartsWith('3')) && wallet.Length is >= 26 and <= 35) return MinedCoin.Btc;
        return MinedCoin.Unknown;
    }

    private static MinedCoin? FromHost(string host)
    {
        if (host.StartsWith("bch.") || host.Contains(".bch.") || host.Contains("-bch") || host.Contains("bchpool") || host.Contains("bitcoincash")) return MinedCoin.Bch;
        if (host.Contains("digibyte") || host.StartsWith("dgb.") || host.Contains(".dgb.") || host.Contains("-dgb")) return MinedCoin.Dgb;
        if (host.Contains("ecash") || host.StartsWith("xec.") || host.Contains(".xec.") || host.Contains("-xec")) return MinedCoin.Xec;
        if (host.StartsWith("bsv.") || host.Contains(".bsv.") || host.Contains("bitcoinsv")) return MinedCoin.Bsv;
        if (host.Contains("peercoin") || host.StartsWith("ppc.") || host.Contains(".ppc.")) return MinedCoin.Ppc;
        if (host.Contains("namecoin") || host.StartsWith("nmc.") || host.Contains(".nmc.")) return MinedCoin.Nmc;
        if (host.Contains("fractal") || host.StartsWith("fb.") || host.Contains(".fb.")) return MinedCoin.Fb;
        return null;
    }

    private static MinedCoin? FromAddress(string wallet)
    {
        var lower = wallet.ToLowerInvariant();
        if (lower.StartsWith("bitcoincash:")) return MinedCoin.Bch;
        if (lower.StartsWith("ecash:")) return MinedCoin.Xec;
        if (lower.StartsWith("dgb1")) return MinedCoin.Dgb;
        if (lower.StartsWith("bc1q") || lower.StartsWith("bc1p")) return MinedCoin.Btc;
        if (lower.StartsWith("pc1")) return MinedCoin.Ppc;
        if (lower.StartsWith("nc1")) return MinedCoin.Nmc;
        // CashAddr without prefix: q or p, 42 characters of the Bech32 charset
        if ((wallet.StartsWith('q') || wallet.StartsWith('p')) && wallet.Length == 42 && wallet.All(CashAddrCharset.Contains)) return MinedCoin.Bch;
        // DigiByte legacy address
        if (wallet.StartsWith('D') && wallet.Length is >= 26 and <= 35) return MinedCoin.Dgb;
        return null;
    }

    /// <summary>
    /// The coins of pool and address when they clearly differ. Payouts to such an address may be lost, but a
    /// multi-coin pool can still be right, so this only warns. Legacy addresses fit several coins and never count.
    /// </summary>
    public static (MinedCoin Pool, MinedCoin Address)? Mismatch(string poolHost, string stratumUser)
    {
        var host = poolHost.Trim().ToLowerInvariant();
        var user = stratumUser.Trim();
        var dot = user.IndexOf('.');
        var pool = FromHost(host) ?? (KnownBtcHosts.Any(host.Contains) ? MinedCoin.Btc : null);
        var address = FromAddress(dot >= 0 ? user[..dot] : user);
        return pool is { } p && address is { } a && p != a ? (p, a) : null;
    }

    /// <summary>Warning text for <see cref="Mismatch"/>, or <c>null</c>.</summary>
    public static string? MismatchWarning(string poolHost, string stratumUser) =>
        Mismatch(poolHost, stratumUser) is { } found
            ? string.Format(Strings.ThisAddressBelongsToThe, found.Address.Symbol(), found.Pool.Symbol())
            : null;
}
