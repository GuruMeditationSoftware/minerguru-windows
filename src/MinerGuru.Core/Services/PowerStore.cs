using System.Text.Json;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Services;

/// <summary>Everything in <c>power.json</c>.</summary>
public sealed class StoredPower
{
    public PowerTariff Tariff { get; set; } = new();
    public List<ScheduleRule> Rules { get; set; } = [];
    public bool RunsSchedules { get; set; } = true;
    public int NightFanPercent { get; set; } = 40;
    public Dictionary<Guid, MinerPowerState> States { get; set; } = [];
}

/// <summary>
/// Tariff, schedules and their execution, in <c>power.json</c> next to the settings. After every refresh round it checks
/// which action the rules ask for and switches the miners that are not in that state yet, at most once per
/// <see cref="MinimumSwitchInterval"/>. Used on the UI thread, like <see cref="MinerStore"/>.
/// </summary>
public sealed class PowerStore
{
    public static readonly TimeSpan MinimumSwitchInterval = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly StoredPower _stored = new();
    private readonly MinerApiClient _api;
    private readonly OtpSessions _otp;
    private readonly Func<IEnumerable<MinerDevice>> _onlineMiners;
    private readonly Func<DateTime> _clock;
    private readonly HashSet<Guid> _switching = [];

    public PowerStore(string dataDirectory, MinerApiClient api, Func<IEnumerable<MinerDevice>> onlineMiners, Func<DateTime>? clock = null, OtpSessions? otp = null)
    {
        _path = Path.Combine(dataDirectory, "power.json");
        _api = api;
        _otp = otp ?? new OtpSessions(dataDirectory, api.CreateOtpSessionAsync);
        _onlineMiners = onlineMiners;
        _clock = clock ?? (() => DateTime.Now);
        try
        {
            if (File.Exists(_path)) _stored = JsonSerializer.Deserialize<StoredPower>(File.ReadAllText(_path)) ?? _stored;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // Unreadable: start with the defaults; the next save replaces the file.
        }
    }

    public PowerTariff Tariff => _stored.Tariff;
    public IReadOnlyList<ScheduleRule> Rules => _stored.Rules;
    public string? LastError { get; private set; }

    /// <summary>The check started by the last change of tariff or rules.</summary>
    public Task Evaluation { get; private set; } = Task.CompletedTask;

    /// <summary>Tariff, rules or a miner's state changed.</summary>
    public event Action? Changed;

    /// <summary>Only one device should switch the miners, otherwise it and the Mac work against each other.</summary>
    public bool RunsSchedules
    {
        get => _stored.RunsSchedules;
        set { _stored.RunsSchedules = value; RulesChanged(); }
    }

    /// <summary>Fixed fan speed of the night mode, in percent.</summary>
    public int NightFanPercent
    {
        get => _stored.NightFanPercent;
        set { _stored.NightFanPercent = Math.Clamp(value, 20, 100); Commit(); }
    }

    // Tariff and rules

    public string CurrencyCode
    {
        get => Tariff.CurrencyCode;
        set { Tariff.CurrencyCode = value; Commit(); }
    }

    public double BasePricePerKwh
    {
        get => Tariff.BasePricePerKwh;
        set { Tariff.BasePricePerKwh = value; Commit(); }
    }

    public TariffZone? Zone(Guid id) => Tariff.Zones.FirstOrDefault(z => z.Id == id);

    /// <summary>Replaces the zone with the same id or adds a new one.</summary>
    public void SaveZone(TariffZone zone)
    {
        Upsert(Tariff.Zones, zone, z => z.Id == zone.Id);
        RulesChanged();
    }

    public void AddZones(params TariffZone[] zones)
    {
        Tariff.Zones.AddRange(zones);
        RulesChanged();
    }

    public void RemoveZone(Guid id)
    {
        Tariff.Zones.RemoveAll(z => z.Id == id);
        // A rule without its zone would never apply again.
        _stored.Rules.RemoveAll(r => r.Trigger.ZoneId == id);
        RulesChanged();
    }

    /// <summary>One step up: the upper zone wins where zones overlap.</summary>
    public void MoveZoneUp(int index)
    {
        if (MoveUp(Tariff.Zones, index)) RulesChanged();
    }

    public void SaveRule(ScheduleRule rule)
    {
        Upsert(_stored.Rules, rule, r => r.Id == rule.Id);
        RulesChanged();
    }

    public void RemoveRule(Guid id)
    {
        _stored.Rules.RemoveAll(r => r.Id == id);
        RulesChanged();
    }

    /// <summary>One step up: the upper rule wins when several apply at once.</summary>
    public void MoveRuleUp(int index)
    {
        if (MoveUp(_stored.Rules, index)) RulesChanged();
    }

    public void SetRuleEnabled(Guid id, bool enabled)
    {
        if (_stored.Rules.FirstOrDefault(r => r.Id == id) is not { } rule) return;
        rule.IsEnabled = enabled;
        RulesChanged();
    }

    /// <summary>A rule keeps its last miner: an empty set would mean all miners. It then applies to none and shows "Miner entfernt".</summary>
    public void Forget(Guid minerId)
    {
        _stored.States.Remove(minerId);
        foreach (var rule in _stored.Rules.Where(r => r.MinerIds.Count > 1)) rule.MinerIds.Remove(minerId);
        Commit();
    }

    private static void Upsert<T>(List<T> list, T item, Predicate<T> same)
    {
        var index = list.FindIndex(same);
        if (index >= 0) list[index] = item;
        else list.Add(item);
    }

    private static bool MoveUp<T>(List<T> list, int index)
    {
        if (index <= 0 || index >= list.Count) return false;
        (list[index - 1], list[index]) = (list[index], list[index - 1]);
        return true;
    }

    public PowerAction State(Guid minerId) => _stored.States.GetValueOrDefault(minerId)?.Action ?? PowerAction.Normal;

    public PowerAction PlannedAction(Guid minerId, DateTime date) => PowerSchedule.Action(minerId, date, Rules, Tariff);

    /// <summary>The action really used for a miner: pause becomes night mode where the firmware cannot pause (unknown while offline).</summary>
    public static PowerAction Effective(PowerAction action, MinerDevice miner) =>
        action == PowerAction.Pause && miner.LatestInfo != null && !miner.CanPause ? PowerAction.NightMode : action;

    // Costs

    /// <summary>Cost per hour at the current draw and price.</summary>
    public double CostPerHour(double watts, DateTime date) => watts / 1000 * Tariff.Price(date);

    /// <summary>Cost of the next hours at the current draw, with the prices and pauses of the plan.</summary>
    public double ProjectedCost(MinerDevice miner, double hours, DateTime start) =>
        PowerSchedule.Cost(miner.LatestInfo?.Power ?? 0, start, hours, Tariff, Rules, miner.Id, miner.CanPause);

    /// <summary>Cost of today, from midnight to midnight, at the current draw.</summary>
    public double CostToday(MinerDevice miner, DateTime now) => ProjectedCost(miner, 24, now.Date);

    // Execution

    /// <summary>Switches every online miner whose state differs from what the rules ask for now.</summary>
    public async Task EvaluateAsync()
    {
        if (!RunsSchedules) return;
        var date = _clock();
        foreach (var miner in _onlineMiners().ToList())
        {
            var wanted = Effective(PlannedAction(miner.Id, date), miner);
            var current = _stored.States.GetValueOrDefault(miner.Id);
            if ((current?.Action ?? PowerAction.Normal) == wanted || _switching.Contains(miner.Id)) continue;
            if (current != null && date - current.Since < MinimumSwitchInterval) continue;
            // No request without a session: repeated refusals would lock the miner.
            if (!_otp.CanWriteUnattended(miner)) continue;
            await SwitchMinerAsync(miner, wanted, date);
        }
    }

    private async Task SwitchMinerAsync(MinerDevice miner, PowerAction action, DateTime date)
    {
        if (miner.LatestInfo is not { } info) return;
        _switching.Add(miner.Id);
        var previous = _stored.States.GetValueOrDefault(miner.Id);
        var host = miner.IpAddress;
        try
        {
            // Leave the old state first.
            if (previous?.Action == PowerAction.Pause) await _api.ResumeAsync(host);
            var saved = previous?.Saved;
            switch (action)
            {
                case PowerAction.Normal:
                    if (saved != null) await ApplySettingsAsync(saved.Payload, miner);
                    saved = null;
                    break;
                case PowerAction.NightMode:
                    saved ??= SavedMinerSettings.From(info);
                    await ApplySettingsAsync(NightPayload(miner, saved), miner);
                    break;
                case PowerAction.Pause:
                    // Night values stay until Normal: restoring them restarts AxeOS, and the restart would end the pause.
                    await _api.PauseAsync(host);
                    break;
            }
            _stored.States[miner.Id] = new MinerPowerState { Action = action, Since = date, Saved = saved };
            LastError = null;
        }
        catch (MinerApiException e)
        {
            LastError = $"{miner.DisplayName}: {e.Message}";
        }
        finally
        {
            _switching.Remove(miner.Id);
            Commit();
        }
    }

    /// <summary>Eco clock and the fixed night fan speed.</summary>
    private MinerSystemUpdatePayload NightPayload(MinerDevice miner, SavedMinerSettings saved)
    {
        var eco = miner.RecommendedPresets.FirstOrDefault(p => p.Name == "Eco");
        return new MinerSystemUpdatePayload
        {
            Frequency = Math.Min(eco?.FrequencyMhz ?? saved.FrequencyMhz, saved.FrequencyMhz),
            CoreVoltage = Math.Min(eco?.CoreVoltageMv ?? saved.CoreVoltageMv, saved.CoreVoltageMv),
            AutoFanSpeed = 0,
            FanSpeed = NightFanPercent,
        };
    }

    /// <summary>AxeOS uses new clock settings only after a reboot, NerdOS at once.</summary>
    private async Task ApplySettingsAsync(MinerSystemUpdatePayload payload, MinerDevice miner)
    {
        await _otp.PerformAsync(miner, interactive: false, token => _api.UpdateSystemSettingsAsync(miner.IpAddress, payload, token));
        if (!miner.IsNerdOs) await _api.RestartAsync(miner.IpAddress);
    }

    private void RulesChanged()
    {
        Commit();
        Evaluation = EvaluateAsync();
    }

    private void Commit()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_stored, Options)); }
        catch (IOException) { LastError = L("Strom-Einstellungen nicht speicherbar.", "Could not save the power settings."); }
        Changed?.Invoke();
    }
}
