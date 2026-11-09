using MinerGuru.Core.Models;

namespace MinerGuru.Core.Services;

/// <summary>One pool a miner works for, as far as the Stratum probe needs it.</summary>
/// <param name="User">The miner's own Stratum user; the payout check runs against its address.</param>
/// <param name="IsPrimary">The first pool of the miner, not the second.</param>
/// <param name="SharePercent">Hashrate share in dual pool mode, <c>null</c> when the miner works for one pool only.</param>
public sealed record BlockProbeTarget(string Host, int Port, bool Tls, string User, MinedCoin Coin, bool IsPrimary, int? SharePercent = null)
{
    /// <summary>The probe logs in as <c>wallet.minerguru</c>, so it never takes the miner's worker name.</summary>
    public string ProbeUser => $"{User.Split('.', 2)[0]}.minerguru";

    /// <summary>
    /// Pools to probe for a miner: none if the firmware reports the block itself (AxeOS 2.13+), both in dual pool
    /// mode, otherwise the one delivering work.
    /// </summary>
    public static IReadOnlyList<BlockProbeTarget> TargetsFor(MinerSystemInfo info)
    {
        if (BlockTemplateSummary.From(info) != null) return [];
        var primary = Make(info.StratumUrl, info.StratumPort, info.StratumTls, info.StratumUser, info.PrimaryPoolCoin, true);
        var secondary = Make(info.FallbackStratumUrl, info.FallbackStratumPort, info.FallbackStratumTls, info.FallbackStratumUser, info.SecondaryPoolCoin, false);
        if (info.IsDualPoolMining)
            return new[] { primary == null ? null : primary with { SharePercent = info.DualPoolPrimaryPercent },
                secondary == null ? null : secondary with { SharePercent = info.DualPoolSecondaryPercent } }.OfType<BlockProbeTarget>().ToList();
        return new[] { info.IsUsingFallbackStratum ? secondary : primary }.OfType<BlockProbeTarget>().ToList();
    }

    private static BlockProbeTarget? Make(string url, int port, bool? tlsFlag, string user, MinedCoin coin, bool isPrimary)
    {
        var host = StratumHost.Clean(url);
        if (host.Length == 0 || port is < 1 or > 65535 || user.Length == 0) return null;
        var scheme = url.ToLowerInvariant();
        var tls = tlsFlag == true || scheme.StartsWith("stratum+ssl://", StringComparison.Ordinal) || scheme.StartsWith("stratum+tls://", StringComparison.Ordinal);
        return new BlockProbeTarget(host, port, tls, user, coin, isPrimary);
    }
}

/// <summary>Results of the Stratum probe per pool, fetched while a miner's page is open. Used on the UI thread.</summary>
public sealed class BlockProbeStore
{
    /// <summary>What is known about one pool. No block and no error: the first probe is still running.</summary>
    public sealed record Entry(BlockTemplateSummary? Block, string? Error, DateTime UpdatedAt);

    /// <summary>At most one connection per pool in this time, unless the user asks.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    private readonly Dictionary<BlockProbeTarget, Entry> _entries = [];
    private readonly HashSet<BlockProbeTarget> _running = [];
    private readonly Func<BlockProbeTarget, Task<StratumJob>> _fetch;

    public BlockProbeStore(Func<BlockProbeTarget, Task<StratumJob>>? fetch = null) =>
        _fetch = fetch ?? (target => StratumProbe.FetchJobAsync(target.Host, target.Port, target.Tls, target.ProbeUser));

    public event Action? Changed;

    public Entry? EntryFor(BlockProbeTarget target) => _entries.GetValueOrDefault(target);

    public bool IsRunning(BlockProbeTarget target) => _running.Contains(target);

    /// <summary>Probes <paramref name="target"/> unless it ran within <see cref="MinimumInterval"/>; <paramref name="force"/> skips that wait.</summary>
    public async Task RefreshAsync(BlockProbeTarget target, bool force = false, DateTime? now = null)
    {
        var time = now ?? DateTime.Now;
        if (_running.Contains(target)) return;
        if (!force && _entries.TryGetValue(target, out var entry) && time - entry.UpdatedAt < MinimumInterval) return;
        _running.Add(target);
        _entries.TryAdd(target, new Entry(null, null, time));
        Changed?.Invoke();

        BlockTemplateSummary? block = null;
        string? error = null;
        try
        {
            var job = await _fetch(target);
            block = BlockTemplateSummary.From(job, target.Coin, target.User, DateTime.Now);
            if (block == null) error = Strings.ThePoolSJobCould;
        }
        catch (StratumProbeException e)
        {
            error = e.Message;
        }
        _entries[target] = new Entry(block, error, now ?? DateTime.Now);
        _running.Remove(target);
        Changed?.Invoke();
    }

    /// <summary>Forgets all results, e.g. when the probe is switched off.</summary>
    public void Reset()
    {
        _entries.Clear();
        Changed?.Invoke();
    }
}
