using System.Text;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class LongTermFeatureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("minerguru-tests").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, true);
    }

    private static readonly Guid Miner = Guid.NewGuid();

    private static HistoryReading Reading(DateTime time, double hashRate = 1000, int accepted = 0) =>
        new(Miner, time, true, hashRate, 60, 50, 20, 70, 30, null, accepted, 0, 5);

    [Fact]
    public void DonationAddressesAreConfigured()
    {
        Assert.All(DonationCoin.All, coin => Assert.True(coin.IsConfigured));
        Assert.StartsWith("bitcoin:", DonationCoin.Bitcoin.PaymentUri);
    }

    [Fact]
    public void AggregatorAveragesAMinuteAndCountsShareDeltas()
    {
        var aggregator = new HistoryAggregator();
        var start = new DateTime(2026, 11, 4, 12, 0, 5, DateTimeKind.Local);
        Assert.Null(aggregator.Add(Reading(start, 1000, 100)));
        Assert.Null(aggregator.Add(Reading(start.AddSeconds(20), 2000, 110)));
        var row = aggregator.Add(Reading(start.AddSeconds(65), 3000, 5));
        Assert.NotNull(row);
        Assert.Equal(1500, row.HashRateGhs);
        Assert.Equal(10, row.SharesAccepted);
        Assert.Equal(1, row.OnlineMinutes);
        var running = aggregator.Flush(start.AddSeconds(66));
        // The counter restarted, so everything it reports is new.
        Assert.Equal(5, Assert.Single(running).SharesAccepted);
    }

    [Fact]
    public void FlushDropsFinishedMinutes()
    {
        var aggregator = new HistoryAggregator();
        var start = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Local);
        aggregator.Add(Reading(start));
        Assert.Single(aggregator.Flush(start.AddSeconds(10)));
        Assert.Single(aggregator.Flush(start.AddMinutes(2)));
        Assert.Empty(aggregator.Flush(start.AddMinutes(2)));
    }

    [Fact]
    public void StepGrowsWithTheSpan()
    {
        Assert.Equal(120, HistoryQuery.Step(TimeSpan.FromDays(1)));
        Assert.Equal(3600, HistoryQuery.Step(TimeSpan.FromDays(7)));
        Assert.True(TimeSpan.FromDays(365).TotalSeconds / HistoryQuery.Step(TimeSpan.FromDays(365)) <= HistoryQuery.MaxPoints);
    }

    [Fact]
    public void HistoryIsStoredAndRolledUp()
    {
        var now = DateTime.Now;
        var store = new HistoryStore(_dir, () => now);
        var start = now.AddHours(-2);
        for (var i = 0; i < 90; i++) store.Record([Reading(start.AddMinutes(i), 1000 + i)], start.AddMinutes(i));
        var minutes = store.Points(Miner, start.AddMinutes(-1), now);
        Assert.Equal(90, minutes.Sum(p => p.RecordedMinutes));
        Assert.All(minutes, p => Assert.Equal(p.RecordedMinutes, p.OnlineMinutes));
        var hours = store.HourlyPoints(start.AddHours(-1), now);
        Assert.Equal(90, hours.Sum(p => p.RecordedMinutes));
        Assert.True(hours.Count is 2 or 3);
        store.Forget(Miner);
        Assert.Empty(store.HourlyPoints(start.AddHours(-1), now));
    }

    [Fact]
    public void HistorySurvivesARestart()
    {
        var now = DateTime.Now;
        var store = new HistoryStore(_dir, () => now);
        store.Record([Reading(now.AddMinutes(-3))], now.AddMinutes(-3));
        store.Record([Reading(now.AddMinutes(-2))], now.AddMinutes(-2));
        var reopened = new HistoryStore(_dir, () => now);
        Assert.Single(reopened.Points(Miner, now.AddHours(-1), now));
    }

    [Fact]
    public void CsvUsesDotsAndQuotes()
    {
        var text = HistoryCsv.Text([new HistoryPoint(Miner, new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc), 1, 1, 1234.5)], _ => "A \"B\"");
        Assert.StartsWith("time,miner,", text);
        Assert.Contains("2026-11-04T12:00:00Z,\"A \"\"B\"\"\",1,1,1234.500,", text);
    }

    [Fact]
    public void PoolRuleFiresOncePerOccurrenceWithinCatchUp()
    {
        var created = new DateTime(2026, 11, 2, 12, 0, 0); // a Monday
        var rule = new PoolScheduleRule { Weekdays = [DayOfWeek.Monday], Minute = 22 * 60, CreatedAt = created };
        var moment = new DateTime(2026, 11, 2, 22, 0, 0);
        Assert.Null(rule.DueOccurrence(moment.AddMinutes(-1), null));
        Assert.Equal(moment, rule.DueOccurrence(moment.AddMinutes(10), null));
        Assert.Null(rule.DueOccurrence(moment.AddMinutes(10), moment));
        Assert.Null(rule.DueOccurrence(moment.AddMinutes(31), null));
        rule.CreatedAt = moment.AddMinutes(5);
        Assert.Null(rule.DueOccurrence(moment.AddMinutes(10), null));
        rule.CreatedAt = created;
        rule.IsEnabled = false;
        Assert.Null(rule.DueOccurrence(moment.AddMinutes(10), null));
    }

    private static MinerDevice OnlineMiner(string poolHost)
    {
        var miner = new MinerDevice(new PersistedMinerConfig { Id = Miner, IpAddress = "10.0.0.2" }, ConnectionState.Online);
        miner.LatestInfo = new MinerSystemInfo { StratumUrl = poolHost, StratumPort = 3333, StratumUser = "old.worker" };
        return miner;
    }

    [Fact]
    public async Task PoolSwitchRunsOnceAndSkipsTheCurrentPool()
    {
        var pool = new PoolConfig { Name = "Lotto", Host = "solo.example", Port = 3333, User = "new.worker" };
        var sent = new List<PoolSettingsForm>();
        var store = new PoolScheduleStore(_dir, _ => pool, (form, _) => { sent.Add(form); return Task.CompletedTask; });
        var now = new DateTime(2026, 11, 2, 22, 5, 0);
        store.Save(new PoolScheduleRule { PoolId = pool.Id, CreatedAt = now.AddDays(-1) });

        store.Evaluate([OnlineMiner("solo.example")], now, false);
        Assert.Empty(sent);
        store.Evaluate([OnlineMiner("other.example")], now, true);
        await store.Evaluation;
        Assert.Equal("solo.example", Assert.Single(sent).Primary.Host);
        store.Evaluate([OnlineMiner("other.example")], now.AddMinutes(1), true);
        await store.Evaluation;
        Assert.Single(sent);

        // The occurrence is remembered across restarts.
        var reopened = new PoolScheduleStore(_dir, _ => pool, (form, _) => { sent.Add(form); return Task.CompletedTask; });
        reopened.Evaluate([OnlineMiner("other.example")], now.AddMinutes(2), true);
        Assert.Single(sent);
    }

    /// <summary>An empty miner set means all miners, so deleting a rule's only miner must not empty it.</summary>
    [Fact]
    public void ADeletedMinerNeverWidensAPoolScheduleOrTask()
    {
        Guid kept = Guid.NewGuid(), deleted = Guid.NewGuid();
        var pools = new PoolScheduleStore(_dir, _ => null, (_, _) => Task.CompletedTask);
        var alone = new PoolScheduleRule { PoolId = Guid.NewGuid(), MinerIds = [deleted] };
        var shared = new PoolScheduleRule { PoolId = Guid.NewGuid(), MinerIds = [kept, deleted] };
        pools.Save(alone);
        pools.Save(shared);
        pools.Forget(deleted);
        Assert.Equal(new[] { deleted }, pools.Rules.Single(r => r.PoolId == alone.PoolId).MinerIds);
        Assert.Equal(new[] { kept }, pools.Rules.Single(r => r.PoolId == shared.PoolId).MinerIds);

        var tasks = new MaintenanceStore(_dir);
        tasks.Save(new MaintenanceTask { Kind = MaintenanceKind.Dust, IntervalDays = 90, NextDue = DateTime.Today, MinerIds = [deleted] });
        tasks.Save(new MaintenanceTask { Kind = MaintenanceKind.Firmware, IntervalDays = 30, NextDue = DateTime.Today, MinerIds = [kept, deleted] });
        tasks.Forget(deleted);
        Assert.Equal(new[] { deleted }, tasks.Tasks.Single(t => t.Kind == MaintenanceKind.Dust).MinerIds);
        Assert.Equal(new[] { kept }, tasks.Tasks.Single(t => t.Kind == MaintenanceKind.Firmware).MinerIds);
    }

    [Fact]
    public void MaintenanceMovesOnAndRemindsDaily()
    {
        var store = new MaintenanceStore(_dir);
        var now = new DateTime(2026, 11, 4, 9, 0, 0);
        var task = new MaintenanceTask { Kind = MaintenanceKind.FanCleaning, IntervalDays = 90, NextDue = now.AddDays(-1) };
        var once = new MaintenanceTask { Kind = MaintenanceKind.Custom, CustomTitle = "Einmal", NextDue = now.AddDays(3) };
        store.Save(task);
        store.Save(once);
        Assert.Equal(MaintenanceStatus.Overdue, task.Status(now));
        Assert.Equal(MaintenanceStatus.DueSoon, once.Status(now));
        Assert.Equal(2, store.DueCount(now));

        var alert = Assert.Single(store.DueEvents(now));
        Assert.Equal(AlertKind.Maintenance, alert.Kind);
        Assert.Empty(store.DueEvents(now.AddHours(5)));
        Assert.Single(store.DueEvents(now.AddHours(25)));

        store.MarkDone(task, now, "ok");
        Assert.Equal(now.AddDays(90), task.NextDue);
        store.MarkDone(once, now, "");
        Assert.DoesNotContain(store.Tasks, t => t.Id == once.Id);
        Assert.Equal("Einmal", store.Records[0].Title);

        var reopened = new MaintenanceStore(_dir);
        Assert.Single(reopened.Tasks);
        Assert.Equal(2, reopened.Records.Count);
    }

    [Fact]
    public void ReportBuildsFiguresFromHourlyPoints()
    {
        var day = new DateTime(2026, 11, 3);
        var points = new[]
        {
            new HistoryPoint(Miner, day.AddHours(1), 60, 60, 1000, 60, 65, null, 20, SharesAccepted: 10, SharesRejected: 1, BestDiff: 5),
            new HistoryPoint(Miner, day.AddHours(2), 30, 60, 2000, 70, 75, null, 20, SharesAccepted: 5),
        };
        var gone = Guid.NewGuid();
        var all = points.Append(new HistoryPoint(gone, day.AddHours(1), 60, 60, 500, 40, 45, null, 10)).ToList();
        var record = new MaintenanceRecord { Title = "Done", DoneAt = day.AddHours(3) };
        var old = new MaintenanceRecord { Title = "Old", DoneAt = day.AddDays(-3) };
        var report = MinerReport.Build(day, day.AddDays(1), all, id => id == Miner ? "Alpha" : null, _ => 0.5, [record, old]);

        var row = report.Rows.First(r => r.Id == Miner);
        Assert.Equal(75, row.UptimePercent);
        Assert.Equal((1000.0 * 60 + 2000 * 30) / 90, row.AverageHashRateGhs!.Value, 6);
        Assert.Equal(0.03, row.EnergyKwh, 6);
        Assert.Equal(0.015, row.Cost, 6);
        Assert.Equal(75, row.MaxTempC);
        Assert.Equal(15, row.SharesAccepted);
        Assert.Contains(report.Rows, r => r.Name == gone.ToString()[..8]);
        Assert.Equal("Done", Assert.Single(report.Maintenance).Title);
        Assert.StartsWith("miner,uptime_percent,", report.CsvText());
        Assert.Contains("\"Alpha\",75.00,", report.CsvText());
    }

    [Fact]
    public void ReportEfficiencyIsHashrateWeighted()
    {
        var a = new MinerReportRow(Guid.NewGuid(), "a", 100, 1000, 0, 0, 20, null, null, 0, 0, null);
        var b = new MinerReportRow(Guid.NewGuid(), "b", 100, 3000, 0, 0, 10, null, null, 0, 0, null);
        Assert.Equal(12.5, new MinerReport(DateTime.Now, DateTime.Now, [a, b], [], []).EfficiencyJPerTh);
    }

    [Fact]
    public void ReportPdfIsAValidDocument()
    {
        var day = new DateTime(2026, 11, 3);
        var rows = Enumerable.Range(0, 80).Select(i => new MinerReportRow(Guid.NewGuid(), $"Größe (€) {i}", 99.5, 1000, 1.5, 0.5, 15, 60, 70, 10, 0, 5)).ToList();
        var daily = Enumerable.Range(0, 7).Select(i => (day.AddDays(i), 1000.0 + i)).ToList();
        var report = new MinerReport(day, day.AddDays(7), rows, daily, [new MaintenanceRecord { Title = "Lüfter", DoneAt = day }]);
        var pdf = ReportPdf.Render(report, new PowerTariff());
        var text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.DoesNotContain("/Count 1 ", text);
        Assert.Contains(@"Gr\366\337e \(\200\) 0", text);
        // Every entry of the cross-reference table points at its object.
        var xref = text.LastIndexOf("\nxref\n", StringComparison.Ordinal);
        var start = text.IndexOf(" f \n", xref, StringComparison.Ordinal) + 4;
        var offsets = text[start..text.IndexOf("trailer", xref, StringComparison.Ordinal)].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < offsets.Length; i++)
            Assert.StartsWith($"{i + 1} 0 obj", text[int.Parse(offsets[i][..10])..]);
    }

    private static List<string> Dialogue(SmtpDialogue dialogue, params (int, string)[] replies)
    {
        var sent = new List<string>();
        foreach (var (code, text) in replies)
            switch (dialogue.Next(code, text))
            {
                case SmtpAction.Send send: sent.Add(send.Line); break;
                case SmtpAction.StartTls: sent.Add("<tls>"); break;
                default: sent.Add("<done>"); break;
            }
        return sent;
    }

    [Fact]
    public void SmtpWalksThroughTlsAndLogin()
    {
        var message = new SmtpMessage("a@b.c", ["x@y.z", "q@r.s"], "Prüfung", ".hello");
        var sent = Dialogue(new SmtpDialogue(message, "user", "secret", true),
            (220, "hi"), (250, "ok\nSTARTTLS"), (220, "go"), (250, "ok"), (334, "u"), (334, "p"), (235, "in"), (250, "from"), (250, "to"), (250, "to"), (354, "data"), (250, "sent"), (221, "bye"));
        Assert.Equal("EHLO minerguru.local", sent[0]);
        Assert.Equal("STARTTLS", sent[1]);
        Assert.Equal("<tls>", sent[2]);
        Assert.Equal("AUTH LOGIN", sent[3]);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("user")), sent[4]);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("secret")), sent[5]);
        Assert.Equal("MAIL FROM:<a@b.c>", sent[6]);
        Assert.Equal("RCPT TO:<x@y.z>", sent[7]);
        Assert.Equal("RCPT TO:<q@r.s>", sent[8]);
        Assert.Equal("DATA", sent[9]);
        Assert.EndsWith("\r\n.", sent[10]);
        Assert.Contains("Subject: =?UTF-8?B?", sent[10]);
        Assert.Equal("QUIT", sent[11]);
        Assert.Equal("<done>", sent[12]);
    }

    [Fact]
    public void SmtpRefusesWithoutStartTlsAndReportsTheServer()
    {
        var message = new SmtpMessage("a@b.c", ["x@y.z"], "s", "b");
        Assert.Throws<SmtpException>(() => Dialogue(new SmtpDialogue(message, "", "", true), (220, "hi"), (250, "no tls")));
        var error = Assert.Throws<SmtpException>(() => Dialogue(new SmtpDialogue(message, "u", "p", false), (220, "hi"), (250, "ok"), (535, "bad password")));
        Assert.Contains("bad password", error.Message);
        Assert.Equal("..a\r\nb", SmtpDialogue.DotStuffed(".a\r\nb"));
    }

    [Fact]
    public void AFailedEncryptionNeverStoresThePassword()
    {
        var mail = new EmailAlerts(_dir, seal: _ => throw new System.Security.Cryptography.CryptographicException("refused"));
        var settings = new EmailSettings { IsEnabled = true, Host = "h", Sender = "a@b.c", Recipients = "x@y.z" };

        Assert.False(mail.Save(settings, "geheim"));
        Assert.False(mail.HasPassword);
        Assert.Equal("", mail.Settings.Host);
        Assert.False(File.Exists(Path.Combine(_dir, "email.json")));
    }

    [Fact]
    public async Task MailGoesOutOnlyWhenComplete()
    {
        var sent = new List<(SmtpMessage Message, SmtpServer Server)>();
        var mail = new EmailAlerts(_dir, (m, s) => { sent.Add((m, s)); return Task.CompletedTask; });
        AlertEvent[] alerts = [new AlertEvent(AlertKind.Offline, Miner, "Offline", "Body", false)];
        await mail.DeliverAsync(alerts);
        Assert.Empty(sent);
        mail.Save(new EmailSettings { IsEnabled = true, Host = "h", Sender = "a@b.c", Recipients = "x@y.z, q@r.s" }, "geheim");
        Assert.DoesNotContain("geheim", File.ReadAllText(Path.Combine(_dir, "email.json")));
        await mail.DeliverAsync(alerts);
        var (message, server) = Assert.Single(sent);
        Assert.Equal("MinerGuru: Offline", message.Subject);
        Assert.Equal(2, message.To.Count);
        Assert.Equal("geheim", server.Password);

        // A later save without a new password keeps the stored one.
        mail.Save(new EmailSettings { IsEnabled = true, Host = "h2", Sender = "a@b.c", Recipients = "x@y.z" }, null);
        Assert.True(new EmailAlerts(_dir).HasPassword);
    }

    [Fact]
    public void CombineKeepsMaintenanceApartFromMiners()
    {
        var combined = AlertCenter.Combine([
            new AlertEvent(AlertKind.Maintenance, Guid.Empty, "a", "x", false, "A"),
            new AlertEvent(AlertKind.Maintenance, Guid.Empty, "b", "y", false, "B"),
            new AlertEvent(AlertKind.Offline, Miner, "c", "z", false, "C"),
        ]);
        Assert.Equal(2, combined.Count);
        Assert.Equal("A, B", combined[0].Body);
    }
}
