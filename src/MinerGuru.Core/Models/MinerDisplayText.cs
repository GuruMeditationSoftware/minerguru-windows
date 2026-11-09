using System.Globalization;

namespace MinerGuru.Core.Models;

/// <summary>Round-trip time to the pool, rated like the Mac app.</summary>
public enum LatencyQuality { Excellent, Good, Fair, Poor, Unavailable }

public static class LatencyRating
{
    public static LatencyQuality Evaluate(double? ms) => ms switch
    {
        null or <= 0 => LatencyQuality.Unavailable,
        < 45 => LatencyQuality.Excellent,
        < 100 => LatencyQuality.Good,
        < 200 => LatencyQuality.Fair,
        _ => LatencyQuality.Poor,
    };

    public static string Label(this LatencyQuality quality) => quality switch
    {
        LatencyQuality.Excellent => Strings.Excellent,
        LatencyQuality.Good => Strings.Good,
        LatencyQuality.Fair => Strings.Fair,
        LatencyQuality.Poor => Strings.High,
        _ => Strings.NoData,
    };
}

/// <summary>The subtitle lines of the key figures, worded as on the Mac.</summary>
public static class MinerDisplayText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool HasSecondFan(this MinerSystemInfo info) => (info.Fan2Rpm ?? 0) > 0;

    public static string HashrateAveragesText(this MinerSystemInfo info)
    {
        if (info.HashRate1m is { } m && info.HashRate1h is { } h)
            return $"1m: {MinerFormatting.FormatHashrate(m)} · 1h: {MinerFormatting.FormatHashrate(h)}";
        if (info.HashRate1m is { } m1) return $"1m Ø: {MinerFormatting.FormatHashrate(m1)}";
        return string.Create(Inv, $"{info.HashRate:0} GH/s Live");
    }

    /// <summary><c>PID Auto</c> (NerdQAxe), <c>Auto</c> or <c>Manuell</c>.</summary>
    public static string FanModeLabel(this MinerSystemInfo info) =>
        info.AutoFanModeRaw == 2 ? "PID Auto" : info.AutoFanSpeed ? Strings.AutoMode : Strings.Manual;

    public static string FanPercentText(this MinerSystemInfo info) =>
        info.HasSecondFan() && info.Fan2SpeedPercent is { } fan2
            ? string.Create(Inv, $"{info.FanSpeedPercent:0} / {fan2:0}")
            : string.Create(Inv, $"{info.FanSpeedPercent:0}");

    public static string FanRpmText(this MinerSystemInfo info) =>
        info.HasSecondFan() ? $"{info.FanRpm} & {info.Fan2Rpm} RPM · {info.FanModeLabel()}" : $"{info.FanRpm} RPM · {info.FanModeLabel()}";

    public static string CoolingText(this MinerSystemInfo info, double warningThresholdC) =>
        info.VrTemp is { } vr
            ? string.Format(Strings.VoltageRegulatorVRC, DisplayFormat.Decimal(vr, 1))
            : info.Temp >= warningThresholdC ? Strings.CoolingElevated : Strings.CoolingOptimal;

    public static string PowerDetailText(this MinerSystemInfo info) =>
        string.Create(Inv, $"{info.VoltageVolts:0.00} V · {info.CurrentAmps:0.00} A");

    public static string DailyEnergyText(this MinerSystemInfo info) =>
        string.Create(Inv, $"~{info.Power * 24 / 1000:0.00} kWh / 24h");

    public static string CoreVoltageText(this MinerSystemInfo info) =>
        string.Format(Strings.TargetMVActualMV, info.CoreVoltageTargetMv, info.CoreVoltageActualMv);

    public static string SharesText(this MinerSystemInfo info) =>
        string.Create(Inv, $"Shares: {info.SharesAccepted} ok / {info.SharesRejected} rej ({info.ShareRejectionRatePercent:0.00}%)");

    public static string PoolLatencyDetailText(this MinerDevice miner)
    {
        if (miner.LatestInfo is not { } info) return "—";
        if (info.IsDualPoolMining)
        {
            string Ms(double? v) => v is { } x ? x.ToString("0", Inv) : "—";
            return $"Dual ({info.DualPoolPrimaryPercent}%/{info.DualPoolSecondaryPercent}%) · P1: {Ms(info.PrimaryPoolStatus?.EffectiveLatencyMs)} ms / P2: {Ms(info.SecondaryPoolStatus?.EffectiveLatencyMs)} ms";
        }
        return $"{LatencyRating.Evaluate(miner.ActivePoolLatencyMs).Label()} · {info.ActivePoolHost}";
    }

    /// <summary><c>Gamma 601 · BCH</c>: the model without the family, then the coin (both in dual pool mode).</summary>
    public static string SidebarMetaLine(this MinerDevice miner)
    {
        var spec = DeviceCardSpec.For(miner);
        var model = spec.Model.Length == 0 ? miner.HardwareTitle : spec.Model;
        if (miner.LatestInfo is not { } info) return model;
        var coin = info.IsDualPoolMining && info.PrimaryPoolCoin != info.SecondaryPoolCoin
            ? $"{info.PrimaryPoolCoin.Symbol()}+{info.SecondaryPoolCoin.Symbol()}"
            : info.ActivePoolCoin.Symbol();
        return $"{model} · {coin}";
    }
}

/// <summary>What a board drawing shows of a miner: family, model line, ASIC count and display kind.</summary>
public sealed record DeviceCardSpec(string Family, string Model, int ChipCount, string? ChipModel, bool HasTft)
{
    public static DeviceCardSpec For(MinerDevice miner) => From(miner.HardwareTitle, miner.LatestInfo);

    public static DeviceCardSpec From(string title, MinerSystemInfo? info)
    {
        var words = title.Split(' ', 2);
        var lower = title.ToLowerInvariant();
        var nerd = info != null ? info.Firmware() == MinerFirmware.NerdOs : lower.StartsWith("nerd");
        return new DeviceCardSpec(
            words[0].ToLowerInvariant(),
            words.Length > 1 ? words[1] : "",
            info?.AsicCount is > 0 and var count ? count : GuessChipCount(lower),
            info?.AsicModel,
            nerd);
    }

    private static int GuessChipCount(string title)
    {
        if (title.Contains("eko")) return 12;
        if (title.Contains("octaxe")) return 8;
        if (title.Contains("hex") || title.Contains("haxe")) return 6;
        if (title.Contains("qaxe") || title.Contains("qx") || title.Contains("q137")) return 4;
        if (title.Contains("duo") || title.Contains("gt")) return 2;
        return 1;
    }

    /// <summary>Chip grid as columns × rows.</summary>
    public (int Columns, int Rows) ChipGrid => ChipCount switch
    {
        <= 1 => (1, 1),
        2 => (2, 1),
        <= 4 => (2, 2),
        <= 6 => (3, 2),
        <= 8 => (4, 2),
        _ => ((int)Math.Ceiling(ChipCount / 2.0), 2),
    };

    /// <summary><c>Gamma 601 · 6× BM1370</c>, the silkscreen line under the chips.</summary>
    public string ModelLine => string.Join(" · ", new[] { Model, ChipModel is { Length: > 0 } c ? (ChipCount > 1 ? $"{ChipCount}× {c}" : c) : null }
        .Where(s => !string.IsNullOrEmpty(s)));
}
