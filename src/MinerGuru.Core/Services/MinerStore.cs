using System.Text.Json;
using MinerGuru.Core.Models;
using MinerGuru.Core.Widgets;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Services;

/// <summary>Total hashrate of all online miners after one refresh round.</summary>
public sealed record FleetHashrateSample(DateTime Timestamp, double HashRateGhs, int OnlineCount);

/// <summary>Stored settings and miner list, one JSON file in the data folder.</summary>
public sealed class StoredSettings
{
    public List<PersistedMinerConfig> Miners { get; set; } = [];
    public int PollingIntervalSeconds { get; set; } = 5;
    public double TempWarningThresholdC { get; set; } = 68;
    public SoloChanceTimeframe SoloChanceTimeframe { get; set; } = SoloChanceTimeframe.PerDay;
    public bool AutoScanOnEmptyLaunch { get; set; } = true;
    public bool KeepRunningInTray { get; set; } = true;
    public bool StratumProbeEnabled { get; set; }
    public bool HideSensitiveData { get; set; }
    /// <summary><c>de</c>, <c>en</c> or null for the Windows display language.</summary>
    public string? Language { get; set; }
    /// <summary><c>null</c> until the user sets one: the subnet of this PC.</summary>
    public string? ScanSubnetPrefix { get; set; }
    public bool UpdateCheckEnabled { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    public string? LatestVersion { get; set; }
    public string? DismissedUpdateVersion { get; set; }
}

/// <summary>
/// The miners, their polling and the fleet figures. Like the Mac app's MinerStore: created and used on the UI thread,
/// so continuations after <c>await</c> come back there and <see cref="Changed"/> fires on it.
/// </summary>
public sealed class MinerStore
{
    public const int MaxHistorySamples = 240;

    private readonly string _settingsPath;
    private readonly StoredSettings _settings;
    private readonly MinerApiClient _api;
    private CancellationTokenSource? _polling;
    private readonly Lazy<string> _detectedPrefix;

    /// <param name="dataDirectory">Folder for <c>settings.json</c>; tests and the mock setup pass their own.</param>
    public MinerStore(string dataDirectory, MinerApiClient? api = null)
    {
        Directory.CreateDirectory(dataDirectory);
        DataDirectory = dataDirectory;
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
        _api = api ?? MinerApiClient.Shared;
        _settings = Load(_settingsPath, out var damagedSettings);
        DamagedSettingsBackup = damagedSettings;
        Privacy.IsActive = _settings.HideSensitiveData;
        Miners = _settings.Miners.Select(c => new MinerDevice(c, ConnectionState.Checking)).ToList();
        PoolLibrary = new PoolLibrary(dataDirectory);
        Otp = new OtpSessions(dataDirectory, _api.CreateOtpSessionAsync);
        Otp.Changed += () => Changed?.Invoke();
        Power = new PowerStore(dataDirectory, _api, () => OnlineMiners, otp: Otp);
        Scanner = new NetworkScanStore(this, _api);
        Alerts = new AlertCenter(dataDirectory);
        History = new HistoryStore(dataDirectory);
        PoolSchedules = new PoolScheduleStore(dataDirectory, PoolLibrary.Find, (form, id) => ApplyPoolSettingsAsync(form, id, interactive: false), Otp.CanWriteUnattended);
        Maintenance = new MaintenanceStore(dataDirectory);
        Email = new EmailAlerts(dataDirectory);
        Updates = new UpdateChecker(_settings, Save);
        Updates.Changed += () => Changed?.Invoke();
        BlockProbes.Changed += () => Changed?.Invoke();
        _detectedPrefix = new Lazy<string>(NetworkScanner.DetectLocalPrefix);
    }

    /// <summary><c>MINERGURU_DATA_DIR</c> if set (mock setup, debug builds only), else <c>%LOCALAPPDATA%\MinerGuru</c>.</summary>
    public static string DefaultDataDirectory =>
        DebugSwitch.Get("MINERGURU_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MinerGuru");

    public string DataDirectory { get; }
    public List<MinerDevice> Miners { get; }
    /// <summary>Saved pool configurations for the pools page.</summary>
    public PoolLibrary PoolLibrary { get; }
    /// <summary>Tariff, power costs and schedules.</summary>
    public PowerStore Power { get; }
    public OtpSessions Otp { get; }
    /// <summary>Block templates read from the pools for miners that do not report them.</summary>
    public BlockProbeStore BlockProbes { get; } = new();
    /// <summary>Notifications when miners go offline.</summary>
    public AlertCenter Alerts { get; }
    /// <summary>Long-term telemetry in SQLite.</summary>
    public HistoryStore History { get; }
    /// <summary>Pool switches at set times.</summary>
    public PoolScheduleStore PoolSchedules { get; }
    /// <summary>Planned and finished maintenance.</summary>
    public MaintenanceStore Maintenance { get; }
    /// <summary>Alerts by mail.</summary>
    public EmailAlerts Email { get; }
    /// <summary>The daily look for a newer version.</summary>
    public UpdateChecker Updates { get; }
    /// <summary>The search for miners on the local network.</summary>
    public NetworkScanStore Scanner { get; }
    public List<FleetHashrateSample> FleetHistory { get; } = [];
    public bool IsRefreshingAll { get; private set; }
    public DateTime? LastGlobalRefresh { get; private set; }

    /// <summary>Something about the miners changed: refresh, add, remove, rename.</summary>
    public event Action? Changed;
    /// <summary>A short message for the status bar (text, is error).</summary>
    public event Action<string, bool>? Banner;

    /// <summary>Whether the Windows widgets are on: then every refresh round leaves a snapshot for them.</summary>
    public bool WritesWidgetSnapshot { get; set; }

    private DateTime _snapshotWrittenAt = DateTime.MinValue;
    private string _snapshotStates = "";

    /// <summary>Every 30 s, and at once when a miner goes on- or offline.</summary>
    private void WriteWidgetSnapshot(DateTime now)
    {
        if (!WritesWidgetSnapshot) return;
        var states = string.Concat(Miners.Select(m => m.IsOnline ? '1' : '0'));
        if (now - _snapshotWrittenAt < TimeSpan.FromSeconds(30) && states == _snapshotStates) return;
        _snapshotWrittenAt = now;
        _snapshotStates = states;
        var snapshot = WidgetSnapshot.Of(Miners, TempWarningThresholdC, SoloChanceTimeframe, now);
        _ = Task.Run(() =>
        {
            try { snapshot.Save(DataDirectory); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        });
    }

    /// <summary>Where an unreadable settings file went at start, or <c>null</c>.</summary>
    public string? DamagedSettingsBackup { get; }

    // Settings

    public int PollingIntervalSeconds
    {
        get => _settings.PollingIntervalSeconds;
        set
        {
            _settings.PollingIntervalSeconds = Math.Max(2, value);
            Save();
            if (_polling != null) StartPolling();
        }
    }

    public double TempWarningThresholdC
    {
        get => _settings.TempWarningThresholdC;
        set { _settings.TempWarningThresholdC = value; Save(); Changed?.Invoke(); }
    }

    /// <summary>Timeframe of the solo-mining odds on the fleet page.</summary>
    public SoloChanceTimeframe SoloChanceTimeframe
    {
        get => _settings.SoloChanceTimeframe;
        set { _settings.SoloChanceTimeframe = value; Save(); Changed?.Invoke(); }
    }

    /// <summary>Whether a start without miners searches the network.</summary>
    public bool AutoScanOnEmptyLaunch
    {
        get => _settings.AutoScanOnEmptyLaunch;
        set { _settings.AutoScanOnEmptyLaunch = value; Save(); }
    }

    /// <summary>Whether MinerGuru may ask a miner's pool for the block template itself. Off until the user turns it on.</summary>
    public bool StratumProbeEnabled
    {
        get => _settings.StratumProbeEnabled;
        set
        {
            _settings.StratumProbeEnabled = value;
            Save();
            if (!value) BlockProbes.Reset();
        }
    }

    /// <summary>Screenshot-safe mode, see <see cref="Privacy"/>.</summary>
    public bool HideSensitiveData
    {
        get => _settings.HideSensitiveData;
        set
        {
            _settings.HideSensitiveData = value;
            Privacy.IsActive = value;
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>Active from the next start, as on the Mac.</summary>
    public string? Language
    {
        get => _settings.Language;
        set { _settings.Language = value; Save(); }
    }

    /// <summary>Whether closing the window leaves the app running at the tray icon.</summary>
    public bool KeepRunningInTray
    {
        get => _settings.KeepRunningInTray;
        set { _settings.KeepRunningInTray = value; Save(); }
    }

    /// <summary>The /24 network to scan: as set by the user, else the one of this PC.</summary>
    public string ScanSubnetPrefix
    {
        get => _settings.ScanSubnetPrefix ?? _detectedPrefix.Value;
        set { _settings.ScanSubnetPrefix = value; Save(); }
    }

    // Fleet figures

    public IEnumerable<MinerDevice> OnlineMiners => Miners.Where(m => m.IsOnline && m.LatestInfo != null);
    public int OnlineCount => OnlineMiners.Count();
    public double TotalHashrateGhs => OnlineMiners.Sum(m => m.LatestInfo!.HashRate);
    public double TotalPowerWatts => OnlineMiners.Sum(m => m.LatestInfo!.Power);

    /// <summary>The fleet in a word, for the tray icon.</summary>
    public string TraySummaryText =>
        Miners.Count == 0 ? "MinerGuru" : OnlineCount == 0 ? "Offline" : MinerFormatting.FormatHashrate(TotalHashrateGhs);

    public double FleetEfficiencyJPerTh
    {
        get
        {
            var th = TotalHashrateGhs / 1000;
            return th > 0.01 && TotalPowerWatts > 0 ? TotalPowerWatts / th : 0;
        }
    }

    /// <summary>Hashrate, network difficulty and solo-mining odds per coin across the online miners.</summary>
    public List<FleetCoinMiningSummary> ActiveFleetCoinSummaries => FleetCoinMiningSummary.Summaries(OnlineMiners);

    public int TotalSharesAccepted => OnlineMiners.Sum(m => m.LatestInfo!.SharesAccepted);
    public int TotalSharesRejected => OnlineMiners.Sum(m => m.LatestInfo!.SharesRejected);

    /// <summary>The miner with the highest all-time best difficulty.</summary>
    public MinerSystemInfo? BestDiffInfo => OnlineMiners.Select(m => m.LatestInfo!).MaxBy(i => i.BestDiffNumeric);

    /// <summary>The miner with the highest best difficulty since its last restart.</summary>
    public MinerSystemInfo? BestSessionInfo => OnlineMiners.Select(m => m.LatestInfo!).Where(i => i.BestSessionDiffNumeric > 0).MaxBy(i => i.BestSessionDiffNumeric);

    public double? AveragePoolLatencyMs
    {
        get
        {
            var values = OnlineMiners.Select(m => m.ActivePoolLatencyMs).Where(v => v is > 0).Select(v => v!.Value).ToList();
            return values.Count > 0 ? values.Average() : null;
        }
    }

    public bool IsHot(MinerDevice miner) => (miner.LatestInfo?.Temp ?? 0) >= TempWarningThresholdC;

    public MinerDevice? Find(Guid id) => Miners.FirstOrDefault(m => m.Id == id);

    // Miner list

    public bool IsAlreadyAdded(string ip)
    {
        var normalized = MinerApiClient.NormalizeHost(ip);
        return Miners.Any(m => string.Equals(m.IpAddress, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds a miner (or renames it if the address is already known) and polls it at once.</summary>
    public MinerDevice? AddMiner(string rawIp, string? customName = null, MinerSystemInfo? initialInfo = null, string? rawJson = null)
    {
        var ip = MinerApiClient.NormalizeHost(rawIp);
        if (ip.Length == 0) return null;
        var name = string.IsNullOrWhiteSpace(customName) ? null : customName.Trim();

        if (Miners.FirstOrDefault(m => string.Equals(m.IpAddress, ip, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            if (name != null)
            {
                existing.CustomName = name;
                SaveMiners();
                Changed?.Invoke();
            }
            return existing;
        }

        var device = new MinerDevice(new PersistedMinerConfig
        {
            IpAddress = ip,
            CustomName = name,
            CachedHostname = initialInfo?.Hostname,
            CachedHardwareModel = initialInfo?.DetectedHardwareTitle(),
        }, initialInfo != null ? ConnectionState.Online : ConnectionState.Checking);
        if (initialInfo != null) Apply(device, initialInfo, rawJson, DateTime.Now);

        Miners.Add(device);
        Miners.Sort((a, b) => string.CompareOrdinal(IpSortKey(a.IpAddress), IpSortKey(b.IpAddress)));
        SaveMiners();
        Changed?.Invoke();
        _ = RefreshMinerAsync(device.Id);
        return device;
    }

    internal void ShowBanner(string message, bool isError) => Banner?.Invoke(message, isError);

    public void RemoveMiner(Guid id)
    {
        Miners.RemoveAll(m => m.Id == id);
        SaveMiners();
        Power.Forget(id);
        Otp.Invalidate(id);
        Alerts.Forget(id);
        PoolSchedules.Forget(id);
        Maintenance.Forget(id);
        _ = Task.Run(() => History.Forget(id));
        // Polling stops with the last miner, so the widgets would keep showing it.
        WriteWidgetSnapshot(DateTime.Now);
        Changed?.Invoke();
    }

    public void RenameMiner(Guid id, string? customName)
    {
        if (Find(id) is not { } miner) return;
        miner.CustomName = string.IsNullOrWhiteSpace(customName) ? null : customName.Trim();
        SaveMiners();
        Changed?.Invoke();
    }

    /// <summary>Key for an ordinal sort: addresses numerically (192.168.1.9 before 192.168.1.10, then the port), host names last.</summary>
    public static string IpSortKey(string ip)
    {
        var (address, port) = ip.LastIndexOf(':') is var colon and > 0 ? (ip[..colon], ip[(colon + 1)..]) : (ip, "");
        var parts = address.Split('.');
        if (parts.Length != 4 || !parts.All(p => int.TryParse(p, out _))) return "~" + ip.ToLowerInvariant();
        return string.Join(".", parts.Select(p => int.Parse(p).ToString("D3"))) + ":" + port.PadLeft(5, '0');
    }

    // Polling

    public void StartPolling()
    {
        _polling?.Cancel();
        var cts = _polling = new CancellationTokenSource();
        _ = PollAsync(cts.Token);
    }

    public void StopPolling() => _polling?.Cancel();

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshAllAsync();
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, PollingIntervalSeconds)), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private sealed record FetchResult(Guid Id, MinerSystemInfo? Info, string? RawJson, string? Error, bool PrimaryReachable = false);

    /// <summary>The watchdog's only action, whether or not notifications are on.</summary>
    private async Task AutoRestartAsync(MinerDevice miner, MinerHealth.AutoRestartReason reason)
    {
        ChangeLog.Source.Value = ChangeSource.Watchdog;
        string? error = null;
        try { await _api.RestartAsync(miner.IpAddress); }
        catch (MinerApiException e) { error = e.Message; }
        var sent = Alerts.Send([AlertCenter.RestartEvent(miner, reason, error)]);
        foreach (var alert in sent)
            DiagnosticLog.Shared.Write("alert", $"{miner.IpAddress} {alert.Kind.ToString().ToLowerInvariant()} raised delivered=1");
        await Email.DeliverAsync(sent);
    }

    public async Task RefreshAllAsync()
    {
        if (Miners.Count == 0 || IsRefreshingAll) return;
        IsRefreshingAll = true;
        Changed?.Invoke();
        var results = await Task.WhenAll(Miners.Select(m => FetchAsync(m.Id, m.IpAddress)));

        var now = DateTime.Now;
        var needsSave = false;
        foreach (var result in results)
            if (Find(result.Id) is { } miner) needsSave |= Apply(miner, result, now);

        LastGlobalRefresh = now;
        IsRefreshingAll = false;
        FleetHistory.Add(new FleetHashrateSample(now, TotalHashrateGhs, OnlineCount));
        if (FleetHistory.Count > MaxHistorySamples) FleetHistory.RemoveRange(0, FleetHistory.Count - MaxHistorySamples);
        if (needsSave) SaveMiners();
        Changed?.Invoke();
        var alerts = Alerts.Evaluate(Miners, now);
        foreach (var (miner, reason) in Alerts.DueRestarts) _ = AutoRestartAsync(miner, reason);
        alerts.AddRange(Alerts.Send(Maintenance.DueEvents(now)));
        _ = Email.DeliverAsync(AlertCenter.Combine(alerts));
        var readings = Miners.Select(m => HistoryReading.Of(m, now)).OfType<HistoryReading>().ToList();
        _ = Task.Run(() => History.Record(readings, now));
        WriteWidgetSnapshot(now);
        // Switch in the background, so a miner's reboot does not hold up the next poll.
        ChangeLog.Source.Value = ChangeSource.Schedule;
        _ = Power.EvaluateAsync();
        PoolSchedules.Evaluate(Miners, now, Power.RunsSchedules);
        ChangeLog.Source.Value = ChangeSource.Manual;
    }

    public async Task RefreshMinerAsync(Guid id)
    {
        if (Find(id) is not { } miner) return;
        var result = await FetchAsync(id, miner.IpAddress);
        // The miner may have been removed while the request was running.
        if (Find(id) is not { } current) return;
        if (Apply(current, result, DateTime.Now)) SaveMiners();
        Changed?.Invoke();
    }

    private async Task<FetchResult> FetchAsync(Guid id, string host)
    {
        try
        {
            var (info, raw) = await _api.FetchSystemInfoAsync(host);
            ChangeLog.Shared.NoteValues(host, raw);
            // Only a miner on its fallback pool needs the check whether the primary one answers.
            var reachable = info.IsUsingFallbackStratum && await MinerApiClient.CanReachAsync(info.StratumUrl, info.StratumPort);
            return new FetchResult(id, info, raw, null, reachable);
        }
        catch (MinerApiException e)
        {
            return new FetchResult(id, null, null, e.Message);
        }
    }

    /// <summary>Writes a fetch result into the miner; <c>true</c> if stored fields (hostname, model) changed.</summary>
    private static bool Apply(MinerDevice miner, FetchResult result, DateTime now)
    {
        if (result.Info == null)
        {
            miner.State = ConnectionState.Offline;
            miner.OfflineReason = result.Error ?? L("Nicht erreichbar", "Unreachable");
            return false;
        }
        miner.PrimaryPoolReachable = result.PrimaryReachable;
        return Apply(miner, result.Info, result.RawJson, now);
    }

    private static bool Apply(MinerDevice miner, MinerSystemInfo info, string? rawJson, DateTime now)
    {
        miner.LatestInfo = info;
        miner.RawJson = rawJson;
        miner.State = ConnectionState.Online;
        miner.OfflineReason = null;
        miner.LastSeenAt = now;

        var title = info.DetectedHardwareTitle();
        var changed = miner.CachedHostname != info.Hostname || miner.CachedHardwareModel != title;
        miner.CachedHostname = info.Hostname;
        miner.CachedHardwareModel = title;

        miner.History.Add(new TelemetrySample(now, info.HashRate, info.Temp, info.VrTemp, info.Power, miner.ActivePoolLatencyMs, info.FanSpeedPercent));
        if (miner.History.Count > MaxHistorySamples) miner.History.RemoveRange(0, miner.History.Count - MaxHistorySamples);
        return changed;
    }

    // Control

    /// <summary>Restarts the miner; the outcome goes to the status bar.</summary>
    public async Task RestartMinerAsync(Guid id)
    {
        if (Find(id) is not { } miner) return;
        try
        {
            await _api.RestartAsync(miner.IpAddress);
        }
        catch (MinerApiException e)
        {
            Banner?.Invoke(L($"Neustart von {miner.DisplayName} fehlgeschlagen: {e.Message}", $"Restarting {miner.DisplayName} failed: {e.Message}"), true);
            return;
        }
        Banner?.Invoke(L($"Neustart für {miner.DisplayName} ausgelöst.", $"Restart of {miner.DisplayName} triggered."), false);
        miner.State = ConnectionState.Checking;
        Changed?.Invoke();
        await Task.Delay(TimeSpan.FromSeconds(4));
        await RefreshMinerAsync(id);
    }

    /// <summary>Schedules pass <paramref name="interactive"/> false: they never ask and only use a stored session.</summary>
    private Task WriteSettingsAsync(MinerDevice miner, MinerSystemUpdatePayload payload, bool interactive) =>
        Otp.PerformAsync(miner, interactive, token => _api.UpdateSystemSettingsAsync(miner.IpAddress, payload, token));

    /// <summary>Online miners with a one-time code and an enabled schedule that cannot run for lack of a session.</summary>
    public IReadOnlyList<MinerDevice> MinersAwaitingOtp => OnlineMiners.Where(m => !Otp.CanWriteUnattended(m)
        && (Power.Rules.Any(r => r.IsEnabled && r.AppliesTo(m.Id)) || PoolSchedules.Rules.Any(r => r.IsEnabled && r.AppliesTo(m.Id)))).ToList();

    /// <summary>Asks for a code on the user's request. Failures go to the banner.</summary>
    public async Task EnterOtpCodeAsync(MinerDevice miner)
    {
        try
        {
            await Otp.AuthorizeAsync(miner);
        }
        catch (OtpException)
        {
            // Cancelled: the miner keeps waiting.
        }
        catch (MinerApiException e)
        {
            Banner?.Invoke(e.Message, true);
        }
    }

    /// <summary>Shows old and new values; set by the app. Without it nothing is asked (tests).</summary>
    public Func<string, IReadOnlyList<ChangeField>, bool, Task<bool>>? ConfirmWrite { get; set; }

    /// <summary>Asks before a write by hand. Schedules were agreed to when they were set up.</summary>
    private async Task ConfirmAsync(MinerDevice miner, MinerSystemUpdatePayload payload, IEnumerable<int>? clearing = null, bool restarts = false)
    {
        if (ChangeLog.Shared.IsReadOnly) throw MinerApiClient.ReadOnlyError();
        if (ConfirmWrite == null) return;
        var current = ChangeLog.Flatten(miner.RawJson ?? "{}") ?? [];
        var fields = ChangeLog.Changes(MinerApiClient.PayloadJson(payload), current);
        fields.AddRange((clearing ?? []).Select(i => new ChangeField($"pools.{i}.stratumURL", null, L("(geleert)", "(cleared)"))));
        if (!await ConfirmWrite(miner.DisplayName, fields, restarts)) throw new WriteDeclinedException();
    }

    /// <summary>Puts the expected address into one slot of a miner, after the usual confirmation. Errors are thrown.</summary>
    public async Task ApplyExpectedAddressAsync(string address, string slotKey, Guid id)
    {
        if (Find(id) is not { } miner) return;
        var form = new PoolSettingsForm(miner);
        var index = slotKey switch
        {
            "primary" => form.PrimarySlot,
            "fallback" => form.SecondarySlot,
            _ => int.TryParse(slotKey.Replace("pool", ""), out var number) ? number : -1,
        };
        if (index < 0 || index >= form.Slots.Count) return;
        form.Slots[index] = form.Slots[index] with { User = PayoutCheck.User(form.Slots[index].User, address) };
        await ApplyPoolSettingsAsync(form, id);
    }

    /// <summary>Sends settings to the miner and refreshes it. Errors are thrown.</summary>
    public async Task UpdateMinerSettingsAsync(Guid id, MinerSystemUpdatePayload payload)
    {
        if (Find(id) is not { } miner) return;
        await ConfirmAsync(miner, payload);
        await WriteSettingsAsync(miner, payload, interactive: true);
        await RefreshMinerAsync(id);
    }

    /// <summary>
    /// Sends the pool settings, clears emptied AxeOS slots and restarts the miner so they take effect. Returns once
    /// the miner answers again, so the caller reloads the configuration it really uses. Errors are thrown.
    /// </summary>
    public async Task ApplyPoolSettingsAsync(PoolSettingsForm form, Guid id, bool interactive = true)
    {
        if (Find(id) is not { } miner) return;
        if (interactive) await ConfirmAsync(miner, form.Payload(miner.SupportsDualPool), form.SlotsToClear, restarts: true);
        await WriteSettingsAsync(miner, form.Payload(miner.SupportsDualPool), interactive);
        foreach (var slot in form.SlotsToClear) await _api.DeletePoolSlotAsync(miner.IpAddress, slot);
        await _api.RestartAsync(miner.IpAddress);
        Banner?.Invoke(L($"Pool-Konfiguration an {miner.DisplayName} übertragen – Miner startet neu.",
                         $"Pool configuration sent to {miner.DisplayName} – the miner is restarting."), false);
        miner.State = ConnectionState.Checking;
        Changed?.Invoke();
        await Task.Delay(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            await RefreshMinerAsync(id);
            if (Find(id)?.IsOnline == true) break;
        }
    }

    // Persistence

    private static StoredSettings Load(string path, out string? damagedBackup)
    {
        damagedBackup = null;
        if (!File.Exists(path)) return new StoredSettings();
        try
        {
            if (JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(path)) is { } settings) return settings;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            DiagnosticLog.Shared.Write("settings", $"unreadable: {e.GetType().Name}");
        }
        catch (IOException e)
        {
            // Not read is not damaged; there is nothing to keep, and the next save writes the file anew.
            DiagnosticLog.Shared.Write("settings", $"not read: {e.GetType().Name}");
            return new StoredSettings();
        }
        // The file goes aside: the miners in it may still be worth more than a fresh start.
        var backup = $"{path}.defekt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(path, backup);
            damagedBackup = backup;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Shared.Write("settings", $"not moved: {e.GetType().Name}");
        }
        return new StoredSettings();
    }

    private void SaveMiners()
    {
        _settings.Miners = Miners.Select(m => m.PersistedConfig).ToList();
        Save();
    }

    private void Save()
    {
        var temp = _settingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _settingsPath, overwrite: true);
    }
}
