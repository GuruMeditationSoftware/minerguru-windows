using System.Globalization;
using System.Text.Json.Serialization;
using MinerGuru.Core.Services;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Models;

public static class WeekdayOrder
{
    /// <summary>Monday first, as the week bar and the day toggles show it.</summary>
    public static readonly IReadOnlyList<DayOfWeek> MondayFirst =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
    public static readonly IReadOnlySet<DayOfWeek> All = MondayFirst.ToHashSet();
    public static readonly IReadOnlySet<DayOfWeek> Workdays = MondayFirst.Take(5).ToHashSet();
    public static readonly IReadOnlySet<DayOfWeek> Weekend = MondayFirst.Skip(5).ToHashSet();

    public static DayOfWeek Previous(DayOfWeek day) => day == DayOfWeek.Sunday ? DayOfWeek.Saturday : day - 1;

    /// <summary>Short name in the UI language, e.g. „Mo“.</summary>
    public static string ShortName(DayOfWeek day) => Culture.DateTimeFormat.GetAbbreviatedDayName(day);

    /// <summary>„Mo–Fr“, „Sa, So“ or „täglich“ for a set of days.</summary>
    public static string Summary(IReadOnlySet<DayOfWeek> days)
    {
        if (days.SetEquals(All)) return Strings.DailyInText;
        if (days.SetEquals(Workdays)) return $"{ShortName(DayOfWeek.Monday)}–{ShortName(DayOfWeek.Friday)}";
        return string.Join(", ", MondayFirst.Where(days.Contains).Select(ShortName));
    }
}

/// <summary>
/// A recurring time window: on the given days from <see cref="StartMinute"/> to <see cref="EndMinute"/> (minutes after
/// midnight). An end before the start runs over midnight into the next day; start == end covers the whole day.
/// </summary>
public sealed class WeeklyWindow
{
    public const int MinutesPerDay = 24 * 60;

    public HashSet<DayOfWeek> Weekdays { get; set; } = [];
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }

    public bool WrapsMidnight => EndMinute < StartMinute;
    public bool IsWholeDay => StartMinute == EndMinute;

    public WeeklyWindow() { }

    public WeeklyWindow(IEnumerable<DayOfWeek> weekdays, int startMinute, int endMinute)
    {
        Weekdays = weekdays.ToHashSet();
        StartMinute = startMinute;
        EndMinute = endMinute;
    }

    public WeeklyWindow Copy() => new(Weekdays, StartMinute, EndMinute);

    /// <summary>Whether the window covers <paramref name="minute"/> of a day that is <paramref name="weekday"/>.</summary>
    public bool Contains(DayOfWeek weekday, int minute)
    {
        if (IsWholeDay) return Weekdays.Contains(weekday);
        // The part after midnight belongs to the day the window started on.
        if (WrapsMidnight)
            return (minute >= StartMinute && Weekdays.Contains(weekday)) || (minute < EndMinute && Weekdays.Contains(WeekdayOrder.Previous(weekday)));
        return minute >= StartMinute && minute < EndMinute && Weekdays.Contains(weekday);
    }

    public bool Contains(DateTime date) => Contains(date.DayOfWeek, date.Hour * 60 + date.Minute);

    /// <summary>„22:00–06:00“.</summary>
    public string TimeRangeText => IsWholeDay ? Strings.AllDay : $"{Clock(StartMinute)}–{Clock(EndMinute)}";

    public static string Clock(int minute) => $"{minute / 60 % 24:00}:{minute % 60:00}";
}

/// <summary>One price zone of a time-of-use tariff, e.g. „Nacht“ 22–6 Uhr for 0.24 per kWh.</summary>
public sealed class TariffZone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Price per kWh in the tariff's currency.</summary>
    public double PricePerKwh { get; set; }
    public WeeklyWindow Window { get; set; } = new();

    public TariffZone Copy() => new() { Id = Id, Name = Name, PricePerKwh = PricePerKwh, Window = Window.Copy() };
}

public sealed record TariffSegment(Guid? ZoneId, int StartMinute, int EndMinute);

/// <summary>
/// The user's electricity tariff: a base price, optionally refined by zones. The first zone that covers a moment
/// sets its price; where no zone applies, the base price does.
/// </summary>
public sealed class PowerTariff
{
    /// <summary>ISO currency code, free to choose.</summary>
    public string CurrencyCode { get; set; } = DefaultCurrency;
    /// <summary>Price per kWh when no zone applies (the fixed price if there are no zones).</summary>
    public double BasePricePerKwh { get; set; } = 0.30;
    public List<TariffZone> Zones { get; set; } = [];

    private static string DefaultCurrency
    {
        get
        {
            try { return new RegionInfo(CultureInfo.CurrentCulture.Name).ISOCurrencySymbol; }
            catch (ArgumentException) { return "EUR"; }
        }
    }

    public bool HasZones => Zones.Count > 0;

    public TariffZone? Zone(DateTime date) => Zones.FirstOrDefault(z => z.Window.Contains(date));

    public TariffZone? Zone(DayOfWeek weekday, int minute) => Zones.FirstOrDefault(z => z.Window.Contains(weekday, minute));

    public double Price(DateTime date) => Zone(date)?.PricePerKwh ?? BasePricePerKwh;

    /// <summary>Stretches of one day with the same zone (<c>null</c> = base price), for the week bar.</summary>
    public List<TariffSegment> Segments(DayOfWeek weekday, int stepMinutes = 15)
    {
        var result = new List<TariffSegment>();
        for (var start = 0; start < WeeklyWindow.MinutesPerDay; start += stepMinutes)
        {
            var zoneId = Zone(weekday, start)?.Id;
            if (result.Count > 0 && result[^1].ZoneId == zoneId) result[^1] = result[^1] with { EndMinute = start + stepMinutes };
            else result.Add(new TariffSegment(zoneId, start, start + stepMinutes));
        }
        return result;
    }

    /// <summary>Pairs of zones that cover the same time somewhere in the week; the earlier one wins there.</summary>
    public List<(TariffZone First, TariffZone Second)> OverlappingZones
    {
        get
        {
            var pairs = new List<(TariffZone, TariffZone)>();
            for (var i = 0; i < Zones.Count; i++)
                foreach (var other in Zones.Skip(i + 1))
                    if (Overlap(Zones[i].Window, other.Window)) pairs.Add((Zones[i], other));
            return pairs;
        }
    }

    /// <summary>Whether some time of the week is not covered by any zone (and falls back to the base price).</summary>
    public bool HasGaps => HasZones && QuarterHours().Any(q => Zone(q.Day, q.Minute) == null);

    private static bool Overlap(WeeklyWindow a, WeeklyWindow b) =>
        QuarterHours().Any(q => a.Contains(q.Day, q.Minute) && b.Contains(q.Day, q.Minute));

    private static IEnumerable<(DayOfWeek Day, int Minute)> QuarterHours() =>
        from day in WeekdayOrder.MondayFirst
        from minute in Enumerable.Range(0, WeeklyWindow.MinutesPerDay / 15)
        select (day, minute * 15);

    /// <summary>The amount in the tariff's currency, in the notation of the UI language.</summary>
    public string FormatPrice(double value, int digits = 2)
    {
        var format = (NumberFormatInfo)Culture.NumberFormat.Clone();
        format.CurrencySymbol = CurrencyCode switch { "EUR" => "€", "USD" => "$", "GBP" => "£", _ => CurrencyCode };
        format.CurrencyDecimalDigits = digits;
        return value.ToString("C", format);
    }

    /// <summary>A price as it stands in a text box: 0,30 or 0,2875.</summary>
    public static string PriceText(double value) => value.ToString("0.00##", Culture);

    /// <summary>What the user typed, with a comma or a point; <c>null</c> if it is no price.</summary>
    public static double? ParsePrice(string text) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Three letters, upper case; <c>null</c> until the code is complete.</summary>
    public static string? ParseCurrency(string text)
    {
        var code = new string(text.Where(char.IsLetter).Take(3).ToArray()).ToUpperInvariant();
        return code.Length == 3 ? code : null;
    }
}

/// <summary>What a schedule does with a miner while it applies.</summary>
public enum PowerAction
{
    /// <summary>The miner's own settings, as they were before any schedule changed them.</summary>
    Normal,
    /// <summary>Lower clock and a fixed, quieter fan speed.</summary>
    NightMode,
    /// <summary>Stop hashing (AxeOS 2.15+); other firmware falls back to the night mode.</summary>
    Pause,
}

public static class PowerActionText
{
    public static string Title(this PowerAction action) => action switch
    {
        PowerAction.NightMode => Strings.NightMode,
        PowerAction.Pause => "Pause",
        _ => "Normal",
    };
}

/// <summary>When a schedule applies: during a tariff zone (follows the zone when it changes) or a time window of its own.</summary>
public sealed class ScheduleTrigger
{
    public Guid? ZoneId { get; set; }
    public WeeklyWindow? Window { get; set; }

    public static ScheduleTrigger During(Guid zoneId) => new() { ZoneId = zoneId };
    public static ScheduleTrigger At(WeeklyWindow window) => new() { Window = window };

    public ScheduleTrigger Copy() => new() { ZoneId = ZoneId, Window = Window?.Copy() };
}

/// <summary>Trigger + action + miners. Rules higher up in the list win when several apply at once.</summary>
public sealed class ScheduleRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ScheduleTrigger Trigger { get; set; } = new();
    public PowerAction Action { get; set; }
    /// <summary>Miners this rule switches; empty means all miners.</summary>
    public HashSet<Guid> MinerIds { get; set; } = [];
    public bool IsEnabled { get; set; } = true;

    public bool AppliesTo(Guid minerId) => MinerIds.Count == 0 || MinerIds.Contains(minerId);

    public bool IsActive(DateTime date, PowerTariff tariff)
    {
        if (!IsEnabled) return false;
        if (Trigger.ZoneId is { } zoneId) return tariff.Zone(date)?.Id == zoneId;
        return Trigger.Window?.Contains(date) == true;
    }

    public ScheduleRule Copy() => new() { Id = Id, Trigger = Trigger.Copy(), Action = Action, MinerIds = [.. MinerIds], IsEnabled = IsEnabled };
}

public static class PowerSchedule
{
    /// <summary>The action the rules ask for at <paramref name="date"/>, normal if none applies.</summary>
    public static PowerAction Action(Guid minerId, DateTime date, IEnumerable<ScheduleRule> rules, PowerTariff tariff) =>
        rules.FirstOrDefault(r => r.AppliesTo(minerId) && r.IsActive(date, tariff))?.Action ?? PowerAction.Normal;

    /// <summary>
    /// Energy cost of drawing <paramref name="watts"/> from <paramref name="start"/> for <paramref name="hours"/>, at the
    /// price valid in every quarter hour. Quarter hours in which the rules pause the miner cost nothing (if it can pause).
    /// </summary>
    public static double Cost(double watts, DateTime start, double hours, PowerTariff tariff,
                              IEnumerable<ScheduleRule>? rules = null, Guid? minerId = null, bool canPause = true)
    {
        if (watts <= 0 || hours <= 0) return 0;
        var steps = (int)Math.Round(hours * 4);
        var total = 0.0;
        for (var i = 0; i < steps; i++)
        {
            var moment = start.AddMinutes(i * 15);
            if (rules != null && minerId is { } id && canPause && Action(id, moment, rules, tariff) == PowerAction.Pause) continue;
            total += watts / 1000 * 0.25 * tariff.Price(moment);
        }
        return total;
    }
}

/// <summary>The miner's own clock and fan settings, saved before a schedule changes them and written back afterwards.</summary>
public sealed class SavedMinerSettings
{
    public int FrequencyMhz { get; set; }
    public int CoreVoltageMv { get; set; }
    public int AutoFanMode { get; set; }
    public int FanPercent { get; set; }

    public static SavedMinerSettings From(MinerSystemInfo info) => new()
    {
        FrequencyMhz = info.FrequencyMhz,
        CoreVoltageMv = info.CoreVoltageTargetMv,
        AutoFanMode = info.AutoFanSpeed ? Math.Max(1, info.AutoFanModeRaw) : 0,
        FanPercent = (int)Math.Round(info.FanSpeedPercent),
    };

    [JsonIgnore]
    public MinerSystemUpdatePayload Payload => new()
    {
        Frequency = FrequencyMhz,
        CoreVoltage = CoreVoltageMv,
        AutoFanSpeed = AutoFanMode,
        FanSpeed = AutoFanMode == 0 ? FanPercent : null,
    };
}

/// <summary>What the schedules last did to one miner.</summary>
public sealed class MinerPowerState
{
    public PowerAction Action { get; set; }
    public DateTime Since { get; set; }
    /// <summary>Settings before the night mode, restored on the way back to normal.</summary>
    public SavedMinerSettings? Saved { get; set; }
}
