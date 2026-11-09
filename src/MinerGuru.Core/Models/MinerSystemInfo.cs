using System.Globalization;
using System.Text.Json.Nodes;

namespace MinerGuru.Core.Models;

/// <summary>Per-pool status in <c>stratum.pools[]</c> (NerdOS and newer AxeOS).</summary>
public sealed record StratumPoolStatus(
    bool? Active, bool? Connected, double? PoolDifficulty, double? NetworkDifficulty,
    int? Accepted, int? Rejected, double? PingRtt, double? ResponseTime, double? BestDiff)
{
    /// <summary>Latency in ms of this slot (<c>pingRtt</c> on NerdOS, <c>responseTime</c> on AxeOS).</summary>
    public double? EffectiveLatencyMs => PingRtt is > 0 ? PingRtt : ResponseTime is > 0 ? ResponseTime : null;
}

/// <summary>The <c>stratum</c> object of NerdOS: pool mode, balance and per-pool status.</summary>
public sealed record StratumDetail(int? PoolMode, int? ActivePoolMode, int? PoolBalance, bool? UsingFallback, IReadOnlyList<StratumPoolStatus> Pools);

/// <summary>
/// One pool slot of AxeOS 2.15+ (<c>pools[]</c>). <see cref="Raw"/> keeps the slot as read, so its advanced
/// fields (TLS, protocol, suggested difficulty …) are written back unchanged.
/// </summary>
public sealed record AxeOsPool(int Id, string StratumUrl, int StratumPort, string StratumUser, string? StratumPassword = null, JsonObject? Raw = null)
{
    /// <summary><c>MAX_POOLS</c> in ESP-Miner.</summary>
    public const int SlotCount = 8;
    /// <summary>The firmware reports this instead of the password; sending it back keeps the stored one.</summary>
    public const string MaskedPassword = "*****";
}

/// <summary>
/// Telemetry from <c>GET /api/system/info</c> across Bitaxe (AxeOS) and NerdOS boards. Fields may be missing,
/// renamed or sent as strings, so <see cref="Parse"/> reads every value leniently, like the Mac app.
/// </summary>
public sealed class MinerSystemInfo
{
    // Hashrate in GH/s
    public double HashRate { get; set; }
    public double? HashRate1m { get; set; }
    public double? HashRate10m { get; set; }
    public double? HashRate1h { get; set; }
    public double? ExpectedHashrate { get; set; }

    // Thermal
    public double Temp { get; set; }
    public double? VrTemp { get; set; }
    public double FanSpeedPercent { get; set; }
    public int FanRpm { get; set; }
    public double? Fan2SpeedPercent { get; set; }
    /// <summary>Second fan (NerdQAxe), only when it reports a speed.</summary>
    public int? Fan2Rpm { get; set; }
    public bool AutoFanSpeed { get; set; } = true;
    public int AutoFanModeRaw { get; set; } = 1;
    public double? TargetTemp { get; set; }
    public bool OverheatMode { get; set; }
    /// <summary>NerdOS: the limit the firmware protects itself at; it has no overheat flag.</summary>
    public double? OverheatTemp { get; set; }
    /// <summary>AxeOS 2.15+: paused on purpose.</summary>
    public bool MiningPaused { get; set; }
    /// <summary>What the pool really set; <see cref="StratumSuggestedDifficulty"/> is only what the miner asked for.</summary>
    public double? ReportedPoolDifficulty { get; set; }
    /// <summary>NerdOS: settings can only be changed with a one-time code.</summary>
    public bool Otp { get; set; }

    // Power and clock
    public double Power { get; set; }
    public double VoltageVolts { get; set; }
    public double CurrentAmps { get; set; }
    public int CoreVoltageTargetMv { get; set; } = 1200;
    public int CoreVoltageActualMv { get; set; } = 1200;
    public int FrequencyMhz { get; set; } = 525;
    public int? DefaultFrequencyMhz { get; set; }
    public int? DefaultCoreVoltageMv { get; set; }

    // Shares and difficulty
    public int SharesAccepted { get; set; }
    public int SharesRejected { get; set; }
    /// <summary>Share of hardware errors in percent, on firmware that reports it.</summary>
    public double? ErrorPercentage { get; set; }
    public string BestDiffFormatted { get; set; } = "0";
    public double BestDiffNumeric { get; set; }
    public string BestSessionDiffFormatted { get; set; } = "0";
    public double BestSessionDiffNumeric { get; set; }
    public double? NetworkDifficulty { get; set; }

    // Pools
    public string StratumUrl { get; set; } = "";
    public int StratumPort { get; set; } = 3333;
    public string StratumUser { get; set; } = "";
    public double? StratumSuggestedDifficulty { get; set; }
    public string FallbackStratumUrl { get; set; } = "";
    public int FallbackStratumPort { get; set; } = 3333;
    public string FallbackStratumUser { get; set; } = "";
    public bool IsUsingFallbackStratum { get; set; }
    public double? MinerReportedPoolLatencyMs { get; set; }
    public StratumDetail? StratumDetail { get; set; }
    /// <summary>Pool slots of AxeOS 2.15+; <c>null</c> on older firmware and NerdOS.</summary>
    public IReadOnlyList<AxeOsPool>? AxePools { get; set; }
    public int? PrimaryPoolIndex { get; set; }
    public int? SecondaryPoolIndex { get; set; }

    // Block template (AxeOS 2.13+)
    public int? BlockHeight { get; set; }
    /// <summary>Pool tag from the coinbase scriptSig, e.g. <c>/solo.ckpool.org/</c>.</summary>
    public string? BlockScriptSig { get; set; }
    public IReadOnlyList<string>? BlockSignals { get; set; }
    public IReadOnlyList<CoinbaseOutput>? CoinbaseOutputs { get; set; }
    public int? CoinbaseOthersCount { get; set; }
    public double? CoinbaseOthersValueSats { get; set; }
    public double? CoinbaseValueTotalSats { get; set; }
    public double? CoinbaseValueUserSats { get; set; }
    /// <summary><c>verified</c>, <c>not_found</c> or <c>not_applicable</c> (AxeOS 2.16+).</summary>
    public string? CoinbasePayoutStatus { get; set; }
    public bool? StratumDecodeCoinbase { get; set; }
    public bool? FallbackStratumDecodeCoinbase { get; set; }
    /// <summary>Top-level TLS flags of primary and fallback (AxeOS reports 0/1, NerdOS true/false).</summary>
    public bool? StratumTls { get; set; }
    public bool? FallbackStratumTls { get; set; }

    /// <summary><c>stratumDecodeCoinbase</c> of the slot that delivers the current job; the firmware default is on.</summary>
    public bool? ActivePoolDecodesCoinbase
    {
        get
        {
            var index = IsUsingFallbackStratum ? SecondaryPoolIndex : PrimaryPoolIndex;
            if (index != null && AxePools?.FirstOrDefault(p => p.Id == index)?.Raw is { } slot && B(slot, "stratumDecodeCoinbase") is { } decode) return decode;
            return IsUsingFallbackStratum ? FallbackStratumDecodeCoinbase : StratumDecodeCoinbase;
        }
    }

    // Hardware and system
    public string? DeviceModel { get; set; }
    public string AsicModel { get; set; } = "BM1370";
    public string? BoardVersion { get; set; }
    public int AsicCount { get; set; } = 1;
    public int? SmallCoreCount { get; set; }
    public string Hostname { get; set; } = "miner";
    public string? MacAddress { get; set; }
    public string? Ssid { get; set; }
    public int? WifiRssi { get; set; }
    public string FirmwareVersion { get; set; } = "";
    public string? AxeOsVersion { get; set; }
    public int UptimeSeconds { get; set; }
    /// <summary>Why the miner last started, in the firmware's words (<c>resetReason</c>, NerdOS <c>lastResetReason</c>).</summary>
    public string? ResetReason { get; set; }
    public int? FreeHeap { get; set; }

    // Derived figures

    /// <summary>Joules per terahash (W / TH/s).</summary>
    public double EfficiencyJPerTh => HashRate / 1000 > 0.01 && Power > 0 ? Power / (HashRate / 1000) : 0;

    public double ShareRejectionRatePercent =>
        SharesAccepted + SharesRejected > 0 ? SharesRejected * 100.0 / (SharesAccepted + SharesRejected) : 0;

    /// <summary>Both pools at once (NerdOS <c>poolMode == 1</c>, or both slots connected without fallback).</summary>
    public bool IsDualPoolMining
    {
        get
        {
            if (StratumDetail is not { } d) return false;
            if (d.ActivePoolMode == 1 || d.PoolMode == 1) return true;
            return d.Pools.Count >= 2 && d.Pools[0].Connected == true && d.Pools[1].Connected == true && d.UsingFallback != true;
        }
    }

    /// <summary>Active pool difficulty if reported via <c>stratum.pools</c> or the suggested difficulty.</summary>
    public double? ActivePoolDifficulty
    {
        get
        {
            if (IsDualPoolMining)
            {
                var first = PrimaryPoolStatus?.PoolDifficulty;
                var second = SecondaryPoolStatus?.PoolDifficulty;
                return DualPoolSecondaryPercent >= DualPoolPrimaryPercent ? second ?? first : first ?? second;
            }
            if (StratumDetail?.Pools is { Count: > 0 } pools)
            {
                if (pools.FirstOrDefault(p => p.Active == true)?.PoolDifficulty is > 0 and var active) return active;
                if (pools[0].PoolDifficulty is > 0 and var first) return first;
            }
            return ReportedPoolDifficulty is > 0 ? ReportedPoolDifficulty : StratumSuggestedDifficulty;
        }
    }

    public int DualPoolPrimaryPercent => Math.Clamp(StratumDetail?.PoolBalance ?? 50, 1, 99);
    public int DualPoolSecondaryPercent => 100 - DualPoolPrimaryPercent;

    public StratumPoolStatus? PrimaryPoolStatus => StratumDetail?.Pools.Count > 0 ? StratumDetail.Pools[0] : null;
    public StratumPoolStatus? SecondaryPoolStatus => StratumDetail?.Pools.Count > 1 ? StratumDetail.Pools[1] : null;

    public string PrimaryPoolEndpoint =>
        StratumUrl.Length == 0 ? Strings.NotConfigured : $"{StratumHost.Clean(StratumUrl, true)}:{StratumPort}";

    public string FallbackPoolEndpoint =>
        FallbackStratumUrl.Length == 0 ? Strings.NotConfigured : $"{StratumHost.Clean(FallbackStratumUrl, true)}:{FallbackStratumPort}";

    /// <summary>The pool being mined on; both with their share in dual pool mode.</summary>
    public string ActivePoolEndpoint
    {
        get
        {
            if (IsDualPoolMining)
                return $"{StratumHost.Clean(StratumUrl, true)} ({DualPoolPrimaryPercent}%) + {StratumHost.Clean(FallbackStratumUrl, true)} ({DualPoolSecondaryPercent}%)";
            return IsUsingFallbackStratum && FallbackStratumUrl.Length > 0 ? FallbackPoolEndpoint : PrimaryPoolEndpoint;
        }
    }

    public string ActivePoolHost =>
        IsDualPoolMining ? $"{StratumHost.Clean(StratumUrl, true)} + {StratumHost.Clean(FallbackStratumUrl, true)}"
        : StratumHost.Clean(IsUsingFallbackStratum && FallbackStratumUrl.Length > 0 ? FallbackStratumUrl : StratumUrl, true);

    public MinedCoin PrimaryPoolCoin => MinedCoinDetector.Detect(StratumHost.Clean(StratumUrl, true), StratumUser,
        PrimaryPoolStatus?.NetworkDifficulty ?? (!IsUsingFallbackStratum ? NetworkDifficulty : null));

    public MinedCoin SecondaryPoolCoin => MinedCoinDetector.Detect(StratumHost.Clean(FallbackStratumUrl, true), FallbackStratumUser,
        SecondaryPoolStatus?.NetworkDifficulty ?? (IsUsingFallbackStratum ? NetworkDifficulty : null));

    /// <summary>The coin of the active pool, the larger share in dual pool mode.</summary>
    public MinedCoin ActivePoolCoin =>
        IsDualPoolMining ? (DualPoolSecondaryPercent > DualPoolPrimaryPercent ? SecondaryPoolCoin : PrimaryPoolCoin)
        : IsUsingFallbackStratum ? SecondaryPoolCoin : PrimaryPoolCoin;

    /// <summary><c>BTC</c> or <c>BTC+BCH</c> in dual pool mode with different coins.</summary>
    public string CoinShortText =>
        IsDualPoolMining && PrimaryPoolCoin != SecondaryPoolCoin ? $"{PrimaryPoolCoin.Symbol()}+{SecondaryPoolCoin.Symbol()}" : ActivePoolCoin.Symbol();

    /// <summary>Session best, or the all-time best where the firmware does not report one.</summary>
    public string SessionBestDiffText => BestSessionDiffNumeric > 0 ? BestSessionDiffFormatted : "—";

    /// <summary>Session best on pool 1; without per-pool figures the miner's own while it mines there alone.</summary>
    public double? PrimaryPoolSessionBestDiff =>
        PrimaryPoolStatus?.BestDiff is > 0 and var perPool ? perPool
        : !IsDualPoolMining && !IsUsingFallbackStratum && BestSessionDiffNumeric > 0 ? BestSessionDiffNumeric : null;

    /// <summary>Session best on pool 2 (secondary or fallback).</summary>
    public double? SecondaryPoolSessionBestDiff =>
        SecondaryPoolStatus?.BestDiff is > 0 and var perPool ? perPool
        : !IsDualPoolMining && IsUsingFallbackStratum && BestSessionDiffNumeric > 0 ? BestSessionDiffNumeric : null;

    /// <summary>Stratum user of the pool in use; in dual pool mode both workers when they differ.</summary>
    public string ActivePoolUser
    {
        get
        {
            if (IsDualPoolMining)
            {
                var w1 = Worker(StratumUser) ?? StratumUser;
                var w2 = Worker(FallbackStratumUser) ?? FallbackStratumUser;
                return w1 == w2 ? StratumUser : $"{w1} / {w2}";
            }
            return IsUsingFallbackStratum && FallbackStratumUser.Length > 0 ? FallbackStratumUser : StratumUser;
        }
    }

    /// <summary>The worker after the last dot of <c>wallet.worker</c>.</summary>
    public static string? Worker(string user)
    {
        var dot = user.LastIndexOf('.');
        return dot >= 0 && dot < user.Length - 1 ? user[(dot + 1)..] : null;
    }

    // Parsing

    public static MinerSystemInfo Parse(JsonObject o)
    {
        var info = new MinerSystemInfo
        {
            HashRate = D(o, "hashRate") ?? 0,
            HashRate1m = D(o, "hashRate_1m"),
            HashRate10m = D(o, "hashRate_10m"),
            HashRate1h = D(o, "hashRate_1h"),
            ExpectedHashrate = D(o, "expectedHashrate"),
            Temp = D(o, "temp") ?? 0,
            VrTemp = D(o, "vrTemp") is > 0 and var vr ? vr : null,
            ErrorPercentage = D(o, "errorPercentage"),
            FanSpeedPercent = D(o, "fanspeed") ?? 0,
            FanRpm = I(o, "fanrpm") ?? 0,
            Fan2SpeedPercent = D(o, "fanspeed2"),
            Fan2Rpm = (I(o, "fanrpm2") ?? I(o, "fan2rpm")) is > 0 and var fan2 ? fan2 : null,
            TargetTemp = D(o, "temptarget") ?? D(o, "pidTargetTemp"),
            OverheatMode = B(o, "overheat_mode") ?? false,
            OverheatTemp = D(o, "overheat_temp"),
            MiningPaused = B(o, "miningPaused") ?? false,
            ReportedPoolDifficulty = D(o, "poolDifficulty"),
            Otp = B(o, "otp") ?? false,
            Power = D(o, "power") ?? 0,
            CoreVoltageTargetMv = I(o, "coreVoltage") ?? 1200,
            FrequencyMhz = I(o, "frequency") ?? I(o, "actualFrequency") ?? 525,
            DefaultFrequencyMhz = I(o, "defaultFrequency"),
            DefaultCoreVoltageMv = I(o, "defaultCoreVoltage"),
            SharesAccepted = I(o, "sharesAccepted") ?? 0,
            SharesRejected = I(o, "sharesRejected") ?? 0,
            NetworkDifficulty = D(o, "networkDifficulty"),
            StratumUrl = S(o, "stratumURL") ?? "",
            StratumPort = I(o, "stratumPort") ?? 3333,
            StratumUser = S(o, "stratumUser") ?? "",
            StratumSuggestedDifficulty = D(o, "stratumSuggestedDifficulty") ?? D(o, "poolDifficulty"),
            FallbackStratumUrl = S(o, "fallbackStratumURL") ?? "",
            FallbackStratumPort = I(o, "fallbackStratumPort") ?? 3333,
            FallbackStratumUser = S(o, "fallbackStratumUser") ?? "",
            BlockHeight = I(o, "blockHeight"),
            BlockScriptSig = S(o, "scriptsig"),
            BlockSignals = (o["blockSignals"] as JsonArray)?.Select(s => s?.ToString() ?? "").Where(s => s.Length > 0).ToList(),
            CoinbaseOutputs = (o["coinbaseOutputs"] as JsonArray)?.OfType<JsonObject>()
                .Select(c => new CoinbaseOutput(D(c, "value") ?? 0, S(c, "address") ?? "", B(c, "isUserOutput") ?? false)).ToList(),
            CoinbaseOthersCount = I(o, "coinbaseOthersCount"),
            CoinbaseOthersValueSats = D(o, "coinbaseOthersValueSatoshis"),
            CoinbaseValueTotalSats = D(o, "coinbaseValueTotalSatoshis"),
            CoinbaseValueUserSats = D(o, "coinbaseValueUserSatoshis"),
            CoinbasePayoutStatus = S(o, "coinbasePayoutStatus"),
            StratumDecodeCoinbase = B(o, "stratumDecodeCoinbase"),
            FallbackStratumDecodeCoinbase = B(o, "fallbackStratumDecodeCoinbase"),
            StratumTls = B(o, "stratumTLS"),
            FallbackStratumTls = B(o, "fallbackStratumTLS"),
            DeviceModel = S(o, "deviceModel"),
            AsicModel = S(o, "ASICModel") ?? S(o, "asicModel") ?? "BM1370",
            BoardVersion = S(o, "boardVersion"),
            SmallCoreCount = I(o, "smallCoreCount"),
            Hostname = S(o, "hostname") ?? "miner",
            MacAddress = S(o, "macAddr"),
            Ssid = S(o, "ssid"),
            WifiRssi = I(o, "wifiRSSI") ?? I(o, "rssi"),
            FirmwareVersion = S(o, "version") ?? "",
            AxeOsVersion = S(o, "axeOSVersion"),
            UptimeSeconds = I(o, "uptimeSeconds") ?? I(o, "uptime") ?? 0,
            ResetReason = o["resetReason"]?.GetValue<string>() ?? o["lastResetReason"]?.GetValue<string>(),
            FreeHeap = I(o, "freeHeap"),
        };

        info.CoreVoltageActualMv = I(o, "coreVoltageActual") ?? info.CoreVoltageTargetMv;
        info.AutoFanModeRaw = I(o, "autofanspeed") ?? (B(o, "autofanspeed") == false ? 0 : 1);
        info.AutoFanSpeed = info.AutoFanModeRaw != 0;

        // Bitaxe and NerdOS often report mV and mA; values above 100 are normalized to V and A.
        var voltage = D(o, "voltage") ?? 0;
        info.VoltageVolts = voltage > 100 ? voltage / 1000 : voltage;
        if (D(o, "currentA") is > 0 and var amps) info.CurrentAmps = amps;
        else
        {
            var current = D(o, "current") ?? 0;
            info.CurrentAmps = current > 100 ? current / 1000 : current;
        }

        (info.BestDiffFormatted, info.BestDiffNumeric) = Difficulty(o, "bestDiff", "bestDifficulty");
        (info.BestSessionDiffFormatted, info.BestSessionDiffNumeric) = Difficulty(o, "bestSessionDiff", "bestSessionDifficulty");

        info.StratumDetail = ParseStratum(o["stratum"] as JsonObject);
        info.AxePools = (o["pools"] as JsonArray)?.OfType<JsonObject>()
            .Where(p => I(p, "id") != null)
            .Select(p => new AxeOsPool(I(p, "id")!.Value, S(p, "stratumURL") ?? "", I(p, "stratumPort") ?? 3333, S(p, "stratumUser") ?? "",
                S(p, "stratumPassword"), (JsonObject)p.DeepClone()))
            .ToList();
        info.PrimaryPoolIndex = I(o, "primaryPoolIndex");
        info.SecondaryPoolIndex = I(o, "secondaryPoolIndex");
        info.IsUsingFallbackStratum = B(o, "isUsingFallbackStratum") ?? info.StratumDetail?.UsingFallback ?? false;

        // Pool latency: top level `responseTime` / `lastpingrtt`, else the active slot.
        if (D(o, "responseTime") is > 0 and var rt) info.MinerReportedPoolLatencyMs = rt;
        else if (D(o, "lastpingrtt") is > 0 and var ping) info.MinerReportedPoolLatencyMs = ping;
        else if (info.StratumDetail?.Pools is { Count: > 0 } pools)
            info.MinerReportedPoolLatencyMs = (pools.FirstOrDefault(p => p.Active == true) ?? pools.FirstOrDefault(p => p.Connected == true) ?? pools[0]).EffectiveLatencyMs;

        // AxeOS ≤ 2.9 and NerdOS report `asicCount`, AxeOS 2.12+ one entry per chip in `hashrateMonitor.asics`;
        // otherwise the board table knows the count.
        var monitorAsics = ((o["hashrateMonitor"] as JsonObject)?["asics"] as JsonArray)?.Count;
        if (I(o, "asicCount") is > 0 and var count) info.AsicCount = count;
        else if (monitorAsics is > 0) info.AsicCount = monitorAsics.Value;
        else if (info.BoardVersion != null && BitaxeFamily.ForBoard(info.BoardVersion) is { } family) info.AsicCount = family.AsicCount;
        else if (info.DeviceModel != null && NerdOsModel.ForDeviceModel(info.DeviceModel) is { } model) info.AsicCount = model.AsicCount;

        return info;
    }

    private static StratumDetail? ParseStratum(JsonObject? s)
    {
        if (s == null) return null;
        var pools = (s["pools"] as JsonArray)?.OfType<JsonObject>().Select(p => new StratumPoolStatus(
            B(p, "active"), B(p, "connected"), D(p, "poolDifficulty"), D(p, "networkDifficulty"),
            I(p, "accepted"), I(p, "rejected"), D(p, "pingRtt"), D(p, "responseTime"), D(p, "bestDiff"))).ToList() ?? [];
        return new StratumDetail(I(s, "poolMode"), I(s, "activePoolMode"), I(s, "poolBalance"), B(s, "usingFallback"), pools);
    }

    private static (string Formatted, double Numeric) Difficulty(JsonObject o, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (o[key] is not JsonValue v) continue;
            if (Number(v) is { } d) return (MinerFormatting.FormatDifficulty(d), d);
            if (v.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)) return (text.Trim(), MinerFormatting.ParseDifficulty(text));
        }
        return ("0", 0);
    }

    // Lenient readers: numbers may come as numbers, strings or booleans.

    internal static double? D(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (Number(v) is { } n) return n;
        if (v.TryGetValue(out string? s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    internal static int? I(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (Number(v) is { } n) return (int)Math.Round(Math.Clamp(n, int.MinValue, int.MaxValue));
        if (v.TryGetValue(out string? s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return (int)Math.Round(parsed);
        return null;
    }

    /// <summary>A JSON number, whatever type it was read or created with (parsed values convert, CLR values only to their own type).</summary>
    private static double? Number(JsonValue v)
    {
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out decimal m)) return (double)m;
        if (v.TryGetValue(out float f)) return f;
        return null;
    }

    internal static bool? B(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue(out bool b)) return b;
        if (Number(v) is { } n) return n != 0;
        if (v.TryGetValue(out string? s))
        {
            var lower = s.Trim().ToLowerInvariant();
            if (lower is "1" or "true" or "yes" or "on") return true;
            if (lower is "0" or "false" or "no" or "off") return false;
        }
        return null;
    }

    internal static string? S(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
