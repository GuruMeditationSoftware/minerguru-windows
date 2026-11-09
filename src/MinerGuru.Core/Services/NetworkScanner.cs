using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinerGuru.Core.Models;

namespace MinerGuru.Core.Services;

public sealed record DiscoveredMiner(string IpAddress, MinerSystemInfo Info, string RawJson, double ResponseTimeMs);

/// <summary>Finds miners in a /24 subnet by asking every address for <c>/api/system/info</c>.</summary>
public static class NetworkScanner
{
    public const string FallbackPrefix = "192.168.1";
    public const int Concurrency = 36;
    public static readonly TimeSpan TimeoutPerHost = TimeSpan.FromSeconds(1.5);

    /// <summary>The /24 prefix (<c>192.168.1</c>) of this PC, preferring the adapter with a gateway over virtual ones.</summary>
    public static string DetectLocalPrefix()
    {
        try
        {
            var candidates =
                from adapter in NetworkInterface.GetAllNetworkInterfaces()
                where adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback
                let properties = adapter.GetIPProperties()
                let hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                from unicast in properties.UnicastAddresses
                where unicast.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivateIPv4(unicast.Address.ToString())
                orderby hasGateway descending
                select unicast.Address.ToString();
            return candidates.FirstOrDefault() is { } ip ? NormalizePrefix(ip) : FallbackPrefix;
        }
        catch (NetworkInformationException)
        {
            return FallbackPrefix;
        }
    }

    public static bool IsPrivateIPv4(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4 || !parts.All(p => byte.TryParse(p, out _))) return false;
        return parts[0] == "10" || (parts[0] == "192" && parts[1] == "168") || (parts[0] == "172" && int.Parse(parts[1]) is >= 16 and <= 31);
    }

    /// <summary>The first three parts of an address, so a full address can be pasted.</summary>
    public static string NormalizePrefix(string raw)
    {
        var trimmed = raw.Trim();
        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : trimmed;
    }

    public static bool IsValidPrefix(string prefix)
    {
        var parts = NormalizePrefix(prefix).Split('.');
        return parts.Length == 3 && parts.All(p => byte.TryParse(p, out _));
    }

    /// <summary><c>prefix.1</c> to <c>prefix.254</c>, then the addresses of the mock setup's <c>MINERGURU_SCAN_EXTRA</c> (debug builds only).</summary>
    public static List<string> Hosts(string prefix)
    {
        var normalized = NormalizePrefix(prefix);
        var hosts = Enumerable.Range(1, 254).Select(i => $"{normalized}.{i}").ToList();
        hosts.AddRange(ExpandExtraHosts(DebugSwitch.Get("MINERGURU_SCAN_EXTRA")));
        return hosts;
    }

    /// <summary><c>192.168.64.1:48001-48024</c>: mock miners listen on ports, not on addresses of their own.</summary>
    public static IEnumerable<string> ExpandExtraHosts(string? value)
    {
        if (value?.Split(':') is not [var host, var ports]) yield break;
        var range = ports.Split('-');
        if (!int.TryParse(range[0], out var first) || !int.TryParse(range[^1], out var last)) yield break;
        for (var port = first; port <= last; port++) yield return $"{host}:{port}";
    }

    /// <summary>Anything can answer on port 80; only a miner reports its hashrate or chip.</summary>
    public static bool LooksLikeMiner(string rawJson)
    {
        try
        {
            return JsonNode.Parse(rawJson) is JsonObject o && (o.ContainsKey("hashRate") || o.ContainsKey("ASICModel") || o.ContainsKey("asicModel"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Probes the hosts in parallel. The callbacks run one at a time, in the caller's context.</summary>
    public static async Task ScanAsync(MinerApiClient api, IReadOnlyList<string> hosts, Action<int> onProgress, Action<DiscoveredMiner> onDiscovered, CancellationToken ct)
    {
        var running = new List<Task<DiscoveredMiner?>>();
        var next = 0;
        var completed = 0;
        while (true)
        {
            while (running.Count < Concurrency && next < hosts.Count && !ct.IsCancellationRequested) running.Add(ProbeAsync(api, hosts[next++], ct));
            if (running.Count == 0) return;
            var done = await Task.WhenAny(running);
            running.Remove(done);
            completed++;
            if (ct.IsCancellationRequested) continue;
            onProgress(completed);
            if (await done is { } miner) onDiscovered(miner);
        }
    }

    private static async Task<DiscoveredMiner?> ProbeAsync(MinerApiClient api, string host, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var (info, raw) = await api.FetchSystemInfoAsync(host, TimeoutPerHost, ct);
            return LooksLikeMiner(raw) ? new DiscoveredMiner(host, info, raw, clock.Elapsed.TotalMilliseconds) : null;
        }
        catch (Exception e) when (e is MinerApiException or OperationCanceledException)
        {
            return null;
        }
    }
}

/// <summary>State and control of the subnet scan, as the Mac's NetworkScanStore. Used on the UI thread.</summary>
public sealed class NetworkScanStore(MinerStore store, MinerApiClient api)
{
    private CancellationTokenSource? _scan;

    public bool IsScanning { get; private set; }
    public int CompletedCount { get; private set; }
    public int TotalCount { get; private set; } = 254;
    public List<DiscoveredMiner> Discovered { get; } = [];

    public event Action? Changed;

    /// <summary>The task of the running scan, for tests.</summary>
    public Task Scan { get; private set; } = Task.CompletedTask;

    /// <summary>Scans the subnet prefix. Found miners stay a list to pick from, unless the fleet is still empty at the end.</summary>
    public void Start()
    {
        _scan?.Cancel();
        var scan = _scan = new CancellationTokenSource();
        var hosts = NetworkScanner.Hosts(store.ScanSubnetPrefix);
        Discovered.Clear();
        CompletedCount = 0;
        TotalCount = hosts.Count;
        IsScanning = true;
        Changed?.Invoke();
        Scan = RunAsync(hosts, scan);
    }

    public void Stop()
    {
        _scan?.Cancel();
        _scan = null;
        IsScanning = false;
        Changed?.Invoke();
    }

    public void AddAllDiscovered()
    {
        var added = AddNew();
        if (added > 0) store.ShowBanner(string.Format(Strings.MinersAddedToTheDashboard, added), false);
        Changed?.Invoke();
    }

    public void Add(DiscoveredMiner miner)
    {
        store.AddMiner(miner.IpAddress, null, miner.Info, miner.RawJson);
        Changed?.Invoke();
    }

    private async Task RunAsync(List<string> hosts, CancellationTokenSource scan)
    {
        await NetworkScanner.ScanAsync(api, hosts, Progress, Found, scan.Token);
        if (scan.IsCancellationRequested) return;
        CompletedCount = TotalCount;
        IsScanning = false;
        if (store.Miners.Count == 0 && Discovered.Count > 0)
        {
            var added = AddNew();
            store.ShowBanner(string.Format(Strings.MinersFoundOnTheLocal, added), false);
        }
        Changed?.Invoke();
    }

    private void Progress(int completed)
    {
        CompletedCount = completed;
        // Every 12 hosts is enough for the bar.
        if (completed % 12 == 0) Changed?.Invoke();
    }

    private void Found(DiscoveredMiner miner)
    {
        Discovered.Add(miner);
        Discovered.Sort((a, b) => string.CompareOrdinal(MinerStore.IpSortKey(a.IpAddress), MinerStore.IpSortKey(b.IpAddress)));
        Changed?.Invoke();
    }

    private int AddNew()
    {
        var added = 0;
        foreach (var miner in Discovered.Where(m => !store.IsAlreadyAdded(m.IpAddress)).ToList())
        {
            store.AddMiner(miner.IpAddress, null, miner.Info, miner.RawJson);
            added++;
        }
        return added;
    }
}
