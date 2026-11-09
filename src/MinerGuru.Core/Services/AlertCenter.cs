using System.Text.Json;
using MinerGuru.Core.Models;

namespace MinerGuru.Core.Services;

/// <summary>Kinds of alerts the user can switch on or off.</summary>
public enum AlertKind { Offline, Maintenance, Overheat, Flatline, Fallback, Restart, Payout }

public static class AlertKindText
{
    public static string Title(this AlertKind kind) => kind switch
    {
        AlertKind.Maintenance => Strings.MaintenanceDue,
        AlertKind.Overheat => Strings.Overheating,
        AlertKind.Flatline => Strings.NoMoreSharesFlatline,
        AlertKind.Fallback => Strings.FallbackPoolActive,
        AlertKind.Restart => Strings.UnexpectedRestart,
        AlertKind.Payout => Strings.PayoutAddress,
        _ => Strings.MinerOfflineBackOnline,
    };
}

/// <summary>One message for the user. Only alarms, never news or advertising.</summary>
/// <param name="Alone">Says what happened to one miner, so it is never folded into a message about several.</param>
public sealed record AlertEvent(AlertKind Kind, Guid MinerId, string Title, string Body, bool IsRecovery, string MinerName = "", bool Alone = false);

/// <summary>Delivers alerts. The app uses Windows notifications; tests use a fake.</summary>
public interface IAlertNotifier
{
    void Deliver(AlertEvent alert);
}

/// <summary>Everything in <c>alerts.json</c>.</summary>
public sealed class StoredAlerts
{
    public bool IsEnabled { get; set; }
    public HashSet<Guid> MutedMiners { get; set; } = [];
    public HashSet<AlertKind> DisabledKinds { get; set; } = [];
    /// <summary>Off by default: restart a miner without shares, at most once an hour.</summary>
    public bool RestartsOnFlatline { get; set; }
    /// <summary>Off by default: restart a miner that stays on the fallback pool although the primary one answers.</summary>
    public bool RestartsToPrimary { get; set; }
    /// <summary>The address each coin should pay to, by symbol.</summary>
    public Dictionary<string, string> ExpectedAddresses { get; set; } = [];
    /// <summary>The address last seen in each slot, to notice changes made elsewhere.</summary>
    public Dictionary<Guid, Dictionary<string, string>> SeenAddresses { get; set; } = [];
    /// <summary>The address each slot was last warned about.</summary>
    public Dictionary<Guid, Dictionary<string, string>> AlertedAddresses { get; set; } = [];
}

/// <summary>Turns poll results into alerts: debounced, per miner and per kind mutable. Used on the UI thread.</summary>
public sealed class AlertCenter
{
    /// <summary>A miner counts as offline after this many failed polls in a row …</summary>
    public const int OfflineFailedPolls = 3;
    /// <summary>… that also span at least this long (a manual restart takes 10–20 s).</summary>
    public static readonly TimeSpan OfflineMinimumDuration = TimeSpan.FromSeconds(90);
    /// <summary>The same alarm for the same miner at most this often.</summary>
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(30);
    /// <summary>A changed address this soon after a write from MinerGuru was the user's own change.</summary>
    public static readonly TimeSpan OwnChangeWindow = TimeSpan.FromMinutes(15);
    /// <summary>Restarts this soon after one from MinerGuru are expected.</summary>
    public static readonly TimeSpan OwnRestartWindow = TimeSpan.FromMinutes(10);
    /// <summary>The miner has to report a block without its address this many polls in a row.</summary>
    public const int PayoutMissPolls = 3;

    private sealed class MinerTrack
    {
        public int FailedPolls;
        public DateTime? FirstFailureAt;
        /// <summary>An offline alarm went out and the all-clear is still due.</summary>
        public bool OfflineAlerted;
        public DateTime? LastOfflineAlertAt;
        public readonly MinerHealth Health = new();
        public bool? WasOnline;
        public int PayoutMissPolls;
        public bool PayoutMissAlerted;
        /// <summary>A due restart was left out because of read-only mode; said once an hour at most.</summary>
        public DateTime? LastSkippedRestartAt;
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly StoredAlerts _stored = new();
    private readonly Dictionary<Guid, MinerTrack> _tracks = [];
    private readonly ChangeLog _log;

    public AlertCenter(string dataDirectory, ChangeLog? log = null)
    {
        _log = log ?? ChangeLog.Shared;
        _path = Path.Combine(dataDirectory, "alerts.json");
        try
        {
            if (File.Exists(_path)) _stored = JsonSerializer.Deserialize<StoredAlerts>(File.ReadAllText(_path)) ?? _stored;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // Unreadable: start with the defaults; the next save replaces the file.
        }
    }

    /// <summary>Set by the app once Windows notifications are ready.</summary>
    public IAlertNotifier? Notifier { get; set; }

    public bool IsEnabled
    {
        get => _stored.IsEnabled;
        set { _stored.IsEnabled = value; Save(); }
    }

    public bool RestartsOnFlatline
    {
        get => _stored.RestartsOnFlatline;
        set { _stored.RestartsOnFlatline = value; Save(); }
    }

    public bool RestartsToPrimary
    {
        get => _stored.RestartsToPrimary;
        set { _stored.RestartsToPrimary = value; Save(); }
    }

    public IReadOnlyDictionary<string, string> ExpectedAddresses => _stored.ExpectedAddresses;

    /// <summary>An empty address removes the coin. A new target makes earlier warnings worth repeating.</summary>
    public void SetExpectedAddress(MinedCoin coin, string address)
    {
        var trimmed = address.Trim();
        if (trimmed.Length == 0) _stored.ExpectedAddresses.Remove(coin.Symbol());
        else _stored.ExpectedAddresses[coin.Symbol()] = trimmed;
        _stored.AlertedAddresses.Clear();
        Save();
    }

    /// <summary>Restarts the last <see cref="Evaluate"/> found due; <see cref="MinerStore"/> carries them out.</summary>
    public List<(MinerDevice Miner, MinerHealth.AutoRestartReason Reason)> DueRestarts { get; private set; } = [];

    public bool IsMuted(Guid minerId) => _stored.MutedMiners.Contains(minerId);

    public void SetMuted(bool muted, Guid minerId)
    {
        if (muted) _stored.MutedMiners.Add(minerId);
        else _stored.MutedMiners.Remove(minerId);
        Save();
    }

    public bool IsKindEnabled(AlertKind kind) => !_stored.DisabledKinds.Contains(kind);

    public void SetKind(AlertKind kind, bool enabled)
    {
        if (enabled) _stored.DisabledKinds.Remove(kind);
        else _stored.DisabledKinds.Add(kind);
        Save();
    }

    public void Forget(Guid minerId)
    {
        _tracks.Remove(minerId);
        _stored.SeenAddresses.Remove(minerId);
        _stored.AlertedAddresses.Remove(minerId);
        if (IsMuted(minerId)) SetMuted(false, minerId);
    }

    /// <summary>
    /// One message per direction for the alerts of one poll, for a notifier that shows only one at a time.
    /// A combined message has no single miner: its <see cref="AlertEvent.MinerId"/> is empty.
    /// </summary>
    public static List<AlertEvent> Combine(IEnumerable<AlertEvent> alerts) =>
        alerts.Where(a => !a.Alone).GroupBy(a => (a.Kind, a.IsRecovery)).Select(group =>
        {
            var all = group.ToList();
            if (all.Count == 1) return all[0];
            var title = group.Key switch
            {
                (AlertKind.Maintenance, _) => string.Format(Strings.MaintenanceTasksAreDue, all.Count),
                (AlertKind.Overheat, false) => string.Format(Strings.MinersAreOverheating, all.Count),
                (AlertKind.Overheat, true) => string.Format(Strings.MinersHaveCooledDown, all.Count),
                (AlertKind.Flatline, false) => string.Format(Strings.MinersHaveStoppedDeliveringShares, all.Count),
                (AlertKind.Fallback, false) => string.Format(Strings.MinersAreOnTheFallback, all.Count),
                (AlertKind.Fallback, true) => string.Format(Strings.MinersAreBackOnThe, all.Count),
                (AlertKind.Restart, _) => string.Format(Strings.MinersRestarted, all.Count),
                (AlertKind.Payout, _) => string.Format(Strings.PayoutAddressWarnings, all.Count),
                (AlertKind.Flatline, true) => string.Format(Strings.MinersAreDeliveringSharesAgain, all.Count),
                (_, true) => string.Format(Strings.MinersAreBackOnline, all.Count),
                _ => string.Format(Strings.MinersAreOffline, all.Count),
            };
            return new AlertEvent(all[0].Kind, Guid.Empty, title, string.Join(", ", all.Select(a => a.MinerName).Distinct()), group.Key.IsRecovery);
        }).Concat(alerts.Where(a => a.Alone)).ToList();

    /// <summary>Call once per fleet poll. Returns the alerts that were sent.</summary>
    public List<AlertEvent> Evaluate(IEnumerable<MinerDevice> miners, DateTime now)
    {
        var events = new List<AlertEvent>();
        var restarts = new List<(MinerDevice, MinerHealth.AutoRestartReason)>();
        foreach (var miner in miners)
        {
            if (!_tracks.TryGetValue(miner.Id, out var track)) _tracks[miner.Id] = track = new MinerTrack();
            LogState(miner, track, now);
            if (miner.State == ConnectionState.Offline)
            {
                track.FailedPolls++;
                track.FirstFailureAt ??= now;
                var down = now - track.FirstFailureAt.Value;
                var repeatDue = track.LastOfflineAlertAt is not { } last || now - last >= RepeatInterval;
                if (!track.OfflineAlerted && track.FailedPolls >= OfflineFailedPolls && down >= OfflineMinimumDuration && repeatDue)
                {
                    track.OfflineAlerted = true;
                    track.LastOfflineAlertAt = now;
                    var minutes = (int)down.TotalMinutes;
                    events.Add(new AlertEvent(AlertKind.Offline, miner.Id,
                        string.Format(Strings.IsOffline, miner.DisplayName),
                        string.Format(Strings.NoAnswerForMinIt, minutes),
                        false, miner.DisplayName));
                }
            }
            else if (miner.State == ConnectionState.Online)
            {
                if (track.OfflineAlerted)
                    events.Add(new AlertEvent(AlertKind.Offline, miner.Id,
                        string.Format(Strings.IsBackOnline, miner.DisplayName),
                        Strings.TheMinerIsAnsweringAgain,
                        true, miner.DisplayName));
                track.FailedPolls = 0;
                track.FirstFailureAt = null;
                track.OfflineAlerted = false;
                if (miner.LatestInfo is { } info)
                {
                    var restartExpected = _log.LastRestart(miner.IpAddress) is { } own && now - own < OwnRestartWindow;
                    track.Health.Update(info, now, miner.PrimaryPoolReachable, restartExpected);
                    events.AddRange(HealthEvents(miner, info, track.Health, now));
                    events.AddRange(PayoutEvents(miner, info, track, now));
                    LogHealth(miner, info, track.Health, now);
                    var lastAutomatic = _log.LastAutomaticRestart(miner.IpAddress);
                    var lastRestart = track.LastSkippedRestartAt is { } skipped && (lastAutomatic == null || skipped > lastAutomatic) ? skipped : lastAutomatic;
                    if (track.Health.DueAutoRestart(RestartsOnFlatline, RestartsToPrimary, lastRestart, now) is { } reason)
                    {
                        if (_log.IsReadOnly)
                        {
                            track.LastSkippedRestartAt = now;
                            events.Add(SkippedRestartEvent(miner, reason));
                        }
                        else restarts.Add((miner, reason));
                    }
                }
            }
        }

        DueRestarts = restarts;
        // The restart message names the same problem: one message, not two at once.
        events.RemoveAll(e => !e.IsRecovery && restarts.Any(r => r.Item1.Id == e.MinerId && RestartKind(r.Item2) == e.Kind));
        var sent = Send(events);
        foreach (var e in events)
        {
            var address = miners.FirstOrDefault(m => m.Id == e.MinerId)?.IpAddress ?? "?";
            DiagnosticLog.Shared.Write("alert", $"{address} {e.Kind.ToString().ToLowerInvariant()} {(e.IsRecovery ? "cleared" : "raised")} delivered={(sent.Contains(e) ? 1 : 0)}", now);
        }
        return sent;
    }

    private static AlertKind RestartKind(MinerHealth.AutoRestartReason reason) =>
        reason == MinerHealth.AutoRestartReason.Flatline ? AlertKind.Flatline : AlertKind.Fallback;

    /// <summary>Read-only mode wins over the watchdog, but the user should know a restart was left out.</summary>
    private static AlertEvent SkippedRestartEvent(MinerDevice miner, MinerHealth.AutoRestartReason reason) =>
        new(RestartKind(reason), miner.Id,
            string.Format(Strings.WasNotRestarted, miner.DisplayName),
            Strings.AnAutomaticRestartWasDue,
            false, miner.DisplayName, Alone: true);

    /// <summary>The result of a watchdog restart, as a message. Noted in the change log like any other write.</summary>
    public static AlertEvent RestartEvent(MinerDevice miner, MinerHealth.AutoRestartReason reason, string? error)
    {
        var name = miner.DisplayName;
        var why = reason == MinerHealth.AutoRestartReason.Flatline
            ? Strings.NoMoreShares
            : Strings.ThePrimaryPoolIsReachable;
        var kind = RestartKind(reason);
        return error == null
            ? new AlertEvent(kind, miner.Id, string.Format(Strings.WasRestartedAutomatically, name), why, false, name, Alone: true)
            : new AlertEvent(kind, miner.Id, string.Format(Strings.CouldNotBeRestarted, name), $"{why} {error}", false, name, Alone: true);
    }

    /// <summary>Default or foreign addresses, addresses changed outside MinerGuru and blocks that skip the miner.</summary>
    private List<AlertEvent> PayoutEvents(MinerDevice miner, MinerSystemInfo info, MinerTrack track, DateTime now)
    {
        var events = new List<AlertEvent>();
        var name = miner.DisplayName;
        var ownChange = _log.LastWrite(miner.IpAddress) is { } own && now - own < OwnChangeWindow;
        var seen = _stored.SeenAddresses.GetValueOrDefault(miner.Id) ?? [];
        var alerted = _stored.AlertedAddresses.GetValueOrDefault(miner.Id) ?? [];
        var changed = false;
        foreach (var slot in PayoutCheck.Slots(info, ExpectedAddresses).Where(s => s.Address.Length > 0))
        {
            var previous = seen.GetValueOrDefault(slot.Key);
            changed |= previous != slot.Address;
            seen[slot.Key] = slot.Address;
            if (previous != null && previous != slot.Address)
            {
                alerted[slot.Key] = slot.Address;
                if (ownChange) continue;
                var (old, fresh) = (PayoutCheck.Shortened(previous), PayoutCheck.Shortened(slot.Address));
                events.Add(new AlertEvent(AlertKind.Payout, miner.Id,
                    string.Format(Strings.AddressOfWasChanged, name),
                    string.Format(Strings.ThisDidNotComeFrom, slot.Title, old, fresh),
                    false, name));
            }
            else if (slot.Verdict.NeedsAttention() && alerted.GetValueOrDefault(slot.Key) != slot.Address)
            {
                alerted[slot.Key] = slot.Address;
                changed = true;
                var address = PayoutCheck.Shortened(slot.Address);
                events.Add(new AlertEvent(AlertKind.Payout, miner.Id,
                    slot.Verdict == PayoutVerdict.FactoryDefault
                        ? string.Format(Strings.UsesADefaultAddress, name)
                        : string.Format(Strings.PaysToAForeignAddress, name),
                    string.Format(Strings.ABlockFoundWouldNot, slot.Title, address),
                    false, name));
            }
        }
        if (changed)
        {
            _stored.SeenAddresses[miner.Id] = seen;
            _stored.AlertedAddresses[miner.Id] = alerted;
            Save();
        }

        track.PayoutMissPolls = PayoutCheck.BlockSkipsMiner(info) ? track.PayoutMissPolls + 1 : 0;
        if (track.PayoutMissPolls == 0)
        {
            track.PayoutMissAlerted = false;
        }
        else if (track.PayoutMissPolls >= PayoutMissPolls && !track.PayoutMissAlerted)
        {
            track.PayoutMissAlerted = true;
            events.Add(new AlertEvent(AlertKind.Payout, miner.Id,
                string.Format(Strings.BlockDoesNotPay, name),
                Strings.ThePoolSBlockTemplate,
                false, name));
        }
        return events;
    }

    private static void LogState(MinerDevice miner, MinerTrack track, DateTime now)
    {
        if (miner.State == ConnectionState.Offline && track.WasOnline != false)
        {
            track.WasOnline = false;
            DiagnosticLog.Shared.Write("state", $"{miner.IpAddress} offline: {miner.OfflineReason}", now);
        }
        else if (miner.State == ConnectionState.Online && track.WasOnline != true)
        {
            track.WasOnline = true;
            DiagnosticLog.Shared.Write("state", $"{miner.IpAddress} online", now);
        }
    }

    private static void LogHealth(MinerDevice miner, MinerSystemInfo info, MinerHealth health, DateTime now)
    {
        if (health.Snapshot(info, now) is not { } snapshot) return;
        DiagnosticLog.Shared.Write("health", $"{miner.IpAddress} {snapshot}", now);
        var profile = DiagnosticLog.Profile(miner, info);
        if (profile == health.LastProfile) return;
        health.LastProfile = profile;
        DiagnosticLog.Shared.Write("miner", $"{miner.IpAddress} {profile}", now);
    }

    private static List<AlertEvent> HealthEvents(MinerDevice miner, MinerSystemInfo info, MinerHealth health, DateTime now)
    {
        var events = new List<AlertEvent>();
        var name = miner.DisplayName;
        if (health.IsOverheating != health.OverheatAlerted)
        {
            var due = health.LastOverheatAlertAt is not { } last || now - last >= RepeatInterval;
            if (health.OverheatAlerted)
            {
                health.OverheatAlerted = false;
                events.Add(new AlertEvent(AlertKind.Overheat, miner.Id,
                    string.Format(Strings.HasCooledDown, name),
                    Strings.TheTemperatureIsBackTo,
                    true, name));
            }
            else if (due)
            {
                health.OverheatAlerted = true;
                health.LastOverheatAlertAt = now;
                var (temp, vr, limit) = ((int)info.Temp, (int)(info.VrTemp ?? 0), (int)(info.OverheatTemp ?? 0));
                events.Add(new AlertEvent(AlertKind.Overheat, miner.Id,
                    string.Format(Strings.IsOverheating, name),
                    info.OverheatMode
                        ? Strings.TheMinerHasThrottledItself
                        : string.Format(Strings.ASICCVoltageRegulatorC, temp, vr, limit),
                    false, name));
            }
        }

        if (health.IsFlatlining != health.FlatlineAlerted)
        {
            var due = health.LastFlatlineAlertAt is not { } last || now - last >= RepeatInterval;
            if (health.FlatlineAlerted)
            {
                health.FlatlineAlerted = false;
                events.Add(new AlertEvent(AlertKind.Flatline, miner.Id,
                    string.Format(Strings.IsDeliveringSharesAgain, name),
                    Strings.NewSharesAreArrivingAgain,
                    true, name));
            }
            else if (due)
            {
                health.FlatlineAlerted = true;
                health.LastFlatlineAlertAt = now;
                var minutes = health.MinutesWithoutShares(now);
                events.Add(new AlertEvent(AlertKind.Flatline, miner.Id,
                    string.Format(Strings.HasStoppedDeliveringShares, name),
                    string.Format(Strings.NoNewShareForMin, minutes),
                    false, name));
            }
        }

        if (health.IsOnFallback != health.FallbackAlerted)
        {
            var due = health.LastFallbackAlertAt is not { } last || now - last >= RepeatInterval;
            if (health.FallbackAlerted)
            {
                health.FallbackAlerted = false;
                events.Add(new AlertEvent(AlertKind.Fallback, miner.Id,
                    string.Format(Strings.IsBackOnThePrimary, name),
                    Strings.TheMinerIsUsingIts,
                    true, name));
            }
            else if (due)
            {
                health.FallbackAlerted = true;
                health.LastFallbackAlertAt = now;
                events.Add(new AlertEvent(AlertKind.Fallback, miner.Id,
                    string.Format(Strings.IsOnTheFallbackPool, name),
                    string.Format(Strings.OnForAtLeast10, info.ActivePoolHost),
                    false, name));
            }
        }

        if (health.IsStuckOnFallback && !health.StuckAlerted)
        {
            health.StuckAlerted = true;
            events.Add(new AlertEvent(AlertKind.Fallback, miner.Id,
                string.Format(Strings.DoesNotSwitchBack, name),
                Strings.ThePrimaryPoolHasBeen,
                false, name));
        }
        else if (!health.IsOnFallback)
        {
            health.StuckAlerted = false;
        }

        if (health.RestartedUnexpectedly)
        {
            var reason = info.ResetReason is { } text
                ? string.Format(Strings.ReasonGivenByTheMiner, text)
                : Strings.TheMinerGivesNoReason;
            events.Add(new AlertEvent(AlertKind.Restart, miner.Id,
                string.Format(Strings.Restarted, name),
                string.Format(Strings.MinerGuruDidNotTriggerThis, reason),
                false, name));
        }

        return events;
    }

    /// <summary>Delivers what the settings allow. Returns the alerts that were sent.</summary>
    public List<AlertEvent> Send(IEnumerable<AlertEvent> events)
    {
        var deliverable = events.Where(e => IsEnabled && !IsMuted(e.MinerId) && IsKindEnabled(e.Kind)).ToList();
        foreach (var alert in deliverable) Notifier?.Deliver(alert);
        return deliverable;
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_stored, Options)); }
        catch (IOException) { /* stays in memory; the next change tries again */ }
    }
}
