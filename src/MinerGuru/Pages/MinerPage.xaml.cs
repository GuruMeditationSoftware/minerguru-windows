using MinerGuru.Core.Services;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MinerGuru.Controls;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Pages;

public sealed partial class MinerPage : Page, IHeaderPage
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly DeviceCard _board = new(168);
    private readonly StatusBlock _led = new(8);
    private readonly List<MetricCard> _kpis = [];
    private Guid _id;
    private MinerDevice? _miner;
    private MinerLogStream? _log;

    public MinerPage()
    {
        InitializeComponent();
        // Next to the board the buttons would be cut off in a narrow window.
        SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 960;
            Grid.SetRow(HeaderActions, narrow ? 1 : 0);
            Grid.SetColumn(HeaderActions, narrow ? 1 : 2);
            MetaRow.Spacing = narrow ? 10 : 24;
        };
        BoardHost.Child = _board;
        LedHost.Child = _led;
        foreach (var (de, en, glyph) in KpiTitles)
        {
            var card = new MetricCard(L(de, en), glyph);
            _kpis.Add(card);
            KpiGrid.Children.Add(card);
        }
        TempChart.ValueLabel = v => $"{v:0} °C";
        TempChart.LineBrush = () => Palette.TextSecondary;
        LatencyChart.ValueLabel = v => $"{v:0} ms";
        LatencyChart.LineBrush = () => Palette.TextSecondary;
        PowerChart.ValueLabel = v => $"{v:0.#} W";
        PowerChart.LineBrush = () => Palette.TextSecondary;
        Loaded += (_, _) =>
        {
            App.Store.Changed += Update;
            Update();
        };
        Unloaded += (_, _) =>
        {
            App.Store.Changed -= Update;
            _log?.Dispose();
            _log = null;
        };
        ActualThemeChanged += (_, _) => Update();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is Guid id) _id = id;
    }

    public (string Title, string Subtitle) Header
    {
        get
        {
            if (App.Store.Find(_id) is not { } miner) return ("Miner", "");
            return (miner.DisplayName, $"{miner.HardwareTitle} • {Privacy.Ip(miner.IpAddress)}");
        }
    }

    private static readonly (string De, string En, string Glyph)[] KpiTitles =
    [
        ("Live Hashrate", "Live Hashrate", ""),
        ("ASIC Temperatur", "ASIC Temperature", ""),
        ("Verbrauch", "Power Draw", ""),
        ("Effizienz", "Efficiency", ""),
        ("Kühlung & Lüfter", "Cooling & Fan", ""),
        ("ASIC Takt & VCore", "ASIC Clock & VCore", ""),
        ("Pool-Latenz (RTT)", "Pool Latency (RTT)", ""),
        ("Best Diff (seit Neustart)", "Best Diff (since restart)", ""),
        ("Shares", "Shares", ""),
    ];

    private void Update()
    {
        if (App.Store.Find(_id) is not { } miner) return;
        _miner = miner;
        var store = App.Store;
        var hot = store.IsHot(miner);
        var info = miner.LatestInfo;

        _board.Update(miner, hot);
        _led.Apply(miner, hot);
        NameText.Text = miner.DisplayName;
        HardwareText.Text = miner.HardwareTitle;
        AsicText.Text = info != null ? $"{info.AsicCount}× {info.AsicModel}" : "";
        BadgeRow.Children.Clear();
        if (info != null)
        {
            BadgeRow.Children.Add(Badges.PoolMode(info));
            BadgeRow.Children.Add(Badges.ActiveCoins(info));
        }
        RestartButton.IsEnabled = miner.IsOnline;

        MetaRow.Children.Clear();
        AddMeta("IP", Privacy.Ip(miner.IpAddress));
        if (info != null)
        {
            AddMeta("Uptime", MinerFormatting.FormatUptime(info.UptimeSeconds));
            if (!string.IsNullOrEmpty(info.Ssid)) AddMeta(L("WLAN", "Wi-Fi"), Privacy.Ssid(info.Ssid) + (info.WifiRssi is { } rssi ? $" ({rssi} dBm)" : ""));
            if (!string.IsNullOrEmpty(info.FirmwareVersion)) AddMeta("Firmware", info.FirmwareVersion);
        }
        var refresh = new HyperlinkButton
        {
            Padding = new Thickness(0),
            Foreground = Palette.TextSecondary,
            Content = RefreshLabel(),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(refresh, L("Jetzt aktualisieren", "Refresh now"));
        refresh.Click += async (_, _) => await App.Store.RefreshMinerAsync(miner.Id);
        MetaRow.Children.Add(refresh);

        OfflineBar.IsOpen = miner.State == ConnectionState.Offline;
        if (OfflineBar.IsOpen)
        {
            OfflineBar.Title = L("Nicht erreichbar", "Unreachable");
            OfflineBar.Message = miner.LastSeenAt is { } seen
                ? L($"{miner.OfflineReason} · zuletzt gesehen {DisplayFormat.Time(seen)}", $"{miner.OfflineReason} · last seen {DisplayFormat.Time(seen)}")
                : miner.OfflineReason ?? "";
        }
        OnlinePart.Visibility = info != null ? Visibility.Visible : Visibility.Collapsed;
        if (info == null) return;

        Payout.Update(miner, info);
        var (rate, rateUnit) = MinerFormatting.SplitHashrate(info.HashRate);
        var latency = miner.ActivePoolLatencyMs;
        _kpis[0].Set(rate, rateUnit, info.HashrateAveragesText());
        _kpis[1].Set(info.Temp.ToString("0.0", Inv), "°C", info.CoolingText(store.TempWarningThresholdC), hot || info.OverheatMode);
        _kpis[2].Set(info.Power.ToString("0.0", Inv), "W", info.PowerDetailText());
        _kpis[3].Set(info.EfficiencyJPerTh > 0 ? info.EfficiencyJPerTh.ToString("0.0", Inv) : "—", "J/TH", info.DailyEnergyText());
        _kpis[4].Set(info.FanPercentText(), "%", info.FanRpmText());
        _kpis[5].Set(info.FrequencyMhz.ToString(Inv), "MHz", info.CoreVoltageText());
        _kpis[6].Title = info.IsDualPoolMining ? L("Pool-Latenz (Dual RTT)", "Pool Latency (Dual RTT)") : L("Pool-Latenz (RTT)", "Pool Latency (RTT)");
        _kpis[6].Set(latency is { } ms ? ms.ToString("0", Inv) : "—", latency != null ? "ms" : null, miner.PoolLatencyDetailText());
        _kpis[7].Set(info.SessionBestDiffText, null, L($"Allzeit-Best: {info.BestDiffFormatted}", $"All-time best: {info.BestDiffFormatted}"));
        _kpis[8].Set(info.SharesAccepted.ToString(Inv), null, info.SharesText());

        Chart.SetPoints(miner.History.Select(s => (s.Timestamp, s.HashRateGhs)));
        TempChart.Reference = App.Store.TempWarningThresholdC;
        TempChart.SecondLine = miner.History.Where(s => s.VrTempC > 0).Select(s => (s.Timestamp, s.VrTempC!.Value)).ToList();
        TempChart.SetPoints(miner.History.Select(s => (s.Timestamp, s.AsicTempC)));
        LatencyChart.SetPoints(miner.History.Where(s => s.PoolLatencyMs > 0).Select(s => (s.Timestamp, s.PoolLatencyMs!.Value)));
        PowerChart.SetPoints(miner.History.Select(s => (s.Timestamp, s.PowerWatts)));

        PoolBadges.Children.Clear();
        PoolBadges.Children.Add(Badges.PoolMode(info));
        PoolBadges.Children.Add(Badges.ActiveCoins(info));
        PoolBadges.Children.Add(Badges.Latency(latency));
        FillRows(PoolRows,
        [
            (L("Aktiv", "Active"), info.ActivePoolEndpoint),
            (L("Primär", "Primary"), info.PrimaryPoolEndpoint),
            ("Fallback", info.FallbackPoolEndpoint),
            (L("Benutzer", "User"), Privacy.StratumUser(info.IsUsingFallbackStratum ? info.FallbackStratumUser : info.StratumUser)),
            (L("Netz-Difficulty", "Network difficulty"), info.NetworkDifficulty is { } net ? MinerFormatting.FormatDifficulty(net) : "—"),
        ]);
        FillRows(SystemRows,
        [
            ("Hostname", info.Hostname),
            ("ASIC", info.AsicCount > 1 ? $"{info.AsicCount}× {info.AsicModel}" : info.AsicModel),
            ("Board", info.BoardVersion ?? info.DeviceModel ?? "—"),
            ("MAC", Privacy.Mac(info.MacAddress ?? "—")),
            (L("Freier Speicher", "Free heap"), info.FreeHeap is { } heap ? $"{DisplayFormat.Integer(heap / 1024)} KB" : "—"),
        ]);

        Blocks.Update(miner);
        LongTerm.Show(miner.Id);
        if (_log == null)
        {
            _log = new MinerLogStream(miner.IpAddress);
            var log = _log;
            // Lines arrive in bursts: one redraw per 500 ms is enough to read them.
            var pending = false;
            log.Changed += () =>
            {
                if (pending) return;
                pending = true;
                DispatcherQueue.TryEnqueue(async () =>
                {
                    await Task.Delay(500);
                    pending = false;
                    if (_log != log) return;
                    Shares.Update(log);
                    LiveLog.Update(log);
                });
            };
            log.StartSampling();
            Shares.Update(log);
            LiveLog.Update(log);
        }
    }

    private static StackPanel RefreshLabel()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Foreground = Palette.TextSecondary });
        row.Children.Add(Palette.UiText(L("Jetzt aktualisieren", "Refresh now"), 11, Palette.TextSecondary));
        return row;
    }

    /// <summary><c>IP 192.168.1.5</c>: tertiary label, mono value.</summary>
    private void AddMeta(string label, string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Palette.UiText(label, 11, Palette.TextTertiary));
        var text = Palette.MonoText(value, 11, Palette.TextSecondary);
        text.IsTextSelectionEnabled = true;
        row.Children.Add(text);
        MetaRow.Children.Add(row);
    }

    private static void FillRows(Grid grid, (string Label, string Value)[] rows)
    {
        if (grid.ColumnDefinitions.Count == 0)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        // Rows stay; only their texts change from one poll to the next.
        if (grid.Children.Count != rows.Length * 2)
        {
            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            for (var i = 0; i < rows.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = Palette.UiText("", 12);
                var value = Palette.MonoText("", 12);
                value.IsTextSelectionEnabled = true;
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                grid.Children.Add(label);
                grid.Children.Add(value);
            }
        }
        for (var i = 0; i < rows.Length; i++)
        {
            var label = (TextBlock)grid.Children[i * 2];
            var value = (TextBlock)grid.Children[i * 2 + 1];
            label.Text = rows[i].Label;
            label.Foreground = Palette.TextTertiary;
            value.Text = rows[i].Value;
            value.Foreground = Palette.TextPrimary;
        }
    }

    // Actions

    private async void Control_Click(object sender, RoutedEventArgs e)
    {
        if (_miner != null) await new Dialogs.MinerControlDialog(_miner) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme }.ShowAsync();
    }

    private async void Json_Click(object sender, RoutedEventArgs e)
    {
        if (_miner != null) await new Dialogs.RawJsonDialog(_miner) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme }.ShowAsync();
    }

    private async void Browser_Click(object sender, RoutedEventArgs e)
    {
        if (_miner != null) await Windows.System.Launcher.LaunchUriAsync(new Uri($"http://{_miner.IpAddress}"));
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_miner == null) return;
        var name = _miner.DisplayName;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = L($"{name} neu starten?", $"Restart {name}?"),
            Content = L("Der Miner hört kurz auf zu schürfen.", "The miner stops mining for a moment."),
            PrimaryButtonText = L("Neu starten", "Restart"),
            CloseButtonText = L("Abbrechen", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await App.Store.RestartMinerAsync(_miner.Id);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_miner != null) await App.MainWindow.ConfirmRemoveAsync(_miner);
    }

    private void MoreMenu_Opening(object sender, object e)
    {
        var alerts = App.Store.Alerts;
        var muted = _miner != null && alerts.IsMuted(_miner.Id);
        MuteItem.Visibility = alerts.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        MuteItem.Text = muted ? L("Benachrichtigungen an", "Notifications on") : L("Stummschalten", "Mute");
        MuteIcon.Glyph = muted ? "\uEA8F" : "\uE7ED";
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_miner != null) App.Store.Alerts.SetMuted(!App.Store.Alerts.IsMuted(_miner.Id), _miner.Id);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_miner == null) return;
        var box = new TextBox
        {
            Text = _miner.CustomName ?? "",
            PlaceholderText = _miner.LatestInfo?.Hostname ?? _miner.IpAddress,
            Header = L("Name in MinerGuru (leer: Hostname des Miners)", "Name in MinerGuru (empty: the miner's hostname)"),
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L("Miner umbenennen", "Rename miner"),
            Content = box,
            PrimaryButtonText = L("Speichern", "Save"),
            CloseButtonText = L("Abbrechen", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) App.Store.RenameMiner(_miner.Id, box.Text);
    }
}
