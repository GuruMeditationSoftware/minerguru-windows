using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using MinerGuru.Dialogs;

namespace MinerGuru.Controls;

/// <summary>Pool switches at set times, on the pools page. A rule is one switch; going back is a second rule.</summary>
public sealed partial class PoolSchedulesCard : UserControl
{
    private static PoolScheduleStore Schedules => App.Store.PoolSchedules;

    private readonly StackPanel _rows = new();
    private readonly TextBlock _error = Palette.UiText("", 12, Palette.Bad);
    private readonly TextBlock _note = Palette.UiText("", 12, Palette.TextSecondary);
    private readonly StackPanel _otp = new();
    private readonly Button _add;
    private string _otpKey = "";

    public PoolSchedulesCard()
    {
        _add = PageParts.TextButton("", Strings.ScheduleASwitch, Add);
        _add.HorizontalAlignment = HorizontalAlignment.Right;
        var header = new Grid();
        var title = PageParts.Title(Strings.ScheduledPoolSwitching);
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);
        header.Children.Add(_add);
        _note.TextWrapping = _error.TextWrapping = TextWrapping.Wrap;

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(header);
        body.Children.Add(_note);
        body.Children.Add(_rows);
        body.Children.Add(_otp);
        body.Children.Add(_error);
        Content = PageParts.Card(body);
        Loaded += (_, _) =>
        {
            Schedules.Changed += Render;
            App.Store.PoolLibrary.Changed += Render;
            App.Store.Changed += RenderOtp;
            Render();
        };
        Unloaded += (_, _) =>
        {
            Schedules.Changed -= Render;
            App.Store.PoolLibrary.Changed -= Render;
            App.Store.Changed -= RenderOtp;
        };
        ActualThemeChanged += (_, _) => Render();
    }

    private void RenderOtp()
    {
        if (OtpWaitingNotice.Key == _otpKey) return;
        _otpKey = OtpWaitingNotice.Key;
        _otp.Children.Clear();
        if (OtpWaitingNotice.Build() is { } notice) _otp.Children.Add(notice);
    }

    private void Render()
    {
        RenderOtp();
        _note.Foreground = Palette.TextSecondary;
        _error.Foreground = Palette.Bad;
        _note.Text = App.Store.Power.RunsSchedules
            ? Strings.MovesMinersToASaved
            : Strings.ThisPCDoesNotRun;
        _add.IsEnabled = App.Store.PoolLibrary.Pools.Count > 0;
        _error.Text = Schedules.LastError is { } error ? string.Format(Strings.LastPoolSwitchFailed, error) : "";
        _error.Visibility = Schedules.LastError != null ? Visibility.Visible : Visibility.Collapsed;
        _rows.Children.Clear();
        foreach (var rule in Schedules.Rules)
        {
            if (_rows.Children.Count > 0) _rows.Children.Add(new Rectangle { Height = 1, Fill = Palette.Hairline });
            _rows.Children.Add(RuleRow(rule));
        }
    }

    private Grid RuleRow(PoolScheduleRule rule)
    {
        var row = PageParts.Row(1, 4);
        var toggle = new ToggleSwitch { IsOn = rule.IsEnabled, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, Strings.SwitchOnOrOff);
        toggle.Toggled += (_, _) => { if (toggle.IsOn != rule.IsEnabled) Schedules.SetEnabled(rule.Id, toggle.IsOn); };
        row.Children.Add(toggle);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var pool = App.Store.PoolLibrary.Find(rule.PoolId)?.Name ?? Strings.PoolDeleted;
        var time = $"{rule.Minute / 60:00}:{rule.Minute % 60:00}";
        var title = Palette.UiText($"{pool} · {WeekdayOrder.Summary(rule.Weekdays)} {time}", 13, rule.IsEnabled ? Palette.TextPrimary : Palette.TextTertiary);
        title.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        text.Children.Add(title);
        var miners = rule.MinerIds.Count == 0
            ? Strings.AllMiners
            : string.Join(", ", App.Store.Miners.Where(m => rule.MinerIds.Contains(m.Id)).Select(m => m.DisplayName)) is { Length: > 0 } names
                ? names : Strings.MinerRemoved;
        text.Children.Add(Palette.MonoText(miners, 11, Palette.TextSecondary));
        row.Children.Add(text.At(1));
        row.Children.Add(PageParts.IconButton("", Strings.Edit, () => _ = EditAsync(rule, isNew: false)).At(2));
        row.Children.Add(PageParts.IconButton("", Strings.DeleteSwitch, () => Schedules.Remove(rule.Id)).At(3));
        return row;
    }

    private void Add() => _ = EditAsync(new PoolScheduleRule { PoolId = App.Store.PoolLibrary.Pools[0].Id }, isNew: true);

    private async Task EditAsync(PoolScheduleRule rule, bool isNew)
    {
        var dialog = new PoolScheduleDialog(rule, isNew, App.Store.PoolLibrary.Pools, App.Store.Miners) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Result is { } saved) Schedules.Save(saved);
    }
}
