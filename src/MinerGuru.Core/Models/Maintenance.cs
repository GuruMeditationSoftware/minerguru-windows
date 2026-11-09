using static MinerGuru.Core.Lang;

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
        MaintenanceKind.Firmware => L("Firmware-Update prüfen", "Check for firmware update"),
        MaintenanceKind.FanCleaning => L("Lüfter reinigen", "Clean the fan"),
        MaintenanceKind.Dust => L("Kühlkörper entstauben", "Dust the heatsink"),
        MaintenanceKind.ThermalPaste => L("Wärmeleitpaste erneuern", "Renew thermal paste"),
        MaintenanceKind.FanReplacement => L("Lüfter tauschen", "Replace the fan"),
        MaintenanceKind.Power => L("Netzteil und Stecker prüfen", "Check power supply and plugs"),
        MaintenanceKind.Cabling => L("Standort und WLAN prüfen", "Check location and Wi-Fi"),
        MaintenanceKind.Pools => L("Pool und Auszahlungsadresse prüfen", "Check pool and payout address"),
        MaintenanceKind.Backup => L("Einstellungen sichern", "Back up settings"),
        MaintenanceKind.Custom => L("Eigene Aufgabe", "Custom task"),
        _ => "",
    };

    public static string Hint(this MaintenanceKind kind) => kind switch
    {
        MaintenanceKind.Firmware => L("Neue Firmware bringt Fehlerbehebungen. Vor dem Update Einstellungen sichern.", "New firmware brings fixes. Back up your settings before updating."),
        MaintenanceKind.FanCleaning => L("Staub am Lüfter kostet Drehzahl und treibt die Temperatur hoch. Nur im Stillstand reinigen.", "Dust on the fan costs speed and drives up the temperature. Clean it only when it has stopped."),
        MaintenanceKind.Dust => L("Mit Druckluft oder Pinsel, Miner vorher vom Strom trennen.", "Use compressed air or a brush, and unplug the miner first."),
        MaintenanceKind.ThermalPaste => L("Steigt die Temperatur bei gleichem Takt, ist die Paste oft trocken.", "If the temperature rises at the same frequency, the paste has often dried out."),
        MaintenanceKind.FanReplacement => L("Lager nutzen sich ab. Laute oder langsame Lüfter rechtzeitig ersetzen.", "Bearings wear out. Replace loud or slow fans in good time."),
        MaintenanceKind.Power => L("Warme Stecker und knappe Netzteile sind die häufigste Brandursache.", "Warm plugs and undersized power supplies are the most common fire hazard."),
        MaintenanceKind.Cabling => L("Luftzufuhr, Umgebungstemperatur und WLAN-Empfang kontrollieren.", "Check airflow, room temperature and Wi-Fi reception."),
        MaintenanceKind.Pools => L("Stimmen Pool und Adresse noch? Abgleich mit der Blockansicht.", "Are pool and address still right? Compare them with the block view."),
        MaintenanceKind.Backup => L("Pools und Takt notieren oder exportieren, bevor etwas schiefgeht.", "Note down or export pools and frequency before something goes wrong."),
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
