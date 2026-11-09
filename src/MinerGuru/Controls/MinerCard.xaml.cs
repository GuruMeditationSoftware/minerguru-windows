using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Controls;

public sealed partial class MinerCard : UserControl
{
    private readonly DeviceCard _board = new(84);
    private readonly (TextBlock Label, TextBlock Value, TextBlock Detail)[] _sensors = new (TextBlock, TextBlock, TextBlock)[4];
    private MinerDevice? _miner;
    private bool _hovered;

    public MinerCard()
    {
        InitializeComponent();
        BoardHost.Child = _board;
        for (var i = 0; i < _sensors.Length; i++)
        {
            var label = Palette.UiText("", 11, Palette.TextTertiary);
            var value = Palette.MonoText("", 12, Palette.TextPrimary);
            var detail = Palette.MonoText(" ");
            var column = new StackPanel { Spacing = 4 };
            column.Children.Add(label);
            column.Children.Add(value);
            column.Children.Add(detail);
            Grid.SetColumn(column, i);
            SensorGrid.Children.Add(column);
            _sensors[i] = (label, value, detail);
        }
        ActualThemeChanged += (_, _) => { if (_miner != null) Update(_miner); };
    }

    public void Update(MinerDevice miner)
    {
        _miner = miner;
        var hot = App.Store.IsHot(miner);
        var info = miner.LatestInfo;

        _board.Update(miner, hot);
        NameText.Text = miner.DisplayName;
        HardwareText.Text = miner.HardwareTitle;
        AddressText.Text = string.IsNullOrEmpty(info?.FirmwareVersion) ? MinerGuru.Core.Privacy.Ip(miner.IpAddress) : $"{MinerGuru.Core.Privacy.Ip(miner.IpAddress)}  {info.FirmwareVersion}";
        Restyle(hot);

        var online = info != null && miner.IsOnline;
        OnlinePart.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        OfflinePart.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        if (!online || info == null)
        {
            var offline = miner.State == ConnectionState.Offline;
            ConnectingRing.IsActive = !offline;
            ConnectingRing.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
            OfflineTitle.Text = offline ? L("Nicht erreichbar", "Unreachable") : L($"Verbinde mit {miner.IpAddress}…", $"Connecting to {miner.IpAddress}…");
            OfflineTitle.Foreground = offline ? Palette.Bad : Palette.TextSecondary;
            OfflineTitle.FontSize = offline ? 13 : 12;
            OfflineReason.Text = offline ? miner.OfflineReason ?? "" : "";
            return;
        }
        ConnectingRing.IsActive = false;

        CoinHost.Child = Badges.ActiveCoins(info, compact: true);
        var (value, unit) = MinerFormatting.SplitHashrate(info.HashRate);
        RateValue.Text = value;
        RateUnit.Text = unit;
        DiffText.Text = L($"Session-Best {info.SessionBestDiffText} · Allzeit {info.BestDiffFormatted}",
                          $"Session best {info.SessionBestDiffText} · all-time {info.BestDiffFormatted}");
        Sparkline.SetPoints(miner.History.TakeLast(30).Select(s => (s.Timestamp, s.HashRateGhs)));

        SetSensor(0, "ASIC", $"{DisplayFormat.Decimal(info.Temp, 1)} °C", null, hot);
        if (info.VrTemp is { } vr) SetSensor(1, "VR", $"{DisplayFormat.Decimal(vr, 0)} °C", null, vr > 75);
        else SetSensor(1, L("Takt", "Clock"), $"{info.FrequencyMhz} MHz", null);
        SetSensor(2, L("Leistung", "Power"), $"{DisplayFormat.Decimal(info.Power, 1)} W", $"{DisplayFormat.Decimal(info.EfficiencyJPerTh, 1)} J/TH");
        if (info.HasSecondFan())
            SetSensor(3, L("Lüfter", "Fan"), $"{DisplayFormat.Decimal(info.FanSpeedPercent, 0)} / {DisplayFormat.Decimal(info.Fan2SpeedPercent ?? info.FanSpeedPercent, 0)} %", $"{info.FanRpm}/{info.Fan2Rpm} RPM");
        else
            SetSensor(3, L("Lüfter", "Fan"), $"{DisplayFormat.Decimal(info.FanSpeedPercent, 0)} %", info.FanRpm > 0 ? $"{info.FanRpm} RPM" : null);

        PoolText.Text = info.ActivePoolEndpoint;
        PoolModeHost.Child = Badges.PoolMode(info, compact: true);
        LatencyHost.Child = Badges.Latency(miner.ActivePoolLatencyMs, compact: true);
    }

    private void SetSensor(int index, string label, string value, string? detail, bool warning = false)
    {
        var (labelText, valueText, detailText) = _sensors[index];
        labelText.Text = label;
        labelText.Foreground = Palette.TextTertiary;
        valueText.Text = value;
        valueText.Foreground = warning ? Palette.Warning : Palette.TextPrimary;
        detailText.Text = detail ?? " ";
        detailText.Foreground = Palette.TextTertiary;
    }

    private void Restyle(bool hot)
    {
        Card.BorderBrush = hot ? Palette.Get("WarningStrokeBrush") : Palette.Hairline;
        // A hint of the raised surface on hover; the Mac card has none, Windows expects some feedback.
        Card.Background = _hovered ? Palette.Get("InsetBrush") : Palette.Get("CardBrush");
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hovered = true;
        Restyle(_miner != null && App.Store.IsHot(_miner));
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hovered = false;
        Restyle(_miner != null && App.Store.IsHot(_miner));
    }

    private void Card_Tapped(object sender, TappedRoutedEventArgs e) => Open_Click(sender, e);

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_miner != null) App.MainWindow.ShowMiner(_miner.Id);
    }

    private async void Browser_Click(object sender, RoutedEventArgs e)
    {
        if (_miner != null) await Windows.System.Launcher.LaunchUriAsync(new Uri($"http://{_miner.IpAddress}"));
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_miner != null) await App.Store.RestartMinerAsync(_miner.Id);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_miner != null) await App.MainWindow.ConfirmRemoveAsync(_miner);
    }
}
