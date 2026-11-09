using System.Text.Json;
using System.Text.Json.Nodes;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class PoolConfigTests
{
    public PoolConfigTests() => Lang.English = false;

    private static readonly PoolConfig Ocean = new() { Name = "OCEAN", Host = "mine.ocean.xyz", Port = 3334, User = "bc1qocean.rig" };
    private static readonly PoolConfig Atlas = new() { Name = "Atlas", Host = "solo.atlaspool.io", Port = 3333, User = "bc1qatlas.rig" };

    /// <summary>AxeOS 2.15+ with slots 0, 1 and 4 filled and one malformed entry, as the Mac's fixture.</summary>
    private static MinerDevice AxeSlots() => Device(MinerSystemInfo.Parse(new JsonObject
    {
        ["hashRate"] = 1000, ["stratumURL"] = "eusolo.ckpool.org", ["stratumUser"] = "bc1qexamplewallet.bitaxe",
        ["primaryPoolIndex"] = 0, ["secondaryPoolIndex"] = 1,
        ["pools"] = new JsonArray
        {
            new JsonObject { ["id"] = 0, ["stratumProtocol"] = "SV1", ["stratumURL"] = "eusolo.ckpool.org", ["stratumPort"] = 3333,
                             ["stratumUser"] = "bc1qexamplewallet.bitaxe", ["stratumPassword"] = "*****", ["stratumTLS"] = 0, ["stratumSuggestedDifficulty"] = 1000 },
            new JsonObject { ["id"] = 1, ["stratumURL"] = "public-pool.io", ["stratumPort"] = 21496,
                             ["stratumUser"] = "bc1qexamplewallet.bitaxe", ["stratumPassword"] = "*****" },
            new JsonObject { ["id"] = 4, ["stratumURL"] = "mine.ocean.xyz", ["stratumPort"] = 3334, ["stratumUser"] = "bc1qocean.rig",
                             ["stratumPassword"] = "*****", ["stratumTLS"] = 1, ["stratumProtocol"] = "SV2" },
            new JsonObject { ["stratumURL"] = "broken-slot-without-id" },
        },
    }));

    private static MinerDevice Fixture(string name) => Device(MinerSystemInfo.Parse(
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")))!));

    private static MinerDevice Device(MinerSystemInfo info) =>
        new(new PersistedMinerConfig { IpAddress = "192.168.1.28" }, ConnectionState.Online) { LatestInfo = info };

    [Fact]
    public void FormLoadsEightSlots()
    {
        var form = new PoolSettingsForm(AxeSlots());
        Assert.Equal(PoolApi.Slots, form.Api);
        Assert.Equal(AxeOsPool.SlotCount, form.Slots.Count);
        Assert.Equal("eusolo.ckpool.org", form.Primary.Host);
        Assert.Equal("public-pool.io", form.Fallback.Host);
        Assert.Equal("mine.ocean.xyz", form.Slots[4].Host);
        Assert.Equal([0, 1, 4], form.StoredSlots.Order());
        Assert.Equal([0, 1, 2, 4], form.VisibleSlots); // filled slots plus the first free one
    }

    [Fact]
    public void OlderFirmwareHasTwoSlots()
    {
        var form = new PoolSettingsForm(Fixture("bitaxe-max-102_axeos-2.5.0"));
        Assert.Equal(PoolApi.Legacy, form.Api);
        Assert.Equal(2, form.Slots.Count);
    }

    [Fact]
    public void SlotPayloadKeepsAdvancedSettingsAndMaskedPassword()
    {
        var form = new PoolSettingsForm(AxeSlots());
        form.Slots[4] = form.Slots[4] with { User = "bc1qocean.rig2" };
        var payload = form.Payload(supportsDualPool: false);

        Assert.Null(payload.StratumUrl); // ignored by AxeOS 2.15+, so not sent
        Assert.Equal(0, payload.PrimaryPoolIndex);
        Assert.Equal(1, payload.SecondaryPoolIndex);
        Assert.Equal([0, 1, 4], payload.Pools!.Select(p => (int)p["id"]!));
        var ocean = payload.Pools![^1];
        Assert.Equal("bc1qocean.rig2", (string)ocean["stratumUser"]!);
        Assert.Equal(1, (int)ocean["stratumTLS"]!);
        Assert.Equal("SV2", (string)ocean["stratumProtocol"]!);
        Assert.Equal(AxeOsPool.MaskedPassword, (string)ocean["stratumPassword"]!); // firmware keeps the stored password

        var json = JsonNode.Parse(JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }))!.AsObject();
        Assert.Equal(["pools", "primaryPoolIndex", "secondaryPoolIndex"], json.Select(p => p.Key).Order());
    }

    [Fact]
    public void ClearedReserveSlotIsDeletedButNotTheFallback()
    {
        var form = new PoolSettingsForm(AxeSlots());
        form.ClearSlot(4);
        Assert.Equal([4], form.SlotsToClear);
        Assert.Equal([0, 1], form.Payload(false).Pools!.Select(p => (int)p["id"]!));

        form.ClearSlot(1); // fallback slot: AxeOS refuses to delete it
        Assert.Equal([4], form.SlotsToClear);
        Assert.NotNull(form.ValidationMessage);
    }

    [Fact]
    public void IncompleteSlotBlocksSaving()
    {
        var form = new PoolSettingsForm(AxeSlots());
        Assert.Null(form.ValidationMessage);
        form.Slots[2] = form.Slots[2] with { Host = "solo.atlaspool.io" };
        Assert.Contains("Slot 3", form.ValidationMessage);
    }

    [Fact]
    public void DualPoolOnlyOnNerdOs()
    {
        Assert.False(AxeSlots().SupportsDualPool);
        Assert.True(Fixture("nerdqaxe-plusplus_nerdos-1.1.0").SupportsDualPool);
    }

    [Fact]
    public void ExistingSlotBecomesPrimaryAndKeepsItsSettings()
    {
        var form = new PoolSettingsForm(AxeSlots());
        form.MakePrimary(Ocean);
        Assert.Equal(4, form.PrimarySlot);
        Assert.Equal(1, (int)form.Slots[4].Advanced!["stratumTLS"]!);
        Assert.Equal("eusolo.ckpool.org", form.Slots[0].Host); // previous primary stays stored
    }

    [Fact]
    public void NewPoolGoesIntoFreeSlot()
    {
        var form = new PoolSettingsForm(AxeSlots());
        form.MakePrimary(Atlas);
        Assert.Equal(2, form.PrimarySlot);
        Assert.Equal("solo.atlaspool.io", form.Slots[2].Host);
        Assert.Null(form.Slots[2].Advanced);
        Assert.Equal("eusolo.ckpool.org", form.Slots[0].Host);
    }

    [Fact]
    public void MakingFallbackPrimarySwapsRoles()
    {
        var form = new PoolSettingsForm(AxeSlots());
        form.MakePrimary(new PoolConfig { Host = "public-pool.io", Port = 21496, User = "bc1qexamplewallet.bitaxe" });
        Assert.Equal(1, form.PrimarySlot);
        Assert.Equal(0, form.SecondarySlot);
    }

    [Fact]
    public void LegacyFirmwareOverwritesSlotsAndKeepsDualSplit()
    {
        var form = new PoolSettingsForm(Fixture("nerdqaxe-plusplus_nerdos-1.1.0"));
        form.MakePrimary(Ocean);
        form.MakeFallback(Atlas);
        var payload = form.Payload(supportsDualPool: true);
        Assert.Equal("mine.ocean.xyz", payload.StratumUrl);
        Assert.Equal(3334, payload.StratumPort);
        Assert.Equal("solo.atlaspool.io", payload.FallbackStratumUrl);
        Assert.Equal(1, payload.PoolMode);
        Assert.Equal(1, payload.PoolBalance);
        Assert.Null(payload.Pools);
        // The unknown password of a pool read off the miner is not sent; a library pool's is.
        Assert.Equal("x", payload.StratumPassword);
        Assert.Null(new PoolSettingsForm(Fixture("nerdqaxe-plusplus_nerdos-1.1.0")).Payload(true).StratumPassword);
    }

    [Fact]
    public void PayloadCleansInputAndSkipsEmptyFallback()
    {
        var form = new PoolSettingsForm();
        form.Primary = new(" stratum+tcp://solo.ckpool.org:3333 ", 3333, " bc1qwallet.rig ", "");
        form.Fallback = new("", 4444, "   ", "x");
        var payload = form.Payload(supportsDualPool: false);
        Assert.Equal("solo.ckpool.org", payload.StratumUrl);
        Assert.Equal("bc1qwallet.rig", payload.StratumUser);
        Assert.Null(payload.StratumPassword);
        Assert.Null(payload.FallbackStratumUrl);
        Assert.Null(payload.FallbackStratumPort);
        Assert.Null(payload.PoolMode);
    }

    [Fact]
    public void DraftIsDirtyOnlyAfterAChange()
    {
        var miner = AxeSlots();
        var baseline = new PoolSettingsForm(miner);
        var draft = baseline.Clone();
        Assert.True(draft.HasSamePoolSettings(baseline));
        draft.MakePrimary(Atlas);
        Assert.False(draft.HasSamePoolSettings(baseline));
        Assert.True(baseline.HasSamePoolSettings(new PoolSettingsForm(miner)));
    }

    [Fact]
    public void LibraryImportsAllSlotsOnceAndPersists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mg-pools-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var library = new PoolLibrary(dir);
        MinerDevice[] miners = [AxeSlots(), Fixture("bitaxe-max-102_axeos-2.5.0")];
        var added = library.Import(miners);
        Assert.True(added >= 3);
        Assert.Equal(0, library.Import(miners));
        Assert.All(library.Pools, p => Assert.Equal("x", p.Password)); // masked password is not stored
        Assert.Equal("CKPool EU (Solo) · bitaxe", library.Pools[0].Name);
        Assert.Equal(library.Pools.Select(p => p.Id), new PoolLibrary(dir).Pools.Select(p => p.Id));

        var copy = library.Duplicate(library.Pools[0].Id)!;
        Assert.Equal("CKPool EU (Solo) · bitaxe Kopie", copy.Name);
        library.Remove(copy.Id);
        Assert.Equal(added, library.Pools.Count);
        Directory.Delete(dir, true);
    }
}
