using System.Globalization;

namespace MinerGuru.Core;

/// <summary>
/// Technical miner figures. Uses the dot notation of the miners' AxeOS web UI (<c>1.05 TH/s</c>, <c>4.40G</c>)
/// on purpose; prices and percentages use <see cref="DisplayFormat"/> (notation of the UI language).
/// </summary>
public static class MinerFormatting
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Hashrate given in GH/s as TH/s (from 1000 GH/s) or GH/s.</summary>
    public static string FormatHashrate(double ghs)
    {
        var (value, unit) = SplitHashrate(ghs);
        return $"{value} {unit}";
    }

    /// <summary>Value and unit apart, for large figures.</summary>
    public static (string Value, string Unit) SplitHashrate(double ghs)
    {
        if (ghs <= 0) return ("0", "GH/s");
        if (ghs >= 1000) return ((ghs / 1000).ToString("0.00", Inv), "TH/s");
        return (ghs.ToString("0", Inv), "GH/s");
    }

    public static string FormatUptime(int seconds)
    {
        if (seconds <= 0) return "—";
        int days = seconds / 86_400, hours = seconds % 86_400 / 3_600, minutes = seconds % 3_600 / 60;
        if (days > 0) return $"{days}d {hours}h {minutes}m";
        if (hours > 0) return $"{hours}h {minutes}m";
        return $"{Math.Max(1, minutes)}m";
    }

    public static string FormatLatency(double? ms)
    {
        if (ms is not > 0) return "—";
        return ms < 10 ? $"{ms.Value.ToString("0.0", Inv)} ms" : $"{ms.Value.ToString("0", Inv)} ms";
    }

    /// <summary>Difficulty with K/M/G/T/P, e.g. <c>4.40G</c>, as the firmware shows it.</summary>
    public static string FormatDifficulty(double value)
    {
        if (value <= 0) return "0";
        (double, string)[] units = [(1e15, "P"), (1e12, "T"), (1e9, "G"), (1e6, "M"), (1e3, "K")];
        foreach (var (threshold, suffix) in units)
        {
            if (value >= threshold)
            {
                var scaled = value / threshold;
                return scaled.ToString(scaled >= 100 ? "0" : "0.00", Inv) + suffix;
            }
        }
        return value.ToString("0", Inv);
    }

    /// <summary>Reads <c>4.40G</c>, <c>885M</c> or a plain number back into a value.</summary>
    public static double ParseDifficulty(string raw)
    {
        var s = raw.Trim().ToUpperInvariant();
        if (s.Length == 0) return 0;
        var multiplier = s[^1] switch { 'K' => 1e3, 'M' => 1e6, 'G' => 1e9, 'T' => 1e12, 'P' => 1e15, _ => 0 };
        if (multiplier > 0 && double.TryParse(s[..^1].Trim(), NumberStyles.Float, Inv, out var scaled)) return scaled * multiplier;
        return double.TryParse(s, NumberStyles.Float, Inv, out var plain) ? plain : 0;
    }
}

/// <summary>Prices, percentages and plain numbers in the notation of the UI language.</summary>
public static class DisplayFormat
{
    public static string Decimal(double value, int fractionDigits) => value.ToString("N" + fractionDigits, Lang.Culture);

    /// <summary>Up to <paramref name="maxFractionDigits"/> fraction digits, trailing zeros dropped: 3.125 → <c>3,125</c>, 275 → <c>275</c>.</summary>
    public static string DecimalUpTo(double value, int maxFractionDigits) =>
        value.ToString("#,##0." + new string('#', maxFractionDigits), Lang.Culture);

    public static string Integer(long value) => value.ToString("N0", Lang.Culture);

    public static string Percent(double value, int fractionDigits) =>
        Lang.English ? $"{Decimal(value, fractionDigits)}%" : $"{Decimal(value, fractionDigits)} %";

    public static string Time(DateTime time) => time.ToString("T", Lang.Culture);
}
