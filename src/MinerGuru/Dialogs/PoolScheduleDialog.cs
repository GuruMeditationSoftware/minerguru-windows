using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MinerGuru.Controls;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Dialogs;

/// <summary>Create or edit one scheduled pool switch: which pool, when, which miners.</summary>
public sealed partial class PoolScheduleDialog : ContentDialog
{
    private readonly PoolScheduleRule _rule;
    private readonly HashSet<DayOfWeek> _days;

    /// <summary>The saved rule once the dialog was confirmed.</summary>
    public PoolScheduleRule? Result { get; private set; }

    public PoolScheduleDialog(PoolScheduleRule rule, bool isNew, IReadOnlyList<PoolConfig> pools, IReadOnlyList<MinerDevice> miners)
    {
        _rule = rule;
        _days = [.. rule.Weekdays];
        var ids = new HashSet<Guid>(rule.MinerIds);
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 600.0;
        PrimaryButtonText = L("Sichern", "Save");
        CloseButtonText = L("Abbrechen", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        var poolBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(poolBox, "Pool");
        foreach (var pool in pools) poolBox.Items.Add(pool.Name);
        poolBox.SelectedIndex = Math.Max(0, pools.ToList().FindIndex(p => p.Id == rule.PoolId));
        var days = new WeekdayPicker(_days);
        days.Changed += () => IsPrimaryButtonEnabled = _days.Count > 0;
        var time = new TimePicker { ClockIdentifier = "24HourClock", MinuteIncrement = 5, Time = TimeSpan.FromMinutes(rule.Minute) };

        var form = new StackPanel { Width = 520, Spacing = 14 };
        form.Children.Add(PageParts.Title(isNew ? L("Pool-Wechsel planen", "Schedule a pool switch") : L("Pool-Wechsel bearbeiten", "Edit pool switch")));
        form.Children.Add(PowerForm.Row("Pool", poolBox));
        form.Children.Add(PowerForm.Row(L("Tage", "Days"), days));
        form.Children.Add(PowerForm.Row(L("Uhrzeit", "Time"), time));
        form.Children.Add(PowerForm.Row("Miner", new MinerScopePicker(ids, miners)));
        Content = form;
        IsPrimaryButtonEnabled = _days.Count > 0;

        PrimaryButtonClick += (_, _) =>
        {
            _rule.PoolId = pools[poolBox.SelectedIndex].Id;
            _rule.Weekdays = _days;
            _rule.Minute = (int)time.Time.TotalMinutes;
            _rule.MinerIds = ids;
            Result = _rule;
        };
    }
}
