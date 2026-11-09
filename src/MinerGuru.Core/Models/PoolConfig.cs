using System.Text.Json.Nodes;
using MinerGuru.Core.Services;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Models;

/// <summary>A named stratum pool (host, port, wallet.worker, password) the user can assign to miner pool slots.</summary>
public sealed class PoolConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3333;
    public string User { get; set; } = "";
    public string Password { get; set; } = "x";

    /// <summary>From a pool read off a miner. Firmware does not report passwords (empty or <c>*****</c>); most pools ignore them.</summary>
    public static PoolConfig From(PoolEndpoint endpoint, string? name = null) => new()
    {
        Name = name ?? SuggestedName(endpoint.Host, endpoint.Port, endpoint.User),
        Host = StratumHost.Clean(endpoint.Host),
        Port = endpoint.Port,
        User = endpoint.User.Trim(),
        Password = endpoint.Password is "" or AxeOsPool.MaskedPassword ? "x" : endpoint.Password,
    };

    public PoolConfig Copy() => (PoolConfig)MemberwiseClone();

    public PoolEndpoint Endpoint => new(Host, Port, User, Password);

    public string HostAndPort => $"{Host}:{Port}";

    public bool IsValid => StratumHost.Clean(Host).Length > 0 && Port is >= 1 and <= 65535;

    /// <summary>Same pool target and account, ignoring name and password.</summary>
    public bool Matches(PoolEndpoint endpoint) =>
        string.Equals(StratumHost.Clean(endpoint.Host), Host, StringComparison.OrdinalIgnoreCase)
        && endpoint.Port == Port
        && endpoint.User.Trim() == User.Trim();

    /// <summary>Readable default name, e.g. <c>CKPool EU (Solo) · rig1</c>.</summary>
    public static string SuggestedName(string host, int port, string user)
    {
        var clean = StratumHost.Clean(host);
        var name = PoolPreset.CommonPools.FirstOrDefault(p => p.Host == clean && p.Port == port)?.Name ?? clean;
        var dot = user.IndexOf('.');
        return dot >= 0 && dot < user.Length - 1 ? $"{name} · {user[(dot + 1)..]}" : name;
    }
}

/// <summary>Well-known pools offered as templates in the pool editor.</summary>
public sealed record PoolPreset(string Name, string Category, string Host, int Port)
{
    public static readonly IReadOnlyList<PoolPreset> CommonPools =
    [
        new("Public Pool (Solo)", "Solo Mining", "public-pool.io", 21496),
        new("CKPool EU (Solo)", "Solo Mining", "eusolo.ckpool.org", 3333),
        new("CKPool Global (Solo)", "Solo Mining", "solo.ckpool.org", 3333),
        new("AtlasPool EU (Solo)", "Solo Mining", "solo.atlaspool.io", 3333),
        new("Braiins Pool", "FPPS / Pool", "stratum.braiins.com", 3333),
    ];
}

/// <summary>One pool slot of a miner. <see cref="Advanced"/> is the AxeOS 2.15+ slot as read, written back unchanged.</summary>
public sealed record PoolEndpoint(string Host = "", int Port = 3333, string User = "", string Password = "x", JsonObject? Advanced = null)
{
    public bool HasContent => Host.Length > 0 || User.Length > 0;

    /// <summary>Host and user set: the minimum AxeOS accepts for a slot.</summary>
    public bool IsComplete => StratumHost.Clean(Host).Length > 0 && User.Trim().Length > 0;

    public static PoolEndpoint From(AxeOsPool pool) =>
        new(StratumHost.Clean(pool.StratumUrl), pool.StratumPort, pool.StratumUser, pool.StratumPassword ?? AxeOsPool.MaskedPassword, pool.Raw);

    /// <summary>Slot payload for AxeOS 2.15+: the edited basics on top of the advanced settings read from the miner.</summary>
    public JsonObject AxePool(int id)
    {
        var pool = Advanced?.DeepClone() as JsonObject ?? [];
        pool["id"] = id;
        pool["stratumURL"] = StratumHost.Clean(Host);
        pool["stratumPort"] = Port;
        pool["stratumUser"] = User.Trim();
        pool["stratumPassword"] = Password.Length == 0 ? "x" : Password;
        return pool;
    }
}

public enum PoolApi
{
    /// <summary><c>stratumURL</c> / <c>fallbackStratumURL</c> (AxeOS before 2.15, NerdOS).</summary>
    Legacy,
    /// <summary><c>pools[]</c> with <c>primaryPoolIndex</c> / <c>secondaryPoolIndex</c> (AxeOS 2.15+).</summary>
    Slots,
}

public enum PoolMode { Failover = 0, Dual = 1 }

/// <summary>
/// Editable copy of a miner's pool settings on the pools page and the <c>PATCH /api/system</c> payload built
/// from it, as the pool part of the Mac's MinerSettingsForm.
/// </summary>
public sealed class PoolSettingsForm
{
    public PoolApi Api { get; set; } = PoolApi.Legacy;
    public PoolMode Mode { get; set; } = PoolMode.Failover;
    public int Balance { get; set; } = 50;
    /// <summary>Two slots (primary, fallback) on older AxeOS and NerdOS, eight on AxeOS 2.15+.</summary>
    public List<PoolEndpoint> Slots { get; set; } = [new(), new()];
    public int PrimarySlot { get; set; }
    public int SecondarySlot { get; set; } = 1;
    /// <summary>Slots that held a pool on the miner when the form was loaded (AxeOS 2.15+).</summary>
    public HashSet<int> StoredSlots { get; set; } = [];

    public PoolSettingsForm() { }

    /// <summary>Pre-filled from the miner's current telemetry.</summary>
    public PoolSettingsForm(MinerDevice miner)
    {
        if (miner.LatestInfo is not { } info) return;
        Mode = info.IsDualPoolMining || info.StratumDetail?.PoolMode == 1 ? PoolMode.Dual : PoolMode.Failover;
        Balance = info.DualPoolPrimaryPercent;
        if (info.AxePools is { } axePools)
        {
            Api = PoolApi.Slots;
            Slots = Enumerable.Range(0, AxeOsPool.SlotCount)
                .Select(i => axePools.FirstOrDefault(p => p.Id == i) is { } pool ? PoolEndpoint.From(pool) : new PoolEndpoint())
                .ToList();
            PrimarySlot = Math.Clamp(info.PrimaryPoolIndex ?? 0, 0, AxeOsPool.SlotCount - 1);
            SecondarySlot = Math.Clamp(info.SecondaryPoolIndex ?? 1, 0, AxeOsPool.SlotCount - 1);
            StoredSlots = axePools.Select(p => p.Id).Where(id => id is >= 0 and < AxeOsPool.SlotCount).ToHashSet();
        }
        else
        {
            // The firmware does not report passwords; an empty one is not sent and stays unchanged on the miner.
            Slots =
            [
                new(StratumHost.Clean(info.StratumUrl), info.StratumPort, info.StratumUser, ""),
                new(StratumHost.Clean(info.FallbackStratumUrl), info.FallbackStratumPort, info.FallbackStratumUser, ""),
            ];
        }
    }

    public PoolSettingsForm Clone() => new()
    {
        Api = Api, Mode = Mode, Balance = Balance, Slots = [.. Slots],
        PrimarySlot = PrimarySlot, SecondarySlot = SecondarySlot, StoredSlots = [.. StoredSlots],
    };

    /// <summary>Pool the miner mines on (dual mode: pool 1).</summary>
    public PoolEndpoint Primary
    {
        get => Slots[PrimarySlot];
        set => Slots[PrimarySlot] = value;
    }

    /// <summary>Fallback pool (dual mode: pool 2).</summary>
    public PoolEndpoint Fallback
    {
        get => Slots[SecondarySlot];
        set => Slots[SecondarySlot] = value;
    }

    /// <summary>Hashrate share of pool 1 / pool 2 in dual-pool mode (1…99 %).</summary>
    public int PrimaryPercent => Math.Clamp(Balance, 1, 99);
    public int SecondaryPercent => 100 - PrimaryPercent;

    /// <summary>Puts a library pool into a slot; advanced AxeOS settings stay only if the host stays the same.</summary>
    public void Assign(PoolConfig config, int index)
    {
        if (index < 0 || index >= Slots.Count) return;
        var endpoint = config.Endpoint;
        if (string.Equals(StratumHost.Clean(Slots[index].Host), config.Host, StringComparison.OrdinalIgnoreCase))
            endpoint = endpoint with { Advanced = Slots[index].Advanced };
        Slots[index] = endpoint;
    }

    /// <summary>
    /// Makes <paramref name="config"/> the primary pool. On AxeOS 2.15+ a slot that already holds it is just selected,
    /// otherwise the pool goes into a free slot so the previous pools stay stored on the miner.
    /// </summary>
    public void MakePrimary(PoolConfig config)
    {
        var target = SlotIndex(config) ?? FreeSlot ?? PrimarySlot;
        if (!config.Matches(Slots[target])) Assign(config, target);
        if (target == SecondarySlot && Api == PoolApi.Slots) SecondarySlot = PrimarySlot;
        PrimarySlot = target;
    }

    /// <summary>Makes <paramref name="config"/> the fallback (dual mode: pool 2), as <see cref="MakePrimary"/>.</summary>
    public void MakeFallback(PoolConfig config)
    {
        var target = SlotIndex(config) ?? FreeSlot ?? SecondarySlot;
        // Swapping roles only works if the current fallback can take over as primary.
        if (target == PrimarySlot && (Api == PoolApi.Legacy || !Slots[SecondarySlot].HasContent)) return;
        if (!config.Matches(Slots[target])) Assign(config, target);
        if (target == PrimarySlot) PrimarySlot = SecondarySlot;
        SecondarySlot = target;
    }

    private int? SlotIndex(PoolConfig config)
    {
        if (Api != PoolApi.Slots) return null;
        var index = Slots.FindIndex(config.Matches);
        return index >= 0 ? index : null;
    }

    private int? FreeSlot
    {
        get
        {
            if (Api != PoolApi.Slots) return null;
            for (var i = 0; i < Slots.Count; i++)
                if (!Slots[i].HasContent && i != PrimarySlot && i != SecondarySlot) return i;
            return null;
        }
    }

    /// <summary>Slots worth showing: filled ones, primary and fallback, plus one free slot to add a pool.</summary>
    public List<int> VisibleSlots
    {
        get
        {
            if (Api != PoolApi.Slots) return Enumerable.Range(0, Slots.Count).ToList();
            var visible = Enumerable.Range(0, Slots.Count).Where(i => Slots[i].HasContent || i == PrimarySlot || i == SecondarySlot).ToList();
            var free = Enumerable.Range(0, Slots.Count).Where(i => !visible.Contains(i)).Cast<int?>().FirstOrDefault();
            if (free is { } f) visible.Add(f);
            visible.Sort();
            return visible;
        }
    }

    public void ClearSlot(int index)
    {
        if (index >= 0 && index < Slots.Count) Slots[index] = new PoolEndpoint();
    }

    /// <summary>Slots that were stored on the miner and have been emptied; AxeOS refuses to delete primary and fallback.</summary>
    public List<int> SlotsToClear =>
        Api != PoolApi.Slots ? [] : StoredSlots.Where(i => !Slots[i].HasContent && i != PrimarySlot && i != SecondarySlot).Order().ToList();

    /// <summary>A slot whose address does not fit its pool; sending stays possible.</summary>
    public string? CoinWarning
    {
        get
        {
            for (var i = 0; i < Slots.Count; i++)
                if (MinedCoinDetector.MismatchWarning(Slots[i].Host, Slots[i].User) is { } warning) return $"{SlotTitle(i)}: {warning}";
            return null;
        }
    }

    /// <summary>Why the pool settings cannot be sent yet, or <c>null</c>.</summary>
    public string? ValidationMessage
    {
        get
        {
            if (StratumHost.Clean(Primary.Host).Length == 0) return L("Der primäre Pool braucht einen Host.", "The primary pool needs a host.");
            for (var i = 0; i < Slots.Count; i++)
            {
                var slot = Slots[i];
                if (!slot.HasContent) continue;
                if (slot.Port is < 1 or > 65535) return L($"{SlotTitle(i)}: Ungültiger Port {slot.Port}.", $"{SlotTitle(i)}: Invalid port {slot.Port}.");
                if (Api == PoolApi.Slots && !slot.IsComplete)
                    return L($"{SlotTitle(i)}: Host und Wallet/Stratum-User sind erforderlich.", $"{SlotTitle(i)}: Host and wallet/stratum user are required.");
            }
            if (Api == PoolApi.Slots && !Slots[SecondarySlot].HasContent && SecondarySlot != PrimarySlot && StoredSlots.Contains(SecondarySlot))
                return L("Der Fallback-Slot ist leer. Wähle einen anderen Fallback-Slot oder belege ihn.",
                         "The fallback slot is empty. Choose another fallback slot or fill this one.");
            return null;
        }
    }

    public string SlotTitle(int index) => Api == PoolApi.Slots ? $"Slot {index + 1}" : index == 0 ? "Pool 1" : "Pool 2";

    /// <summary>
    /// Pool configuration. Dual-pool fields only go to firmware that supports them; an empty fallback pool is
    /// left untouched on the miner. AxeOS 2.15+ gets all filled slots.
    /// </summary>
    public MinerSystemUpdatePayload Payload(bool supportsDualPool)
    {
        if (Api == PoolApi.Slots)
        {
            return new MinerSystemUpdatePayload
            {
                Pools = Slots.Select((slot, i) => slot.IsComplete ? slot.AxePool(i) : null).OfType<JsonObject>().ToList(),
                PrimaryPoolIndex = PrimarySlot,
                SecondaryPoolIndex = SecondarySlot,
            };
        }
        var fallbackHost = StratumHost.Clean(Fallback.Host);
        var fallbackUser = Fallback.User.Trim();
        return new MinerSystemUpdatePayload
        {
            PoolMode = supportsDualPool ? (int)Mode : null,
            PoolBalance = supportsDualPool && Mode == PoolMode.Dual ? PrimaryPercent : null,
            StratumUrl = StratumHost.Clean(Primary.Host),
            StratumPort = Primary.Port,
            StratumUser = Primary.User.Trim(),
            StratumPassword = Primary.Password.Length == 0 ? null : Primary.Password,
            FallbackStratumUrl = fallbackHost.Length == 0 ? null : fallbackHost,
            FallbackStratumPort = fallbackHost.Length == 0 ? null : Fallback.Port,
            FallbackStratumUser = fallbackUser.Length == 0 ? null : fallbackUser,
            FallbackStratumPassword = Fallback.Password.Length == 0 ? null : Fallback.Password,
        };
    }

    /// <summary>Whether the user changed anything compared to <paramref name="other"/>.</summary>
    public bool HasSamePoolSettings(PoolSettingsForm other) =>
        Api == other.Api && Mode == other.Mode && PrimaryPercent == other.PrimaryPercent
        && Slots.SequenceEqual(other.Slots) && PrimarySlot == other.PrimarySlot && SecondarySlot == other.SecondarySlot;
}
