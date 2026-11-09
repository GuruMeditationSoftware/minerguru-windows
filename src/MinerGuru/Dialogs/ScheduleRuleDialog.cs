using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MinerGuru.Controls;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Dialogs;

/// <summary>Create or edit one schedule, as the Mac's ScheduleRuleEditorSheet: when, what, which miners.</summary>
public sealed partial class ScheduleRuleDialog : ContentDialog
{
    private static readonly PowerAction[] Actions = [PowerAction.NightMode, PowerAction.Pause, PowerAction.Normal];

    private readonly ScheduleRule _rule;
    private readonly IReadOnlyList<TariffZone> _zones;
    private readonly IReadOnlyList<MinerDevice> _miners;
    private readonly WeeklyWindow _window;
    private readonly RadioButton _zoneOption;
    private readonly ComboBox _zoneBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _actionBox = new() { MinWidth = 200 };
    private readonly UIElement _zoneFields;
    private readonly UIElement _windowFields;
    private readonly TextBlock _normalNote = PowerForm.Note(L(
        "„Normal“ hält Miner bei ihren eigenen Einstellungen, auch wenn ein Zeitplan weiter unten etwas anderes will.",
        "\"Normal\" keeps miners on their own settings, even if a schedule further down wants something else."));
    private readonly CheckBox _allMiners = new() { Content = L("Alle Miner", "All miners") };
    private readonly StackPanel _minerChecks = new();

    /// <summary>The saved schedule once the dialog was confirmed.</summary>
    public ScheduleRule? Result { get; private set; }

    /// <param name="rule">A copy: it is edited directly.</param>
    public ScheduleRuleDialog(ScheduleRule rule, bool isNew, IReadOnlyList<TariffZone> zones, IReadOnlyList<MinerDevice> miners)
    {
        _rule = rule;
        _zones = zones;
        _miners = miners;
        _window = rule.Trigger.Window?.Copy() ?? new WeeklyWindow(WeekdayOrder.All, 22 * 60, 6 * 60);
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 600.0;
        PrimaryButtonText = L("Sichern", "Save");
        CloseButtonText = L("Abbrechen", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        _zoneOption = new RadioButton { Content = L("Während einer Tarifzone", "During a tariff zone"), GroupName = "when", IsEnabled = zones.Count > 0 };
        var windowOption = new RadioButton { Content = L("Zu festen Uhrzeiten", "At fixed times"), GroupName = "when" };
        var options = new StackPanel();
        options.Children.Add(_zoneOption);
        options.Children.Add(windowOption);

        AutomationProperties.SetName(_zoneBox, L("Tarifzone", "Tariff zone"));
        AutomationProperties.SetName(_actionBox, L("Aktion", "Action"));
        foreach (var zone in zones) _zoneBox.Items.Add($"{zone.Name} ({WeekdayOrder.Summary(zone.Window.Weekdays)} {zone.Window.TimeRangeText})");
        _zoneBox.SelectedIndex = Math.Max(0, zones.ToList().FindIndex(z => z.Id == rule.Trigger.ZoneId));
        var zoneFields = new StackPanel { Spacing = 14 };
        zoneFields.Children.Add(PowerForm.Row("Zone", _zoneBox));
        var zoneNote = PowerForm.Note(L("Ändert sich die Zone, zieht der Zeitplan mit.", "If the zone changes, the schedule follows."));
        zoneNote.Margin = new Thickness(108, -8, 0, 0);
        zoneFields.Children.Add(zoneNote);
        _zoneFields = zoneFields;

        var days = new WeekdayPicker(_window.Weekdays);
        days.Changed += Validate;
        var windowFields = new StackPanel { Spacing = 14 };
        windowFields.Children.Add(PowerForm.Row(L("Tage", "Days"), days));
        windowFields.Children.Add(PowerForm.Row(L("Uhrzeit", "Time"), new TimeWindowFields(_window)));
        _windowFields = windowFields;

        foreach (var action in Actions) _actionBox.Items.Add(action.Title());
        _actionBox.SelectedIndex = Array.IndexOf(Actions, rule.Action);
        _actionBox.SelectionChanged += (_, _) =>
        {
            _rule.Action = Actions[_actionBox.SelectedIndex];
            SyncAction();
        };
        _normalNote.Margin = new Thickness(108, -8, 0, 0);

        _allMiners.Click += (_, _) =>
        {
            _rule.MinerIds = _allMiners.IsChecked == true ? [] : _miners.Select(m => m.Id).ToHashSet();
            RenderMiners();
        };
        var minerPanel = new StackPanel();
        minerPanel.Children.Add(_allMiners);
        minerPanel.Children.Add(new ScrollViewer { Content = _minerChecks, MaxHeight = 170 });

        var form = new StackPanel { Width = 520, Spacing = 14 };
        var title = Palette.UiText(isNew ? L("Neuer Zeitplan", "New schedule") : L("Zeitplan bearbeiten", "Edit schedule"), 15, Palette.TextPrimary);
        title.FontWeight = FontWeights.Medium;
        form.Children.Add(title);
        form.Children.Add(PowerForm.Row(L("Wann", "When"), options));
        form.Children.Add(_zoneFields);
        form.Children.Add(_windowFields);
        form.Children.Add(PowerForm.Row(L("Aktion", "Action"), _actionBox));
        form.Children.Add(_normalNote);
        form.Children.Add(PowerForm.Row("Miner", minerPanel));
        Content = form;

        _zoneOption.Checked += (_, _) => SyncWhen();
        windowOption.Checked += (_, _) => SyncWhen();
        (rule.Trigger.ZoneId != null && zones.Count > 0 ? _zoneOption : windowOption).IsChecked = true;
        SyncWhen();
        SyncAction();
        PrimaryButtonClick += (_, _) =>
        {
            _rule.Trigger = _zoneOption.IsChecked == true ? ScheduleTrigger.During(_zones[_zoneBox.SelectedIndex].Id) : ScheduleTrigger.At(_window);
            Result = _rule;
        };
    }

    private void SyncWhen()
    {
        var byZone = _zoneOption.IsChecked == true;
        _zoneFields.Visibility = byZone ? Visibility.Visible : Visibility.Collapsed;
        _windowFields.Visibility = byZone ? Visibility.Collapsed : Visibility.Visible;
        Validate();
    }

    private void SyncAction()
    {
        _normalNote.Visibility = _rule.Action == PowerAction.Normal ? Visibility.Visible : Visibility.Collapsed;
        RenderMiners();
    }

    private void RenderMiners()
    {
        _allMiners.IsChecked = _rule.MinerIds.Count == 0;
        _minerChecks.Children.Clear();
        if (_rule.MinerIds.Count == 0) return;
        foreach (var miner in _miners)
        {
            var check = new CheckBox { Content = MinerLabel(miner), IsChecked = _rule.MinerIds.Contains(miner.Id) };
            var id = miner.Id;
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true) _rule.MinerIds.Add(id);
                else if (_rule.MinerIds.Count > 1) _rule.MinerIds.Remove(id);
                else check.IsChecked = true;    // one stays: an empty set would mean all miners
            };
            _minerChecks.Children.Add(check);
        }
    }

    private string MinerLabel(MinerDevice miner) =>
        _rule.Action == PowerAction.Pause && PowerStore.Effective(PowerAction.Pause, miner) != PowerAction.Pause
            ? L($"{miner.DisplayName} (kann nicht pausieren, Nachtmodus)", $"{miner.DisplayName} (cannot pause, night mode)")
            : miner.DisplayName;

    private void Validate() => IsPrimaryButtonEnabled = _zoneOption.IsChecked == true ? _zones.Count > 0 : _window.Weekdays.Count > 0;
}
