
namespace MinerGuru.Core.Models;

/// <summary>Timeframe of the solo-mining odds on the fleet page. Stored by name.</summary>
public enum SoloChanceTimeframe { PerBlock, PerHour, PerDay, PerWeek, PerMonth, PerYear }

public static class SoloChanceTimeframeExtensions
{
    public static string Title(this SoloChanceTimeframe timeframe) => timeframe switch
    {
        SoloChanceTimeframe.PerBlock => Strings.PerBlock,
        SoloChanceTimeframe.PerHour => Strings.PerHour,
        SoloChanceTimeframe.PerDay => Strings.PerDay,
        SoloChanceTimeframe.PerWeek => Strings.PerWeek,
        SoloChanceTimeframe.PerMonth => Strings.PerMonth,
        _ => Strings.PerYear,
    };

    /// <summary>Length in seconds; "per block" is the coin's target block time.</summary>
    public static double DurationSeconds(this SoloChanceTimeframe timeframe, MinedCoin coin) => timeframe switch
    {
        SoloChanceTimeframe.PerBlock => coin.TargetBlockTimeSeconds(),
        SoloChanceTimeframe.PerHour => 3_600,
        SoloChanceTimeframe.PerDay => 86_400,
        SoloChanceTimeframe.PerWeek => 86_400 * 7,
        SoloChanceTimeframe.PerMonth => 86_400 * 30.4375,
        _ => 86_400 * 365.25,
    };

    /// <summary>Network blocks mined in this timeframe.</summary>
    public static double BlockCount(this SoloChanceTimeframe timeframe, MinedCoin coin) =>
        timeframe.DurationSeconds(coin) / Math.Max(1, coin.TargetBlockTimeSeconds());
}

public static class MinedCoinChain
{
    /// <summary>Target block interval in seconds.</summary>
    public static double TargetBlockTimeSeconds(this MinedCoin coin) => coin switch
    {
        MinedCoin.Dgb => 75, // DigiByte's SHA-256 algorithm
        MinedCoin.Fb => 30,
        _ => 600,
    };

    /// <summary>Current block subsidy, without fees.</summary>
    public static double BlockSubsidyCoins(this MinedCoin coin) => coin switch
    {
        MinedCoin.Dgb => 275,
        MinedCoin.Xec => 1_812_500,
        MinedCoin.Fb => 25,
        MinedCoin.Ppc => 35,
        _ => 3.125,
    };

    /// <summary>Used only until a Stratum pool reports the network difficulty.</summary>
    public static double FallbackNetworkDifficulty(this MinedCoin coin) => coin switch
    {
        MinedCoin.Bch => 750_830_101_440,
        MinedCoin.Bsv => 85_000_000_000,
        MinedCoin.Dgb => 120_000_000,
        MinedCoin.Xec => 250_000_000_000,
        MinedCoin.Fb => 5_000_000_000_000,
        MinedCoin.Ppc or MinedCoin.Nmc => 10_000_000_000_000,
        _ => 144_080_230_085_156,
    };
}

/// <summary>Fleet hashrate, network difficulty and solo-mining odds for one coin, like the Mac's FleetCoinMiningSummary.</summary>
public sealed record FleetCoinMiningSummary(
    MinedCoin Coin,
    double TotalHashrateGhs,
    double NetworkDifficulty,
    string ContributingMinersText,
    bool IsLiveDifficulty)
{
    /// <summary>Expected blocks (Poisson λ) in the timeframe: λ = H·T / (D·2³²).</summary>
    public double ExpectedBlocksLambda(SoloChanceTimeframe timeframe)
    {
        if (TotalHashrateGhs <= 0 || NetworkDifficulty <= 0) return 0;
        return TotalHashrateGhs * 1e9 * timeframe.DurationSeconds(Coin) / (NetworkDifficulty * 4_294_967_296.0);
    }

    /// <summary>Probability of at least one block: P = 1 − e^−λ.</summary>
    public double Probability(SoloChanceTimeframe timeframe)
    {
        var lambda = ExpectedBlocksLambda(timeframe);
        if (lambda <= 0) return 0;
        return lambda < 1e-7 ? lambda : 1 - Math.Exp(-lambda);
    }

    /// <summary>E.g. <c>0,016 %</c>, <c>5,65 %</c> or <c>0,0000180 %</c>.</summary>
    public string FormattedProbabilityPercent(SoloChanceTimeframe timeframe)
    {
        var pct = Probability(timeframe) * 100;
        if (pct <= 0) return DisplayFormat.Percent(0, 0);
        var digits = pct switch { >= 10 => 1, >= 1 => 2, >= 0.01 => 3, >= 0.0001 => 5, _ => 7 };
        return DisplayFormat.Percent(pct, digits);
    }

    /// <summary>E.g. <c>1 : 6.283</c>, <c>1 : 156,2 Mio.</c> or <c>1 : 2,50 Mrd.</c>.</summary>
    public string FormattedOddsRatio(SoloChanceTimeframe timeframe)
    {
        var p = Probability(timeframe);
        if (p <= 0) return "—";
        var oneIn = Math.Max(1, Math.Round(1 / p));
        if (oneIn >= 1e9) return $"1 : {DisplayFormat.Decimal(oneIn / 1e9, 2)} {Strings.BillionShort}";
        if (oneIn >= 1e7) return $"1 : {DisplayFormat.Decimal(oneIn / 1e6, 1)} {Strings.MillionShort}";
        return $"1 : {DisplayFormat.Decimal(oneIn, 0)}";
    }

    /// <summary>Average time to a block at the current hashrate, e.g. <c>~12,0 Std.</c> or <c>~19.598 J.</c>.</summary>
    public string ExpectedTimePerBlockFormatted
    {
        get
        {
            var perDay = ExpectedBlocksLambda(SoloChanceTimeframe.PerDay);
            if (perDay <= 0) return "—";
            var days = 1 / perDay;
            if (days < 1) return $"~{DisplayFormat.Decimal(days * 24, 1)} {Strings.HoursShort}";
            if (days < 60) return $"~{DisplayFormat.Decimal(days, 1)} {Strings.DaysInText}";
            var years = days / 365.25;
            if (years >= 1_000) return $"~{DisplayFormat.Decimal(Math.Round(years), 0)} {Strings.YearsShort}";
            return $"~{DisplayFormat.Decimal(years, 1)} {Strings.Years}";
        }
    }

    /// <summary>
    /// One summary per coin across the online miners. Dual-pool miners split their hashrate by the configured
    /// percentages. Bitcoin comes first, the other coins by descending hashrate.
    /// </summary>
    public static List<FleetCoinMiningSummary> Summaries(IEnumerable<MinerDevice> onlineMiners)
    {
        var buckets = new Dictionary<MinedCoin, (double Hashrate, double? Difficulty, List<string> Miners)>();

        void Add(MinedCoin coin, double hashrate, double? difficulty, string label)
        {
            var bucket = buckets.TryGetValue(coin, out var b) ? b : (0, null, new List<string>());
            bucket.Hashrate += hashrate;
            if (difficulty is > 0) bucket.Difficulty = difficulty;
            bucket.Miners.Add(label);
            buckets[coin] = bucket;
        }

        foreach (var miner in onlineMiners)
        {
            if (miner.LatestInfo is not { } info) continue;
            if (info.IsDualPoolMining)
            {
                int p1 = info.DualPoolPrimaryPercent, p2 = info.DualPoolSecondaryPercent;
                Add(info.PrimaryPoolCoin, info.HashRate * p1 / 100.0, info.PrimaryPoolStatus?.NetworkDifficulty, $"{miner.DisplayName} ({p1}%)");
                Add(info.SecondaryPoolCoin, info.HashRate * p2 / 100.0, info.SecondaryPoolStatus?.NetworkDifficulty, $"{miner.DisplayName} ({p2}%)");
            }
            else
            {
                var status = info.IsUsingFallbackStratum ? info.SecondaryPoolStatus : info.PrimaryPoolStatus;
                Add(info.ActivePoolCoin, info.HashRate, status?.NetworkDifficulty ?? info.NetworkDifficulty, miner.DisplayName);
            }
        }

        return buckets
            .Select(pair => new FleetCoinMiningSummary(
                pair.Key,
                pair.Value.Hashrate,
                pair.Value.Difficulty ?? pair.Key.FallbackNetworkDifficulty(),
                string.Join(" + ", pair.Value.Miners),
                pair.Value.Difficulty != null))
            .OrderBy(s => s.Coin == MinedCoin.Btc ? 0 : 1)
            .ThenByDescending(s => s.TotalHashrateGhs)
            .ToList();
    }
}
