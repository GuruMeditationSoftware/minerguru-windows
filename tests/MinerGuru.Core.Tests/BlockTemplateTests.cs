using System.Text.Json.Nodes;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using Xunit;

namespace MinerGuru.Core.Tests;

public class BlockTemplateTests
{
    public BlockTemplateTests() => Lang.English = false;

    /// <summary>ckpool-style template: 98 % to the miner, 2 % pool fee, witness commitment.</summary>
    private static JsonObject Fields() => (JsonObject)JsonNode.Parse("""
        {
          "stratumURL": "eusolo.ckpool.org", "stratumPort": 3333, "stratumUser": "bc1qexamplewallet.bitaxe",
          "blockHeight": 974212, "scriptsig": "/solo.ckpool.org/", "blockSignals": ["BIP-54"],
          "stratumDecodeCoinbase": true,
          "coinbaseValueTotalSatoshis": 314000000, "coinbaseValueUserSatoshis": 307720000,
          "coinbaseOutputs": [
            { "value": 307720000, "address": "bc1qexamplewallet", "isUserOutput": 1 },
            { "value": 6280000, "address": "bc1qpoolfeeexample", "isUserOutput": 0 },
            { "value": 0, "address": "OP_RETURN: .!..a.Xb..9.f..Z.", "isUserOutput": 0 }
          ],
          "coinbaseOthersCount": 0, "coinbaseOthersValueSatoshis": 0
        }
        """)!;

    private static BlockTemplateSummary? Block(JsonObject fields) => BlockTemplateSummary.From(MinerSystemInfo.Parse(fields));

    private static MinerSystemInfo Fixture(string name) =>
        MinerSystemInfo.Parse((JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")))!);

    [Fact]
    public void DecodesTheAxeOsBlockFields()
    {
        var block = Block(Fields())!;
        Assert.Equal(974212, block.Height);
        Assert.Equal("/solo.ckpool.org/", block.PoolTag);
        Assert.Equal(["BIP-54"], block.Signals);
        Assert.Equal(MinedCoin.Btc, block.Coin);
        Assert.Equal(3, block.Outputs.Count);
        Assert.True(block.Outputs[0].IsUserOutput);
        Assert.Equal(PayoutState.Verified, block.PayoutState);
        Assert.Equal(1_500_000, block.FeesSats);
        Assert.Equal(0.98, block.UserShare);
    }

    [Fact]
    public void WitnessCommitmentIsDataNotPayout()
    {
        var commitment = new CoinbaseOutput(0, "OP_RETURN: .!..a.Xb..9.f..Z.");
        Assert.True(commitment.IsDataOutput);
        Assert.True(commitment.IsWitnessCommitment);
        Assert.Null(commitment.DataText);

        var tag = new CoinbaseOutput(0, "OP_RETURN: RSKBLOCK:abc123");
        Assert.False(tag.IsWitnessCommitment);
        Assert.Equal("RSKBLOCK:abc123", tag.DataText);
    }

    [Fact]
    public void DecodingOffKeepsTheSumButHidesRecipients()
    {
        var fields = Fields();
        fields["stratumDecodeCoinbase"] = false;
        var block = Block(fields)!;
        Assert.Equal(PayoutState.DecodingOff, block.PayoutState);
        Assert.Empty(block.Outputs);
        Assert.Equal(314_000_000, block.TotalSats);
        Assert.Null(block.UserSats);
    }

    [Fact]
    public void ActiveSlotDecidesAboutDecoding()
    {
        // AxeOS 2.15: the fallback slot is active and does not decode, the primary does.
        var fields = Fields();
        fields["isUsingFallbackStratum"] = 1;
        fields["primaryPoolIndex"] = 0;
        fields["secondaryPoolIndex"] = 1;
        fields["pools"] = JsonNode.Parse("""
            [
              { "id": 0, "stratumURL": "eusolo.ckpool.org", "stratumUser": "bc1qexamplewallet.bitaxe", "stratumDecodeCoinbase": true },
              { "id": 1, "stratumURL": "public-pool.io", "stratumUser": "bc1qexamplewallet.bitaxe", "stratumDecodeCoinbase": false }
            ]
            """);
        var info = MinerSystemInfo.Parse(fields);
        Assert.False(info.ActivePoolDecodesCoinbase);
        Assert.Equal(PayoutState.DecodingOff, BlockTemplateSummary.From(info)?.PayoutState);
    }

    [Fact]
    public void LeftoverSumFromAnUndecodedJobIsIgnored()
    {
        var fields = Fields();
        fields["coinbaseValueTotalSatoshis"] = 3.179677500620704e17;
        var block = Block(fields)!;
        Assert.Null(block.TotalSats);
        Assert.Null(block.FeesSats);
        Assert.Null(block.UserShare);
    }

    [Theory]
    [InlineData("verified", PayoutState.Verified)]
    [InlineData("not_found", PayoutState.NotFound)]
    [InlineData("not_applicable", PayoutState.NotApplicable)]
    public void FirmwareStatusWins(string raw, PayoutState expected)
    {
        var fields = Fields();
        fields["coinbasePayoutStatus"] = raw;
        Assert.Equal(expected, Block(fields)?.PayoutState);
    }

    [Fact]
    public void MissingOwnAddressIsDerivedOnOlderFirmware()
    {
        var fields = Fields();
        fields["coinbaseValueUserSatoshis"] = 0;
        fields["coinbaseOutputs"] = JsonNode.Parse("""[{ "value": 314000000, "address": "bc1qsomeoneelse", "isUserOutput": 0 }]""");
        fields["stratumUser"] = "bc1q7n70rumyv6lvu8avpml0c3uggvssfu52egum3q.nerdqaxe";
        Assert.Equal(PayoutState.NotFound, Block((JsonObject)fields.DeepClone())?.PayoutState);
        fields["stratumUser"] = "myaccount.worker1";
        Assert.Equal(PayoutState.NotApplicable, Block(fields)?.PayoutState);
    }

    [Fact]
    public void NoBlockDataWithoutHeight()
    {
        Assert.Null(Block(new JsonObject { ["hashRate"] = 1000 }));
        Assert.Null(Block(new JsonObject { ["blockHeight"] = 0 }));
        Assert.Null(BlockTemplateSummary.From(Fixture("nerdqaxe-plusplus_nerdos-1.1.0")));
        Assert.Null(BlockTemplateSummary.From(Fixture("bitaxe-max-102_axeos-2.5.0")));
        Assert.NotNull(BlockTemplateSummary.From(Fixture("bitaxe-gamma-601_axeos-2.15.3")));
    }

    [Theory]
    [InlineData(840_000, MinedCoin.Btc, 312_500_000.0)]
    [InlineData(1_049_999, MinedCoin.Btc, 312_500_000.0)]
    [InlineData(1_050_000, MinedCoin.Btc, 156_250_000.0)]
    [InlineData(900_000, MinedCoin.Bch, 312_500_000.0)]
    [InlineData(20_000_000, MinedCoin.Dgb, null)]
    public void SubsidyFollowsTheHalvings(int height, MinedCoin coin, double? expected) =>
        Assert.Equal(expected, BlockTemplateSummary.SubsidySatsFor(height, coin));

    [Fact]
    public void CoinsAreFormattedWithAllDecimals()
    {
        Assert.Equal("3,14000000 BTC", BlockTemplateSummary.FormatCoins(314_000_000, MinedCoin.Btc).Replace('\u00A0', ' '));
        Assert.Equal("1.000,50 XEC", BlockTemplateSummary.FormatCoins(100_050, MinedCoin.Xec).Replace('\u00A0', ' '));
    }
}
