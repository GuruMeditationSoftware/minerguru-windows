using System.Text.Json.Nodes;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class PayoutCheckTests : IDisposable
{
    public PayoutCheckTests() => Lang.English = false;

    private const string Mine = "bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq";
    private const string Other = "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa";
    private const string Cash = "qruvqq2x9vyd9gykre058r4lda4gulpres33nr9aml";

    private readonly string _folder = Directory.CreateTempSubdirectory("mg-payout").FullName;
    private readonly DateTime _start = new(2026, 11, 4, 12, 0, 0);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static MinerSystemInfo Info(string user = Mine + ".a", string? fallback = null, string url = "solo.ckpool.org", string? status = null)
    {
        var o = new JsonObject
        {
            ["stratumURL"] = url, ["stratumPort"] = 3333, ["stratumUser"] = user, ["uptimeSeconds"] = 7200,
            ["fallbackStratumURL"] = "public-pool.io", ["fallbackStratumPort"] = 21496, ["fallbackStratumUser"] = fallback ?? user,
        };
        if (status != null) o["coinbasePayoutStatus"] = status;
        return MinerSystemInfo.Parse(o);
    }

    private static MinerDevice Device(Guid id, string host, MinerSystemInfo info) =>
        new(new PersistedMinerConfig { Id = id, IpAddress = host, CustomName = "Gamma" }, ConnectionState.Online) { LatestInfo = info };

    [Fact]
    public void Verdicts()
    {
        Assert.Equal(PayoutVerdict.FactoryDefault, PayoutCheck.Verdict("replace-this-with-your-btc-address.bitaxe", null));
        Assert.Equal(PayoutVerdict.FactoryDefault, PayoutCheck.Verdict("bc1q7n70rumyv6lvu8avpml0c3uggvssfu52egum3q.nerdqaxe", Mine));
        Assert.Equal(PayoutVerdict.Expected, PayoutCheck.Verdict($"{Mine}.w", Mine));
        Assert.Equal(PayoutVerdict.Foreign, PayoutCheck.Verdict($"{Other}.w", Mine));
        Assert.Equal(PayoutVerdict.Valid, PayoutCheck.Verdict($"{Other}.w", null));
        Assert.Equal(PayoutVerdict.Valid, PayoutCheck.Verdict($"{Other}.w", "bc1qar0"));
        Assert.Equal(PayoutVerdict.Expected, PayoutCheck.Verdict($"{Cash}.w", $"bitcoincash:{Cash}"));
        Assert.Equal(PayoutVerdict.Account, PayoutCheck.Verdict("wicked_miner.1", Mine));
        Assert.Equal(PayoutVerdict.Account, PayoutCheck.Verdict("bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdx.w", null));
        Assert.Equal(PayoutVerdict.Empty, PayoutCheck.Verdict("", Mine));
    }

    [Fact]
    public void WorkerStaysWhenTheAddressChanges()
    {
        Assert.Equal("new.worker", PayoutCheck.User("old.worker", "new"));
        Assert.Equal("new", PayoutCheck.User("old", "new"));
    }

    [Fact]
    public void SlotsAreCompared()
    {
        var slots = PayoutCheck.Slots(Info(fallback: $"{Other}.a"), new Dictionary<string, string> { ["BTC"] = Mine });
        Assert.Equal(["primary", "fallback"], slots.Select(s => s.Key));
        Assert.Equal([PayoutVerdict.Expected, PayoutVerdict.Foreign], slots.Select(s => s.Verdict));
    }

    [Fact]
    public void MissingAddressCountsOnlyOnBitcoinSoloPools()
    {
        Assert.True(PayoutCheck.BlockSkipsMiner(Info(status: "not_found")));
        Assert.False(PayoutCheck.BlockSkipsMiner(Info(url: "pool.braiins.com", status: "not_found")));
        Assert.False(PayoutCheck.BlockSkipsMiner(Info(status: "verified")));
    }

    [Fact]
    public void ChangedAddressAlarmsAndOwnChangeStaysQuiet()
    {
        var log = new ChangeLog();
        var center = new AlertCenter(_folder, log) { IsEnabled = true };
        var id = Guid.NewGuid();
        center.Evaluate([Device(id, "10.0.0.1", Info())], _start);
        var sent = center.Evaluate([Device(id, "10.0.0.1", Info($"{Other}.a", $"{Mine}.a"))], _start.AddMinutes(1));
        Assert.Equal([AlertKind.Payout], sent.Select(a => a.Kind));
        log.RecordWrite("10.0.0.1", "PATCH", "/api/system", null, null, _start.AddMinutes(2));
        Assert.Empty(center.Evaluate([Device(id, "10.0.0.1", Info())], _start.AddMinutes(3)));
    }

    [Fact]
    public void DefaultAddressAlarmsOnceAcrossLaunches()
    {
        var log = new ChangeLog();
        var id = Guid.NewGuid();
        var stock = Device(id, "10.0.0.2", Info("replace-this-with-your-btc-address.bitaxe"));
        var center = new AlertCenter(_folder, log) { IsEnabled = true };
        Assert.Contains(center.Evaluate([stock], _start), a => a.Kind == AlertKind.Payout);
        Assert.Empty(new AlertCenter(_folder, log).Evaluate([stock], _start.AddMinutes(1)));
    }

    [Fact]
    public void ForeignAddressFollowsTheExpectedOne()
    {
        var center = new AlertCenter(_folder, new ChangeLog()) { IsEnabled = true };
        var value = Device(Guid.NewGuid(), "10.0.0.3", Info($"{Other}.a", $"{Mine}.a"));
        Assert.Empty(center.Evaluate([value], _start));
        center.SetExpectedAddress(MinedCoin.Btc, Mine);
        Assert.Equal([AlertKind.Payout], center.Evaluate([value], _start.AddMinutes(1)).Select(a => a.Kind));
    }

    [Fact]
    public void BlockWithoutTheMinerNeedsThreePolls()
    {
        var center = new AlertCenter(_folder, new ChangeLog()) { IsEnabled = true };
        var value = Device(Guid.NewGuid(), "10.0.0.4", Info(status: "not_found"));
        var counts = Enumerable.Range(0, 4).Select(i => center.Evaluate([value], _start.AddMinutes(i)).Count).ToList();
        Assert.Equal([0, 0, 1, 0], counts);
    }

    [Fact]
    public void WatchdogAsksForOneRestart()
    {
        var log = new ChangeLog();
        var center = new AlertCenter(_folder, log) { IsEnabled = true, RestartsToPrimary = true };
        var o = new JsonObject
        {
            ["isUsingFallbackStratum"] = 1, ["uptimeSeconds"] = 7200, ["stratumURL"] = "a.example",
            ["fallbackStratumURL"] = "b.example", ["hashRate_1m"] = 1000, ["hashRate"] = 1000,
        };
        var stuck = Device(Guid.NewGuid(), "10.0.0.5", MinerSystemInfo.Parse(o));
        stuck.PrimaryPoolReachable = true;
        var sent = new List<AlertEvent>();
        foreach (var minute in new[] { 0, 10, 15 }) sent = center.Evaluate([stuck], _start.AddMinutes(minute));
        Assert.Single(center.DueRestarts);
        Assert.Empty(sent); // The restart message says it, not an alarm right before it.
        log.Record(new ChangeEntry(_start.AddMinutes(15), "10.0.0.5", "restart", [], ChangeSource.Watchdog, null));
        center.Evaluate([stuck], _start.AddMinutes(16));
        Assert.Empty(center.DueRestarts);
    }

    [Fact]
    public void ReadOnlySaysOnceAnHourThatARestartWasLeftOut()
    {
        var log = new ChangeLog { IsReadOnly = true };
        var center = new AlertCenter(_folder, log) { IsEnabled = true, RestartsToPrimary = true };
        var o = new JsonObject
        {
            ["isUsingFallbackStratum"] = 1, ["uptimeSeconds"] = 7200, ["stratumURL"] = "a.example",
            ["fallbackStratumURL"] = "b.example", ["hashRate_1m"] = 1000, ["hashRate"] = 1000,
        };
        var stuck = Device(Guid.NewGuid(), "10.0.0.6", MinerSystemInfo.Parse(o));
        stuck.PrimaryPoolReachable = true;
        var skipped = 0;
        foreach (var minute in new[] { 0, 10, 15, 16, 30, 76 })
        {
            var events = center.Evaluate([stuck], _start.AddMinutes(minute));
            Assert.Empty(center.DueRestarts);
            skipped += events.Count(e => e.Title.Contains("nicht neu gestartet") || e.Title.Contains("not restarted"));
        }
        Assert.Equal(2, skipped);
    }
}

public class ChangeLogTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mg-changes").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void ShowsOnlyChangedValues()
    {
        var current = ChangeLog.Flatten("""{"frequency":500,"coreVoltage":1150}""")!;
        Assert.Equal([new ChangeField("frequency", "500", "600")], ChangeLog.Changes("""{"frequency":600,"coreVoltage":1150}""", current));
    }

    [Fact]
    public void ComparesPoolSlotsByID()
    {
        var current = ChangeLog.Flatten("""{"pools":[{"id":2,"stratumUser":"old.a"}]}""")!;
        Assert.Equal([new ChangeField("pools.2.stratumUser", "old.a", "new.a")],
            ChangeLog.Changes("""{"pools":[{"id":2,"stratumUser":"new.a"}]}""", current));
    }

    [Fact]
    public void PasswordsNeverShow()
    {
        Assert.Equal([new ChangeField("stratumPassword", null, "•••")], ChangeLog.Changes("""{"stratumPassword":"secret"}""", new Dictionary<string, string>()));
        Assert.Empty(ChangeLog.Changes("""{"pools":[{"id":0,"stratumPassword":"*****"}]}""", new Dictionary<string, string>()));
    }

    [Fact]
    public void UnchangedCertificateIsNoChange()
    {
        var current = ChangeLog.Flatten("""{"pools":[{"id":0,"stratumCert":"PEM"}]}""")!;
        Assert.Empty(ChangeLog.Changes("""{"pools":[{"id":0,"stratumCert":"PEM"}]}""", current));
        Assert.Equal([new ChangeField("pools.0.stratumCert", null, "•••")],
            ChangeLog.Changes("""{"pools":[{"id":0,"stratumCert":"NEW"}]}""", current));
    }

    [Fact]
    public void StoresEntries()
    {
        var log = new ChangeLog(_folder);
        log.NoteValues("1.2.3.4", """{"frequency":500}""");
        log.RecordWrite("1.2.3.4", "PATCH", "/api/system", """{"frequency":600}""", null);
        log.RecordWrite("1.2.3.4", "POST", "/api/system/restart", null, "HTTP 500");
        log.RecordWrite("1.2.3.4", "POST", "/api/otp/session", null, null);
        var again = new ChangeLog(_folder);
        Assert.Equal(2, again.Entries().Count);
        Assert.Equal("restart", again.Entries()[0].Action);
        Assert.Equal("HTTP 500", again.Entries()[0].Failure);
        Assert.Contains("frequency: 500 -> 600", again.ExportText());
        Assert.Null(again.LastAutomaticRestart("1.2.3.4"));
        again.IsReadOnly = true;
        Assert.True(new ChangeLog(_folder).IsReadOnly);
    }
}
