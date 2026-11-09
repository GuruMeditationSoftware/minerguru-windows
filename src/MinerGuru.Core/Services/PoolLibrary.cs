using System.Net.Sockets;
using System.Text.Json;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Services;

/// <summary>Saved pool configurations, in <c>pools.json</c> next to the settings.</summary>
public sealed class PoolLibrary
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public PoolLibrary(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "pools.json");
        try
        {
            if (File.Exists(_path)) Pools = JsonSerializer.Deserialize<List<PoolConfig>>(File.ReadAllText(_path)) ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // Unreadable: start empty; the next save replaces the file.
        }
    }

    public List<PoolConfig> Pools { get; } = [];

    public event Action? Changed;

    public PoolConfig? Find(Guid id) => Pools.FirstOrDefault(p => p.Id == id);

    /// <summary>The entry with the same host, port and account as <paramref name="endpoint"/>, if any.</summary>
    public PoolConfig? Matching(PoolEndpoint endpoint) => endpoint.HasContent ? Pools.FirstOrDefault(p => p.Matches(endpoint)) : null;

    public void Add(PoolConfig config)
    {
        Pools.Add(config);
        Save();
    }

    public void Update(PoolConfig config)
    {
        var index = Pools.FindIndex(p => p.Id == config.Id);
        if (index < 0) return;
        Pools[index] = config;
        Save();
    }

    public void Remove(Guid id)
    {
        Pools.RemoveAll(p => p.Id == id);
        Save();
    }

    public PoolConfig? Duplicate(Guid id)
    {
        if (Find(id) is not { } original) return null;
        var copy = original.Copy();
        copy.Id = Guid.NewGuid();
        copy.Name += L(" Kopie", " Copy");
        Add(copy);
        return copy;
    }

    /// <summary>Adds every pool configured on a miner (all slots) that is not in the library yet; returns how many.</summary>
    public int Import(IEnumerable<MinerDevice> miners)
    {
        var added = 0;
        foreach (var miner in miners.Where(m => m.LatestInfo != null))
        {
            foreach (var endpoint in new PoolSettingsForm(miner).Slots)
            {
                if (endpoint.Host.Length == 0 || Matching(endpoint) != null) continue;
                Pools.Add(PoolConfig.From(endpoint));
                added++;
            }
        }
        if (added > 0) Save();
        return added;
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(Pools, Options));
        }
        catch (IOException)
        {
            // Kept in memory; the next change tries again.
        }
        Changed?.Invoke();
    }

    /// <summary>Time for a TCP connect to the pool in ms, or <c>null</c> if it does not answer within 3 s.</summary>
    public static async Task<double?> MeasureLatencyMsAsync(string host, int port)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await client.ConnectAsync(StratumHost.Clean(host), port, timeout.Token);
            return System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            return null;
        }
    }
}
