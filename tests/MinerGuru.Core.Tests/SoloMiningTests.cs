using System.Text.Json.Nodes;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using Xunit;

namespace MinerGuru.Core.Tests;

public class SoloMiningTests
{
    private const double TwoPow32 = 4_294_967_296.0;

    public SoloMiningTests() => Lang.English = false;

    /// <summary>Network difficulty that yields <paramref name="lambda"/> expected blocks per day at 1 TH/s.</summary>
    private static double DifficultyFor(double lambda, double ghs = 1_000) => ghs * 1e9 * 86_400 / (lambda * TwoPow32);

    private static FleetCoinMiningSummary Summary(double ghs, double difficulty, MinedCoin coin = MinedCoin.Btc) =>
        new(coin, ghs, difficulty, "", true);

    [Fact]
    public void TimeframeDurations()
    {
        Assert.Equal(3_600, SoloChanceTimeframe.PerHour.DurationSeconds(MinedCoin.Btc));
        Assert.Equal(600, SoloChanceTimeframe.PerBlock.DurationSeconds(MinedCoin.Btc));
        Assert.Equal(75, SoloChanceTimeframe.PerBlock.DurationSeconds(MinedCoin.Dgb));
        Assert.Equal(144, SoloChanceTimeframe.PerDay.BlockCount(MinedCoin.Btc));
        Assert.Equal(2_880, SoloChanceTimeframe.PerDay.BlockCount(MinedCoin.Fb));
    }

    [Fact]
    public void ProbabilityIsPoissonAtLeastOneBlock()
    {
        var s = Summary(1_000, DifficultyFor(2));
        Assert.Equal(1 - Math.Exp(-2), s.Probability(SoloChanceTimeframe.PerDay), 9);
        Assert.True(s.Probability(SoloChanceTimeframe.PerWeek) > s.Probability(SoloChanceTimeframe.PerDay));
        Assert.True(s.Probability(SoloChanceTimeframe.PerYear) <= 1);
    }

    [Fact]
    public void ZeroHashrateOrDifficulty()
    {
        foreach (var s in new[] { Summary(0, 1e12), Summary(100, 0) })
        {
            Assert.Equal(0, s.Probability(SoloChanceTimeframe.PerDay));
            Assert.Equal("0 %", s.FormattedProbabilityPercent(SoloChanceTimeframe.PerDay));
            Assert.Equal("—", s.FormattedOddsRatio(SoloChanceTimeframe.PerDay));
            Assert.Equal("—", s.ExpectedTimePerBlockFormatted);
        }
    }

    [Fact]
    public void FormattedProbabilityAndOdds()
    {
        var s = Summary(1_000, DifficultyFor(0.001));
        Assert.Equal("0,100 %", s.FormattedProbabilityPercent(SoloChanceTimeframe.PerDay));
        Assert.Equal("1 : 1.001", s.FormattedOddsRatio(SoloChanceTimeframe.PerDay));
        Assert.Equal("1 : 156,2 Mio.", Summary(1_000, DifficultyFor(1.0 / 156_200_000)).FormattedOddsRatio(SoloChanceTimeframe.PerDay));
        Assert.Equal("1 : 2,50 Mrd.", Summary(1_000, DifficultyFor(1.0 / 2_500_000_000)).FormattedOddsRatio(SoloChanceTimeframe.PerDay));
    }

    [Theory]
    [InlineData(2.0, "~12,0 Std.")]
    [InlineData(0.1, "~10,0 Tage")]
    [InlineData(1.0 / 730.5, "~2,0 Jahre")]
    [InlineData(1.0 / (365.25 * 19_598), "~19.598 J.")]
    public void ExpectedTimePerBlock(double lambdaPerDay, string expected) =>
        Assert.Equal(expected, Summary(1_000, DifficultyFor(lambdaPerDay)).ExpectedTimePerBlockFormatted);

    [Fact]
    public void DualPoolMinersAreSplitByCoinAndBitcoinComesFirst()
    {
        // NerdQAxe: BTC + BCH in dual pool mode; Bitaxe: BCH only. BTC leads although it has less hashrate.
        var nerd = Device("nerdqaxe-plusplus_nerdos-1.1.0", "nerdqaxe");
        var bitaxe = Device("bitaxe-gamma-601_axeos-2.15.3", "bitaxe");
        var summaries = FleetCoinMiningSummary.Summaries([nerd, bitaxe]);
        Assert.Equal([MinedCoin.Btc, MinedCoin.Bch], summaries.Select(s => s.Coin));

        var info = nerd.LatestInfo!;
        var (btc, bch) = (summaries[0], summaries[1]);
        Assert.Equal(info.HashRate * info.DualPoolPrimaryPercent / 100.0, btc.TotalHashrateGhs, 6);
        Assert.Equal($"nerdqaxe ({info.DualPoolPrimaryPercent}%)", btc.ContributingMinersText);
        Assert.Equal(info.HashRate * info.DualPoolSecondaryPercent / 100.0 + bitaxe.LatestInfo!.HashRate, bch.TotalHashrateGhs, 6);
        Assert.Equal($"nerdqaxe ({info.DualPoolSecondaryPercent}%) + bitaxe", bch.ContributingMinersText);
        Assert.True(btc.TotalHashrateGhs < bch.TotalHashrateGhs);
    }

    [Fact]
    public void MinersWithoutTelemetryAreIgnored() =>
        Assert.Empty(FleetCoinMiningSummary.Summaries([new MinerDevice(new PersistedMinerConfig { IpAddress = "x" })]));

    private static MinerDevice Device(string fixture, string name) =>
        new(new PersistedMinerConfig { IpAddress = name, CustomName = name })
        {
            LatestInfo = MinerSystemInfo.Parse((JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture + ".json")))!),
        };
}
