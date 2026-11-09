using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using MinerGuru.Controls;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using MinerGuru.Dialogs;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Pages;

/// <summary>A label, a figure and a line of detail, as the Mac's PowerFigure.</summary>
internal sealed partial class PowerFigure : Grid
{
    private readonly TextBlock _value = new() { FontSize = 22, Style = (Style)Application.Current.Resources["FigureValueStyle"] };
    private readonly TextBlock _detail = new() { FontSize = 12, Foreground = Palette.TextSecondary, TextTrimming = TextTrimming.CharacterEllipsis };

    public PowerFigure(string label)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["FigureLabelStyle"] });
        // Long amounts shrink instead of being cut, as on the Mac.
        stack.Children.Add(new Viewbox { Child = _value, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left });
        stack.Children.Add(_detail);
        Children.Add(new Border { Padding = new Thickness(16), Child = stack, Style = (Style)Application.Current.Resources["CardBorderStyle"] });
        ActualThemeChanged += (_, _) => _detail.Foreground = Palette.TextSecondary;
    }

    public void Update(string value, string detail)
    {
        _value.Text = value;
        _detail.Text = detail;
        ToolTipService.SetToolTip(_detail, detail);
    }
}

public sealed partial class PowerPage : Page, IHeaderPage
{
    /// <summary>Relative column widths of the cost table.</summary>
    private static readonly double[] CostColumns = [210, 210, 90, 100, 90, 90, 130];

    private readonly PowerFigure _priceFigure = new(Strings.PriceNow);
    private readonly PowerFigure _powerFigure = new(Strings.Power);
    private readonly PowerFigure _hourFigure = new(Strings.CostPerHour);
    private readonly PowerFigure _todayFigure = new(Strings.Today);
    private readonly PowerFigure _monthFigure = new(Strings.Days30);
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    /// <summary>What the rows show; they are only rebuilt when it changes, so a poll leaves focus and tooltips alone.</summary>
    private string? _renderedRows;
    private int _renderedMinute = -1;
    /// <summary>The control that had the click, to get the focus back after its row was rebuilt.</summary>
    private string? _refocus;
    /// <summary>The cost table's content; a poll that changes nothing leaves its tooltips open.</summary>
    private string? _renderedCosts;
    private bool _syncingInputs;

    public PowerPage()
    {
        InitializeComponent();
        foreach (var figure in new[] { _priceFigure, _powerFigure, _hourFigure, _todayFigure, _monthFigure }) FigureGrid.Children.Add(figure);
        _clock.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            App.Store.Changed += Refresh;
            Power.Changed += Refresh;
            _clock.Start();
            SyncInputs();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            App.Store.Changed -= Refresh;
            Power.Changed -= Refresh;
            _clock.Stop();
        };
        ActualThemeChanged += (_, _) =>
        {
            _renderedRows = null;
            _renderedMinute = -1;
            _renderedCosts = null;
            Refresh();
        };
    }

    private static PowerStore Power => App.Store.Power;

    public (string Title, string Subtitle) Header => (Strings.PowerPage, Strings.TariffSchedulesAndCosts);

    private void SyncInputs()
    {
        _syncingInputs = true;
        CurrencyBox.Text = Power.CurrencyCode;
        BasePriceBox.Text = PowerTariff.PriceText(Power.BasePricePerKwh);
        RunsSchedulesBox.IsChecked = Power.RunsSchedules;
        NightFanBox.Value = Power.NightFanPercent;
        _syncingInputs = false;
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        UpdateFigures(now);
        UpdateCostTable(now);

        var rows = RowsKey(now);
        if (rows != _renderedRows)
        {
            _renderedRows = rows;
            RenderTariff(now);
            RenderRules(now);
            _renderedMinute = -1;
        }
        if (now.Minute != _renderedMinute)
        {
            _renderedMinute = now.Minute;
            WeekBar.Update(Power.Tariff, now);
        }
    }

    private static string RowsKey(DateTime now)
    {
        var tariff = Power.Tariff;
        var zones = tariff.Zones.Select(z => $"{z.Id} {z.Name} {z.PricePerKwh} {WeekdayOrder.Summary(z.Window.Weekdays)} {z.Window.TimeRangeText}");
        var rules = Power.Rules.Select(r => $"{r.Id} {r.IsEnabled} {r.Action} {TriggerText(r.Trigger)} {MinersText(r)} {r.IsActive(now, tariff)}");
        return string.Join('|', [OtpWaitingNotice.Key, tariff.CurrencyCode, tariff.BasePricePerKwh.ToString(), tariff.Zone(now)?.Id.ToString() ?? "", Power.LastError ?? "", App.Store.Miners.Count > 0 ? "m" : "", .. zones, "/", .. rules]);
    }

    // Figures

    private void UpdateFigures(DateTime now)
    {
        var store = App.Store;
        var tariff = Power.Tariff;
        var zone = tariff.Zone(now);
        var watts = store.TotalPowerWatts;
        var today = FleetCost(24, now.Date);
        var month = FleetCost(24 * 30, now);
        var totalTh = store.TotalHashrateGhs / 1000;
        _priceFigure.Update(tariff.FormatPrice(tariff.Price(now), 3),
            zone != null ? $"{zone.Name} · {zone.Window.TimeRangeText}" : tariff.HasZones ? Strings.BasePrice : Strings.FixedPrice);
        _powerFigure.Update($"{watts:0} W", string.Format(Strings.MinersOnline, store.OnlineMiners.Count()));
        _hourFigure.Update(tariff.FormatPrice(Power.CostPerHour(watts, now), 3), Strings.AtCurrentPower);
        _todayFigure.Update(tariff.FormatPrice(today), Strings.Hours0To24Projected);
        _monthFigure.Update(tariff.FormatPrice(month),
            totalTh > 0.01 ? string.Format(Strings.PerTHS, tariff.FormatPrice(month / totalTh)) : Strings.WithPricesAndPauses);
    }

    private static double FleetCost(double hours, DateTime start) => App.Store.OnlineMiners.Sum(m => Power.ProjectedCost(m, hours, start));

    // Tariff

    private void RenderTariff(DateTime now)
    {
        var tariff = Power.Tariff;
        BasePriceLabel.Text = tariff.HasZones ? Strings.BasePricePerKWh : Strings.PricePerKWh;
        AutomationProperties.SetName(BasePriceBox, BasePriceLabel.Text);
        NoZonesText.Visibility = tariff.HasZones ? Visibility.Collapsed : Visibility.Visible;
        ZoneRows.Visibility = tariff.HasZones ? Visibility.Visible : Visibility.Collapsed;
        ZoneRows.Children.Clear();
        var active = tariff.Zone(now);
        for (var i = 0; i < tariff.Zones.Count; i++)
        {
            if (i > 0) ZoneRows.Children.Add(new Rectangle { Height = 1, Fill = Palette.Hairline });
            ZoneRows.Children.Add(ZoneRow(tariff.Zones[i], i, tariff.Zones[i] == active));
        }

        TariffHints.Children.Clear();
        foreach (var (first, second) in tariff.OverlappingZones)
            TariffHints.Children.Add(Hint(string.Format(Strings.AndOverlapAppliesThereBecause, first.Name, second.Name), "\uE7BA", Palette.Warning));
        if (tariff.HasGaps)
            TariffHints.Children.Add(Hint(Strings.TimesWithoutAZoneCost, "\uE946", Palette.TextSecondary));
        TariffHints.Visibility = TariffHints.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Grid ZoneRow(TariffZone zone, int index, bool isNow)
    {
        var row = RowGrid(1, 7);
        var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = ZonePalette.Color(index), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(swatch);

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var name = Palette.UiText(zone.Name, 13, Palette.TextPrimary);
        name.FontWeight = FontWeights.Medium;
        title.Children.Add(name);
        if (isNow)
        {
            var nowLabel = Palette.UiText(Strings.Now, 11, Palette.Accent);
            nowLabel.FontWeight = FontWeights.Medium;
            nowLabel.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(nowLabel);
        }
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(title);
        text.Children.Add(Palette.MonoText($"{WeekdayOrder.Summary(zone.Window.Weekdays)} · {zone.Window.TimeRangeText}", 11, Palette.TextSecondary));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var price = Palette.MonoText($"{Power.Tariff.FormatPrice(zone.PricePerKwh, 3)} / kWh", 12, Palette.TextSecondary);
        price.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(price, 2);
        row.Children.Add(price);

        var ruleCount = Power.Rules.Count(r => r.Trigger.ZoneId == zone.Id);
        if (ruleCount > 0)
        {
            var count = Palette.UiText(ruleCount == 1 ? Strings.OneSchedule : string.Format(Strings.RuleCountSchedules, ruleCount), 11, Palette.TextSecondary);
            count.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(count, 3);
            row.Children.Add(count);
        }

        AddIconButton(row, 4, "\uE70E", Strings.MoveUpWinsWhereZones, () => Power.MoveZoneUp(index), $"zone-up-{zone.Id}", enabled: index > 0);
        AddIconButton(row, 5, "\uE70F", Strings.Edit, () => _ = EditZoneAsync(zone.Copy(), isNew: false));
        AddIconButton(row, 6, "\uE74D", Strings.DeleteZoneSchedulesForThis, () => Power.RemoveZone(zone.Id));
        return row;
    }

    private void AddZone_Click(object sender, RoutedEventArgs e) => _ = EditZoneAsync(new TariffZone
    {
        Name = Strings.Night,
        PricePerKwh = Power.BasePricePerKwh,
        Window = new WeeklyWindow(WeekdayOrder.All, 22 * 60, 6 * 60),
    }, isNew: true);

    private async Task EditZoneAsync(TariffZone zone, bool isNew)
    {
        var dialog = new TariffZoneDialog(zone, isNew, Power.CurrencyCode) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Result is { } saved) Power.SaveZone(saved);
    }

    private void DayNightTemplate_Click(object sender, RoutedEventArgs e) => Power.AddZones(
        new TariffZone { Name = Strings.Night, PricePerKwh = Discounted, Window = new WeeklyWindow(WeekdayOrder.All, 22 * 60, 6 * 60) },
        new TariffZone { Name = Strings.Day, PricePerKwh = Power.BasePricePerKwh, Window = new WeeklyWindow(WeekdayOrder.Workdays, 6 * 60, 22 * 60) });

    private void WeekendTemplate_Click(object sender, RoutedEventArgs e) => Power.AddZones(
        new TariffZone { Name = Strings.Weekend, PricePerKwh = Discounted, Window = new WeeklyWindow(WeekdayOrder.Weekend, 0, 0) });

    private static double Discounted => Math.Round(Power.BasePricePerKwh * 0.8, 3);

    private void CurrencyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingInputs && PowerTariff.ParseCurrency(CurrencyBox.Text) is { } code) Power.CurrencyCode = code;
    }

    private void CurrencyBox_LostFocus(object sender, RoutedEventArgs e) => CurrencyBox.Text = Power.CurrencyCode;

    private void BasePriceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingInputs && PowerTariff.ParsePrice(BasePriceBox.Text) is { } price) Power.BasePricePerKwh = price;
    }

    private void BasePriceBox_LostFocus(object sender, RoutedEventArgs e) => BasePriceBox.Text = PowerTariff.PriceText(Power.BasePricePerKwh);

    // Schedules

    private void RenderRules(DateTime now)
    {
        AddRuleButton.IsEnabled = App.Store.Miners.Count > 0;
        var rules = Power.Rules;
        NoRulesText.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RuleRows.Visibility = rules.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RuleRows.Children.Clear();
        for (var i = 0; i < rules.Count; i++)
        {
            if (i > 0) RuleRows.Children.Add(new Rectangle { Height = 1, Fill = Palette.Hairline });
            RuleRows.Children.Add(RuleRow(rules[i], i, now));
        }

        ScheduleError.Children.Clear();
        if (OtpWaitingNotice.Build() is { } notice) ScheduleError.Children.Add(notice);
        if (Power.LastError is { } error)
            ScheduleError.Children.Add(Hint(string.Format(Strings.LastSwitchFailed, error), "\uE7BA", Palette.Bad));
        ScheduleError.Visibility = ScheduleError.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _refocus = null;
    }

    private Grid RuleRow(ScheduleRule rule, int index, DateTime now)
    {
        var row = RowGrid(2, 7);
        var toggle = Refocusing($"rule-on-{rule.Id}", new ToggleSwitch { IsOn = rule.IsEnabled, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center });
        ToolTipService.SetToolTip(toggle, Strings.ScheduleOnOrOff);
        AutomationProperties.SetName(toggle, Strings.ScheduleOnOrOff);
        toggle.Toggled += (_, _) =>
        {
            _refocus = $"rule-on-{rule.Id}";
            Power.SetRuleEnabled(rule.Id, toggle.IsOn);
        };
        row.Children.Add(toggle);

        var icon = new FontIcon { Glyph = ActionGlyph(rule.Action), FontSize = 14, Width = 20, Foreground = rule.IsEnabled ? Palette.TextPrimary : Palette.TextTertiary, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(icon, 1);
        row.Children.Add(icon);

        var text = new StackPanel { Spacing = 2 };
        var title = Palette.UiText($"{rule.Action.Title()} · {TriggerText(rule.Trigger)}", 13, rule.IsEnabled ? Palette.TextPrimary : Palette.TextSecondary);
        title.FontWeight = FontWeights.Medium;
        text.Children.Add(title);
        var miners = Palette.MonoText(MinersText(rule), 11, Palette.TextSecondary);
        miners.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(miners);
        Grid.SetColumn(text, 2);
        row.Children.Add(text);

        if (rule.IsActive(now, Power.Tariff))
        {
            var active = Palette.UiText(Strings.ActiveInText, 11, Palette.Accent);
            active.FontWeight = FontWeights.Medium;
            active.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(active, 3);
            row.Children.Add(active);
        }

        AddIconButton(row, 4, "\uE70E", Strings.MoveUpWinsWhenSeveral, () => Power.MoveRuleUp(index), $"rule-up-{rule.Id}", enabled: index > 0);
        AddIconButton(row, 5, "\uE70F", Strings.Edit, () => _ = EditRuleAsync(rule.Copy(), isNew: false));
        AddIconButton(row, 6, "\uE74D", Strings.DeleteSchedule, () => Power.RemoveRule(rule.Id));
        return row;
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var trigger = Power.Tariff.Zones.FirstOrDefault() is { } zone
            ? ScheduleTrigger.During(zone.Id)
            : ScheduleTrigger.At(new WeeklyWindow(WeekdayOrder.All, 22 * 60, 6 * 60));
        _ = EditRuleAsync(new ScheduleRule { Trigger = trigger, Action = PowerAction.NightMode }, isNew: true);
    }

    private async Task EditRuleAsync(ScheduleRule rule, bool isNew)
    {
        var dialog = new ScheduleRuleDialog(rule, isNew, Power.Tariff.Zones, App.Store.Miners) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Result is { } saved) Power.SaveRule(saved);
    }

    private static string TriggerText(ScheduleTrigger trigger)
    {
        if (trigger.ZoneId is { } id)
            return Power.Zone(id) is { } zone ? string.Format(Strings.During, zone.Name) : Strings.ZoneDeleted;
        return trigger.Window is { } window ? $"{WeekdayOrder.Summary(window.Weekdays)} {window.TimeRangeText}" : "";
    }

    private static string MinersText(ScheduleRule rule) => rule.MinerIds.Count == 0
        ? Strings.AllMiners
        : string.Join(", ", App.Store.Miners.Where(m => rule.MinerIds.Contains(m.Id)).Select(m => m.DisplayName)) is { Length: > 0 } names
            ? names : Strings.MinerRemoved;

    private static string ActionGlyph(PowerAction action) => action switch
    {
        PowerAction.NightMode => "\uE708",
        PowerAction.Pause => "\uE769",
        _ => "\uE945",
    };

    private void RunsSchedulesBox_Click(object sender, RoutedEventArgs e) => Power.RunsSchedules = RunsSchedulesBox.IsChecked == true;

    private void NightFanBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingInputs || double.IsNaN(args.NewValue)) return;
        var percent = (int)Math.Round(args.NewValue / 5) * 5;
        Power.NightFanPercent = percent;
        if (sender.Value != Power.NightFanPercent) sender.Value = Power.NightFanPercent;
    }

    // Costs

    private void UpdateCostTable(DateTime now)
    {
        var store = App.Store;
        NoMinersText.Visibility = store.Miners.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CostTable.Visibility = store.Miners.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var costs = string.Join('|', store.Miners.Select(m => CostKey(m, now)));
        if (costs == _renderedCosts) return;
        _renderedCosts = costs;
        CostTable.Children.Clear();
        if (store.Miners.Count == 0) return;
        CostTable.Children.Add(CostHeader());
        CostTable.Children.Add(new Rectangle { Height = 1, Fill = Palette.Hairline });
        foreach (var miner in store.Miners) CostTable.Children.Add(CostRow(miner, now));
    }

    private static string CostKey(MinerDevice miner, DateTime now) =>
        $"{miner.DisplayName} {miner.IsOnline} {miner.LatestInfo?.Power:0.0} {miner.LatestInfo?.HashRate:0} {Power.State(miner.Id)} {Power.PlannedAction(miner.Id, now)} {Power.RunsSchedules} {Power.Tariff.CurrencyCode} {Power.Tariff.Price(now)} {now:HH}";

    private static Grid CostColumnsGrid()
    {
        var grid = new Grid { ColumnSpacing = 16 };
        foreach (var width in CostColumns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        return grid;
    }

    private static void AddCell(Grid grid, int column, FrameworkElement cell)
    {
        if (cell is TextBlock text)
        {
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            if (column >= 2) text.TextAlignment = TextAlignment.Right;
        }
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static Grid CostHeader()
    {
        string[] titles =
        [
            "Miner", Strings.Schedule, Strings.Power, Strings.PerHourInText,
            Strings.TodayInText, Strings.Days30, Strings.Days30PerTHs,
        ];
        var header = CostColumnsGrid();
        for (var i = 0; i < titles.Length; i++)
        {
            var title = Palette.UiText(titles[i], 11, Palette.TextSecondary);
            title.FontWeight = FontWeights.Medium;
            AddCell(header, i, title);
        }
        return header;
    }

    private static Grid CostRow(MinerDevice miner, DateTime now)
    {
        var tariff = Power.Tariff;
        var info = miner.LatestInfo;
        var online = miner.IsOnline && info != null;
        var watts = info?.Power ?? 0;
        var th = (info?.HashRate ?? 0) / 1000;
        var month = Power.ProjectedCost(miner, 24 * 30, now);
        var row = CostColumnsGrid();

        AddCell(row, 0, Palette.MonoText(miner.DisplayName, 12, Palette.TextPrimary));
        AddCell(row, 1, ScheduleState(miner, now));
        AddCell(row, 2, Palette.MonoText(online ? $"{watts.ToString("0.0", Culture)} W" : "Offline", 12, online ? Palette.TextPrimary : Palette.TextTertiary));
        AddCell(row, 3, Palette.MonoText(online ? tariff.FormatPrice(Power.CostPerHour(watts, now), 3) : "—", 12, Palette.TextPrimary));
        AddCell(row, 4, Palette.MonoText(online ? tariff.FormatPrice(Power.CostToday(miner, now)) : "—", 12, Palette.TextPrimary));
        AddCell(row, 5, Palette.MonoText(online ? tariff.FormatPrice(month) : "—", 12, Palette.TextPrimary));
        AddCell(row, 6, Palette.MonoText(online && th > 0.01 ? tariff.FormatPrice(month / th) : "—", 12, Palette.TextPrimary));
        return row;
    }

    private static StackPanel ScheduleState(MinerDevice miner, DateTime now)
    {
        var planned = PowerStore.Effective(Power.PlannedAction(miner.Id, now), miner);
        var current = Power.State(miner.Id);
        var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        cell.Children.Add(new FontIcon { Glyph = ActionGlyph(current), FontSize = 12, Foreground = Palette.TextPrimary });
        cell.Children.Add(Palette.UiText(current.Title(), 12, Palette.TextPrimary));
        if (planned != current)
        {
            var next = Palette.UiText($"→ {planned.Title()}", 12, Palette.TextSecondary);
            ToolTipService.SetToolTip(next, Power.RunsSchedules
                ? Strings.SwitchesOnceTheMinerIs
                : Strings.ThisPCDoesNotRunSchedules);
            cell.Children.Add(next);
        }
        return cell;
    }

    // Shared

    /// <summary>A row with Auto columns around the one star column at <paramref name="starColumn"/>.</summary>
    private static Grid RowGrid(int starColumn, int columns)
    {
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 8, 0, 8) };
        for (var i = 0; i < columns; i++)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = i == starColumn ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        return row;
    }

    private void AddIconButton(Grid row, int column, string glyph, string tip, Action click, string? focusKey = null, bool enabled = true)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Style = (Style)Application.Current.Resources["BarButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = enabled,
        };
        // The disabled state of the default button would draw a box.
        button.Resources["ButtonBackgroundDisabled"] = Palette.Rgb(0, 0, 0, 0);
        button.Resources["ButtonBorderBrushDisabled"] = Palette.Rgb(0, 0, 0, 0);
        ToolTipService.SetToolTip(button, tip);
        AutomationProperties.SetName(button, tip);
        if (focusKey != null) Refocusing(focusKey, button);
        button.Click += (_, _) =>
        {
            _refocus = focusKey;
            click();
        };
        Grid.SetColumn(button, column);
        row.Children.Add(button);
    }

    /// <summary>Gives a rebuilt control the focus its predecessor had, so it does not jump to the sidebar.</summary>
    private T Refocusing<T>(string key, T control) where T : Control
    {
        if (key == _refocus)
        {
            RoutedEventHandler? restore = null;
            restore = (_, _) =>
            {
                control.Loaded -= restore;
                control.Focus(FocusState.Programmatic);
            };
            control.Loaded += restore;
        }
        return control;
    }

    private static Grid Hint(string text, string glyph, Microsoft.UI.Xaml.Media.Brush color)
    {
        var hint = new Grid { ColumnSpacing = 6 };
        hint.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hint.ColumnDefinitions.Add(new ColumnDefinition());
        hint.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12, Foreground = color, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var label = Palette.UiText(text, 12, Palette.TextSecondary);
        label.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(label, 1);
        hint.Children.Add(label);
        return hint;
    }
}
