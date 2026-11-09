using System.Net;
using System.Text.Json.Nodes;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class PowerTariffTests : IDisposable
{
    public PowerTariffTests() => Lang.English = false;

    private readonly string _folder = Directory.CreateTempSubdirectory("mg-power").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>2026-11-02 is a Monday.</summary>
    private static DateTime Date(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0);

    private readonly TariffZone _night = new() { Name = "Nacht", PricePerKwh = 0.24, Window = new(WeekdayOrder.All, 22 * 60, 6 * 60) };
    private readonly TariffZone _day = new() { Name = "Tag", PricePerKwh = 0.36, Window = new(WeekdayOrder.Workdays, 6 * 60, 22 * 60) };

    private PowerTariff Tariff => new() { CurrencyCode = "EUR", BasePricePerKwh = 0.30, Zones = [_night, _day] };

    [Fact]
    public void WindowOverMidnightBelongsToTheStartDay()
    {
        var friNight = new WeeklyWindow([DayOfWeek.Friday], 22 * 60, 6 * 60);
        Assert.True(friNight.Contains(DayOfWeek.Friday, 23 * 60));
        Assert.True(friNight.Contains(DayOfWeek.Saturday, 2 * 60));   // Saturday early morning
        Assert.False(friNight.Contains(DayOfWeek.Friday, 2 * 60));    // Friday early morning belongs to Thursday
        Assert.False(friNight.Contains(DayOfWeek.Saturday, 23 * 60));
    }

    [Fact]
    public void SameStartAndEndCoversTheWholeDay()
    {
        var weekend = new WeeklyWindow(WeekdayOrder.Weekend, 0, 0);
        Assert.True(weekend.Contains(DayOfWeek.Sunday, 0));
        Assert.True(weekend.Contains(DayOfWeek.Saturday, 23 * 60 + 59));
        Assert.False(weekend.Contains(DayOfWeek.Monday, 12 * 60));
    }

    [Fact]
    public void PriceFollowsZonesAndFallsBackToBasePrice()
    {
        Assert.Equal(0.36, Tariff.Price(Date(2, 12)));  // Monday noon: day
        Assert.Equal(0.24, Tariff.Price(Date(3, 3)));   // Tuesday 3 am: night
        Assert.Equal(0.30, Tariff.Price(Date(7, 12)));  // Saturday noon: no zone
        Assert.True(Tariff.HasGaps);
        Assert.Empty(Tariff.OverlappingZones);
    }

    [Fact]
    public void FirstZoneWinsOnOverlap()
    {
        var peak = new TariffZone { Name = "Spitze", PricePerKwh = 0.50, Window = new(WeekdayOrder.Workdays, 17 * 60, 20 * 60) };
        var withPeak = new PowerTariff { BasePricePerKwh = 0.30, Zones = [peak, _night, _day] };
        Assert.Equal(0.50, withPeak.Price(Date(2, 18)));
        Assert.Single(withPeak.OverlappingZones);
    }

    [Fact]
    public void SegmentsSplitTheDayAtZoneBorders()
    {
        var monday = Tariff.Segments(DayOfWeek.Monday);
        Assert.Equal([_night.Id, _day.Id, _night.Id], monday.Select(s => s.ZoneId));
        Assert.Equal(6 * 60, monday[0].EndMinute);
        Assert.Equal(22 * 60, monday[^1].StartMinute);
        Assert.Equal([_night.Id, null, _night.Id], Tariff.Segments(DayOfWeek.Saturday).Select(s => s.ZoneId));
    }

    [Fact]
    public void RulesFollowTheirZoneAndFirstRuleWins()
    {
        Guid minerA = Guid.NewGuid(), minerB = Guid.NewGuid();
        var rules = new[]
        {
            new ScheduleRule { Trigger = ScheduleTrigger.During(_night.Id), Action = PowerAction.Pause, MinerIds = [minerA] },
            new ScheduleRule { Trigger = ScheduleTrigger.During(_night.Id), Action = PowerAction.NightMode },
        };
        var at3Am = Date(3, 3);
        Assert.Equal(PowerAction.Pause, PowerSchedule.Action(minerA, at3Am, rules, Tariff));
        Assert.Equal(PowerAction.NightMode, PowerSchedule.Action(minerB, at3Am, rules, Tariff));
        Assert.Equal(PowerAction.Normal, PowerSchedule.Action(minerA, Date(3, 12), rules, Tariff));

        // The zone moves, the rule moves with it.
        var later = Tariff;
        later.Zones[0].Window.StartMinute = 1 * 60;
        later.Zones[1].Window.EndMinute = 1 * 60;
        Assert.Equal(PowerAction.Normal, PowerSchedule.Action(minerB, Date(2, 23), rules, later));
    }

    [Fact]
    public void CostUsesThePriceOfEveryQuarterHourAndSkipsPauses()
    {
        var miner = Guid.NewGuid();
        // 100 W for a whole Monday: 6 h night + 16 h day + 2 h night.
        var monday = Date(2, 0);
        var cost = PowerSchedule.Cost(100, monday, 24, Tariff);
        Assert.Equal(0.8 * 0.24 + 1.6 * 0.36, cost, 4);

        var pauseAtNight = new[] { new ScheduleRule { Trigger = ScheduleTrigger.During(_night.Id), Action = PowerAction.Pause } };
        Assert.Equal(1.6 * 0.36, PowerSchedule.Cost(100, monday, 24, Tariff, pauseAtNight, miner), 4);

        // Firmware without pause keeps drawing power.
        Assert.Equal(cost, PowerSchedule.Cost(100, monday, 24, Tariff, pauseAtNight, miner, canPause: false), 4);
    }

    [Theory]
    [InlineData("0,30", 0.30)]
    [InlineData("0.2875", 0.2875)]
    [InlineData(" 1 ", 1.0)]
    public void PricesAreReadWithCommaOrPoint(string text, double expected) => Assert.Equal(expected, PowerTariff.ParsePrice(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    public void NoPriceIsNull(string text) => Assert.Null(PowerTariff.ParsePrice(text));

    [Fact]
    public void AmountsUseTheCurrencyAndUiLanguage()
    {
        Assert.Equal("0,300 €", Tariff.FormatPrice(0.3, 3).Replace('\u00A0', ' '));
        Assert.Equal("0,30 CHF", new PowerTariff { CurrencyCode = "CHF" }.FormatPrice(0.3).Replace('\u00A0', ' '));
        Assert.Equal("EUR", PowerTariff.ParseCurrency("eu1r"));
        Assert.Null(PowerTariff.ParseCurrency("eu"));
    }

    [Fact]
    public void WeekdaySummaries()
    {
        Assert.Equal("täglich", WeekdayOrder.Summary(WeekdayOrder.All));
        Assert.Equal("Mo–Fr", WeekdayOrder.Summary(WeekdayOrder.Workdays));
        Assert.Equal("Sa, So", WeekdayOrder.Summary(WeekdayOrder.Weekend));
        Assert.Equal("22:00–06:00", _night.Window.TimeRangeText);
    }

    // Store

    private DateTime _now = Date(2, 22);

    private PowerStore Store(MinerApiClient? api = null, params MinerDevice[] miners) =>
        new(_folder, api ?? new MinerApiClient(new Recorder()), () => miners, () => _now);

    [Fact]
    public void TariffRulesAndStatesArePersisted()
    {
        var store = Store();
        store.CurrencyCode = "CHF";
        store.SaveZone(_night);
        store.SaveRule(new ScheduleRule { Trigger = ScheduleTrigger.During(_night.Id), Action = PowerAction.NightMode, IsEnabled = false });
        store.SaveRule(new ScheduleRule { Trigger = ScheduleTrigger.At(new(WeekdayOrder.Weekend, 0, 0)), Action = PowerAction.Pause });
        store.RunsSchedules = false;
        store.NightFanPercent = 55;

        var reloaded = Store();
        Assert.Equal("CHF", reloaded.CurrencyCode);
        Assert.Equal(_night.Id, reloaded.Tariff.Zones.Single().Id);
        Assert.Equal(WeekdayOrder.All.Count, reloaded.Tariff.Zones[0].Window.Weekdays.Count);
        Assert.Equal(2, reloaded.Rules.Count);
        Assert.False(reloaded.Rules[0].IsEnabled);
        Assert.Equal(PowerAction.Pause, reloaded.Rules[1].Action);
        Assert.False(reloaded.RunsSchedules);
        Assert.Equal(55, reloaded.NightFanPercent);
    }

    [Fact]
    public void RemovingAZoneRemovesItsRules()
    {
        var store = Store();
        store.AddZones(_night, _day);
        store.SaveRule(new ScheduleRule { Trigger = ScheduleTrigger.During(_night.Id), Action = PowerAction.NightMode });
        store.SaveRule(new ScheduleRule { Trigger = ScheduleTrigger.During(_day.Id), Action = PowerAction.Pause });
        store.RemoveZone(_night.Id);
        Assert.Equal(_day.Id, store.Tariff.Zones.Single().Id);
        Assert.Equal(_day.Id, store.Rules.Single().Trigger.ZoneId);
    }

    [Fact]
    public void MovingUpChangesWhoWins()
    {
        var store = Store();
        store.AddZones(_night, _day);
        store.MoveZoneUp(1);
        Assert.Equal(_day.Id, store.Tariff.Zones[0].Id);
        store.MoveZoneUp(0);
        Assert.Equal(_day.Id, store.Tariff.Zones[0].Id);
    }

    // Switching: what really goes to the miners

    private sealed class Recorder : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public bool Fails { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Fails) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            var body = request.Method == HttpMethod.Patch ? " " + await request.Content!.ReadAsStringAsync(ct) : "";
            Calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}{body}");
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static MinerDevice Miner(string firmware, bool slots, bool autoFan = true)
    {
        var json = new JsonObject
        {
            ["power"] = 15.0, ["frequency"] = 600, ["coreVoltage"] = 1250, ["autofanspeed"] = autoFan ? 1 : 0, ["fanspeed"] = 60, ["version"] = firmware,
        };
        if (slots) json["pools"] = new JsonArray();
        return new(new PersistedMinerConfig { IpAddress = "192.168.1.28" }, ConnectionState.Online) { LatestInfo = MinerSystemInfo.Parse(json) };
    }

    private static ScheduleRule Always(PowerAction action) => new() { Trigger = ScheduleTrigger.At(new(WeekdayOrder.All, 0, 0)), Action = action };

    [Fact]
    public async Task NightModeSavesTheSettingsAndComesBackAfterTheInterval()
    {
        var http = new Recorder();
        var miner = Miner("v2.13.2", slots: false);
        var store = Store(new MinerApiClient(http), miner);
        var rule = Always(PowerAction.NightMode);
        store.SaveRule(rule);
        await store.Evaluation;

        Assert.Equal(PowerAction.NightMode, store.State(miner.Id));
        Assert.Equal(2, http.Calls.Count);
        Assert.Contains("\"autofanspeed\":0,\"fanspeed\":40", http.Calls[0]);
        Assert.Equal("POST /api/system/restart", http.Calls[1]);

        // Too soon: the miner just rebooted.
        http.Calls.Clear();
        _now = Date(2, 22, 10);
        store.RemoveRule(rule.Id);
        await store.Evaluation;
        Assert.Empty(http.Calls);
        Assert.Equal(PowerAction.NightMode, store.State(miner.Id));

        _now = Date(2, 22, 16);
        await store.EvaluateAsync();
        Assert.Equal(PowerAction.Normal, store.State(miner.Id));
        Assert.Equal("PATCH /api/system {\"frequency\":600,\"coreVoltage\":1250,\"autofanspeed\":1}", http.Calls[0]);
        Assert.Equal("POST /api/system/restart", http.Calls[1]);
    }

    [Fact]
    public async Task PauseUsesTheFirmwareAndIsLeftByResuming()
    {
        var http = new Recorder();
        var miner = Miner("v2.15.4", slots: true);
        var store = Store(new MinerApiClient(http), miner);
        var rule = Always(PowerAction.Pause);
        store.SaveRule(rule);
        await store.Evaluation;
        Assert.Equal(["POST /api/system/pause"], http.Calls);

        http.Calls.Clear();
        _now = Date(2, 23);
        store.RemoveRule(rule.Id);
        await store.Evaluation;
        Assert.Equal(["POST /api/system/resume"], http.Calls);
    }

    [Fact]
    public async Task FirmwareWithoutPauseGoesToNightMode()
    {
        var http = new Recorder();
        var miner = Miner("v2.13.2", slots: false);
        var store = Store(new MinerApiClient(http), miner);
        store.SaveRule(Always(PowerAction.Pause));
        await store.Evaluation;
        Assert.Equal(PowerAction.NightMode, store.State(miner.Id));
        Assert.DoesNotContain("POST /api/system/pause", http.Calls);
    }

    [Fact]
    public async Task ThisDeviceCanLeaveTheSwitchingToAnother()
    {
        var http = new Recorder();
        var miner = Miner("v2.15.4", slots: true);
        var store = Store(new MinerApiClient(http), miner);
        store.RunsSchedules = false;
        store.SaveRule(Always(PowerAction.Pause));
        await store.EvaluateAsync();
        Assert.Empty(http.Calls);
    }

    [Fact]
    public async Task AFailedSwitchKeepsTheStateAndReportsIt()
    {
        var http = new Recorder { Fails = true };
        var miner = Miner("v2.15.4", slots: true);
        var store = Store(new MinerApiClient(http), miner);
        store.SaveRule(Always(PowerAction.Pause));
        await store.Evaluation;
        Assert.Equal(PowerAction.Normal, store.State(miner.Id));
        Assert.NotNull(store.LastError);

        http.Fails = false;
        await store.EvaluateAsync();
        Assert.Equal(PowerAction.Pause, store.State(miner.Id));
        Assert.Null(store.LastError);
    }

    [Fact]
    public async Task PauseAfterNightModeNeedsNoRestart()
    {
        var http = new Recorder();
        var miner = Miner("v2.15.4", slots: true);
        var store = Store(new MinerApiClient(http), miner);
        var rule = Always(PowerAction.NightMode);
        store.SaveRule(rule);
        await store.Evaluation;
        Assert.Equal(PowerAction.NightMode, store.State(miner.Id));

        // A restart here would end the pause right away.
        http.Calls.Clear();
        _now = Date(2, 22, 20);
        rule.Action = PowerAction.Pause;
        store.SaveRule(rule);
        await store.Evaluation;
        Assert.Equal(["POST /api/system/pause"], http.Calls);
        Assert.Equal(PowerAction.Pause, store.State(miner.Id));

        // Back to normal: resume, then the values from before the night with their restart.
        http.Calls.Clear();
        _now = Date(2, 22, 40);
        store.RemoveRule(rule.Id);
        await store.Evaluation;
        Assert.Equal("POST /api/system/resume", http.Calls[0]);
        Assert.Equal("PATCH /api/system {\"frequency\":600,\"coreVoltage\":1250,\"autofanspeed\":1}", http.Calls[1]);
        Assert.Equal("POST /api/system/restart", http.Calls[2]);
        Assert.Equal(PowerAction.Normal, store.State(miner.Id));
    }

    [Fact]
    public void ADeletedMinerNeverWidensARule()
    {
        Guid kept = Guid.NewGuid(), deleted = Guid.NewGuid();
        var store = Store();
        var alone = Always(PowerAction.Pause);
        alone.MinerIds = [deleted];
        var shared = Always(PowerAction.NightMode);
        shared.MinerIds = [kept, deleted];
        store.SaveRule(alone);
        store.SaveRule(shared);
        store.Forget(deleted);
        Assert.Equal(new[] { deleted }, store.Rules[0].MinerIds);
        Assert.Equal(new[] { kept }, store.Rules[1].MinerIds);
        Assert.False(store.Rules[0].AppliesTo(kept));
    }

    [Fact]
    public void ControlRangesKeepTheMinersOwnValues()
    {
        var info = MinerSystemInfo.Parse(new JsonObject { ["ASICModel"] = "BM1370", ["frequency"] = 900, ["coreVoltage"] = 980 });
        var miner = new MinerDevice(new PersistedMinerConfig { IpAddress = "192.168.1.29" }, ConnectionState.Online) { LatestInfo = info };
        Assert.Equal((400, 900), miner.FrequencyRange);
        Assert.Equal((980, 1250), miner.CoreVoltageRange);
    }

    [Fact]
    public async Task ManualFanIsRestoredWithItsSpeed()
    {
        var http = new Recorder();
        var miner = Miner("v2.13.2", slots: false, autoFan: false);
        var store = Store(new MinerApiClient(http), miner);
        var rule = Always(PowerAction.NightMode);
        store.SaveRule(rule);
        await store.Evaluation;
        http.Calls.Clear();
        _now = Date(2, 23);
        store.RemoveRule(rule.Id);
        await store.Evaluation;
        Assert.Contains("\"autofanspeed\":0,\"fanspeed\":60", http.Calls[0]);
    }
}
