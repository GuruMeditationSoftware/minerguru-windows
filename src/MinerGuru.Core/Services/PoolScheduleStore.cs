using System.Text.Json;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Services;

/// <summary>Everything in <c>pool-schedules.json</c>.</summary>
public sealed class StoredPoolSchedules
{
    public List<PoolScheduleRule> Rules { get; set; } = [];
    /// <summary>The last occurrence carried out, per <c>rule|miner</c>.</summary>
    public Dictionary<string, DateTime> Applied { get; set; } = [];
}

/// <summary>Scheduled pool switches and their execution. Used on the UI thread, like <see cref="MinerStore"/>.</summary>
public sealed class PoolScheduleStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly StoredPoolSchedules _stored = new();
    private readonly Func<Guid, PoolConfig?> _pool;
    private readonly Func<PoolSettingsForm, Guid, Task> _apply;
    private readonly Func<MinerDevice, bool> _canWrite;

    /// <param name="pool">Looks up a pool of the library.</param>
    /// <param name="apply">Sends pool settings to a miner and restarts it.</param>
    /// <param name="canWrite">Whether a miner can be written to without anyone at the screen.</param>
    public PoolScheduleStore(string dataDirectory, Func<Guid, PoolConfig?> pool, Func<PoolSettingsForm, Guid, Task> apply, Func<MinerDevice, bool>? canWrite = null)
    {
        _path = Path.Combine(dataDirectory, "pool-schedules.json");
        _pool = pool;
        _apply = apply;
        _canWrite = canWrite ?? (_ => true);
        try
        {
            if (File.Exists(_path)) _stored = JsonSerializer.Deserialize<StoredPoolSchedules>(File.ReadAllText(_path)) ?? _stored;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // Unreadable: start with the defaults; the next save replaces the file.
        }
    }

    public IReadOnlyList<PoolScheduleRule> Rules => _stored.Rules;
    /// <summary>Last failed switch, shown on the pools page until the next one succeeds.</summary>
    public string? LastError { get; private set; }
    /// <summary>The switches of the latest evaluation, for tests.</summary>
    public Task Evaluation { get; private set; } = Task.CompletedTask;

    public event Action? Changed;

    public void Save(PoolScheduleRule rule)
    {
        var index = _stored.Rules.FindIndex(r => r.Id == rule.Id);
        if (index >= 0) _stored.Rules[index] = rule;
        else _stored.Rules.Add(rule);
        Persist();
    }

    public void Remove(Guid id)
    {
        _stored.Rules.RemoveAll(r => r.Id == id);
        foreach (var key in _stored.Applied.Keys.Where(k => k.StartsWith($"{id}|")).ToList()) _stored.Applied.Remove(key);
        Persist();
    }

    public void SetEnabled(Guid id, bool enabled)
    {
        if (_stored.Rules.Find(r => r.Id == id) is not { } rule) return;
        rule.IsEnabled = enabled;
        Persist();
    }

    /// <summary>A rule keeps its last miner: an empty set would mean all miners. It then applies to none and shows "Miner entfernt".</summary>
    public void Forget(Guid minerId)
    {
        foreach (var rule in _stored.Rules.Where(r => r.MinerIds.Count > 1)) rule.MinerIds.Remove(minerId);
        Persist();
    }

    /// <summary>Call once per fleet poll; switches run in the background.</summary>
    public void Evaluate(IEnumerable<MinerDevice> miners, DateTime now, bool switchingAllowed)
    {
        if (!switchingAllowed || _stored.Rules.Count == 0) return;
        var switches = new List<Task>();
        foreach (var rule in _stored.Rules)
        {
            if (_pool(rule.PoolId) is not { } pool) continue;
            foreach (var miner in miners)
            {
                if (!miner.IsOnline || miner.LatestInfo == null || !rule.AppliesTo(miner.Id)) continue;
                var key = $"{rule.Id}|{miner.Id}";
                DateTime? last = _stored.Applied.TryGetValue(key, out var applied) ? applied : null;
                if (rule.DueOccurrence(now, last) is not { } moment) continue;
                // Stays due without a session, so it still runs if a code comes in time.
                if (!pool.Matches(new PoolSettingsForm(miner).Primary) && !_canWrite(miner)) continue;
                _stored.Applied[key] = moment;
                switches.Add(SwitchAsync(miner, pool));
            }
        }
        if (switches.Count == 0) return;
        Persist(notify: false);
        Evaluation = Task.WhenAll(switches);
    }

    private async Task SwitchAsync(MinerDevice miner, PoolConfig pool)
    {
        var form = new PoolSettingsForm(miner);
        // Already there: no restart.
        if (pool.Matches(form.Primary)) return;
        form.MakePrimary(pool);
        if (form.ValidationMessage is { } problem)
        {
            Fail($"{miner.DisplayName}: {problem}");
            return;
        }
        try
        {
            await _apply(form, miner.Id);
            LastError = null;
            Changed?.Invoke();
        }
        catch (Exception e) when (e is MinerApiException or HttpRequestException or TaskCanceledException)
        {
            Fail($"{miner.DisplayName}: {e.Message}");
        }
    }

    private void Fail(string message)
    {
        LastError = message;
        Changed?.Invoke();
    }

    private void Persist(bool notify = true)
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_stored, Options)); }
        catch (IOException) { /* Stays in memory; the next save tries again. */ }
        if (notify) Changed?.Invoke();
    }
}
