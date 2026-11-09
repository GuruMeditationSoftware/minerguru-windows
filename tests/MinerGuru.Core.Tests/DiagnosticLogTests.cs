using System.Text.Json.Nodes;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class DiagnosticLogTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mg-log").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private DiagnosticLog Log() => new(Path.Combine(_folder, "minerguru.log"));

    [Fact]
    public void WritesOneLinePerEvent()
    {
        var log = Log();
        log.Write("state", "first\nsecond");
        log.Write("alert", "third");
        var lines = log.ExportText().Trim().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("[state] first second", lines[0]);
    }

    [Fact]
    public void KeepsOnlyTwoFiles()
    {
        var log = Log();
        var line = new string('x', 100_000);
        for (var i = 0; i < 25; i++) log.Write("test", $"{i} {line}");
        Assert.Equal(["minerguru.1.log", "minerguru.log"], Directory.GetFiles(_folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var text = log.ExportText();
        Assert.DoesNotContain("] 0 x", text);
        Assert.Contains("] 24 x", text);
        Assert.True(text.Length <= 2 * DiagnosticLog.MaxBytes);
    }

    [Fact]
    public void StaysSilentUntilActivated()
    {
        var log = new DiagnosticLog();
        log.Write("test", "nothing");
        Assert.Equal("", log.ExportText());
    }

    [Fact]
    public void ProfileNamesFieldsButNoValues()
    {
        var miner = new MinerDevice(new PersistedMinerConfig { Id = Guid.NewGuid(), IpAddress = "192.168.1.50" }, ConnectionState.Online)
        {
            RawJson = """{"stratumUser":"bc1qsecret.worker","stratumPassword":"hunter2","ssid":"HomeWifi","temp":55}""",
        };
        var profile = DiagnosticLog.Profile(miner, MinerSystemInfo.Parse(new JsonObject { ["ASICModel"] = "BM1370" }));
        Assert.Contains("keys=ssid,stratumPassword,stratumUser,temp", profile);
        foreach (var secret in new[] { "secret", "hunter2", "HomeWifi" }) Assert.DoesNotContain(secret, profile);
    }

    [Fact]
    public void MeasurementsEveryFifteenMinutes()
    {
        var start = new DateTime(2026, 11, 4, 12, 0, 0);
        var info = MinerSystemInfo.Parse(new JsonObject { ["temp"] = 55.5, ["hashRate_1m"] = 1000, ["sharesAccepted"] = 5, ["uptimeSeconds"] = 7200 });
        var health = new MinerHealth();
        health.Update(info, start);
        Assert.Contains("temp=55.5", health.Snapshot(info, start));
        Assert.Null(health.Snapshot(info, start.AddMinutes(14)));
        Assert.NotNull(health.Snapshot(info, start.AddMinutes(15)));
    }
}
