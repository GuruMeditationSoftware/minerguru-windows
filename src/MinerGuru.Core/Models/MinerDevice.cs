
namespace MinerGuru.Core.Models;

public enum ConnectionState { Unknown, Checking, Online, Offline }

/// <summary>One telemetry sample per refresh, for the charts.</summary>
public sealed record TelemetrySample(DateTime Timestamp, double HashRateGhs, double AsicTempC, double? VrTempC, double PowerWatts, double? PoolLatencyMs, double FanPercent);

/// <summary>Stored entry of a miner in the user's network.</summary>
public sealed class PersistedMinerConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IpAddress { get; set; } = "";
    public string? CustomName { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public string? CachedHostname { get; set; }
    public string? CachedHardwareModel { get; set; }
}

/// <summary>Clock preset of a chip (eco, standard, boost).</summary>
public sealed record MinerClockPreset(string Name, string Subtitle, int FrequencyMhz, int CoreVoltageMv);

/// <summary>Live state and telemetry history of a miner.</summary>
public sealed class MinerDevice
{
    public MinerDevice(PersistedMinerConfig config, ConnectionState state = ConnectionState.Unknown)
    {
        Id = config.Id;
        IpAddress = config.IpAddress;
        CustomName = config.CustomName;
        AddedAt = config.AddedAt;
        CachedHostname = config.CachedHostname;
        CachedHardwareModel = config.CachedHardwareModel;
        State = state;
    }

    public Guid Id { get; }
    public string IpAddress { get; set; }
    public string? CustomName { get; set; }
    public DateTime AddedAt { get; }
    public DateTime? LastSeenAt { get; set; }
    public string? CachedHostname { get; set; }
    public string? CachedHardwareModel { get; set; }

    public ConnectionState State { get; set; }
    public string? OfflineReason { get; set; }
    public MinerSystemInfo? LatestInfo { get; set; }
    public string? RawJson { get; set; }
    public List<TelemetrySample> History { get; } = [];

    public bool IsOnline => State == ConnectionState.Online;

    public PersistedMinerConfig PersistedConfig => new()
    {
        Id = Id,
        IpAddress = IpAddress,
        CustomName = CustomName,
        AddedAt = AddedAt,
        CachedHostname = LatestInfo?.Hostname ?? CachedHostname,
        CachedHardwareModel = LatestInfo?.DetectedHardwareTitle() ?? CachedHardwareModel,
    };

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomName)) return CustomName.Trim();
            if (!string.IsNullOrEmpty(LatestInfo?.Hostname)) return LatestInfo.Hostname;
            if (!string.IsNullOrEmpty(CachedHostname)) return CachedHostname;
            return IpAddress;
        }
    }

    public string HardwareTitle => LatestInfo?.DetectedHardwareTitle() ?? CachedHardwareModel ?? Strings.UnknownMiner;

    public bool IsNerdOs => LatestInfo?.Firmware() == MinerFirmware.NerdOs;
    public bool RequiresOtp => LatestInfo?.Otp == true;

    /// <summary>NerdOS reports its dual-pool settings in the <c>stratum</c> block; AxeOS has no dual-pool mode.</summary>
    public bool SupportsDualPool => IsNerdOs && LatestInfo?.StratumDetail != null;

    /// <summary>Pause needs AxeOS 2.15+; there <c>pools[]</c> is present.</summary>
    public bool CanPause => LatestInfo is { } info && info.Firmware() == MinerFirmware.AxeOs && info.AxePools != null;

    /// <summary>A miner on its fallback pool: this PC reached the primary pool in the last poll.</summary>
    public bool PrimaryPoolReachable { get; set; }

    /// <summary>Latency of the active pool: miner-reported, weighted by share in dual pool mode.</summary>
    public double? ActivePoolLatencyMs
    {
        get
        {
            if (LatestInfo is not { } info) return null;
            if (info.IsDualPoolMining)
            {
                var p1 = info.PrimaryPoolStatus?.EffectiveLatencyMs;
                var p2 = info.SecondaryPoolStatus?.EffectiveLatencyMs;
                if (p1 is { } a && p2 is { } b) return a * info.DualPoolPrimaryPercent / 100 + b * info.DualPoolSecondaryPercent / 100;
                return p2 ?? p1 ?? info.MinerReportedPoolLatencyMs;
            }
            return info.MinerReportedPoolLatencyMs;
        }
    }

    /// <summary>Eco / standard / boost on the clock steps of this chip; NerdOS reports its own factory clock.</summary>
    public IReadOnlyList<MinerClockPreset> RecommendedPresets
    {
        get
        {
            if (LatestInfo is not { } info || info.AsicTuning() is not { } tuning) return [];
            var nerd = info.Firmware() == MinerFirmware.NerdOs;
            return tuning.Presets(nerd ? info.DefaultFrequencyMhz : null, nerd ? info.DefaultCoreVoltageMv : null)
                .Select(p => new MinerClockPreset(p.Name,
                    info.ExpectedHashrateGhs(p.FrequencyMhz) is { } ghs ? "≈ " + MinerFormatting.FormatHashrate(ghs) : "",
                    p.FrequencyMhz, p.VoltageMv))
                .ToList();
        }
    }

    /// <summary>Clock field range: the chip's steps (else 350–800 MHz), always with the miner's own and factory value.</summary>
    public (int Min, int Max) FrequencyRange =>
        SettingRange(LatestInfo?.AsicTuning()?.FrequencyOptions, (350, 800), LatestInfo?.FrequencyMhz, LatestInfo?.DefaultFrequencyMhz);

    /// <summary>Core voltage field range, like <see cref="FrequencyRange"/> (else 1000–1400 mV).</summary>
    public (int Min, int Max) CoreVoltageRange =>
        SettingRange(LatestInfo?.AsicTuning()?.VoltageOptions, (1000, 1400), LatestInfo?.CoreVoltageTargetMv, LatestInfo?.DefaultCoreVoltageMv);

    /// <summary>The current value stays inside: a fan change must not rewrite a clock outside the usual steps.</summary>
    private static (int Min, int Max) SettingRange(int[]? options, (int Min, int Max) fallback, params int?[] values)
    {
        int[] bounds = options is { Length: > 0 } ? options : [fallback.Min, fallback.Max];
        var known = bounds.Concat(values.Where(v => v > 0).Select(v => v!.Value)).ToList();
        return (known.Min(), known.Max());
    }
}
