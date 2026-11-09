using System.Globalization;
using System.Text.RegularExpressions;

namespace MinerGuru.Core.Models;

/// <summary>
/// One ASIC result with its difficulty, parsed from the miner's live log. Results at or above the pool difficulty
/// are submitted to the pool as shares.
/// <list type="bullet">
/// <item>AxeOS: <c>asic_result: ID: …, Nonce F0AE9D21 diff 1284.3 of 874.</c></item>
/// <item>NerdOS: <c>asic_result: (Sec) Job ID: 78 … diff 141114.6/3075/791M</c></item>
/// </list>
/// </summary>
public sealed partial record ShareSample(DateTime Date, double Difficulty, double? PoolDifficulty, int? Pool)
{
    public bool IsSubmitted => PoolDifficulty is not { } pool || Difficulty >= pool;

    /// <summary>Parses an <c>asic_result</c> log line; <c>null</c> for every other line.</summary>
    public static ShareSample? Parse(string line, DateTime date)
    {
        if (!line.Contains("asic_result", StringComparison.OrdinalIgnoreCase)) return null;
        var match = DiffPattern().Match(line);
        if (!match.Success) return null;
        var difficulty = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        double? poolDifficulty = match.Groups[2].Success ? double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : null;
        var pool = PoolPattern().Match(line);
        return new ShareSample(date, difficulty, poolDifficulty, pool.Success ? (pool.Groups[1].Value == "Pri" ? 1 : 2) : null);
    }

    [GeneratedRegex(@"\bdiff\s+([0-9]+(?:\.[0-9]+)?)(?:(?:\s+of\s+|/)([0-9]+(?:\.[0-9]+)?))?")]
    private static partial Regex DiffPattern();

    [GeneratedRegex(@"\((Pri|Sec)\)")]
    private static partial Regex PoolPattern();
}
