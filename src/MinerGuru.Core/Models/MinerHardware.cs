namespace MinerGuru.Core.Models;

/// <summary>Firmware family of a miner. Decides which API fields exist and how settings are written.</summary>
public enum MinerFirmware
{
    /// <summary>ESP-Miner / AxeOS (all Bitaxe boards). Always reports <c>boardVersion</c>.</summary>
    AxeOs,
    /// <summary>ESP-Miner-NerdQAxePlus / NerdOS. Reports <c>deviceModel</c>, no <c>boardVersion</c>.</summary>
    NerdOs,
}

/// <summary>
/// Bitaxe board families from ESP-Miner's <c>device_config.h</c>. Board versions of a family share a number range,
/// so new revisions are recognised too.
/// </summary>
public sealed record BitaxeFamily(string Name, int AsicCount, AsicTuning? Tuning)
{
    public static readonly BitaxeFamily Max = new("Bitaxe Max", 1, null);
    public static readonly BitaxeFamily Ultra = new("Bitaxe Ultra", 1, null);
    public static readonly BitaxeFamily Hex = new("Bitaxe Hex", 6, null);
    public static readonly BitaxeFamily Supra = new("Bitaxe Supra", 1, null);
    public static readonly BitaxeFamily Gamma = new("Bitaxe Gamma", 1, null);
    public static readonly BitaxeFamily GammaDuo = new("Bitaxe Gamma Duo", 2, AsicTuning.Bm1370Xp);
    public static readonly BitaxeFamily SupraHex = new("Bitaxe Supra Hex", 6, null);
    public static readonly BitaxeFamily GammaTurbo = new("Bitaxe GT", 2, null);
    public static readonly BitaxeFamily NajaDuo = new("Bitaxe Naja Duo", 2, null);
    public static readonly BitaxeFamily GammaHex = new("Bitaxe Gamma Hex", 6, AsicTuning.Bm1370Hex);

    /// <summary>Family for an AxeOS <c>boardVersion</c> such as <c>601</c>, <c>302</c> or the early <c>2.2</c> / <c>0.11</c>.</summary>
    public static BitaxeFamily? ForBoard(string boardVersion)
    {
        var board = boardVersion.Trim();
        if (board == "2.2") return Max;
        if (board == "0.11") return Ultra;
        if (!int.TryParse(board, out var n)) return null;
        return n switch
        {
            >= 100 and < 200 => Max,
            >= 200 and < 300 => Ultra,
            >= 300 and < 400 => Hex,
            >= 400 and < 500 => Supra,
            >= 600 and < 650 => Gamma,
            >= 650 and < 700 => GammaDuo,
            >= 700 and < 800 => SupraHex,
            >= 800 and < 900 => GammaTurbo,
            >= 1200 and < 1300 => NajaDuo,
            >= 1300 and < 1400 => GammaHex,
            _ => null,
        };
    }
}

/// <summary>NerdOS boards from ESP-Miner-NerdQAxePlus <c>main/boards/*.cpp</c>.</summary>
public sealed record NerdOsModel(string Name, int AsicCount)
{
    // Matched against the lowercased `deviceModel`, most specific first. An empty name keeps the reported spelling.
    private static readonly (string[] Prefixes, NerdOsModel Model)[] Table =
    [
        (["nerdqaxeplusplus", "nerdqaxe++"], new("NerdQAxe++", 4)),
        (["nerdqaxeplus", "nerdqaxe+"], new("NerdQAxe+", 4)),
        (["nerdoctaxe"], new("", 8)),
        (["nerdhaxe"], new("", 6)),
        (["nerdeko"], new("", 12)),
        (["nerdqx", "q1370", "q1373", "nerdqaxe"], new("", 4)),
        (["nerdaxe"], new("", 1)),
    ];

    public static NerdOsModel? ForDeviceModel(string deviceModel)
    {
        var lower = deviceModel.Trim().ToLowerInvariant();
        return Table.FirstOrDefault(e => e.Prefixes.Any(lower.StartsWith)).Model;
    }
}

/// <summary>Factory clock and the steps the firmware offers for one ASIC (ESP-Miner <c>AsicConfig</c>).</summary>
public sealed record AsicTuning(int DefaultFrequencyMhz, int DefaultVoltageMv, int[] FrequencyOptions, int[] VoltageOptions)
{
    public static readonly AsicTuning Bm1397 = new(425, 1400, [400, 425, 450, 475, 485, 500, 525, 550, 575, 600], [1100, 1150, 1200, 1250, 1300, 1350, 1400, 1450, 1500]);
    public static readonly AsicTuning Bm1366 = new(485, 1200, [400, 425, 450, 475, 485, 500, 525, 550, 575], [1100, 1150, 1200, 1250, 1300]);
    public static readonly AsicTuning Bm1368 = new(490, 1166, [400, 425, 450, 475, 485, 490, 500, 525, 550, 575], [1100, 1150, 1166, 1200, 1250, 1300]);
    public static readonly AsicTuning Bm1370 = new(525, 1150, [400, 490, 525, 550, 600, 625, 690], [1000, 1060, 1100, 1150, 1200, 1250]);
    /// <summary>Gamma Duo runs the BM1370 at lower clocks.</summary>
    public static readonly AsicTuning Bm1370Xp = new(400, 1150, [350, 375, 380, 400, 410], [1000, 1060, 1100, 1150, 1200, 1250]);
    /// <summary>Gamma Hex ships at the top BM1370 clock.</summary>
    public static readonly AsicTuning Bm1370Hex = new(690, 1200, [400, 490, 525, 550, 600, 625, 690], [1000, 1060, 1100, 1150, 1200, 1250]);
    public static readonly AsicTuning Bm1373 = new(327, 1000, [327, 350, 375, 380, 400, 410], [1000, 1060, 1100, 1150, 1200, 1250]);

    /// <summary>Tuning for an <c>ASICModel</c> string (<c>BM1370</c>, <c>BM1372/BM1373</c>, …).</summary>
    public static AsicTuning? ForAsic(string model)
    {
        var upper = model.ToUpperInvariant();
        if (upper.Contains("1373") || upper.Contains("1372")) return Bm1373;
        if (upper.Contains("1370")) return Bm1370;
        if (upper.Contains("1368")) return Bm1368;
        if (upper.Contains("1366")) return Bm1366;
        if (upper.Contains("1397")) return Bm1397;
        return null;
    }

    /// <summary>Eco, standard and boost: the firmware steps next to the standard clock and voltage.</summary>
    public IReadOnlyList<(string Name, int FrequencyMhz, int VoltageMv)> Presets(int? standardFrequencyMhz = null, int? standardVoltageMv = null)
    {
        var frequency = standardFrequencyMhz ?? DefaultFrequencyMhz;
        var voltage = standardVoltageMv ?? DefaultVoltageMv;
        var result = new List<(string, int, int)>();
        var eco = FrequencyOptions.Where(f => f <= frequency * 0.95).ToArray();
        var ecoVoltage = VoltageOptions.Where(v => v < voltage).ToArray();
        if (eco.Length > 0 && ecoVoltage.Length > 0) result.Add(("Eco", eco[^1], ecoVoltage[^1]));
        result.Add(("Standard", frequency, voltage));
        var boost = FrequencyOptions.Where(f => f >= frequency * 1.04).ToArray();
        var boostVoltage = VoltageOptions.Where(v => v > voltage).ToArray();
        if (boost.Length > 0 && boostVoltage.Length > 0) result.Add(("Boost", boost[0], boostVoltage[0]));
        return result;
    }
}

public static class MinerHardwareExtensions
{
    public static MinerFirmware Firmware(this MinerSystemInfo info)
    {
        if (!string.IsNullOrEmpty(info.BoardVersion)) return MinerFirmware.AxeOs;
        if (!string.IsNullOrEmpty(info.DeviceModel) || info.StratumDetail != null) return MinerFirmware.NerdOs;
        return MinerFirmware.AxeOs;
    }

    public static BitaxeFamily? BitaxeFamily(this MinerSystemInfo info) =>
        info.Firmware() == MinerFirmware.AxeOs && info.BoardVersion != null ? Models.BitaxeFamily.ForBoard(info.BoardVersion) : null;

    /// <summary><c>Bitaxe Gamma 601</c>, <c>Bitaxe Hex 302</c>, <c>NerdQAxe++</c>, <c>NerdOCTAXE-γ</c> …</summary>
    public static string DetectedHardwareTitle(this MinerSystemInfo info)
    {
        if (info.Firmware() == MinerFirmware.NerdOs)
        {
            var model = info.DeviceModel?.Trim();
            if (!string.IsNullOrEmpty(model))
            {
                var known = NerdOsModel.ForDeviceModel(model)?.Name ?? "";
                return known.Length == 0 ? model : known;
            }
            return $"NerdOS ({info.AsicCount}x {info.AsicModel})";
        }
        var board = info.BoardVersion?.Trim() ?? "";
        if (info.BitaxeFamily() is { } family) return board.Length == 0 ? family.Name : $"{family.Name} {board}";
        // Very old AxeOS reported the family in `deviceModel` instead of a known board version.
        var oldModel = info.DeviceModel?.Trim();
        if (!string.IsNullOrEmpty(oldModel)) return $"Bitaxe {char.ToUpperInvariant(oldModel[0])}{oldModel[1..]}";
        return board.Length == 0 ? $"Bitaxe ({info.AsicModel})" : $"Bitaxe ({info.AsicModel} v{board})";
    }

    public static AsicTuning? AsicTuning(this MinerSystemInfo info) => info.BitaxeFamily()?.Tuning ?? Models.AsicTuning.ForAsic(info.AsicModel);

    /// <summary>Expected hashrate in GH/s at a clock: frequency × small cores × chips, as ESP-Miner computes it.</summary>
    public static double? ExpectedHashrateGhs(this MinerSystemInfo info, int frequencyMhz) =>
        info.SmallCoreCount is > 0 ? frequencyMhz * (double)info.SmallCoreCount * info.AsicCount / 1000 : null;
}
