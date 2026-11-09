using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Services;

/// <summary>Who caused a write to a miner.</summary>
public enum ChangeSource { Manual, Schedule, Watchdog }

/// <summary>One value a write changes. <c>Before</c> is <c>null</c> where the miner does not report the field.</summary>
public sealed record ChangeField(string Key, string? Before, string After)
{
    public string Title => ChangeLog.Title(Key);

    /// <summary>The line a person reads, in the confirmation and the change log.</summary>
    public string Summary => $"{Title}: {(Before == null ? "–" : ChangeLog.Shown(Before, Key))} → {ChangeLog.Shown(After, Key)}";
}

/// <param name="Failure"><c>null</c> when the miner accepted the write, otherwise the reason it failed.</param>
public sealed record ChangeEntry(DateTime Date, string Host, string Action, List<ChangeField> Fields, ChangeSource Source, string? Failure);

/// <summary>
/// What MinerGuru changed on miners, and the optional read-only mode. <see cref="MinerApiClient"/> is the only
/// place that writes to a miner, so everything lands here, automatic actions included.
/// </summary>
public sealed class ChangeLog(string? folder = null)
{
    public static ChangeLog Shared { get; } = new();
    public const int MaxEntries = 500;
    private const string SecretMask = "•••";
    private const string FirmwareMask = "*****";

    /// <summary>Raised around a rule's or the watchdog's writes.</summary>
    public static readonly AsyncLocal<ChangeSource> Source = new();

    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, string>> _values = [];
    private List<ChangeEntry> _entries = [];
    private string? _folder = folder;
    private bool _loaded;
    private bool _readOnly;

    public void Activate(string dataDirectory)
    {
        lock (_lock)
        {
            _folder = dataDirectory;
            _loaded = false;
        }
    }

    private string? EntriesPath => _folder == null ? null : Path.Combine(_folder, "changes.json");
    private string? ReadOnlyPath => _folder == null ? null : Path.Combine(_folder, "readonly");

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        if (EntriesPath is not { } path || !File.Exists(path)) return;
        try { _entries = JsonSerializer.Deserialize<List<ChangeEntry>>(File.ReadAllText(path)) ?? []; }
        catch (Exception e) when (e is JsonException or IOException) { /* unreadable: the next write replaces it */ }
    }

    public bool IsReadOnly
    {
        get
        {
            lock (_lock) return ReadOnlyPath is { } path ? File.Exists(path) : _readOnly;
        }
        set
        {
            lock (_lock)
            {
                _readOnly = value;
                if (ReadOnlyPath is not { } path) return;
                if (value) File.WriteAllText(path, "");
                else File.Delete(path);
            }
        }
    }

    /// <summary>Newest first.</summary>
    public List<ChangeEntry> Entries()
    {
        lock (_lock)
        {
            Load();
            return Enumerable.Reverse(_entries).ToList();
        }
    }

    /// <summary>Remembers what a miner reports, to show the value a later write replaces.</summary>
    public void NoteValues(string host, string? rawJson)
    {
        if (rawJson == null || Flatten(rawJson) is not { } current) return;
        lock (_lock) _values[host] = current;
    }

    public void Record(ChangeEntry entry)
    {
        lock (_lock)
        {
            Load();
            _entries.Add(entry);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
            if (EntriesPath is not { } path) return;
            try { File.WriteAllText(path, JsonSerializer.Serialize(_entries)); }
            catch (IOException) { /* stays in memory; the next write tries again */ }
        }
    }

    /// <summary>Records a write that went to the miner. Session requests for one-time codes change nothing.</summary>
    public void RecordWrite(string host, string method, string path, string? body, string? failure, DateTime? at = null)
    {
        if (path.Contains("/otp/")) return;
        Dictionary<string, string> current;
        lock (_lock) current = _values.GetValueOrDefault(host) ?? [];
        var fields = body == null ? [] : Changes(body, current);
        Record(new ChangeEntry(at ?? DateTime.Now, host, ActionOf(method, path), fields, Source.Value, failure));
    }

    public DateTime? LastWrite(string host) => Last(e => e.Host == host);
    public DateTime? LastRestart(string host) => Last(e => e.Host == host && e.Action == "restart");

    /// <summary>The watchdog's last attempt on <paramref name="host"/>, successful or not.</summary>
    public DateTime? LastAutomaticRestart(string host) => Last(e => e.Host == host && e.Action == "restart" && e.Source == ChangeSource.Watchdog);

    private DateTime? Last(Func<ChangeEntry, bool> match)
    {
        lock (_lock)
        {
            Load();
            return _entries.LastOrDefault(match)?.Date;
        }
    }

    public string ExportText() => string.Concat(Entries().AsEnumerable().Reverse().Select(e =>
    {
        var changes = string.Join("; ", e.Fields.Select(f => $"{f.Key}: {f.Before ?? "-"} -> {f.After}"));
        var stamp = new DateTimeOffset(e.Date).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        return $"{stamp} {e.Host} {e.Source.ToString().ToLowerInvariant()} {e.Action} {(e.Failure == null ? "ok" : $"failed: {e.Failure}")} {changes}\n";
    }));

    /// <summary><c>restart</c>, <c>pause</c>, <c>resume</c>, <c>settings</c> or <c>clearSlot</c>.</summary>
    public static string ActionOf(string method, string path)
    {
        if (method == "DELETE") return "clearSlot";
        foreach (var name in new[] { "restart", "pause", "resume" })
            if (path.EndsWith(name, StringComparison.Ordinal)) return name;
        return "settings";
    }

    public static string ActionTitle(string action) => action switch
    {
        "restart" => L("Neustart", "Restart"),
        "pause" => L("Pausieren", "Pause"),
        "resume" => L("Fortsetzen", "Resume"),
        "clearSlot" => L("Pool-Slot geleert", "Pool slot cleared"),
        _ => L("Einstellungen", "Settings"),
    };

    public static string SourceTitle(ChangeSource source) => source switch
    {
        ChangeSource.Schedule => L("Zeitplan", "Schedule"),
        ChangeSource.Watchdog => L("Automatik", "Automatic"),
        _ => L("Von Hand", "Manual"),
    };

    /// <summary>Top-level values, and pool slots as <c>pools.&lt;id&gt;.&lt;field&gt;</c>, for comparing. Only <see cref="Changes"/> leaves this class.</summary>
    public static Dictionary<string, string>? Flatten(string rawJson)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(rawJson) as JsonObject; }
        catch (JsonException) { return null; }
        if (root == null) return null;
        var result = new Dictionary<string, string>();
        foreach (var (key, value) in root)
        {
            if (key == "pools" && value is JsonArray pools)
            {
                for (var i = 0; i < pools.Count; i++)
                {
                    if (pools[i] is not JsonObject pool) continue;
                    var id = pool["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var number) ? number : i;
                    foreach (var (field, item) in pool)
                        if (field != "id" && Comparable(field, item) is { } text) result[$"pools.{id}.{field}"] = text;
                }
            }
            else if (Comparable(key, value) is { } text)
            {
                result[key] = text;
            }
        }
        return result;
    }

    /// <summary>The fields of a write whose value differs from what the miner reports.</summary>
    public static List<ChangeField> Changes(string body, IReadOnlyDictionary<string, string> current)
    {
        var result = new List<ChangeField>();
        foreach (var (key, after) in (Flatten(body) ?? []).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var before = current.GetValueOrDefault(key);
            if (before == after) continue;
            result.Add(IsSecret(key) ? new ChangeField(key, null, SecretMask) : new ChangeField(key, before, after));
        }
        return result;
    }

    private static bool IsSecret(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower.Contains("pass") || lower.Contains("token") || lower.Contains("cert") || lower.Contains("otp");
    }

    /// <summary>The firmware's mask for a stored password means "keep it", so that is no change.</summary>
    private static string? Comparable(string key, JsonNode? value)
    {
        if (value is not JsonValue plain) return null;
        var element = plain.GetValue<JsonElement>();
        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            JsonValueKind.Number => element.GetDouble().ToString("0.######", CultureInfo.InvariantCulture),
            _ => null,
        };
        return text != null && IsSecret(key) && text is FirmwareMask or "" ? null : text;
    }

    /// <summary>A short name for a field; unknown fields keep the name the miner uses.</summary>
    public static string Title(string key)
    {
        var parts = key.Split('.');
        var isSlot = key.StartsWith("pools.", StringComparison.Ordinal) && parts.Length == 3;
        var name = isSlot ? parts[2] : key;
        var prefix = isSlot ? $"Pool {(int.TryParse(parts[1], out var i) ? i + 1 : 1)}: " : "";
        return prefix + name switch
        {
            "hostname" => "Hostname",
            "frequency" => L("Frequenz", "Frequency"),
            "coreVoltage" => L("Kernspannung", "Core voltage"),
            "autofanspeed" => L("Lüfterautomatik", "Automatic fan"),
            "fanspeed" or "manualFanSpeed" => L("Lüfter", "Fan"),
            "stratumURL" => L("Pool-Adresse", "Pool address"),
            "stratumPort" => "Pool-Port",
            "stratumUser" => L("Pool-Benutzer", "Pool user"),
            "stratumPassword" => L("Pool-Passwort", "Pool password"),
            "fallbackStratumURL" => L("Fallback-Adresse", "Fallback address"),
            "fallbackStratumPort" => "Fallback-Port",
            "fallbackStratumUser" => L("Fallback-Benutzer", "Fallback user"),
            "fallbackStratumPassword" => L("Fallback-Passwort", "Fallback password"),
            "overclockEnabled" => L("Eigene Taktwerte", "Custom clock values"),
            "temptarget" or "pidTargetTemp" => L("Zieltemperatur", "Target temperature"),
            "poolMode" => L("Pool-Modus", "Pool mode"),
            "poolBalance" => L("Pool-Aufteilung", "Pool split"),
            "primaryPoolIndex" => L("Primärer Pool-Platz", "Primary pool slot"),
            "secondaryPoolIndex" => L("Fallback-Platz", "Fallback slot"),
            "id" => L("Platz", "Slot"),
            "stratumProtocol" => L("Protokoll", "Protocol"),
            "stratumTLS" => "TLS",
            "stratumSuggestedDifficulty" => L("Vorgeschlagene Difficulty", "Suggested difficulty"),
            "stratumExtranonceSubscribe" => "Extranonce-Subscribe",
            "stratumDecodeCoinbase" => L("Coinbase auswerten", "Decode coinbase"),
            "stratumShareWarning" => L("Share-Warnung", "Share warning"),
            "stratumV2ChannelType" => L("SV2-Kanaltyp", "SV2 channel type"),
            "stratumV2AuthorityPubkey" => L("SV2-Authority-Key", "SV2 authority key"),
            "stratumV2RequireAuth" => L("SV2-Authentifizierung", "SV2 authentication"),
            _ => name,
        };
    }

    /// <summary>Values the firmware stores as numbers, in words. Everything else stays as it is.</summary>
    public static string Shown(string value, string key)
    {
        switch (key[(key.LastIndexOf('.') + 1)..])
        {
            case "overclockEnabled" or "autofanspeed" or "stratumExtranonceSubscribe" or "stratumDecodeCoinbase" or "stratumShareWarning" or "stratumV2RequireAuth":
                return value switch
                {
                    "1" or "true" => L("an", "on"),
                    "0" or "false" => L("aus", "off"),
                    _ => value,
                };
            case "poolMode":
                return value switch { "0" => "Failover", "1" => "Dual-Pool", _ => value };
            case "poolBalance":
                return $"{value} %";
            case "primaryPoolIndex" or "secondaryPoolIndex":
                return int.TryParse(value, out var index) ? (index + 1).ToString() : value;
            default:
                return value;
        }
    }
}
