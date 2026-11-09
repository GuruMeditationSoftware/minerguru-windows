using System.Globalization;

namespace MinerGuru.Core.Models;

/// <summary>
/// Watches one miner for overheating, flatline (ESP-Miner #1053: the miner answers and draws power,
/// but no shares arrive and its averages freeze), a fallback pool it does not leave (#1618) and
/// restarts nobody asked for. All but the restart need more than one poll, to avoid false alarms.
/// </summary>
public sealed class MinerHealth
{
    public enum AutoRestartReason { Flatline, StuckOnFallback }

    /// <summary>NerdOS has no overheat flag: warn this close to its limit …</summary>
    public const double OverheatMargin = 3;
    /// <summary>… for this many polls in a row. As many polls below the limit end the alarm.</summary>
    public const int OverheatPolls = 3;
    /// <summary>A flatline needs at least this long without a share, and ten expected share intervals.</summary>
    public static readonly TimeSpan FlatlineMinimum = TimeSpan.FromMinutes(10);
    public const double FlatlineShareIntervals = 10;
    /// <summary><c>hashRate_1m</c> must also stand still this long and over this many polls.</summary>
    public static readonly TimeSpan FrozenDuration = TimeSpan.FromMinutes(5);
    public const int FrozenPolls = 5;
    /// <summary>Shares take a while after a restart.</summary>
    public const int StartupGraceSeconds = 5 * 60;
    /// <summary>Uptime has to fall by more than this to count as a restart: answers can arrive out of order.</summary>
    public const int UptimeToleranceSeconds = 60;
    /// <summary>On the fallback pool this long in a row …</summary>
    public static readonly TimeSpan FallbackAlarm = TimeSpan.FromMinutes(10);
    /// <summary>… and with the primary pool answering this long, the miner should have switched back.</summary>
    public static readonly TimeSpan StuckOnFallback = TimeSpan.FromMinutes(15);
    /// <summary>The watchdog restarts a miner at most this often, then only alarms remain.</summary>
    public static readonly TimeSpan AutoRestartInterval = TimeSpan.FromHours(1);
    /// <summary>How often the diagnostic log gets the measurements.</summary>
    public static readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(15);

    private int? _shares;
    private DateTime? _sharesChangedAt;
    private double? _hashRate;
    private DateTime? _hashRateChangedAt;
    private int _samePolls;
    private int _hotPolls;
    private int _coolPolls;
    private DateTime? _lastSnapshotAt;
    private TimeSpan _peakStall;
    private DateTime? _fallbackSince;
    private DateTime? _primaryUpSince;
    private int? _lastUptime;

    public bool IsOverheating { get; private set; }
    public bool IsFlatlining { get; private set; }
    public bool IsOnFallback { get; private set; }
    public bool IsStuckOnFallback { get; private set; }
    /// <summary>The last poll found a restart MinerGuru did not ask for.</summary>
    public bool RestartedUnexpectedly { get; private set; }

    public bool OverheatAlerted { get; set; }
    public DateTime? LastOverheatAlertAt { get; set; }
    public bool FlatlineAlerted { get; set; }
    public DateTime? LastFlatlineAlertAt { get; set; }
    public bool FallbackAlerted { get; set; }
    public DateTime? LastFallbackAlertAt { get; set; }
    public bool StuckAlerted { get; set; }
    public string? LastProfile { get; set; }

    /// <summary>Minutes without a new share, for the alarm text.</summary>
    public int MinutesWithoutShares(DateTime now) => (int)(now - (_sharesChangedAt ?? now)).TotalMinutes;

    public void Update(MinerSystemInfo info, DateTime now, bool primaryReachable = false, bool restartExpected = false)
    {
        UpdateOverheat(info);
        UpdateFlatline(info, now);
        UpdateFallback(info, now, primaryReachable);
        RestartedUnexpectedly = _lastUptime is { } last && info.UptimeSeconds + UptimeToleranceSeconds < last && !restartExpected;
        _lastUptime = info.UptimeSeconds;
    }

    private void UpdateFallback(MinerSystemInfo info, DateTime now, bool primaryReachable)
    {
        if (!info.IsUsingFallbackStratum || info.IsDualPoolMining)
        {
            _fallbackSince = null;
            _primaryUpSince = null;
            IsOnFallback = false;
            IsStuckOnFallback = false;
            return;
        }
        // Right after a start any miner may try the fallback pool, and a paused one proves nothing.
        if (info.UptimeSeconds < StartupGraceSeconds || info.MiningPaused)
        {
            _fallbackSince = null;
            _primaryUpSince = null;
            IsStuckOnFallback = false;
            return;
        }
        _fallbackSince ??= now;
        _primaryUpSince = primaryReachable ? _primaryUpSince ?? now : null;
        IsOnFallback = IsOnFallback || now - _fallbackSince.Value >= FallbackAlarm;
        IsStuckOnFallback = IsOnFallback && _primaryUpSince is { } up && now - up >= StuckOnFallback;
    }

    /// <summary>What the watchdog should do now, if the user allowed it. Never with an overheated miner.</summary>
    public AutoRestartReason? DueAutoRestart(bool onFlatline, bool toPrimary, DateTime? lastRestart, DateTime now)
    {
        if (IsOverheating) return null;
        if (lastRestart is { } last && now - last < AutoRestartInterval) return null;
        if (onFlatline && IsFlatlining) return AutoRestartReason.Flatline;
        if (toPrimary && IsStuckOnFallback) return AutoRestartReason.StuckOnFallback;
        return null;
    }

    private void UpdateOverheat(MinerSystemInfo info)
    {
        var hot = info.OverheatMode;
        if (info.OverheatTemp is > 0 and var limit)
            hot = hot || info.Temp >= limit - OverheatMargin || (info.VrTemp ?? 0) > limit;
        _hotPolls = hot ? _hotPolls + 1 : 0;
        _coolPolls = hot ? 0 : _coolPolls + 1;
        if (info.OverheatMode || _hotPolls >= OverheatPolls) IsOverheating = true;
        else if (_coolPolls >= OverheatPolls) IsOverheating = false;
    }

    private void UpdateFlatline(MinerSystemInfo info, DateTime now)
    {
        var rate = info.HashRate1m ?? 0;
        if (rate != _hashRate)
        {
            _hashRate = rate;
            _hashRateChangedAt = now;
            _samePolls = 0;
        }
        else
        {
            _samePolls++;
        }

        var stall = now - (_sharesChangedAt ?? now);
        if (stall > _peakStall) _peakStall = stall;
        var restarting = info.UptimeSeconds < StartupGraceSeconds;
        if (info.SharesAccepted != _shares || restarting || info.MiningPaused || rate <= 0)
        {
            _shares = info.SharesAccepted;
            _sharesChangedAt = now;
            IsFlatlining = false;
            return;
        }

        var stalled = now - (_sharesChangedAt ?? now) >= FlatlineWindow(rate, info.ActivePoolDifficulty);
        var frozen = _samePolls >= FrozenPolls && now - (_hashRateChangedAt ?? now) >= FrozenDuration;
        IsFlatlining = stalled && frozen;
    }

    /// <summary>The measurements behind both alarms, once every <see cref="SnapshotInterval"/>: what thresholds are judged by.</summary>
    public string? Snapshot(MinerSystemInfo info, DateTime now)
    {
        if (_lastSnapshotAt is { } last && now - last < SnapshotInterval) return null;
        _lastSnapshotAt = now;
        var rate = info.HashRate1m ?? 0;
        var window = FlatlineWindow(rate, info.ActivePoolDifficulty);
        var line = string.Create(CultureInfo.InvariantCulture,
            $"temp={info.Temp} vr={Number(info.VrTemp)} limit={Number(info.OverheatTemp)} flag={(info.OverheatMode ? 1 : 0)} paused={(info.MiningPaused ? 1 : 0)} rate1m={rate} shares={info.SharesAccepted} uptime={info.UptimeSeconds} diff={Number(info.ActivePoolDifficulty)} stallMin={MinutesWithoutShares(now)} peakStallMin={(int)_peakStall.TotalMinutes} windowMin={(int)window.TotalMinutes} frozenPolls={_samePolls} hotPolls={_hotPolls}");
        _peakStall = TimeSpan.Zero;
        return line;
    }

    private static string Number(double? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    /// <summary>How long a healthy miner can go without a share: <c>difficulty × 2³² / hashrate</c> per share on average.</summary>
    public static TimeSpan FlatlineWindow(double hashRateGHs, double? poolDifficulty)
    {
        if (poolDifficulty is not > 0 || hashRateGHs <= 0) return FlatlineMinimum;
        var seconds = poolDifficulty.Value * 4_294_967_296 / (hashRateGHs * 1e9);
        var window = TimeSpan.FromSeconds(FlatlineShareIntervals * seconds);
        return window > FlatlineMinimum ? window : FlatlineMinimum;
    }
}
