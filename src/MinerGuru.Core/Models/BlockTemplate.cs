using MinerGuru.Core.Services;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Models;

/// <summary>One output of the coinbase transaction the miner is hashing on (<c>coinbaseOutputs[]</c>, AxeOS 2.13+).</summary>
/// <param name="ValueSats">Value in satoshis.</param>
/// <param name="Address">Address decoded by the firmware, <c>OP_RETURN: text</c> for data outputs, <c>UNKNOWN:hex</c> otherwise.</param>
/// <param name="IsUserOutput">The firmware matched the scriptPubKey against the address in the Stratum user.</param>
public sealed record CoinbaseOutput(double ValueSats, string Address, bool IsUserOutput = false)
{
    /// <summary>Data output without value, e.g. the SegWit witness commitment or a merge-mining tag.</summary>
    public bool IsDataOutput => Address.StartsWith("OP_RETURN", StringComparison.Ordinal);

    /// <summary>Payload of a data output as the firmware prints it (non-printable bytes come as <c>.</c>).</summary>
    private string DataPayload => Address["OP_RETURN".Length..].TrimStart(':', ' ');

    /// <summary>SegWit witness commitment: payload starts with <c>aa21a9ed</c>, printed as <c>.!..</c>.</summary>
    public bool IsWitnessCommitment => IsDataOutput && DataPayload.StartsWith(".!..", StringComparison.Ordinal);

    /// <summary>Readable text of a data output (pool or merge-mining tag), <c>null</c> for binary data.</summary>
    public string? DataText
    {
        get
        {
            if (!IsDataOutput || IsWitnessCommitment) return null;
            var payload = DataPayload;
            var readable = payload.Count(c => c != '.');
            return payload.Length >= 3 && readable >= payload.Length * 0.6 ? payload : null;
        }
    }
}

public enum PayoutState
{
    /// <summary>The coinbase pays the address from the Stratum user.</summary>
    Verified,
    /// <summary>Address in the Stratum user, but no output pays it.</summary>
    NotFound,
    /// <summary>No address in the Stratum user (account pool), nothing to check.</summary>
    NotApplicable,
    /// <summary>Coinbase decoding is switched off for the active pool.</summary>
    DecodingOff,
    Unknown,
}

/// <summary>
/// What the miner knows about the block it is hashing on right now: the pool's template as seen through the
/// coinbase (height, pool tag, payout). The transaction list itself never reaches the miner.
/// </summary>
public sealed class BlockTemplateSummary
{
    /// <summary>Recipients shown one by one; shared coinbases (Ocean) list the rest as a sum.</summary>
    public const int MaxListedRecipients = 6;

    public required int Height { get; init; }
    public string? PoolTag { get; init; }
    public IReadOnlyList<string> Signals { get; init; } = [];
    public required MinedCoin Coin { get; init; }
    /// <summary>Coinbase decoding of the active pool slot; <c>null</c> if the firmware does not say.</summary>
    public bool? IsDecoding { get; init; }
    public IReadOnlyList<CoinbaseOutput> Outputs { get; init; } = [];
    public int OthersCount { get; init; }
    public double OthersValueSats { get; init; }
    /// <summary>Sum of all outputs (subsidy + fees); <c>null</c> if missing or implausible.</summary>
    public double? TotalSats { get; init; }
    public double? UserSats { get; init; }
    public required PayoutState PayoutState { get; init; }
    /// <summary>Transactions in the template, from the merkle branch count; only known from a Stratum probe.</summary>
    public (int Lower, int Upper)? TransactionRange { get; init; }
    /// <summary>Set when MinerGuru read the template from the pool itself instead of the miner reporting it.</summary>
    public DateTime? ProbedAt { get; init; }

    /// <summary><c>null</c> if the firmware reports no block data (NerdOS, AxeOS &lt; 2.13, Stratum V2 standard jobs).</summary>
    public static BlockTemplateSummary? From(MinerSystemInfo info)
    {
        if (info.BlockHeight is not > 0) return null;
        var coin = info.ActivePoolCoin;
        var tag = info.BlockScriptSig?.Trim() ?? "";
        var isDecoding = info.ActivePoolDecodesCoinbase;
        IReadOnlyList<CoinbaseOutput> outputs = isDecoding == false ? [] : info.CoinbaseOutputs ?? [];

        double? total = info.CoinbaseValueTotalSats is > 0 and var value && value <= MaxPlausibleSats(coin) ? value : null;
        var user = info.CoinbaseValueUserSats ?? outputs.Where(o => o.IsUserOutput).Sum(o => o.ValueSats);
        double? userSats = isDecoding == false || total == null ? null : Math.Min(user, total.Value);

        return new BlockTemplateSummary
        {
            Height = info.BlockHeight.Value,
            Coin = coin,
            Signals = info.BlockSignals ?? [],
            PoolTag = tag.Length == 0 || tag.StartsWith('[') ? null : tag,
            IsDecoding = isDecoding,
            Outputs = outputs,
            OthersCount = info.CoinbaseOthersCount ?? 0,
            OthersValueSats = info.CoinbaseOthersValueSats ?? 0,
            TotalSats = total,
            UserSats = userSats,
            PayoutState = PayoutStateFor(info.CoinbasePayoutStatus, isDecoding, outputs, userSats,
                info.IsUsingFallbackStratum ? info.FallbackStratumUser : info.StratumUser),
        };
    }

    /// <summary>
    /// Template read by the Stratum probe. <paramref name="stratumUser"/> is the miner's own user, so the check
    /// answers whether the pool pays the miner's address, not the probe's worker name.
    /// </summary>
    public static BlockTemplateSummary? From(StratumJob job, MinedCoin coin, string stratumUser, DateTime probedAt)
    {
        if (job.Coinbase is not { Height: > 0 } coinbase) return null;
        var wallet = stratumUser.Split('.', 2)[0];
        var userScript = CoinAddress.ScriptPubKey(wallet);

        var all = coinbase.Outputs.Select(o => new CoinbaseOutput(o.ValueSats, Label(o.Script, coin),
            userScript != null && o.Script.SequenceEqual(userScript))).ToList();
        var indices = Enumerable.Range(0, all.Count).ToList();
        var listedPayers = indices.Where(i => !all[i].IsDataOutput && !all[i].IsUserOutput)
            .OrderByDescending(i => all[i].ValueSats).Take(MaxListedRecipients).ToHashSet();
        var listed = indices.Where(i => all[i].IsDataOutput || all[i].IsUserOutput || listedPayers.Contains(i)).ToList();
        var others = indices.Except(listed).ToList();

        var total = (double)coinbase.TotalSats;
        var state = all.Any(o => o.IsUserOutput) ? PayoutState.Verified
            : userScript != null ? PayoutState.NotFound
            : LooksLikeAddress(wallet) ? PayoutState.Unknown : PayoutState.NotApplicable;
        // From the pool: a real block has about 13 branches; more than 30 would overflow the shift below.
        var branches = Math.Clamp(job.MerkleBranchCount, 0, 30);

        return new BlockTemplateSummary
        {
            Height = coinbase.Height.Value,
            PoolTag = coinbase.PoolTags.Count == 0 ? null : string.Join(" · ", coinbase.PoolTags),
            Coin = coin,
            IsDecoding = true,
            Outputs = listed.Select(i => all[i]).ToList(),
            OthersCount = others.Count,
            OthersValueSats = others.Sum(i => all[i].ValueSats),
            TotalSats = total > 0 ? total : null,
            UserSats = all.Where(o => o.IsUserOutput).Sum(o => o.ValueSats),
            PayoutState = state,
            TransactionRange = branches == 0 ? (1, 1) : ((1 << (branches - 1)) + 1, 1 << branches),
            ProbedAt = probedAt,
        };
    }

    /// <summary>New coins the block creates, from height and coin; <c>null</c> for coins with another schedule.</summary>
    public double? SubsidySats => SubsidySatsFor(Height, Coin);

    /// <summary>Fees of the transactions in the template: total minus subsidy.</summary>
    public double? FeesSats => TotalSats is { } total && SubsidySats is { } subsidy && total >= subsidy ? total - subsidy : null;

    /// <summary>Share of the block reward that goes to the own address (0…1).</summary>
    public double? UserShare => UserSats is { } user && TotalSats is > 0 and var total ? user / total : null;

    // Rules

    /// <summary>Bitcoin-style halving schedule: 50 coins, halved every 210,000 blocks.</summary>
    public static double? SubsidySatsFor(int height, MinedCoin coin)
    {
        if (coin is not (MinedCoin.Btc or MinedCoin.Bch or MinedCoin.Bsv or MinedCoin.Xec)) return null;
        var halvings = height / 210_000;
        return halvings < 64 ? 5_000_000_000L >> halvings : 0;
    }

    /// <summary>Whole supply as upper bound: the firmware reports leftovers when it never decoded a job.</summary>
    private static double MaxPlausibleSats(MinedCoin coin) => coin == MinedCoin.Dgb ? 21e9 * 1e8 : 21e6 * 1e8;

    public static PayoutState PayoutStateFor(string? reported, bool? isDecoding, IReadOnlyList<CoinbaseOutput> outputs, double? userSats, string stratumUser)
    {
        switch (reported?.ToLowerInvariant())
        {
            case "verified": return PayoutState.Verified;
            case "not_found": return PayoutState.NotFound;
            case "not_applicable": return PayoutState.NotApplicable;
        }
        // AxeOS < 2.16 has no status: derive it the way the firmware does.
        if (isDecoding == false) return PayoutState.DecodingOff;
        if (outputs.Count == 0) return PayoutState.Unknown;
        if (outputs.Any(o => o.IsUserOutput) || userSats > 0) return PayoutState.Verified;
        return LooksLikeAddress(stratumUser.Split('.', 2)[0]) ? PayoutState.NotFound : PayoutState.NotApplicable;
    }

    /// <summary>Rough format check for the wallet part of a Stratum user; account names fail it.</summary>
    public static bool LooksLikeAddress(string raw)
    {
        var value = raw.Trim();
        var lower = value.ToLowerInvariant();
        string[] bech32Prefixes = ["bc1", "tb1", "bcrt1", "dgb1", "bitcoincash:", "ecash:", "bchtest:"];
        if (bech32Prefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal))) return value.Length >= 26;
        const string base58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        if (value.Length is >= 25 and <= 35 && value.All(base58.Contains)) return true;
        // CashAddr without prefix
        return value.Length == 42 && lower[0] is 'q' or 'p';
    }

    /// <summary>Text for one output in the firmware's notation, so <see cref="CoinbaseOutput"/> reads both alike.</summary>
    private static string Label(byte[] script, MinedCoin coin)
    {
        if (script.Length > 0 && script[0] == 0x6a)
        {
            var payload = script[1..];
            var data = payload.Length == 0 ? payload : payload[0] switch
            {
                >= 0x01 and <= 0x4b => payload[1..],
                0x4c => payload[Math.Min(2, payload.Length)..],
                0x4d => payload[Math.Min(3, payload.Length)..],
                _ => payload,
            };
            return "OP_RETURN: " + new string(data.Select(b => b is >= 0x20 and <= 0x7e ? (char)b : '.').ToArray());
        }
        return CoinAddress.Address(script, coin) ?? "UNKNOWN:" + Convert.ToHexStringLower(script);
    }

    // Formatting

    /// <summary>Amount in whole coins with all decimals, e.g. <c>3,12841234 BTC</c>.</summary>
    public static string FormatCoins(double sats, MinedCoin coin)
    {
        var decimals = coin == MinedCoin.Xec ? 2 : 8;
        var divisor = coin == MinedCoin.Xec ? 100.0 : 100_000_000.0;
        return $"{(sats / divisor).ToString($"N{decimals}", Culture)} {coin.Symbol()}";
    }
}
