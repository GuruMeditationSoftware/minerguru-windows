using System.Text.Json;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;

namespace MinerGuru.Core.Widgets;

/// <summary>
/// What the Windows widgets show: one JSON file in the data folder, rewritten by the app after a refresh round.
/// The widget service reads it; when it is old, the service asks the miners itself.
/// </summary>
public sealed record WidgetSnapshot
{
    public const int CurrentVersion = 1;

    public enum MinerState { Online, Offline, Checking }

    public sealed record Miner(Guid Id, string Name, string Model, string Host, MinerState State, double HashRateGhs, double? TempC, double? PowerW);

    /// <summary>Solo odds of one coin, formatted in the snapshot's language.</summary>
    public sealed record Solo(string Coin, string Odds, string Period, string BlockEvery);

    public int Version { get; init; } = CurrentVersion;
    public DateTime Time { get; init; }
    /// <summary><c>de</c> or <c>en</c>: the widgets speak the app's language.</summary>
    public string Language { get; init; } = "de";
    public List<Miner> Miners { get; init; } = [];
    public double TempWarningC { get; init; } = 68;
    public string? BestDiff { get; init; }
    /// <summary>Every coin the fleet mines, Bitcoin first, then by hashrate.</summary>
    public List<Solo> SoloOdds { get; init; } = [];

    public IEnumerable<Miner> Online => Miners.Where(m => m.State == MinerState.Online);
    public IEnumerable<Miner> Offline => Miners.Where(m => m.State == MinerState.Offline);
    public IEnumerable<Miner> Hot => Online.Where(IsHot);
    public double TotalHashRateGhs => Online.Sum(m => m.HashRateGhs);
    public double TotalPowerW => Online.Sum(m => m.PowerW ?? 0);

    public double? EfficiencyJPerTh
    {
        get
        {
            var th = TotalHashRateGhs / 1000;
            return th > 0.01 && TotalPowerW > 0 ? TotalPowerW / th : null;
        }
    }

    public bool IsHot(Miner m) => m.State == MinerState.Online && (m.TempC ?? 0) >= TempWarningC;

    /// <summary>The snapshot of a refresh round; texts in the current <see cref="Lang"/>.</summary>
    public static WidgetSnapshot Of(IEnumerable<MinerDevice> miners, double tempWarningC, SoloChanceTimeframe timeframe, DateTime now)
    {
        var list = miners.ToList();
        var online = list.Where(m => m.IsOnline && m.LatestInfo != null).ToList();
        return new WidgetSnapshot
        {
            Time = now,
            Language = Lang.English ? "en" : "de",
            TempWarningC = tempWarningC,
            Miners = list.Select(m =>
            {
                var state = m.State switch
                {
                    ConnectionState.Online when m.LatestInfo != null => MinerState.Online,
                    ConnectionState.Offline => MinerState.Offline,
                    _ => MinerState.Checking,
                };
                var info = m.LatestInfo;
                return new Miner(m.Id, m.DisplayName, m.HardwareTitle, m.IpAddress, state,
                    state == MinerState.Online ? info!.HashRate : 0,
                    info?.Temp > 0 ? info.Temp : null, info?.Power > 0 ? info.Power : null);
            }).ToList(),
            BestDiff = online.Select(m => m.LatestInfo!).MaxBy(i => i.BestDiffNumeric)?.BestDiffFormatted,
            SoloOdds = FleetCoinMiningSummary.Summaries(online).Where(s => s.TotalHashrateGhs > 0)
                .Select(s => new Solo(s.Coin.Symbol(), s.FormattedOddsRatio(timeframe), timeframe.Title(), s.ExpectedTimePerBlockFormatted))
                .ToList(),
        };
    }

    // File

    public static string PathIn(string dataDirectory) => System.IO.Path.Combine(dataDirectory, "widgets", "snapshot.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static WidgetSnapshot? Load(string dataDirectory)
    {
        try
        {
            var s = JsonSerializer.Deserialize<WidgetSnapshot>(File.ReadAllText(PathIn(dataDirectory)), Json);
            return s?.Version == CurrentVersion ? s : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Writes atomically, so the widget service never reads half a file.</summary>
    public void Save(string dataDirectory)
    {
        var path = PathIn(dataDirectory);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}
