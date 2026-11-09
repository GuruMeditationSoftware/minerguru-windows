using System.Globalization;
using System.Text;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Models;

/// <summary>One poll of one miner; only the id, time and state for a miner that did not answer.</summary>
public sealed record HistoryReading(
    Guid MinerId, DateTime Time, bool IsOnline, double HashRateGhs = 0, double AsicTempC = 0, double? VrTempC = null,
    double PowerWatts = 0, double FanPercent = 0, double? PoolLatencyMs = null, double? ErrorPercent = null,
    int SharesAccepted = 0, int SharesRejected = 0, double BestDiff = 0)
{
    public static HistoryReading? Of(MinerDevice miner, DateTime time)
    {
        if (miner.IsOnline && miner.LatestInfo is { } info)
            return new HistoryReading(miner.Id, time, true, info.HashRate, info.Temp, info.VrTemp, info.Power, info.FanSpeedPercent,
                miner.ActivePoolLatencyMs, info.ErrorPercentage, info.SharesAccepted, info.SharesRejected, info.BestSessionDiffNumeric);
        return miner.State == ConnectionState.Offline ? new HistoryReading(miner.Id, time, false) : null;
    }
}

/// <summary>One stored time bucket of a miner: averages over the online minutes, plus sums for shares and minutes.</summary>
public sealed record HistoryPoint(
    Guid MinerId, DateTime Time,
    /// <summary>Minutes the miner answered.</summary>
    int OnlineMinutes,
    /// <summary>Minutes MinerGuru was watching it.</summary>
    int RecordedMinutes,
    double? HashRateGhs = null, double? AsicTempC = null, double? MaxAsicTempC = null, double? VrTempC = null,
    double? PowerWatts = null, double? FanPercent = null, double? PoolLatencyMs = null, double? ErrorPercent = null,
    int SharesAccepted = 0, int SharesRejected = 0, double? BestDiff = null)
{
    /// <summary>J/TH; hashrate is stored in GH/s.</summary>
    public double? EfficiencyJPerTh => HashRateGhs is > 0 && PowerWatts is { } watts ? watts / (HashRateGhs.Value / 1000) : null;

    public double EnergyKwh => (PowerWatts ?? 0) * OnlineMinutes / 60 / 1000;

    public double? RejectRate => SharesAccepted + SharesRejected is > 0 and var total ? (double)SharesRejected / total * 100 : null;
}

public enum HistoryRange { Day, Week, Month, Quarter, Year }

public enum HistoryMetric { Hashrate, Temperature, Efficiency, Power, Errors }

public static class HistoryText
{
    public static TimeSpan Span(this HistoryRange range) => range switch
    {
        HistoryRange.Day => TimeSpan.FromDays(1),
        HistoryRange.Week => TimeSpan.FromDays(7),
        HistoryRange.Month => TimeSpan.FromDays(30),
        HistoryRange.Quarter => TimeSpan.FromDays(90),
        _ => TimeSpan.FromDays(365),
    };

    public static string Title(this HistoryRange range) => range switch
    {
        HistoryRange.Day => L("24 Std.", "24 hrs"),
        HistoryRange.Week => L("7 Tage", "7 days"),
        HistoryRange.Month => L("30 Tage", "30 days"),
        HistoryRange.Quarter => L("90 Tage", "90 days"),
        _ => L("1 Jahr", "1 year"),
    };

    public static string Title(this HistoryMetric metric) => metric switch
    {
        HistoryMetric.Hashrate => "Hashrate",
        HistoryMetric.Temperature => L("Temperatur", "Temperature"),
        HistoryMetric.Efficiency => L("Effizienz", "Efficiency"),
        HistoryMetric.Power => L("Leistung", "Power"),
        _ => L("Fehlerquote", "Error rate"),
    };

    public static double? Value(this HistoryMetric metric, HistoryPoint point) => metric switch
    {
        HistoryMetric.Hashrate => point.HashRateGhs,
        HistoryMetric.Temperature => point.AsicTempC,
        HistoryMetric.Efficiency => point.EfficiencyJPerTh,
        HistoryMetric.Power => point.PowerWatts,
        _ => point.ErrorPercent ?? point.RejectRate,
    };

    public static string Format(this HistoryMetric metric, double value) => metric switch
    {
        HistoryMetric.Hashrate => MinerFormatting.FormatHashrate(value),
        HistoryMetric.Temperature => $"{value.ToString("0.0", Culture)} °C",
        HistoryMetric.Efficiency => $"{value.ToString("0.0", Culture)} J/TH",
        HistoryMetric.Power => $"{value.ToString("0.0", Culture)} W",
        _ => $"{value.ToString("0.00", Culture)} %",
    };
}

public static class HistoryQuery
{
    /// <summary>Charts need no more points than this.</summary>
    public const int MaxPoints = 720;

    /// <summary>Splits chart values where rows are missing, so an offline stretch stays a visible gap.</summary>
    public static List<List<(DateTime Time, double Value)>> SplitAtGaps(IEnumerable<(DateTime Time, double Value)> values, int stepSeconds)
    {
        var gap = TimeSpan.FromSeconds(stepSeconds * 3);
        var runs = new List<List<(DateTime Time, double Value)>>();
        foreach (var value in values)
        {
            if (runs.Count == 0 || value.Time - runs[^1][^1].Time > gap) runs.Add([]);
            runs[^1].Add(value);
        }
        return runs;
    }

    /// <summary>Bucket size in seconds: at least a minute, an hour beyond two days, and few enough for a chart.</summary>
    public static int Step(TimeSpan span)
    {
        var seconds = (long)span.TotalSeconds;
        var wanted = (int)Math.Ceiling((double)seconds / MaxPoints);
        var bucket = seconds > 2 * 86_400 ? 3600 : 60;
        return Math.Max(bucket, (wanted + bucket - 1) / bucket * bucket);
    }
}

/// <summary>Turns polls into per-minute rows. Pure logic, so the tests need no database.</summary>
public sealed class HistoryAggregator
{
    private sealed class Bucket(long minute)
    {
        public readonly long Minute = minute;
        public int Polls, OnlinePolls, VrCount, LatencyCount, ErrorCount, Accepted, Rejected;
        public double HashRate, Temp, MaxTemp, Power, Fan, Vr, Latency, Error, Best;
    }

    private readonly Dictionary<Guid, Bucket> _buckets = [];
    private readonly Dictionary<Guid, (int Accepted, int Rejected)> _lastCounters = [];

    /// <summary>Adds a poll; returns the row of a minute that just ended.</summary>
    public HistoryPoint? Add(HistoryReading r)
    {
        var minute = new DateTimeOffset(r.Time).ToUnixTimeSeconds() / 60;
        HistoryPoint? finished = null;
        if (!_buckets.TryGetValue(r.MinerId, out var bucket) || bucket.Minute != minute)
        {
            if (bucket != null) finished = Row(r.MinerId, bucket);
            _buckets[r.MinerId] = bucket = new Bucket(minute);
        }
        bucket.Polls++;
        if (!r.IsOnline) return finished;
        bucket.OnlinePolls++;
        bucket.HashRate += r.HashRateGhs;
        bucket.Temp += r.AsicTempC;
        bucket.MaxTemp = Math.Max(bucket.MaxTemp, r.AsicTempC);
        if (r.VrTempC is > 0) { bucket.Vr += r.VrTempC.Value; bucket.VrCount++; }
        bucket.Power += r.PowerWatts;
        bucket.Fan += r.FanPercent;
        if (r.PoolLatencyMs is > 0) { bucket.Latency += r.PoolLatencyMs.Value; bucket.LatencyCount++; }
        if (r.ErrorPercent is { } error) { bucket.Error += error; bucket.ErrorCount++; }
        bucket.Best = Math.Max(bucket.Best, r.BestDiff);
        // Counters restart with the miner; what it counted since then is new.
        if (_lastCounters.TryGetValue(r.MinerId, out var last))
        {
            bucket.Accepted += r.SharesAccepted >= last.Accepted ? r.SharesAccepted - last.Accepted : r.SharesAccepted;
            bucket.Rejected += r.SharesRejected >= last.Rejected ? r.SharesRejected - last.Rejected : r.SharesRejected;
        }
        _lastCounters[r.MinerId] = (r.SharesAccepted, r.SharesRejected);
        return finished;
    }

    /// <summary>The running minutes as they stand; a minute that is over is done with.</summary>
    public List<HistoryPoint> Flush(DateTime now)
    {
        var rows = _buckets.Select(pair => Row(pair.Key, pair.Value)).ToList();
        var minute = new DateTimeOffset(now).ToUnixTimeSeconds() / 60;
        foreach (var id in _buckets.Where(pair => pair.Value.Minute < minute).Select(pair => pair.Key).ToList()) _buckets.Remove(id);
        return rows;
    }

    public void Forget(Guid minerId)
    {
        _buckets.Remove(minerId);
        _lastCounters.Remove(minerId);
    }

    private static HistoryPoint Row(Guid id, Bucket b)
    {
        var online = b.OnlinePolls > 0;
        static double? Average(double sum, int count) => count > 0 ? sum / count : null;
        return new HistoryPoint(id, DateTimeOffset.FromUnixTimeSeconds(b.Minute * 60).LocalDateTime, online ? 1 : 0, 1,
            Average(b.HashRate, b.OnlinePolls), Average(b.Temp, b.OnlinePolls), online ? b.MaxTemp : null,
            Average(b.Vr, b.VrCount), Average(b.Power, b.OnlinePolls), Average(b.Fan, b.OnlinePolls), Average(b.Latency, b.LatencyCount),
            Average(b.Error, b.ErrorCount), b.Accepted, b.Rejected, online && b.Best > 0 ? b.Best : null);
    }
}

public static class HistoryCsv
{
    private const string Header = "time,miner,online_minutes,recorded_minutes,hashrate_ghs,asic_temp_c,max_asic_temp_c,vr_temp_c,power_w,efficiency_j_th,fan_percent,pool_latency_ms,error_percent,shares_accepted,shares_rejected,best_diff";

    /// <summary>Machine-readable export: ISO times, dots as decimal separator, empty fields for missing values.</summary>
    public static string Text(IEnumerable<HistoryPoint> points, Func<Guid, string> minerName)
    {
        static string N(double? value) => value?.ToString("0.000", CultureInfo.InvariantCulture) ?? "";
        var text = new StringBuilder(Header).Append('\n');
        foreach (var p in points)
            text.AppendJoin(',', p.Time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                $"\"{minerName(p.MinerId).Replace("\"", "\"\"")}\"", p.OnlineMinutes, p.RecordedMinutes, N(p.HashRateGhs), N(p.AsicTempC),
                N(p.MaxAsicTempC), N(p.VrTempC), N(p.PowerWatts), N(p.EfficiencyJPerTh), N(p.FanPercent), N(p.PoolLatencyMs), N(p.ErrorPercent),
                p.SharesAccepted, p.SharesRejected, N(p.BestDiff)).Append('\n');
        return text.ToString();
    }
}
