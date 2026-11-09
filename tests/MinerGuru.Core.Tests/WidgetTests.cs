using System.Text.Json.Nodes;
using MinerGuru.Core;
using MinerGuru.Core.Widgets;
using Xunit;
using M = MinerGuru.Core.Widgets.WidgetSnapshot.Miner;
using State = MinerGuru.Core.Widgets.WidgetSnapshot.MinerState;

namespace MinerGuru.Core.Tests;

public class WidgetTests
{
    public WidgetTests() => Lang.English = false;

    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0);

    private static WidgetSnapshot Fleet(params M[] miners) => new()
    {
        Time = Now,
        Miners = [.. miners],
        TempWarningC = 68,
        BestDiff = "886M",
        SoloOdds =
        [
            new("BTC", "1 : 1.137.819", "Pro Tag", "~3.115 J."),
            new("BCH", "1 : 3.239", "Pro Tag", "~8,9 Jahre"),
        ],
    };

    private static M Miner(string name, State state = State.Online, double ghs = 1_000, double temp = 55) =>
        new(Guid.NewGuid(), name, "Gamma 601", "10.0.0.2", state, state == State.Online ? ghs : 0, temp, 15);

    /// <summary>Every text of the card in reading order, joined with " | ".</summary>
    private static string Texts(string card)
    {
        var texts = new List<string>();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o["type"]?.GetValue<string>() == "TextBlock") texts.Add(o["text"]!.GetValue<string>());
                    foreach (var (_, child) in o) Walk(child);
                    break;
                case JsonArray a:
                    foreach (var child in a) Walk(child);
                    break;
            }
        }
        Walk(JsonNode.Parse(card));
        return string.Join(" | ", texts);
    }

    [Fact]
    public void FleetFiguresCountOnlyOnlineMiners()
    {
        var s = Fleet(Miner("A", ghs: 2_000), Miner("B", State.Offline), Miner("C", temp: 70));
        Assert.Equal(3_000, s.TotalHashRateGhs);
        Assert.Equal(30, s.TotalPowerW);
        Assert.Single(s.Offline);
        Assert.Equal("C", Assert.Single(s.Hot).Name);
    }

    [Fact]
    public void StatusNamesTheMostUrgentProblem()
    {
        var hot = WidgetCards.Build(WidgetCards.Fleet, "small", Fleet(Miner("A"), Miner("Keller", State.Offline), Miner("Hex", temp: 70)), Now);
        Assert.Contains("Hex heiß", Texts(hot));
        var offline = WidgetCards.Build(WidgetCards.Fleet, "small", Fleet(Miner("A"), Miner("Keller", State.Offline)), Now);
        Assert.Contains("Keller offline", Texts(offline));
        var calm = WidgetCards.Build(WidgetCards.Fleet, "small", Fleet(Miner("A"), Miner("B")), Now);
        Assert.Contains("2/2 online", Texts(calm));
    }

    [Fact]
    public void FleetListsMinersByUrgency()
    {
        var s = Fleet(Miner("Gamma"), Miner("Keller", State.Offline));
        Assert.Contains("Keller | offline", Texts(WidgetCards.Build(WidgetCards.Fleet, "large", s, Now)));
        Assert.Contains("Best Diff | 886M", Texts(WidgetCards.Build(WidgetCards.Fleet, "medium", s, Now)));
        // Medium names three miners, problems first.
        var many = Fleet(Miner("A", ghs: 500), Miner("B", ghs: 3_000), Miner("C", ghs: 2_000), Miner("Keller", State.Offline), Miner("Hex", temp: 70));
        var medium = Texts(WidgetCards.Build(WidgetCards.Fleet, "medium", many, Now));
        Assert.Contains("Hex | 1.00 TH/s | 70 °C | ● | Keller | offline |  | ● | B | 3.00 TH/s", medium);
        Assert.DoesNotContain("| C |", medium);
    }

    [Fact]
    public void OldDataShowsItsTime()
    {
        var s = Fleet(Miner("A")) with { Time = Now.AddMinutes(-40) };
        Assert.Contains("Stand 11:20", Texts(WidgetCards.Build(WidgetCards.Fleet, "small", s, Now)));
    }

    [Fact]
    public void SoloShowsEveryCoin()
    {
        var texts = Texts(WidgetCards.Build(WidgetCards.SoloChance, "small", Fleet(Miner("A")), Now));
        Assert.Contains("BTC | 1 : 1.137.819", texts);
        Assert.Contains("BCH | 1 : 3.239", texts);
        Assert.Contains("BCH | 1 : 3.239 | ~8,9 Jahre", Texts(WidgetCards.Build(WidgetCards.SoloChance, "medium", Fleet(Miner("A")), Now)));
        var single = Fleet(Miner("A")) with { SoloOdds = [new("BTC", "1 : 104.858", "Pro Tag", "~287,1 Jahre")] };
        Assert.Contains("BTC · Pro Tag | 1 : 104.858 | Ø 1 Block alle ~287,1 Jahre", Texts(WidgetCards.Build(WidgetCards.SoloChance, "small", single, Now)));
    }

    [Fact]
    public void EmptyFleetAndEnglish()
    {
        Assert.Contains("Keine Miner", Texts(WidgetCards.Build(WidgetCards.Fleet, "small", null, Now)));
        Lang.English = true;
        Assert.Contains("No miners", Texts(WidgetCards.Build(WidgetCards.Fleet, "small", Fleet(), Now)));
    }

    [Fact]
    public void SnapshotRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mg-widget-" + Guid.NewGuid());
        try
        {
            var s = Fleet(Miner("A"), Miner("B", State.Offline));
            s.Save(dir);
            var loaded = WidgetSnapshot.Load(dir)!;
            Assert.Equal(s.Time, loaded.Time);
            Assert.Equal(2, loaded.Miners.Count);
            Assert.Equal(State.Offline, loaded.Miners[1].State);
            Assert.Equal("BCH", loaded.SoloOdds[1].Coin);
        }
        finally { Directory.Delete(dir, true); }
    }
}
