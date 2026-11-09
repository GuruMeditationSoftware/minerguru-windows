namespace MinerGuru.Core.Models;

/// <summary>
/// Moves miners to a pool of the library at a set time on set weekdays, for example to a lottery pool on weekend
/// nights and back on Monday morning. A rule is one switch; going back is a second rule.
/// </summary>
public sealed class PoolScheduleRule
{
    /// <summary>A miner that was offline at the set time is still switched when it comes back within this time.</summary>
    public static readonly TimeSpan CatchUp = TimeSpan.FromMinutes(30);

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PoolId { get; set; }
    /// <summary>Miners this rule switches; empty means all miners.</summary>
    public HashSet<Guid> MinerIds { get; set; } = [];
    public HashSet<DayOfWeek> Weekdays { get; set; } = [.. WeekdayOrder.All];
    /// <summary>Minutes after midnight.</summary>
    public int Minute { get; set; } = 22 * 60;
    public bool IsEnabled { get; set; } = true;
    /// <summary>A rule never fires for a time before it existed.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public bool AppliesTo(Guid minerId) => MinerIds.Count == 0 || MinerIds.Contains(minerId);

    /// <summary>The last set time at or before <paramref name="now"/>, looking back one week at most.</summary>
    public DateTime? LastOccurrence(DateTime now)
    {
        for (var back = 0; back <= 7; back++)
        {
            var day = now.Date.AddDays(-back);
            if (!Weekdays.Contains(day.DayOfWeek)) continue;
            var moment = day.AddMinutes(Minute);
            if (moment <= now) return moment;
        }
        return null;
    }

    /// <summary>The occurrence that should be carried out now, if it was not carried out for this miner yet.</summary>
    public DateTime? DueOccurrence(DateTime now, DateTime? lastApplied)
    {
        if (!IsEnabled || LastOccurrence(now) is not { } moment) return null;
        if (now - moment > CatchUp || moment < CreatedAt) return null;
        return lastApplied == null || moment > lastApplied ? moment : null;
    }
}
