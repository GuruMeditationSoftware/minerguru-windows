using System.Net;
using System.Text;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class NetworkScannerTests : IDisposable
{
    public NetworkScannerTests() => Lang.English = false;

    private readonly string _folder = Directory.CreateTempSubdirectory("mg-scan").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData("192.168.1.55", "192.168.1")]
    [InlineData(" 10.0.0 ", "10.0.0")]
    [InlineData("10.0", "10.0")]
    public void PrefixKeepsThreeParts(string raw, string expected) => Assert.Equal(expected, NetworkScanner.NormalizePrefix(raw));

    [Theory]
    [InlineData("192.168.1", true)]
    [InlineData("192.168.1.5", true)]
    [InlineData("10.0", false)]
    [InlineData("300.1.1", false)]
    [InlineData("abc", false)]
    public void PrefixValidation(string prefix, bool expected) => Assert.Equal(expected, NetworkScanner.IsValidPrefix(prefix));

    [Theory]
    [InlineData("192.168.1.2", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.254.1.1", false)]
    public void PrivateAddresses(string ip, bool expected) => Assert.Equal(expected, NetworkScanner.IsPrivateIPv4(ip));

    [Fact]
    public void HostsCoverTheSubnet()
    {
        var hosts = NetworkScanner.Hosts("192.168.1.77");
        Assert.Equal(254, hosts.Count);
        Assert.Equal("192.168.1.1", hosts[0]);
        Assert.Equal("192.168.1.254", hosts[^1]);
    }

    [Fact]
    public void ExtraHostsExpandTheirPorts()
    {
        Assert.Equal(["10.0.0.1:48001", "10.0.0.1:48002"], NetworkScanner.ExpandExtraHosts("10.0.0.1:48001-48002"));
        Assert.Empty(NetworkScanner.ExpandExtraHosts(null));
    }

    [Theory]
    [InlineData("{\"hashRate\":12.5}", true)]
    [InlineData("{\"ASICModel\":\"BM1370\"}", true)]
    [InlineData("{\"status\":\"ok\"}", false)]
    [InlineData("<html>", false)]
    public void OnlyMinersCount(string json, bool expected) => Assert.Equal(expected, NetworkScanner.LooksLikeMiner(json));

    /// <summary>Answers like miners on some addresses, like a router on one, nothing elsewhere.</summary>
    private sealed class Network : HttpMessageHandler
    {
        public HashSet<int> Miners { get; } = [];
        public int Router { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(5, ct);
            var last = int.Parse(request.RequestUri!.Host.Split('.')[3]);
            if (last == Router) return Reply("{\"status\":\"ok\"}");
            if (Miners.Contains(last)) return Reply($"{{\"hashRate\":1000,\"hostname\":\"bitaxe-{last}\",\"ASICModel\":\"BM1370\"}}");
            throw new HttpRequestException("no route");
        }

        private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private MinerStore Store(Network network)
    {
        var store = new MinerStore(_folder, new MinerApiClient(network));
        store.ScanSubnetPrefix = "192.168.1";
        return store;
    }

    [Fact]
    public async Task EmptyFleetTakesEverythingFound()
    {
        var store = Store(new Network { Miners = { 9, 10, 200 }, Router = 1 });
        string? banner = null;
        store.Banner += (text, _) => banner = text;
        store.Scanner.Start();
        await store.Scanner.Scan;

        Assert.False(store.Scanner.IsScanning);
        Assert.Equal(254, store.Scanner.CompletedCount);
        Assert.Equal(["192.168.1.9", "192.168.1.10", "192.168.1.200"], store.Scanner.Discovered.Select(m => m.IpAddress));
        Assert.Equal(3, store.Miners.Count);
        Assert.Contains("3 Miner", banner);
    }

    [Fact]
    public async Task ExistingFleetGetsAListToPickFrom()
    {
        var store = Store(new Network { Miners = { 9, 10 } });
        store.AddMiner("192.168.1.9");
        store.Scanner.Start();
        await store.Scanner.Scan;
        Assert.Single(store.Miners);
        Assert.Equal(2, store.Scanner.Discovered.Count);

        store.Scanner.AddAllDiscovered();
        Assert.Equal(2, store.Miners.Count);
        Assert.Equal(ConnectionState.Online, store.Find(store.Miners[1].Id)!.State);
    }

    [Fact]
    public async Task StoppingEndsTheScanWithoutAddingAnything()
    {
        var store = Store(new Network { Miners = { 9 } });
        store.Scanner.Start();
        store.Scanner.Stop();
        await store.Scanner.Scan;
        Assert.False(store.Scanner.IsScanning);
        Assert.Empty(store.Miners);
    }

    [Fact]
    public void SubnetAndAutoScanArePersisted()
    {
        var store = Store(new Network());
        Assert.True(store.AutoScanOnEmptyLaunch);
        store.AutoScanOnEmptyLaunch = false;
        store.ScanSubnetPrefix = "10.0.5";
        var reloaded = new MinerStore(_folder);
        Assert.False(reloaded.AutoScanOnEmptyLaunch);
        Assert.Equal("10.0.5", reloaded.ScanSubnetPrefix);
    }
}
