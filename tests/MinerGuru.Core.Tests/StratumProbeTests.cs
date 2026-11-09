using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class StratumProbeTests
{
    public StratumProbeTests() => Lang.English = false;

    private const string Genesis = "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa";
    private const string GenesisScript = "76a91462e907b15cbf27d5425399ebf6f0fb50ebb88f1888ac";

    private static string Hex(byte[]? bytes) => bytes == null ? "" : Convert.ToHexStringLower(bytes);

    // Addresses

    [Theory]
    [InlineData(Genesis)]
    [InlineData("bitcoincash:qp3wjpa3tjlj042z2wv7hahsldgwhwy0rq9sywjpyy")]
    [InlineData("qp3wjpa3tjlj042z2wv7hahsldgwhwy0rq9sywjpyy")]
    [InlineData("ecash:qp3wjpa3tjlj042z2wv7hahsldgwhwy0rquas9fmzn")]
    [InlineData("solo:" + Genesis)]
    public void SameKeyGivesTheSameScript(string address) => Assert.Equal(GenesisScript, Hex(CoinAddress.ScriptPubKey(address)));

    [Theory]
    [InlineData("bc1q28kkr5hk4gnqe3evma6runjrd2pvqyp8fpwfzu", MinedCoin.Btc)]
    [InlineData("bc1p5d7rjq7g6rdk2yhzks9smlaqtedr4dekq08ge8ztwac72sfr9rusxg3297", MinedCoin.Btc)]
    [InlineData("3LyFQTKbPwXtgrsGpMbbEECY7ViBeFbonU", MinedCoin.Btc)]
    [InlineData("DRNiw2k4iFuHGS2fF1wMahounXo6H9iGbh", MinedCoin.Dgb)]
    [InlineData("dgb1qdxxrkdx4m9r7hlqa6smwzwawfen56nndrnze36", MinedCoin.Dgb)]
    [InlineData("bitcoincash:qp3wjpa3tjlj042z2wv7hahsldgwhwy0rq9sywjpyy", MinedCoin.Bch)]
    [InlineData("ecash:qp3wjpa3tjlj042z2wv7hahsldgwhwy0rquas9fmzn", MinedCoin.Xec)]
    public void AddressesSurviveTheRoundTrip(string address, MinedCoin coin) =>
        Assert.Equal(address, CoinAddress.Address(CoinAddress.ScriptPubKey(address)!, coin));

    [Theory]
    [InlineData("myaccount")]
    [InlineData("bc1q28kkr5hk4gnqe3evma6runjrd2pvqyp8fpwfzv")]
    [InlineData("1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNb")]
    [InlineData("")]
    public void AccountNamesAndTyposAreNoAddress(string value) => Assert.Null(CoinAddress.ScriptPubKey(value));

    // Recorded pools (MockMiners/stratum-captures of the Mac app)

    private static readonly string Captures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "stratum");

    private sealed record Capture(JsonElement Json)
    {
        public string File => Json.GetProperty("file").GetString()!;
        public string User => Json.GetProperty("user").GetString()!;
        public bool Authorized => Json.GetProperty("authorized").GetBoolean();
        public MinedCoin Coin => Enum.Parse<MinedCoin>(Json.GetProperty("coin").GetString()!, ignoreCase: true);
        public string[] Lines => System.IO.File.ReadAllLines(Path.Combine(Captures, File)).Where(l => l.Length > 0).ToArray();
        public JsonElement[] Outputs => Json.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array ? outputs.EnumerateArray().ToArray() : [];
    }

    private static Capture[] All() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Captures, "expected.json"))).RootElement
        .EnumerateArray().Select(e => new Capture(e)).ToArray();

    private static Capture Named(string file) => All().Single(c => c.File == file);

    public static TheoryData<string> Files
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var capture in All()) data.Add(capture.File);
            return data;
        }
    }

    private static StratumJob? Job(Capture capture)
    {
        var session = new StratumProbeSession(capture.User, "test");
        return capture.Lines.Select(session.Receive).FirstOrDefault(j => j != null);
    }

    private static BlockTemplateSummary? Block(Capture capture) =>
        Job(capture) is { } job ? BlockTemplateSummary.From(job, capture.Coin, capture.User, DateTime.Now) : null;

    [Theory]
    [MemberData(nameof(Files))]
    public void ProbeReadsTheRecordedTemplate(string file)
    {
        var capture = Named(file);
        if (!capture.Authorized)
        {
            var error = Assert.Throws<StratumProbeException>(() => Job(capture));
            Assert.Equal(StratumFailure.Rejected, error.Failure);
            Assert.Equal("Invalid address", error.Detail);
            return;
        }
        var job = Job(capture)!;
        Assert.Equal(capture.Json.GetProperty("merkleBranches").GetInt32(), job.MerkleBranchCount);

        var coinbase = job.Coinbase!;
        Assert.Equal(capture.Json.GetProperty("height").GetInt32(), coinbase.Height);
        Assert.Equal(capture.Json.GetProperty("totalSats").GetDouble(), coinbase.TotalSats);
        var expected = capture.Outputs;
        Assert.Equal(expected.Select(o => o.TryGetProperty("value", out var v) ? v.GetDouble() : 0), coinbase.Outputs.Select(o => (double)o.ValueSats));
        Assert.Equal(expected.Select(o => o.TryGetProperty("scriptPubKey", out var s) ? s.GetString() ?? "" : ""), coinbase.Outputs.Select(o => Hex(o.Script)));

        // The recording printed every address in Bitcoin notation; BCH and eCash use CashAddr here.
        if (capture.Coin is MinedCoin.Btc or MinedCoin.Dgb or MinedCoin.Bsv)
            foreach (var (output, reference) in coinbase.Outputs.Zip(expected))
                if (reference.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.String)
                    Assert.Equal(address.GetString(), CoinAddress.Address(output.Script, capture.Coin));

        var block = Block(capture)!;
        var paysUs = expected.Any(o => o.GetProperty("paysTestAddress").GetBoolean());
        Assert.Equal(paysUs ? PayoutState.Verified : PayoutState.NotFound, block.PayoutState);
        Assert.Equal(paysUs, block.UserSats > 0);
        Assert.Equal(expected.Length, block.Outputs.Count + block.OthersCount);
    }

    [Fact]
    public void SharedCoinbaseListsTheLargestAndSumsTheRest()
    {
        var block = Block(Named("ocean-btc.jsonl"))!;
        Assert.Equal(BlockTemplateSummary.MaxListedRecipients, block.Outputs.Count(o => !o.IsDataOutput));
        Assert.True(block.OthersCount > 0);
        Assert.Equal(block.TotalSats, block.Outputs.Sum(o => o.ValueSats) + block.OthersValueSats);
    }

    [Fact]
    public void NerdminersWorksWithoutExtranonce()
    {
        var block = Block(Named("nerdminers-btc.jsonl"))!;
        Assert.Equal(PayoutState.Verified, block.PayoutState);
        Assert.Contains("nerdminers", block.PoolTag);
        Assert.Equal((2049, 4096), block.TransactionRange);
    }

    /// <summary>The branch count comes from the pool; from 31 branches on the shift overflowed.</summary>
    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(1000)]
    public void OversizedMerkleBranchesStayInRange(int branches)
    {
        var capture = Named("nerdminers-btc.jsonl");
        var job = Job(capture)! with { MerkleBranchCount = branches };
        var block = BlockTemplateSummary.From(job, capture.Coin, capture.User, DateTime.Now)!;
        Assert.Equal(((1 << 29) + 1, 1 << 30), block.TransactionRange);
    }

    [Fact]
    public void WitnessCommitmentReadsLikeTheFirmwareReportsIt()
    {
        var block = Block(Named("ckpool-eu-btc.jsonl"))!;
        Assert.Contains(block.Outputs, o => o.IsWitnessCommitment);
        Assert.Equal(Genesis, block.Outputs[0].Address);
    }

    [Fact]
    public void AccountUserIsNotChecked()
    {
        var block = BlockTemplateSummary.From(Job(Named("kryptex-bsv.jsonl"))!, MinedCoin.Bsv, "myaccount.worker1", DateTime.Now)!;
        Assert.Equal(PayoutState.NotApplicable, block.PayoutState);
    }

    // Session

    [Fact]
    public void JobBeforeAuthorizeResultCounts()
    {
        var session = new StratumProbeSession($"{Genesis}.minerguru", "test");
        Assert.Equal(2, session.OpeningLines.Count);
        Assert.Contains("mining.authorize", session.OpeningLines[1]);
        Assert.Null(session.Receive("""{"id":1,"result":[[["mining.notify","ab"]],"f000000f",8],"error":null}"""));
        var job = session.Receive("""{"id":null,"method":"mining.notify","params":["1","00","01","02",[],"20000000","1703a30c","6900f0a0",true]}""")!;
        Assert.Equal("f000000f", job.Extranonce1);
        Assert.Equal(8, job.Extranonce2Size);
    }

    [Fact]
    public void ProbeNeverSubmits() => Assert.DoesNotContain(new StratumProbeSession("x", "test").OpeningLines, l => l.Contains("mining.submit"));

    [Fact]
    public void OpeningLinesUseTheWireFormat() => Assert.Equal(
        """{"id":2,"method":"mining.authorize","params":["user","x"]}""", new StratumProbeSession("user", "test").OpeningLines[1]);

    // Real socket against a replayed pool

    private static async Task<int> Replay(Capture capture, TcpListener listener)
    {
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.ReadAsync(new byte[4096]);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(string.Join("\n", capture.Lines) + "\n"));
            await stream.ReadAsync(new byte[4096]);
        });
        return await Task.FromResult(port);
    }

    [Fact]
    public async Task FetchReadsAJobOverASocket()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var capture = Named("ckpool-eu-btc.jsonl");
        var port = await Replay(capture, listener);
        var job = await StratumProbe.FetchJobAsync("127.0.0.1", port, false, capture.User, TimeSpan.FromSeconds(5));
        listener.Stop();
        Assert.Equal(capture.Json.GetProperty("height").GetInt32(), job.Coinbase?.Height);
    }

    [Fact]
    public async Task SilentPoolTimesOut()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var error = await Assert.ThrowsAsync<StratumProbeException>(() => StratumProbe.FetchJobAsync("127.0.0.1", port, false, "x", TimeSpan.FromMilliseconds(200)));
        listener.Stop();
        Assert.Equal(StratumFailure.Timeout, error.Failure);
    }

    [Fact]
    public async Task IncompleteEndpointFailsAtOnce()
    {
        var error = await Assert.ThrowsAsync<StratumProbeException>(() => StratumProbe.FetchJobAsync("", 3333, false, "x"));
        Assert.Equal(StratumFailure.InvalidEndpoint, error.Failure);
    }

    // Targets and store

    private static MinerSystemInfo Info(string json) => MinerSystemInfo.Parse((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!);

    [Fact]
    public void FirmwareWithBlockDataNeedsNoProbe() =>
        Assert.Empty(BlockProbeTarget.TargetsFor(Info("""{"blockHeight":900000,"stratumURL":"a.pool","stratumPort":3333,"stratumUser":"u"}""")));

    [Fact]
    public void ProbeUserKeepsTheWalletNotTheWorker()
    {
        var target = BlockProbeTarget.TargetsFor(Info($$"""{"stratumURL":"stratum+ssl://a.pool","stratumPort":4333,"stratumUser":"{{Genesis}}.bitaxe"}"""))[0];
        Assert.Equal($"{Genesis}.minerguru", target.ProbeUser);
        Assert.Equal("a.pool", target.Host);
        Assert.True(target.Tls);
        Assert.True(target.IsPrimary);
    }

    [Fact]
    public void ActivePoolIsProbed()
    {
        var targets = BlockProbeTarget.TargetsFor(Info("""
            {"isUsingFallbackStratum":1,"stratumURL":"a.pool","stratumPort":3333,"stratumUser":"u","fallbackStratumURL":"b.pool","fallbackStratumPort":3334,"fallbackStratumUser":"v"}
            """));
        Assert.Equal("b.pool", Assert.Single(targets).Host);
    }

    private static readonly BlockProbeTarget Target = new("a.pool", 3333, false, $"{Genesis}.bitaxe", MinedCoin.Btc, true);

    [Fact]
    public async Task StoreWaitsBetweenProbesUnlessForced()
    {
        var calls = 0;
        var capture = Named("ckpool-eu-btc.jsonl");
        var store = new BlockProbeStore(_ =>
        {
            calls++;
            return Task.FromResult(Job(capture)!);
        });
        var start = new DateTime(2026, 11, 3, 12, 0, 0);
        await store.RefreshAsync(Target, now: start);
        Assert.Equal(PayoutState.Verified, store.EntryFor(Target)?.Block?.PayoutState);
        await store.RefreshAsync(Target, now: start.AddMinutes(4));
        Assert.Equal(1, calls);
        await store.RefreshAsync(Target, force: true, now: start.AddMinutes(4));
        Assert.Equal(2, calls);
        await store.RefreshAsync(Target, now: start.AddMinutes(10));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task StoreKeepsTheReasonOfAFailure()
    {
        var store = new BlockProbeStore(_ => throw new StratumProbeException(StratumFailure.Rejected, "Invalid address"));
        await store.RefreshAsync(Target);
        Assert.Equal("Der Pool lehnt den User ab: Invalid address", store.EntryFor(Target)?.Error);
        Assert.False(store.IsRunning(Target));
        store.Reset();
        Assert.Null(store.EntryFor(Target));
    }
}
