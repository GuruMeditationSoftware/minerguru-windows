using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Models;

/// <summary>How the payout address of a pool slot looks.</summary>
public enum PayoutVerdict
{
    /// <summary>The address the user set for this coin.</summary>
    Expected,
    /// <summary>A valid address, and no expected address is set for the coin.</summary>
    Valid,
    /// <summary>A valid address that is not the expected one.</summary>
    Foreign,
    /// <summary>The firmware's placeholder or a default that pays someone else.</summary>
    FactoryDefault,
    /// <summary>No valid address: an account name at a pool, or a typing error.</summary>
    Account,
    Empty,
}

/// <summary>One configured pool slot and whom it pays.</summary>
public sealed record PayoutSlot(string Key, string Title, string Host, string User, string Address, MinedCoin Coin, PayoutVerdict Verdict);

/// <summary>Checks the payout addresses of a miner. Only reads: MinerGuru never changes an address by itself.</summary>
public static class PayoutCheck
{
    /// <summary>Shipped defaults of the firmwares: <c>replace-this-with-your-btc-address</c> on AxeOS,
    /// and on NerdOS a real address of somebody else.</summary>
    public static readonly HashSet<string> KnownDefaults = ["bc1q7n70rumyv6lvu8avpml0c3uggvssfu52egum3q"];
    private static readonly string[] Placeholders = ["replace-this", "your-btc-address", "yourbtcaddress", "your_btc_address"];

    public static bool NeedsAttention(this PayoutVerdict verdict) => verdict is PayoutVerdict.Foreign or PayoutVerdict.FactoryDefault;

    public static string Description(this PayoutVerdict verdict) => verdict switch
    {
        PayoutVerdict.Expected => L("Entspricht deiner Soll-Adresse", "Matches your expected address"),
        PayoutVerdict.Valid => L("Gültige Adresse, keine Soll-Adresse festgelegt", "Valid address, no expected address set"),
        PayoutVerdict.Foreign => L("Weicht von deiner Soll-Adresse ab", "Differs from your expected address"),
        PayoutVerdict.FactoryDefault => L("Standard-Adresse der Firmware: Ein Fund ginge nicht an dich", "Firmware default: a block found would not go to you"),
        PayoutVerdict.Account => L("Konto bei einem Pool oder Tippfehler", "Pool account or typing error"),
        _ => L("Nichts eingetragen", "Nothing entered"),
    };

    /// <summary>The part of a Stratum user in front of the first dot.</summary>
    public static string Address(string user) => user.Trim().Split('.')[0];

    public static PayoutVerdict Verdict(string user, string? expected)
    {
        var address = Address(user);
        var lower = address.ToLowerInvariant();
        if (address.Length == 0) return PayoutVerdict.Empty;
        if (KnownDefaults.Contains(lower) || Placeholders.Any(lower.Contains)) return PayoutVerdict.FactoryDefault;
        if (CoinAddress.ScriptPubKey(address) is not { } script)
            return !string.IsNullOrEmpty(expected) && lower == expected.ToLowerInvariant() ? PayoutVerdict.Expected : PayoutVerdict.Account;
        // A half-typed expected address is no target yet.
        if (string.IsNullOrEmpty(expected) || CoinAddress.ScriptPubKey(expected) is not { } target) return PayoutVerdict.Valid;
        return target.SequenceEqual(script) ? PayoutVerdict.Expected : PayoutVerdict.Foreign;
    }

    /// <summary>All slots with something configured: AxeOS 2.15+ lists up to eight, others primary and fallback.</summary>
    public static List<PayoutSlot> Slots(MinerSystemInfo info, IReadOnlyDictionary<string, string> expected)
    {
        PayoutSlot? Slot(string key, string title, string host, string user)
        {
            if (host.Length == 0 && user.Length == 0) return null;
            var coin = MinedCoinDetector.Detect(host, user);
            return new PayoutSlot(key, title, host, user, Address(user), coin, Verdict(user, expected.GetValueOrDefault(coin.Symbol())));
        }
        if (info.AxePools is { Count: > 0 } pools)
            return pools.Select(p => Slot($"pool{p.Id}", $"Pool {p.Id + 1}", p.StratumUrl, p.StratumUser)).OfType<PayoutSlot>().ToList();
        return new[]
        {
            Slot("primary", L("Primär", "Primary"), info.StratumUrl, info.StratumUser),
            Slot("fallback", "Fallback", info.FallbackStratumUrl, info.FallbackStratumUser),
        }.OfType<PayoutSlot>().ToList();
    }

    /// <summary><c>worker</c> of the slot's user stays, the address in front of it changes.</summary>
    public static string User(string user, string address)
    {
        var trimmed = user.Trim();
        var dot = trimmed.IndexOf('.');
        return dot >= 0 ? address + trimmed[dot..] : address;
    }

    /// <summary>Only solo pools pay the block to the miner's own address; shared and donation pools show <c>not_found</c> by design.</summary>
    public static bool IsSoloPool(string host, string user)
    {
        var lower = host.ToLowerInvariant();
        return lower.Contains("solo") || lower.Contains("ckpool") || lower.Contains("public-pool")
            || user.StartsWith("solo:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The firmware says the block it works on does not pay the miner.</summary>
    public static bool BlockSkipsMiner(MinerSystemInfo info) =>
        info.CoinbasePayoutStatus == "not_found" && !info.IsDualPoolMining
        && info.ActivePoolCoin == MinedCoin.Btc && IsSoloPool(info.ActivePoolHost, info.ActivePoolUser);

    /// <summary>Short form for notifications.</summary>
    public static string Shortened(string address) => address.Length > 14 ? $"{address[..6]}…{address[^5..]}" : address;
}
