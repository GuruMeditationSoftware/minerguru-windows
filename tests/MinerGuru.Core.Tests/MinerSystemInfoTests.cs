using System.Text.Json.Nodes;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class MinerSystemInfoTests
{
    public MinerSystemInfoTests() => Lang.English = false;

    private static MinerSystemInfo Fixture(string name) =>
        MinerSystemInfo.Parse((JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")))!);

    [Fact]
    public void AxeOs215GammaIsRecognisedWithPoolSlots()
    {
        var info = Fixture("bitaxe-gamma-601_axeos-2.15.3");
        Assert.Equal(MinerFirmware.AxeOs, info.Firmware());
        Assert.Equal("Bitaxe Gamma 601", info.DetectedHardwareTitle());
        Assert.Equal(1, info.AsicCount); // from hashrateMonitor.asics
        Assert.NotNull(info.AxePools);
        Assert.Equal(1038.9, info.HashRate, 3);
        Assert.Equal(0, info.AutoFanModeRaw);
        Assert.False(info.AutoFanSpeed);
    }

    [Fact]
    public void MillivoltsAndMilliampsAreNormalized()
    {
        var info = Fixture("bitaxe-gamma-601_axeos-2.15.3");
        Assert.Equal(5.1328, info.VoltageVolts, 3);
        Assert.Equal(11.8906, info.CurrentAmps, 3);
    }

    [Fact]
    public void NerdOsDualPoolReportsBothCoins()
    {
        var info = Fixture("nerdqaxe-plusplus_nerdos-1.1.0");
        Assert.Equal(MinerFirmware.NerdOs, info.Firmware());
        Assert.Equal("NerdQAxe++", info.DetectedHardwareTitle());
        Assert.Equal(4, info.AsicCount);
        Assert.True(info.IsDualPoolMining);
        Assert.Equal(MinedCoin.Btc, info.PrimaryPoolCoin);
        Assert.Equal(MinedCoin.Bch, info.SecondaryPoolCoin);
        Assert.Equal("BTC+BCH", info.CoinShortText);
        Assert.Equal(6.5, info.CurrentAmps, 3);
    }

    [Fact]
    public void PoolTableFiguresPerPool()
    {
        var nerd = Fixture("nerdqaxe-plusplus_nerdos-1.1.0");
        Assert.Equal(26281248, nerd.PrimaryPoolSessionBestDiff);
        Assert.Equal(885703588, nerd.SecondaryPoolSessionBestDiff);
        // Same worker on both pools: the full user of pool 1
        Assert.Equal("bc1qexamplewalletaddressxxxxxxxxxxxxxxxxxx.nerdqaxe", nerd.ActivePoolUser);

        var single = MinerSystemInfo.Parse(new JsonObject
        {
            ["stratumUser"] = "bc1qwallet.rig1", ["fallbackStratumUser"] = "qqcash.rig2",
            ["isUsingFallbackStratum"] = 1, ["bestSessionDiff"] = 5000,
        });
        Assert.Equal("qqcash.rig2", single.ActivePoolUser);
        Assert.Null(single.PrimaryPoolSessionBestDiff);
        Assert.Equal(5000, single.SecondaryPoolSessionBestDiff);
        Assert.Equal("rig1", MinerSystemInfo.Worker("bc1qwallet.rig1"));
        Assert.Null(MinerSystemInfo.Worker("bc1qwallet."));
    }

    [Fact]
    public void OldAxeOsHasNoPoolSlotsAndCannotPause()
    {
        var info = Fixture("bitaxe-max-102_axeos-2.5.0");
        Assert.Equal("Bitaxe Max 102", info.DetectedHardwareTitle());
        Assert.Null(info.AxePools);
        var miner = new MinerDevice(new PersistedMinerConfig { IpAddress = "x" }) { LatestInfo = info };
        Assert.False(miner.CanPause);
        Assert.True(new MinerDevice(new PersistedMinerConfig { IpAddress = "y" }) { LatestInfo = Fixture("bitaxe-gamma-601_axeos-2.15.3") }.CanPause);
    }

    [Fact]
    public void DifficultyAcceptsNumbersAndStrings()
    {
        var info = MinerSystemInfo.Parse(new JsonObject { ["bestDiff"] = "4.40G", ["bestSessionDiff"] = 93790386 });
        Assert.Equal(4.40e9, info.BestDiffNumeric, 0);
        Assert.Equal("4.40G", info.BestDiffFormatted);
        Assert.Equal("93.79M", info.BestSessionDiffFormatted);
    }

    [Fact]
    public void LenientNumbersFromStrings()
    {
        var info = MinerSystemInfo.Parse(new JsonObject { ["hashRate"] = "512.5", ["frequency"] = "600", ["autofanspeed"] = true, ["isUsingFallbackStratum"] = "1" });
        Assert.Equal(512.5, info.HashRate);
        Assert.Equal(600, info.FrequencyMhz);
        Assert.True(info.AutoFanSpeed);
        Assert.True(info.IsUsingFallbackStratum);
    }

    [Fact]
    public void PresetsUseTheChipsClockSteps()
    {
        var info = Fixture("bitaxe-gamma-601_axeos-2.15.3");
        var miner = new MinerDevice(new PersistedMinerConfig { IpAddress = "x" }) { LatestInfo = info };
        var presets = miner.RecommendedPresets.Select(p => (p.Name, p.FrequencyMhz, p.CoreVoltageMv)).ToList();
        Assert.Equal([("Eco", 490, 1100), ("Standard", 525, 1150), ("Boost", 550, 1200)], presets);
    }
}

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0 GH/s")]
    [InlineData(512.4, "512 GH/s")]
    [InlineData(4749.1, "4.75 TH/s")]
    public void Hashrate(double ghs, string expected) => Assert.Equal(expected, MinerFormatting.FormatHashrate(ghs));

    [Theory]
    [InlineData("stratum+tcp://eusolo.ckpool.org:3333/x", false, "eusolo.ckpool.org")]
    [InlineData("stratum+tcp://eusolo.ckpool.org:3333/x", true, "eusolo.ckpool.org:3333")]
    [InlineData(" public-pool.io ", false, "public-pool.io")]
    public void StratumHostClean(string raw, bool keepPort, string expected) => Assert.Equal(expected, StratumHost.Clean(raw, keepPort));

    [Fact]
    public void IpSortKeyOrdersNumerically()
    {
        var ips = new[] { "192.168.1.10", "127.0.0.1:48010", "192.168.1.9", "127.0.0.1:48002", "miner.local" };
        var sorted = ips.OrderBy(MinerStore.IpSortKey, StringComparer.Ordinal).ToArray();
        Assert.Equal(["127.0.0.1:48002", "127.0.0.1:48010", "192.168.1.9", "192.168.1.10", "miner.local"], sorted);
    }

    [Fact]
    public void CoinDetection()
    {
        Assert.Equal(MinedCoin.Bch, MinedCoinDetector.Detect("bch.solopool.eu", "x"));
        Assert.Equal(MinedCoin.Btc, MinedCoinDetector.Detect("eusolo.ckpool.org", "x"));
        Assert.Equal(MinedCoin.Dgb, MinedCoinDetector.Detect("pool.example", "dgb1qxyz"));
        Assert.Equal(MinedCoin.Btc, MinedCoinDetector.Detect("pool.example", "x", 120e12));
        Assert.Equal(MinedCoin.Unknown, MinedCoinDetector.Detect("", ""));
    }
}

public class MinerStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mg-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void MinersAndSettingsSurviveARestart()
    {
        var store = new MinerStore(_dir);
        store.AddMiner("http://127.0.0.1:1/", "Test");
        store.AddMiner("127.0.0.1:1", "Umbenannt"); // same address: renames instead of adding
        store.PollingIntervalSeconds = 9;

        var reloaded = new MinerStore(_dir);
        var miner = Assert.Single(reloaded.Miners);
        Assert.Equal("127.0.0.1:1", miner.IpAddress);
        Assert.Equal("Umbenannt", miner.DisplayName);
        Assert.Equal(9, reloaded.PollingIntervalSeconds);
        Assert.True(reloaded.IsAlreadyAdded("HTTP://127.0.0.1:1"));
    }
}
