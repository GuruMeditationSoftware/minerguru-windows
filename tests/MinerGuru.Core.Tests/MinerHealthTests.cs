using System.Text.Json.Nodes;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class MinerHealthTests : IDisposable
{
    public MinerHealthTests() => Lang.English = false;

    private readonly string _folder = Directory.CreateTempSubdirectory("mg-health").FullName;
    private readonly DateTime _start = new(2026, 11, 4, 12, 0, 0);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static MinerSystemInfo Info(
        double temp = 55, double vr = 60, double? limit = null, bool flag = false,
        int shares = 100, double rate = 1000, int uptime = 7200, bool paused = false)
    {
        var o = new JsonObject
        {
            ["temp"] = temp, ["vrTemp"] = vr, ["overheat_mode"] = flag ? 1 : 0, ["sharesAccepted"] = shares,
            ["hashRate_1m"] = rate, ["hashRate"] = rate, ["uptimeSeconds"] = uptime, ["miningPaused"] = paused,
            ["stratumSuggestedDifficulty"] = 800,
        };
        if (limit is { } l) o["overheat_temp"] = l;
        return MinerSystemInfo.Parse(o);
    }

    private void Run(MinerHealth health, int from, int to, Func<int, MinerSystemInfo> make)
    {
        for (var minute = from; minute <= to; minute++) health.Update(make(minute), _start.AddMinutes(minute));
    }

    [Fact]
    public void AxeOSFlagAlarmsAtOnce()
    {
        var health = new MinerHealth();
        health.Update(Info(flag: true), _start);
        Assert.True(health.IsOverheating);
    }

    [Fact]
    public void NerdOSNeedsThreePollsNearTheLimit()
    {
        var health = new MinerHealth();
        for (var i = 0; i < 2; i++) health.Update(Info(temp: 72, limit: 75), _start);
        Assert.False(health.IsOverheating);
        health.Update(Info(temp: 72, limit: 75), _start);
        Assert.True(health.IsOverheating);
    }

    [Fact]
    public void SingleSpikeStaysQuiet()
    {
        var health = new MinerHealth();
        foreach (var temp in new double[] { 72, 71, 60, 74, 70, 60 }) health.Update(Info(temp: temp, limit: 75), _start);
        Assert.False(health.IsOverheating);
    }

    [Fact]
    public void VoltageRegulatorCountsOnlyAboveTheLimit()
    {
        var health = new MinerHealth();
        for (var i = 0; i < 3; i++) health.Update(Info(vr: 75, limit: 75), _start);
        Assert.False(health.IsOverheating);
        for (var i = 0; i < 3; i++) health.Update(Info(vr: 76, limit: 75), _start);
        Assert.True(health.IsOverheating);
    }

    [Fact]
    public void CoolingDownNeedsThreePolls()
    {
        var health = new MinerHealth();
        for (var i = 0; i < 3; i++) health.Update(Info(temp: 73, limit: 75), _start);
        for (var i = 0; i < 2; i++) health.Update(Info(temp: 60, limit: 75), _start);
        Assert.True(health.IsOverheating);
        health.Update(Info(temp: 60, limit: 75), _start);
        Assert.False(health.IsOverheating);
    }

    [Fact]
    public void FlatlineWhenSharesAndHashrateStand()
    {
        var health = new MinerHealth();
        Run(health, 0, 9, _ => Info());
        Assert.False(health.IsFlatlining);
        Run(health, 10, 10, _ => Info());
        Assert.True(health.IsFlatlining);
        Assert.Equal(10, health.MinutesWithoutShares(_start.AddMinutes(10)));
    }

    [Fact]
    public void ANewShareEndsTheFlatline()
    {
        var health = new MinerHealth();
        Run(health, 0, 11, _ => Info());
        Run(health, 12, 12, _ => Info(shares: 101));
        Assert.False(health.IsFlatlining);
    }

    [Fact]
    public void MovingHashrateIsNoFlatline()
    {
        var health = new MinerHealth();
        Run(health, 0, 30, m => Info(rate: 1000 + m));
        Assert.False(health.IsFlatlining);
    }

    [Fact]
    public void PausedRestartingOrIdleMinersAreLeftAlone()
    {
        MinerHealth paused = new(), restarted = new(), idle = new();
        Run(paused, 0, 30, _ => Info(paused: true));
        Run(restarted, 0, 4, m => Info(uptime: 60 + m * 60));
        Run(idle, 0, 30, _ => Info(rate: 0));
        Assert.False(paused.IsFlatlining);
        Assert.False(restarted.IsFlatlining);
        Assert.False(idle.IsFlatlining);
    }

    [Fact]
    public void PoolDifficultyBeatsTheSuggestedOne()
    {
        Assert.Equal(65536, MinerSystemInfo.Parse(new JsonObject { ["stratumSuggestedDifficulty"] = 1000, ["poolDifficulty"] = 65536 }).ActivePoolDifficulty);
        Assert.Equal(1000, MinerSystemInfo.Parse(new JsonObject { ["stratumSuggestedDifficulty"] = 1000 }).ActivePoolDifficulty);
    }

    [Fact]
    public void HighDifficultyWaitsForTenExpectedShares()
    {
        Assert.Equal(42_950, MinerHealth.FlatlineWindow(1000, 1_000_000).TotalSeconds, 0);
        Assert.Equal(TimeSpan.FromMinutes(10), MinerHealth.FlatlineWindow(1000, 800));
        Assert.Equal(TimeSpan.FromMinutes(10), MinerHealth.FlatlineWindow(1000, null));
    }

    private AlertCenter Center() => new(_folder) { IsEnabled = true };

    private static MinerDevice Miner(MinerSystemInfo info) =>
        new(new PersistedMinerConfig { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), IpAddress = "192.168.1.50", CustomName = "Gamma" }, ConnectionState.Online) { LatestInfo = info };

    [Fact]
    public void OverheatAlarmsOnceAndGivesTheAllClear()
    {
        var center = Center();
        var sent = new List<AlertEvent>();
        var flags = new[] { true, true, false, false, false };
        for (var i = 0; i < flags.Length; i++) sent.AddRange(center.Evaluate([Miner(Info(flag: flags[i]))], _start.AddMinutes(i)));
        Assert.Equal([AlertKind.Overheat, AlertKind.Overheat], sent.Select(a => a.Kind));
        Assert.Equal([false, true], sent.Select(a => a.IsRecovery));
        Assert.Equal("Gamma ist überhitzt", sent[0].Title);
    }

    [Fact]
    public void OverheatCanBeSwitchedOff()
    {
        var center = Center();
        center.SetKind(AlertKind.Overheat, false);
        Assert.Empty(center.Evaluate([Miner(Info(flag: true))], _start));
    }

    [Fact]
    public void FlatlineAlarmsAfterTenMinutesAndNewSharesClearIt()
    {
        var center = Center();
        var sent = new List<AlertEvent>();
        for (var m = 0; m <= 11; m++) sent.AddRange(center.Evaluate([Miner(Info())], _start.AddMinutes(m)));
        Assert.Equal([AlertKind.Flatline], sent.Select(a => a.Kind));
        var after = center.Evaluate([Miner(Info(shares: 101))], _start.AddMinutes(12));
        Assert.Equal([true], after.Select(a => a.IsRecovery));
    }

    [Fact]
    public void SeveralMinersAreCombinedByKind()
    {
        var alerts = new[] { new AlertEvent(AlertKind.Overheat, Guid.NewGuid(), "a", "x", false, "A"), new AlertEvent(AlertKind.Overheat, Guid.NewGuid(), "b", "y", false, "B") };
        var combined = Assert.Single(AlertCenter.Combine(alerts));
        Assert.Equal("2 Miner sind überhitzt", combined.Title);
    }
}

public class FallbackWatchTests
{
    public FallbackWatchTests() => Lang.English = false;

    private readonly DateTime _start = new(2026, 11, 4, 12, 0, 0);
    private DateTime At(int minutes) => _start.AddMinutes(minutes);

    private static MinerSystemInfo Info(bool fallback = true, int uptime = 7200, Action<JsonObject>? extra = null)
    {
        var o = new JsonObject
        {
            ["isUsingFallbackStratum"] = fallback ? 1 : 0, ["uptimeSeconds"] = uptime,
            ["stratumURL"] = "a.example", ["fallbackStratumURL"] = "b.example", ["hashRate_1m"] = 1000, ["hashRate"] = 1000,
        };
        extra?.Invoke(o);
        return MinerSystemInfo.Parse(o);
    }

    [Fact]
    public void FallbackAlarmsAfterTenMinutesInARow()
    {
        var h = new MinerHealth();
        h.Update(Info(), At(0));
        h.Update(Info(), At(9));
        Assert.False(h.IsOnFallback);
        h.Update(Info(), At(10));
        Assert.True(h.IsOnFallback);
        h.Update(Info(fallback: false), At(11));
        Assert.False(h.IsOnFallback);
    }

    [Fact]
    public void StartupIsForgiven()
    {
        var h = new MinerHealth();
        h.Update(Info(uptime: 60), At(0));
        h.Update(Info(uptime: 310), At(5));
        h.Update(Info(uptime: 700), At(12));
        Assert.False(h.IsOnFallback);
        h.Update(Info(uptime: 910), At(15));
        Assert.True(h.IsOnFallback);
    }

    [Fact]
    public void StuckNeedsThePrimaryPoolReachableForFifteenMinutes()
    {
        var h = new MinerHealth();
        h.Update(Info(), At(0), primaryReachable: false);
        h.Update(Info(), At(10), primaryReachable: true);
        h.Update(Info(), At(24), primaryReachable: true);
        Assert.True(h.IsOnFallback && !h.IsStuckOnFallback);
        h.Update(Info(), At(25), primaryReachable: true);
        Assert.True(h.IsStuckOnFallback);
    }

    [Fact]
    public void DroppingUptimeIsAnUnexpectedRestart()
    {
        var h = new MinerHealth();
        h.Update(Info(fallback: false, uptime: 5000), At(0));
        h.Update(Info(fallback: false, uptime: 20), At(1));
        Assert.True(h.RestartedUnexpectedly);
        h.Update(Info(fallback: false, uptime: 80), At(2));
        Assert.False(h.RestartedUnexpectedly);
        h.Update(Info(fallback: false, uptime: 10), At(3), restartExpected: true);
        Assert.False(h.RestartedUnexpectedly);
    }

    [Fact]
    public void AnswersOutOfOrderAreNoRestart()
    {
        var h = new MinerHealth();
        h.Update(Info(fallback: false, uptime: 5000), At(0));
        h.Update(Info(fallback: false, uptime: 4999), At(0));
        Assert.False(h.RestartedUnexpectedly);
    }

    [Fact]
    public void ResetReasonComesFromEitherFirmware()
    {
        Assert.Equal("Brownout", Info(extra: o => o["resetReason"] = "Brownout").ResetReason);
        Assert.Equal("Watchdog", Info(extra: o => o["lastResetReason"] = "Watchdog").ResetReason);
    }

    [Fact]
    public void WatchdogLimits()
    {
        var h = new MinerHealth();
        foreach (var minute in new[] { 0, 10, 15 }) h.Update(Info(), At(minute), primaryReachable: true);
        Assert.Null(h.DueAutoRestart(false, false, null, At(15)));
        Assert.Equal(MinerHealth.AutoRestartReason.StuckOnFallback, h.DueAutoRestart(false, true, null, At(15)));
        Assert.Null(h.DueAutoRestart(false, true, At(-30), At(15)));
        h.Update(Info(extra: o => o["overheat_mode"] = 1), At(16), primaryReachable: true);
        Assert.Null(h.DueAutoRestart(true, true, null, At(16)));
    }
}
