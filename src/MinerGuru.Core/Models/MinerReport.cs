using System.Globalization;
using System.Text;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Models;

public enum ReportPeriod { Yesterday, Last7Days, Last30Days, ThisMonth, LastMonth }

public static class ReportPeriodText
{
    public static string Title(this ReportPeriod period) => period switch
    {
        ReportPeriod.Yesterday => L("Gestern", "Yesterday"),
        ReportPeriod.Last7Days => L("Letzte 7 Tage", "Last 7 days"),
        ReportPeriod.Last30Days => L("Letzte 30 Tage", "Last 30 days"),
        ReportPeriod.ThisMonth => L("Dieser Monat", "This month"),
        _ => L("Letzter Monat", "Last month"),
    };

    public static (DateTime Start, DateTime End) Interval(this ReportPeriod period, DateTime now)
    {
        var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, now.Kind);
        return period switch
        {
            ReportPeriod.Yesterday => (now.Date.AddDays(-1), now.Date),
            ReportPeriod.Last7Days => (now.Date.AddDays(-7), now),
            ReportPeriod.Last30Days => (now.Date.AddDays(-30), now),
            ReportPeriod.ThisMonth => (month, now),
            _ => (month.AddMonths(-1), month),
        };
    }
}

/// <summary>Figures of one miner over a report period.</summary>
public sealed record MinerReportRow(
    Guid Id, string Name, double? UptimePercent, double? AverageHashRateGhs, double EnergyKwh, double Cost, double? EfficiencyJPerTh,
    double? AverageTempC, double? MaxTempC, int SharesAccepted, int SharesRejected, double? BestDiff);

/// <summary>Fleet report built from the hourly history: uptime, output, energy and cost per miner, plus the maintenance done.</summary>
public sealed record MinerReport(
    DateTime Start, DateTime End, List<MinerReportRow> Rows,
    /// <summary>Total fleet hashrate per day: start of the day and GH/s.</summary>
    List<(DateTime Day, double HashRateGhs)> Daily,
    List<MaintenanceRecord> Maintenance)
{
    public double TotalEnergyKwh => Rows.Sum(r => r.EnergyKwh);
    public double TotalCost => Rows.Sum(r => r.Cost);
    public int SharesAccepted => Rows.Sum(r => r.SharesAccepted);
    public int SharesRejected => Rows.Sum(r => r.SharesRejected);
    public double? BestDiff => Rows.Max(r => r.BestDiff);
    public double? MaxTempC => Rows.Max(r => r.MaxTempC);
    public double? UptimePercent => Rows.Average(r => r.UptimePercent);

    /// <summary>Sum of the miners' averages.</summary>
    public double? AverageHashRateGhs => Rows.Any(r => r.AverageHashRateGhs != null) ? Rows.Sum(r => r.AverageHashRateGhs) : null;

    /// <summary>Average of the miners' efficiency, weighted by their hashrate.</summary>
    public double? EfficiencyJPerTh
    {
        get
        {
            double watts = 0, terahashes = 0;
            foreach (var row in Rows)
            {
                if (row.EfficiencyJPerTh is not { } efficiency || row.AverageHashRateGhs is not { } hashRate) continue;
                watts += efficiency * hashRate / 1000;
                terahashes += hashRate / 1000;
            }
            return terahashes > 0 ? watts / terahashes : null;
        }
    }

    /// <summary>Spreadsheet export, one line per miner.</summary>
    public string CsvText()
    {
        static string N(double? value, string format = "0.00") => value?.ToString(format, CultureInfo.InvariantCulture) ?? "";
        var text = new StringBuilder("miner,uptime_percent,avg_hashrate_ghs,energy_kwh,cost,efficiency_j_th,avg_temp_c,max_temp_c,shares_accepted,shares_rejected,best_diff\n");
        foreach (var r in Rows)
            text.AppendJoin(',', $"\"{r.Name.Replace("\"", "\"\"")}\"", N(r.UptimePercent), N(r.AverageHashRateGhs), N(r.EnergyKwh, "0.000"), N(r.Cost),
                N(r.EfficiencyJPerTh), N(r.AverageTempC), N(r.MaxTempC), r.SharesAccepted, r.SharesRejected, N(r.BestDiff, "0")).Append('\n');
        return text.ToString();
    }

    /// <param name="points">Hourly history of all miners in the interval.</param>
    /// <param name="minerName">Display name of a miner; deleted miners give <c>null</c> and are listed under a short id.</param>
    /// <param name="price">Energy price per kWh at a time.</param>
    public static MinerReport Build(DateTime start, DateTime end, IReadOnlyCollection<HistoryPoint> points, Func<Guid, string?> minerName,
        Func<DateTime, double> price, IEnumerable<MaintenanceRecord> maintenance)
    {
        var rows = points.GroupBy(p => p.MinerId).Select(group =>
        {
            var list = group.ToList();
            var online = list.Sum(p => p.OnlineMinutes);
            var recorded = list.Sum(p => p.RecordedMinutes);
            double? Average(Func<HistoryPoint, double?> value)
            {
                double sum = 0, weight = 0;
                foreach (var p in list)
                {
                    if (value(p) is not { } v || p.OnlineMinutes <= 0) continue;
                    sum += v * p.OnlineMinutes;
                    weight += p.OnlineMinutes;
                }
                return weight > 0 ? sum / weight : null;
            }
            var hashRate = Average(p => p.HashRateGhs);
            var power = Average(p => p.PowerWatts);
            return new MinerReportRow(group.Key, minerName(group.Key) ?? group.Key.ToString()[..8], recorded > 0 ? (double)online / recorded * 100 : null, hashRate,
                list.Sum(p => p.EnergyKwh), list.Sum(p => p.EnergyKwh * price(p.Time)),
                hashRate is > 0 && power is { } watts ? watts / (hashRate.Value / 1000) : null,
                Average(p => p.AsicTempC), list.Max(p => p.MaxAsicTempC), list.Sum(p => p.SharesAccepted), list.Sum(p => p.SharesRejected), list.Max(p => p.BestDiff));
        }).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // Average over the hours of the day, per miner, then the sum of the fleet.
        var daily = points.Where(p => p.OnlineMinutes > 0).GroupBy(p => p.Time.Date).OrderBy(day => day.Key)
            .Select(day => (day.Key, day.GroupBy(p => p.MinerId).Sum(hours => hours.Sum(p => p.HashRateGhs ?? 0) / hours.Count()))).ToList();

        return new MinerReport(start, end, rows, daily, maintenance.Where(r => r.DoneAt >= start && r.DoneAt < end).OrderByDescending(r => r.DoneAt).ToList());
    }
}
