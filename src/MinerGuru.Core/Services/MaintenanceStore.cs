using System.Text.Json;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Services;

/// <summary>Everything in <c>maintenance.json</c>.</summary>
public sealed class StoredMaintenance
{
    public List<MaintenanceTask> Tasks { get; set; } = [];
    public List<MaintenanceRecord> Records { get; set; } = [];
    public Dictionary<Guid, DateTime> Reminded { get; set; } = [];
}

/// <summary>Planned maintenance and the record of what was done. Used on the UI thread, like <see cref="MinerStore"/>.</summary>
public sealed class MaintenanceStore
{
    private static readonly TimeSpan RemindAgain = TimeSpan.FromDays(1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly StoredMaintenance _stored = new();

    public MaintenanceStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "maintenance.json");
        try
        {
            if (File.Exists(_path)) _stored = JsonSerializer.Deserialize<StoredMaintenance>(File.ReadAllText(_path)) ?? _stored;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // Unreadable: start with the defaults; the next save replaces the file.
        }
    }

    public IReadOnlyList<MaintenanceTask> Tasks => _stored.Tasks;
    /// <summary>Newest first.</summary>
    public IReadOnlyList<MaintenanceRecord> Records => _stored.Records;

    public event Action? Changed;

    public int DueCount(DateTime now) => Tasks.Count(t => t.DaysUntilDue(now) <= MaintenanceTask.DueSoonDays);

    public void Save(MaintenanceTask task)
    {
        var index = _stored.Tasks.FindIndex(t => t.Id == task.Id);
        if (index >= 0) _stored.Tasks[index] = task;
        else _stored.Tasks.Add(task);
        Persist();
    }

    public void Remove(Guid id)
    {
        _stored.Tasks.RemoveAll(t => t.Id == id);
        _stored.Reminded.Remove(id);
        Persist();
    }

    public void RemoveRecord(Guid id)
    {
        _stored.Records.RemoveAll(r => r.Id == id);
        Persist();
    }

    public void MarkDone(MaintenanceTask task, DateTime date, string notes)
    {
        _stored.Records.Insert(0, new MaintenanceRecord { Kind = task.Kind, Title = task.Title, MinerIds = [.. task.MinerIds], DoneAt = date, Notes = notes });
        if (task.NextDueAfterDone(date) is { } next) task.NextDue = next;
        else _stored.Tasks.RemoveAll(t => t.Id == task.Id);
        _stored.Reminded.Remove(task.Id);
        Persist();
    }

    /// <summary>A task keeps its last miner: an empty set would mean all miners. It then shows "Miner entfernt".</summary>
    public void Forget(Guid minerId)
    {
        foreach (var task in _stored.Tasks.Where(t => t.MinerIds.Count > 1)) task.MinerIds.Remove(minerId);
        Persist();
    }

    /// <summary>Reminders for due tasks, at most once a day per task.</summary>
    public List<AlertEvent> DueEvents(DateTime now)
    {
        var events = new List<AlertEvent>();
        foreach (var task in _stored.Tasks.Where(t => t.DaysUntilDue(now) <= 0))
        {
            if (_stored.Reminded.TryGetValue(task.Id, out var last) && now - last < RemindAgain) continue;
            _stored.Reminded[task.Id] = now;
            var hint = task.Kind.Hint();
            events.Add(new AlertEvent(AlertKind.Maintenance, Guid.Empty, L($"Wartung fällig: {task.Title}", $"Maintenance due: {task.Title}"),
                hint.Length > 0 ? hint : L("Trag sie in MinerGuru als erledigt ein.", "Mark it as done in MinerGuru."), false, task.Title));
        }
        if (events.Count > 0) Persist(notify: false);
        return events;
    }

    private void Persist(bool notify = true)
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_stored, Options)); }
        catch (IOException) { /* Stays in memory; the next save tries again. */ }
        if (notify) Changed?.Invoke();
    }
}
