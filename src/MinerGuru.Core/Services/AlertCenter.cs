using System.Text.Json;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Services;

/// <summary>Kinds of alerts the user can switch on or off.</summary>
public enum AlertKind { Offline, Maintenance, Overheat, Flatline, Fallback, Restart, Payout }

public static class AlertKindText
{
    public static string Title(this AlertKind kind) => kind switch
    {
        AlertKind.Maintenance => L("Fällige Wartung", "Maintenance due"),
        AlertKind.Overheat => L("Überhitzung", "Overheating"),
        AlertKind.Flatline => L("Keine Shares mehr (Flatline)", "No more shares (flatline)"),
        AlertKind.Fallback => L("Fallback-Pool aktiv", "Fallback pool active"),
        AlertKind.Restart => L("Unerwarteter Neustart", "Unexpected restart"),
        AlertKind.Payout => L("Auszahlungsadresse", "Payout address"),
        _ => L("Miner offline / wieder online", "Miner offline / back online"),
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
                (AlertKind.Maintenance, _) => L($"{all.Count} Wartungen sind fällig", $"{all.Count} maintenance tasks are due"),
                (AlertKind.Overheat, false) => L($"{all.Count} Miner sind überhitzt", $"{all.Count} miners are overheating"),
                (AlertKind.Overheat, true) => L($"{all.Count} Miner sind wieder kühl", $"{all.Count} miners have cooled down"),
                (AlertKind.Flatline, false) => L($"{all.Count} Miner liefern keine Shares mehr", $"{all.Count} miners have stopped delivering shares"),
                (AlertKind.Fallback, false) => L($"{all.Count} Miner laufen auf dem Fallback-Pool", $"{all.Count} miners are on the fallback pool"),
                (AlertKind.Fallback, true) => L($"{all.Count} Miner sind zurück auf dem Primär-Pool", $"{all.Count} miners are back on the primary pool"),
                (AlertKind.Restart, _) => L($"{all.Count} Miner sind neu gestartet", $"{all.Count} miners restarted"),
                (AlertKind.Payout, _) => L($"{all.Count} Warnungen zur Auszahlungsadresse", $"{all.Count} payout address warnings"),
                (AlertKind.Flatline, true) => L($"{all.Count} Miner liefern wieder Shares", $"{all.Count} miners are delivering shares again"),
                (_, true) => L($"{all.Count} Miner sind wieder online", $"{all.Count} miners are back online"),
                _ => L($"{all.Count} Miner sind offline", $"{all.Count} miners are offline"),
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
                        L($"{miner.DisplayName} ist offline", $"{miner.DisplayName} is offline"),
                        L($"Keine Antwort seit {minutes} min. Vielleicht ohne Strom, außer WLAN-Reichweite oder im Hotspot-Modus.",
                          $"No answer for {minutes} min. It may have lost power, be out of Wi-Fi range or in hotspot mode."),
                        false, miner.DisplayName));
                }
            }
            else if (miner.State == ConnectionState.Online)
            {
                if (track.OfflineAlerted)
                    events.Add(new AlertEvent(AlertKind.Offline, miner.Id,
                        L($"{miner.DisplayName} ist wieder online", $"{miner.DisplayName} is back online"),
                        L("Der Miner antwortet wieder.", "The miner is answering again."),
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
            L($"{miner.DisplayName} wurde nicht neu gestartet", $"{miner.DisplayName} was not restarted"),
            L("Ein automatischer Neustart war fällig, aber „Nur lesen“ ist an.", "An automatic restart was due, but read-only mode is on."),
            false, miner.DisplayName, Alone: true);

    /// <summary>The result of a watchdog restart, as a message. Noted in the change log like any other write.</summary>
    public static AlertEvent RestartEvent(MinerDevice miner, MinerHealth.AutoRestartReason reason, string? error)
    {
        var name = miner.DisplayName;
        var why = reason == MinerHealth.AutoRestartReason.Flatline
            ? L("Keine Shares mehr.", "No more shares.")
            : L("Der Primär-Pool ist wieder erreichbar, der Miner blieb aber auf dem Fallback-Pool.", "The primary pool is reachable again, but the miner stayed on the fallback pool.");
        var kind = RestartKind(reason);
        return error == null
            ? new AlertEvent(kind, miner.Id, L($"{name} wurde automatisch neu gestartet", $"{name} was restarted automatically"), why, false, name, Alone: true)
            : new AlertEvent(kind, miner.Id, L($"{name} ließ sich nicht neu starten", $"{name} could not be restarted"), $"{why} {error}", false, name, Alone: true);
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
                    L($"Adresse von {name} wurde geändert", $"Address of {name} was changed"),
                    L($"{slot.Title}: {old} → {fresh}. Das kam nicht von MinerGuru.", $"{slot.Title}: {old} → {fresh}. This did not come from MinerGuru."),
                    false, name));
            }
            else if (slot.Verdict.NeedsAttention() && alerted.GetValueOrDefault(slot.Key) != slot.Address)
            {
                alerted[slot.Key] = slot.Address;
                changed = true;
                var address = PayoutCheck.Shortened(slot.Address);
                events.Add(new AlertEvent(AlertKind.Payout, miner.Id,
                    slot.Verdict == PayoutVerdict.FactoryDefault
                        ? L($"{name} nutzt eine Standard-Adresse", $"{name} uses a default address")
                        : L($"{name} zahlt an eine fremde Adresse", $"{name} pays to a foreign address"),
                    L($"{slot.Title}: {address}. Ein Fund ginge nicht an dich.", $"{slot.Title}: {address}. A block found would not go to you."),
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
                L($"Block zahlt nicht an {name}", $"Block does not pay {name}"),
                L("Die Block-Vorlage des Pools enthält die Adresse des Miners nicht. Prüfe die Pool-Einstellungen.",
                  "The pool's block template does not contain the miner's address. Check the pool settings."),
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
                    L($"{name} ist wieder kühl", $"{name} has cooled down"),
                    L("Die Temperatur ist wieder im normalen Bereich.", "The temperature is back to normal."),
                    true, name));
            }
            else if (due)
            {
                health.OverheatAlerted = true;
                health.LastOverheatAlertAt = now;
                var (temp, vr, limit) = ((int)info.Temp, (int)(info.VrTemp ?? 0), (int)(info.OverheatTemp ?? 0));
                events.Add(new AlertEvent(AlertKind.Overheat, miner.Id,
                    L($"{name} ist überhitzt", $"{name} is overheating"),
                    info.OverheatMode
                        ? L("Der Miner hat sich gedrosselt und bleibt so bis zum Neustart. Prüfe vorher die Kühlung.",
                            "The miner has throttled itself and stays that way until it restarts. Check the cooling first.")
                        : L($"ASIC {temp} °C, Spannungsregler {vr} °C, Grenze {limit} °C. Prüfe die Kühlung.",
                            $"ASIC {temp} °C, voltage regulator {vr} °C, limit {limit} °C. Check the cooling."),
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
                    L($"{name} liefert wieder Shares", $"{name} is delivering shares again"),
                    L("Es kommen wieder neue Shares an.", "New shares are arriving again."),
                    true, name));
            }
            else if (due)
            {
                health.FlatlineAlerted = true;
                health.LastFlatlineAlertAt = now;
                var minutes = health.MinutesWithoutShares(now);
                events.Add(new AlertEvent(AlertKind.Flatline, miner.Id,
                    L($"{name} liefert keine Shares mehr", $"{name} has stopped delivering shares"),
                    L($"Seit {minutes} min kein neuer Share, obwohl der Miner antwortet. Ein Neustart hilft meist.",
                      $"No new share for {minutes} min although the miner responds. A restart usually helps."),
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
                    L($"{name} ist zurück auf dem Primär-Pool", $"{name} is back on the primary pool"),
                    L("Der Miner nutzt wieder seinen Primär-Pool.", "The miner is using its primary pool again."),
                    true, name));
            }
            else if (due)
            {
                health.FallbackAlerted = true;
                health.LastFallbackAlertAt = now;
                events.Add(new AlertEvent(AlertKind.Fallback, miner.Id,
                    L($"{name} läuft auf dem Fallback-Pool", $"{name} is on the fallback pool"),
                    L($"Seit mindestens 10 min auf {info.ActivePoolHost}. Der Primär-Pool ist vielleicht gestört.",
                      $"On {info.ActivePoolHost} for at least 10 min. The primary pool may be down."),
                    false, name));
            }
        }

        if (health.IsStuckOnFallback && !health.StuckAlerted)
        {
            health.StuckAlerted = true;
            events.Add(new AlertEvent(AlertKind.Fallback, miner.Id,
                L($"{name} wechselt nicht zurück", $"{name} does not switch back"),
                L("Der Primär-Pool ist seit 15 min erreichbar, der Miner bleibt aber auf dem Fallback-Pool. Ein Neustart holt ihn zurück.",
                  "The primary pool has been reachable for 15 min, but the miner stays on the fallback pool. A restart brings it back."),
                false, name));
        }
        else if (!health.IsOnFallback)
        {
            health.StuckAlerted = false;
        }

        if (health.RestartedUnexpectedly)
        {
            var reason = info.ResetReason is { } text
                ? L($"Grund laut Miner: {text}.", $"Reason given by the miner: {text}.")
                : L("Der Miner nennt keinen Grund.", "The miner gives no reason.");
            events.Add(new AlertEvent(AlertKind.Restart, miner.Id,
                L($"{name} ist neu gestartet", $"{name} restarted"),
                L($"MinerGuru hat das nicht ausgelöst. {reason}", $"MinerGuru did not trigger this. {reason}"),
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
