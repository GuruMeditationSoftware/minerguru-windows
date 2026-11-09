using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MinerGuru.Controls;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;

namespace MinerGuru.Dialogs;

/// <summary>Settings of one miner: clock and fan, name and hostname. Pools live on the pools page, as their own card.</summary>
public sealed partial class MinerControlDialog : ContentDialog
{
    private readonly MinerDevice _miner;
    private readonly InfoBar _status = new() { IsOpen = false, IsClosable = true };
    private readonly NumberBox _frequency = new() { SmallChange = 25, LargeChange = 25, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 170 };
    private readonly NumberBox _voltage = new() { SmallChange = 10, LargeChange = 10, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 170 };
    private readonly ToggleSwitch _autoFan = new() { Header = Strings.AutomaticTemperatureControlAutoFan };
    private readonly Slider _fan = new() { Minimum = 20, Maximum = 100, StepFrequency = 5, Header = Strings.ManualFanSpeed };
    private readonly TextBox _alias = new() { PlaceholderText = Strings.YourOwnNameEmptyHostname };
    private readonly TextBox _hostname = new() { PlaceholderText = "bitaxe", FontFamily = Palette.Mono };

    public MinerControlDialog(MinerDevice miner)
    {
        _miner = miner;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 640.0;
        Title = string.Format(Strings.MinerSettings, miner.DisplayName);
        CloseButtonText = Strings.Close;

        // Clock and voltage go out with every fan change: the range holds the miner's values, so they go out unchanged.
        (_frequency.Minimum, _frequency.Maximum) = miner.FrequencyRange;
        (_voltage.Minimum, _voltage.Maximum) = miner.CoreVoltageRange;
        if (miner.LatestInfo is { } info)
        {
            _frequency.Value = info.FrequencyMhz;
            _voltage.Value = info.CoreVoltageTargetMv;
            _autoFan.IsOn = info.AutoFanSpeed;
            _fan.Value = Math.Clamp(info.FanSpeedPercent > 0 ? info.FanSpeedPercent : 75, 20, 100);
            _hostname.Text = info.Hostname;
        }
        _alias.Text = miner.CustomName ?? "";
        _fan.Visibility = _autoFan.IsOn ? Visibility.Collapsed : Visibility.Visible;
        _autoFan.Toggled += (_, _) => _fan.Visibility = _autoFan.IsOn ? Visibility.Collapsed : Visibility.Visible;

        var pivot = new Pivot();
        pivot.Items.Add(new PivotItem { Header = Strings.ClockFan, Content = Scroll(PerformanceTab()) });
        pivot.Items.Add(new PivotItem { Header = Strings.SystemName, Content = Scroll(SystemTab()) });

        var content = new StackPanel { Spacing = 8, Width = 560 };
        content.Children.Add(_status);
        content.Children.Add(pivot);
        Content = content;
    }

    private static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        MaxHeight = 470,
        Padding = new Thickness(0, 12, 16, 8),
    };

    private static TextBlock Heading(string text)
    {
        var heading = Palette.UiText(text, 13, Palette.TextPrimary);
        heading.FontWeight = FontWeights.Medium;
        return heading;
    }

    private StackPanel PerformanceTab()
    {
        var tab = new StackPanel { Spacing = 14 };

        tab.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Title = Strings.OverclockingAtYourOwnRisk,
            Message = Strings.HigherClockAndVoltageProduce,
        });

        if (_miner.RecommendedPresets.Count > 0)
        {
            tab.Children.Add(Heading(string.Format(Strings.QuickProfilesFor, _miner.HardwareTitle)));
            foreach (var preset in _miner.RecommendedPresets)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new StackPanel { Spacing = 2 };
                text.Children.Add(Palette.UiText(preset.Name, 13, Palette.TextPrimary));
                text.Children.Add(Palette.UiText(preset.Subtitle, 11, Palette.TextSecondary));
                row.Children.Add(text);
                var value = Palette.MonoText($"{preset.FrequencyMhz} MHz  •  {preset.CoreVoltageMv} mV", 11, Palette.TextSecondary);
                value.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(value, 1);
                row.Children.Add(value);
                var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 8, 12, 8) };
                button.Click += (_, _) =>
                {
                    _frequency.Value = preset.FrequencyMhz;
                    _voltage.Value = preset.CoreVoltageMv;
                };
                tab.Children.Add(button);
            }
        }

        tab.Children.Add(Heading(Strings.ManualTuningASICClockCore));
        tab.Children.Add(Row(Strings.FrequencyMHz, _frequency));
        tab.Children.Add(Row("Core Voltage (mV)", _voltage));

        tab.Children.Add(Heading(Strings.FanControl));
        tab.Children.Add(_autoFan);
        tab.Children.Add(_fan);

        var apply = new Button { Content = Strings.ApplyClockFanToThe, Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
        apply.Click += async (_, _) => await SendAsync(new MinerSystemUpdatePayload
        {
            Frequency = (int)_frequency.Value,
            CoreVoltage = (int)_voltage.Value,
            OverclockEnabled = 1,
            AutoFanSpeed = _autoFan.IsOn ? Math.Max(1, _miner.LatestInfo?.AutoFanModeRaw ?? 1) : 0,
            FanSpeed = _autoFan.IsOn ? null : (int)_fan.Value,
        }, Strings.SettingsSentClockAndVoltage);
        // An emptied box holds NaN, which would reach the miner as 0.
        var missing = Palette.UiText(Strings.FrequencyAndCoreVoltageNeed, 12, Palette.Warning);
        void CheckValues()
        {
            var complete = !double.IsNaN(_frequency.Value) && !double.IsNaN(_voltage.Value);
            apply.IsEnabled = complete;
            missing.Visibility = complete ? Visibility.Collapsed : Visibility.Visible;
        }
        _frequency.ValueChanged += (_, _) => CheckValues();
        _voltage.ValueChanged += (_, _) => CheckValues();
        CheckValues();
        tab.Children.Add(missing);
        tab.Children.Add(apply);
        return tab;
    }

    private StackPanel SystemTab()
    {
        var tab = new StackPanel { Spacing = 14 };

        tab.Children.Add(Heading(Strings.DisplayNameInMinerGuru));
        var saveAlias = new Button { Content = Strings.Save, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        saveAlias.Click += (_, _) =>
        {
            App.Store.RenameMiner(_miner.Id, _alias.Text);
            Show(Strings.DisplayNameSaved, InfoBarSeverity.Success);
        };
        tab.Children.Add(FieldRow(_alias, saveAlias));

        tab.Children.Add(Heading(Strings.MinerHostnameOnTheNetwork));
        var saveHost = new Button { Content = Strings.SendHostname };
        saveHost.Click += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_hostname.Text))
                await SendAsync(new MinerSystemUpdatePayload { Hostname = _hostname.Text.Trim() }, Strings.HostnameSent);
        };
        tab.Children.Add(FieldRow(_hostname, saveHost));

        tab.Children.Add(Heading(Strings.Pools));
        var pools = new Button { Content = Strings.OpenThePoolsPage };
        pools.Click += (_, _) =>
        {
            Hide();
            App.MainWindow.ShowPools();
        };
        tab.Children.Add(pools);
        return tab;
    }

    private static Grid Row(string label, FrameworkElement control)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = Palette.UiText(label, 13, Palette.TextSecondary);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private static Grid FieldRow(FrameworkElement field, FrameworkElement button)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(field);
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return row;
    }

    private void Show(string message, InfoBarSeverity severity)
    {
        _status.Message = message;
        _status.Severity = severity;
        _status.IsOpen = true;
    }

    private async Task SendAsync(MinerSystemUpdatePayload payload, string success)
    {
        try
        {
            await App.Store.UpdateMinerSettingsAsync(_miner.Id, payload);
            Show(success, InfoBarSeverity.Success);
        }
        catch (WriteDeclinedException)
        {
            // Declined in the confirmation: nothing happened, nothing to report.
        }
        catch (Exception e) when (e is MinerApiException or HttpRequestException or TaskCanceledException)
        {
            Show(e.Message, InfoBarSeverity.Error);
        }
    }
}
