using Microsoft.Data.Sqlite;
using MinerGuru.Core.Models;

namespace MinerGuru.Core.Services;

/// <summary>
/// Long-term telemetry in a local SQLite file. Every poll goes into a per-minute average; minute rows stay for
/// <see cref="MinuteRetention"/>, hourly rollups for <see cref="HourRetention"/>. Safe to use from any thread.
/// </summary>
public sealed class HistoryStore
{
    public static readonly TimeSpan MinuteRetention = TimeSpan.FromDays(14);
    public static readonly TimeSpan HourRetention = TimeSpan.FromDays(3 * 365);
    private static readonly TimeSpan RollupInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RollupWindow = TimeSpan.FromHours(3);
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private const string Aggregates = """
        SUM(online), SUM(total),
          SUM(hashrate * online) / NULLIF(SUM(online), 0), SUM(asic * online) / NULLIF(SUM(online), 0), MAX(asic_max),
          AVG(vr), SUM(power * online) / NULLIF(SUM(online), 0), AVG(fan), AVG(latency), AVG(error),
          SUM(accepted), SUM(rejected), MAX(best)
        """;

    private readonly object _lock = new();
    private readonly string _connection;
    private readonly HistoryAggregator _aggregator = new();
    private readonly Func<DateTime> _clock;
    private DateTime _lastRollup = DateTime.MinValue;
    private bool _hasRolledUp;
    private readonly string _path;
    private readonly IReadOnlyList<string> _migrations;

    /// <summary>A damaged file was moved here and a new, empty history started; null while the file is fine.</summary>
    public string? DamagedBackup { get; private set; }

    /// <summary>Raised once per damaged file, on the thread that noticed it.</summary>
    public event Action? Damaged;

    /// <summary>Schema changes, in order. Never edit one that shipped; add a new one. <c>PRAGMA user_version</c> counts those done.</summary>
    private static readonly string[] Migrations = [];

    public HistoryStore(string dataDirectory, Func<DateTime>? clock = null, IReadOnlyList<string>? migrations = null)
    {
        _clock = clock ?? (() => DateTime.Now);
        _path = Path.Combine(dataDirectory, "history.sqlite");
        _migrations = migrations ?? Migrations;
        _connection = new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();
        Run(Prepare, false);
    }

    private bool Prepare(SqliteConnection db)
    {
        Execute(db, """
            CREATE TABLE IF NOT EXISTS samples(
              resolution INTEGER NOT NULL, miner TEXT NOT NULL, ts INTEGER NOT NULL,
              online INTEGER NOT NULL, total INTEGER NOT NULL,
              hashrate REAL, asic REAL, asic_max REAL, vr REAL, power REAL, fan REAL, latency REAL, error REAL,
              accepted INTEGER NOT NULL, rejected INTEGER NOT NULL, best REAL,
              PRIMARY KEY(resolution, miner, ts)) WITHOUT ROWID
            """);
        Migrate(db, _migrations);
        return true;
    }

    /// <summary>
    /// Every access goes through here, so a damaged file cannot take the app down: it moves aside, kept for
    /// rescue, and the history starts anew. Other errors, such as a full disk, stay with the caller.
    /// </summary>
    private T Run<T>(Func<SqliteConnection, T> work, T fallback)
    {
        lock (_lock)
        {
            try
            {
                using var db = Open();
                return work(db);
            }
            catch (SqliteException e) when (e.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
            {
                DiagnosticLog.Shared.Write("history", $"damaged file, error {e.SqliteErrorCode}");
            }
            if (MoveDamagedFileAside())
            {
                using var fresh = Open();
                Prepare(fresh);
                Damaged?.Invoke();
            }
            return fallback;
        }
    }

    private bool MoveDamagedFileAside()
    {
        var backup = $"{_path}.defekt-{_clock():yyyyMMdd-HHmmss}";
        try
        {
            File.Move(_path, backup, overwrite: true);
            foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
                if (File.Exists(_path + suffix)) File.Move(_path + suffix, backup + suffix, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Shared.Write("history", $"could not move the damaged file: {e.GetType().Name}");
            return false;
        }
        DamagedBackup = backup;
        return true;
    }

    private static void Migrate(SqliteConnection db, IReadOnlyList<string> steps)
    {
        using var read = db.CreateCommand();
        read.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(read.ExecuteScalar());
        for (var i = version; i < steps.Count; i++)
        {
            using var transaction = db.BeginTransaction();
            Execute(db, steps[i]);
            Execute(db, $"PRAGMA user_version = {i + 1}");
            transaction.Commit();
        }
    }

    /// <summary>Polls write once a minute at most, so the file stays closed in between.</summary>
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        return db;
    }

    /// <summary>Adds one poll of the fleet.</summary>
    public void Record(IEnumerable<HistoryReading> readings, DateTime now)
    {
        lock (_lock)
        {
            var finished = readings.Select(_aggregator.Add).OfType<HistoryPoint>().ToList();
            var due = now - _lastRollup >= RollupInterval;
            if (finished.Count == 0 && !due) return;
            Run(db =>
            {
                Write(db, finished);
                if (!due) return true;
                _lastRollup = now;
                RollUp(db, now);
                return true;
            }, false);
        }
    }

    public void Forget(Guid minerId)
    {
        lock (_lock)
        {
            _aggregator.Forget(minerId);
            Run(db =>
            {
                Execute(db, "DELETE FROM samples WHERE miner = $a", minerId.ToString());
                return true;
            }, false);
        }
    }

    /// <summary>Points of one miner between two times, oldest first.</summary>
    public List<HistoryPoint> Points(Guid minerId, DateTime from, DateTime to)
    {
        lock (_lock)
        {
            var open = _aggregator.Flush(_clock());
            return Run(db =>
            {
                Write(db, open);
                return Query(db, minerId, from, to, HistoryQuery.Step(to - from));
            }, []);
        }
    }

    /// <summary>Hourly points of all miners, for reports.</summary>
    public List<HistoryPoint> HourlyPoints(DateTime from, DateTime to)
    {
        lock (_lock)
        {
            var open = _aggregator.Flush(_clock());
            return Run(db =>
            {
                Write(db, open);
                RollUp(db, _clock());
                return Query(db, null, from, to, 3600);
            }, []);
        }
    }

    private static void Write(SqliteConnection db, List<HistoryPoint> rows)
    {
        if (rows.Count == 0) return;
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO samples VALUES
            (60, $miner, $ts, $online, $total, $hashrate, $asic, $asicMax, $vr, $power, $fan, $latency, $error, $accepted, $rejected, $best)
            """;
        foreach (var p in rows)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$miner", p.MinerId.ToString());
            command.Parameters.AddWithValue("$ts", Seconds(p.Time));
            command.Parameters.AddWithValue("$online", p.OnlineMinutes);
            command.Parameters.AddWithValue("$total", p.RecordedMinutes);
            Add("$hashrate", p.HashRateGhs);
            Add("$asic", p.AsicTempC);
            Add("$asicMax", p.MaxAsicTempC);
            Add("$vr", p.VrTempC);
            Add("$power", p.PowerWatts);
            Add("$fan", p.FanPercent);
            Add("$latency", p.PoolLatencyMs);
            Add("$error", p.ErrorPercent);
            command.Parameters.AddWithValue("$accepted", p.SharesAccepted);
            command.Parameters.AddWithValue("$rejected", p.SharesRejected);
            Add("$best", p.BestDiff);
            command.ExecuteNonQuery();
        }
        transaction.Commit();

        void Add(string name, double? value) => command.Parameters.AddWithValue(name, value is { } v ? v : DBNull.Value);
    }

    /// <summary>Averages are weighted by online minutes, so a half hour online does not count like a whole one.</summary>
    private void RollUp(SqliteConnection db, DateTime now)
    {
        // The first round after a start also covers what the last run left unfinished.
        var window = _hasRolledUp ? RollupWindow : MinuteRetention;
        _hasRolledUp = true;
        Execute(db, $"""
            INSERT OR REPLACE INTO samples
            SELECT 3600, miner, ts / 3600 * 3600, {Aggregates}
            FROM samples WHERE resolution = 60 AND ts >= $a / 3600 * 3600 GROUP BY miner, ts / 3600
            """, Seconds(now - window));
        Execute(db, "DELETE FROM samples WHERE resolution = 60 AND ts < $a", Seconds(now - MinuteRetention));
        Execute(db, "DELETE FROM samples WHERE resolution = 3600 AND ts < $a", Seconds(now - HourRetention));
    }

    private static List<HistoryPoint> Query(SqliteConnection db, Guid? minerId, DateTime from, DateTime to, int step)
    {
        using var command = db.CreateCommand();
        var filter = minerId != null ? "AND miner = $miner" : "";
        command.CommandText = $"""
            SELECT miner, ts / {step} * {step}, {Aggregates}
            FROM samples WHERE resolution = $resolution AND ts >= $from AND ts <= $to {filter}
            GROUP BY miner, ts / {step} ORDER BY 2, 1
            """;
        command.Parameters.AddWithValue("$resolution", step >= 3600 ? 3600 : 60);
        command.Parameters.AddWithValue("$from", Seconds(from));
        command.Parameters.AddWithValue("$to", Seconds(to));
        if (minerId != null) command.Parameters.AddWithValue("$miner", minerId.Value.ToString());
        using var reader = command.ExecuteReader();
        var points = new List<HistoryPoint>();
        while (reader.Read())
        {
            double? Number(int i) => reader.IsDBNull(i) ? null : reader.GetDouble(i);
            points.Add(new HistoryPoint(Guid.Parse(reader.GetString(0)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).LocalDateTime,
                reader.GetInt32(2), reader.GetInt32(3), Number(4), Number(5), Number(6), Number(7), Number(8), Number(9), Number(10), Number(11),
                reader.GetInt32(12), reader.GetInt32(13), Number(14)));
        }
        return points;
    }

    private static void Execute(SqliteConnection db, string sql, object? parameter = null)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        if (parameter != null) command.Parameters.AddWithValue("$a", parameter);
        command.ExecuteNonQuery();
    }

    private static long Seconds(DateTime time) => new DateTimeOffset(time).ToUnixTimeSeconds();
}
