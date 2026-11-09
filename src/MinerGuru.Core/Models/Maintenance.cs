
namespace MinerGuru.Core.Models;

/// <summary>What a maintenance task is about. Each kind brings its name and a sensible interval.</summary>
public enum MaintenanceKind { Firmware, FanCleaning, Dust, ThermalPaste, FanReplacement, Power, Cabling, Pools, Backup, Custom }

public enum MaintenanceStatus { Overdue, DueSoon, Planned }

public static class MaintenanceKindText
{
    public static int DefaultIntervalDays(this MaintenanceKind kind) => kind switch
    {
        MaintenanceKind.Firmware or MaintenanceKind.Pools or MaintenanceKind.Custom => 30,
        MaintenanceKind.FanCleaning or MaintenanceKind.Backup => 90,
        MaintenanceKind.Dust or MaintenanceKind.Power or MaintenanceKind.Cabling => 180,
        MaintenanceKind.ThermalPaste => 365,
        _ => 730,
    };

    public static string Title(this MaintenanceKind kind) => kind switch
    {
        MaintenanceKind.Firmware => Strings.CheckForFirmwareUpdate,
        MaintenanceKind.FanCleaning => Strings.CleanTheFan,
        MaintenanceKind.Dust => Strings.DustTheHeatsink,
        MaintenanceKind.ThermalPaste => Strings.RenewThermalPaste,
        MaintenanceKind.FanReplacement => Strings.ReplaceTheFan,
        MaintenanceKind.Power => Strings.CheckPowerSupplyAndPlugs,
        MaintenanceKind.Cabling => Strings.CheckLocationAndWiFi,
        MaintenanceKind.Pools => Strings.CheckPoolAndPayoutAddress,
        MaintenanceKind.Backup => Strings.BackUpSettings,
        MaintenanceKind.Custom => Strings.CustomTask,
        _ => "",
    };

    public static string Hint(this MaintenanceKind kind) => kind switch
    {
        MaintenanceKind.Firmware => Strings.NewFirmwareBringsFixesBack,
        MaintenanceKind.FanCleaning => Strings.DustOnTheFanCosts,
        MaintenanceKind.Dust => Strings.UseCompressedAirOrA,
        MaintenanceKind.ThermalPaste => Strings.IfTheTemperatureRisesAt,
        MaintenanceKind.FanReplacement => Strings.BearingsWearOutReplaceLoud,
        MaintenanceKind.Power => Strings.WarmPlugsAndUndersizedPower,
        MaintenanceKind.Cabling => Strings.CheckAirflowRoomTemperatureAnd,
        MaintenanceKind.Pools => Strings.ArePoolAndAddressStill,
        MaintenanceKind.Backup => Strings.NoteDownOrExportPools,
        MaintenanceKind.Custom => "",
        _ => "",
    };
}

public sealed class MaintenanceTask
{
    public const int DueSoonDays = 7;

    public Guid Id { get; set; } = Guid.NewGuid();
    public MaintenanceKind Kind { get; set; }
    /// <summary>Own title, for <see cref="MaintenanceKind.Custom"/> or to rename a task.</summary>
    public string CustomTitle { get; set; } = "";
    /// <summary>Miners this task is for; empty means all miners.</summary>
    public HashSet<Guid> MinerIds { get; set; } = [];
    /// <summary>Days until it is due again; 0 = once.</summary>
    public int IntervalDays { get; set; }
    public DateTime NextDue { get; set; }
    public string Notes { get; set; } = "";

    public string Title => string.IsNullOrWhiteSpace(CustomTitle) ? Kind.Title() : CustomTitle.Trim();

    public int DaysUntilDue(DateTime now) => (NextDue.Date - now.Date).Days;

    public MaintenanceStatus Status(DateTime now) => DaysUntilDue(now) switch
    {
        < 0 => MaintenanceStatus.Overdue,
        <= DueSoonDays => MaintenanceStatus.DueSoon,
        _ => MaintenanceStatus.Planned,
    };

    /// <summary>The next date after it was done on <paramref name="date"/>; <c>null</c> for a task that runs once.</summary>
    public DateTime? NextDueAfterDone(DateTime date) => IntervalDays > 0 ? date.AddDays(IntervalDays) : null;
}

/// <summary>One finished maintenance job, kept as a record per miner.</summary>
public sealed class MaintenanceRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MaintenanceKind Kind { get; set; }
    public string Title { get; set; } = "";
    public HashSet<Guid> MinerIds { get; set; } = [];
    public DateTime DoneAt { get; set; }
    public string Notes { get; set; } = "";
}
