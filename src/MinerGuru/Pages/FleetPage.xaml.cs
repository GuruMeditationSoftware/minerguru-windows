using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using MinerGuru.Controls;
using MinerGuru.Core;
using MinerGuru.Core.Models;

namespace MinerGuru.Pages;

public sealed partial class FleetPage : Page, IHeaderPage
{
    private readonly DeviceCardStack _stack = new(176);
    private readonly Dictionary<Guid, MinerCard> _cards = [];
    private readonly List<(TextBlock Value, TextBlock Unit, TextBlock Detail)> _figures = [];
    private readonly List<Rectangle> _figureLines = [];
    private readonly Dictionary<MinedCoin, CoinSoloChanceCard> _soloCards = [];

    public FleetPage()
    {
        InitializeComponent();
        StackHost.Child = _stack;
        BuildFigureStrip();
        BuildTimeframeMenu();
        Loaded += (_, _) =>
        {
            App.Store.Changed += Update;
            Update();
        };
        Unloaded += (_, _) => App.Store.Changed -= Update;
        ActualThemeChanged += (_, _) =>
        {
            _stack.Update([]);
            Update();
        };
    }

    public (string Title, string Subtitle) Header
    {
        get
        {
            var store = App.Store;
            return (Strings.Fleet, store.Miners.Count == 0
                ? Strings.NoMinersYet
                : string.Format(Strings.OfMinersOnline, store.OnlineCount, store.Miners.Count));
        }
    }

    // Solo odds: one card per coin the online miners work on

    private void BuildTimeframeMenu()
    {
        foreach (var timeframe in Enum.GetValues<SoloChanceTimeframe>())
        {
            var item = new RadioMenuFlyoutItem { Text = timeframe.Title(), GroupName = "Timeframe", Tag = timeframe };
            item.Click += (_, _) => App.Store.SoloChanceTimeframe = timeframe;
            TimeframeMenu.Items.Add(item);
        }
    }

    private void UpdateSolo()
    {
        var store = App.Store;
        var timeframe = store.SoloChanceTimeframe;
        TimeframeButton.Content = timeframe.Title();
        foreach (var item in TimeframeMenu.Items.OfType<RadioMenuFlyoutItem>()) item.IsChecked = (SoloChanceTimeframe)item.Tag == timeframe;

        var summaries = store.ActiveFleetCoinSummaries;
        SoloSection.Visibility = summaries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var cards = new List<CoinSoloChanceCard>();
        foreach (var summary in summaries)
        {
            if (!_soloCards.TryGetValue(summary.Coin, out var card)) _soloCards[summary.Coin] = card = new CoinSoloChanceCard();
            card.Update(summary, timeframe);
            cards.Add(card);
        }
        if (!SoloGrid.Children.Cast<CoinSoloChanceCard>().SequenceEqual(cards))
        {
            SoloGrid.Children.Clear();
            foreach (var card in cards) SoloGrid.Children.Add(card);
        }
    }

    // The strip under the chart: five figures with hairlines between them

    private static string[] FigureTitles =>
    [
        Strings.Power, Strings.Efficiency, Strings.Shares,
        Strings.BestDiffSinceRestart, Strings.AveragePoolLatency,
    ];

    private void BuildFigureStrip()
    {
        for (var i = 0; i < FigureTitles.Length; i++)
        {
            if (i > 0)
            {
                FigureStrip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(41) });
                var line = new Rectangle { Width = 1, HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetColumn(line, FigureStrip.ColumnDefinitions.Count - 1);
                FigureStrip.Children.Add(line);
                _figureLines.Add(line);
            }
            FigureStrip.ColumnDefinitions.Add(new ColumnDefinition());

            var value = new TextBlock { FontSize = 20, Style = (Style)Application.Current.Resources["FigureValueStyle"] };
            var unit = new TextBlock { FontSize = 12, Margin = new Thickness(4, 0, 0, 2), VerticalAlignment = VerticalAlignment.Bottom };
            var valueRow = new StackPanel { Orientation = Orientation.Horizontal };
            valueRow.Children.Add(value);
            valueRow.Children.Add(unit);
            var detail = Palette.MonoText();
            var title = new TextBlock { Text = FigureTitles[i], Style = (Style)Application.Current.Resources["FigureLabelStyle"] };
            var column = new StackPanel { Spacing = 4 };
            column.Children.Add(title);
            column.Children.Add(valueRow);
            column.Children.Add(detail);
            Grid.SetColumn(column, FigureStrip.ColumnDefinitions.Count - 1);
            FigureStrip.Children.Add(column);
            _figures.Add((value, unit, detail));
        }
    }

    private void SetFigure(int index, string value, string? unit, string detail)
    {
        var (valueText, unitText, detailText) = _figures[index];
        valueText.Text = value;
        unitText.Text = unit ?? "";
        unitText.Foreground = Palette.TextSecondary;
        detailText.Text = detail;
        detailText.Foreground = Palette.TextTertiary;
    }

    // Update

    private void UpdateBar_CloseButtonClick(InfoBar sender, object args) => App.Store.Updates.DismissBanner();

    private async void UpdateDownload_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri(MinerGuru.Core.Models.AppInfo.ReleasesUrl));

    private void Update()
    {
        var store = App.Store;
        var update = store.Updates.BannerVersion;
        UpdateBar.Title = update == null ? "" : string.Format(Strings.MinerGuruIsAvailable, update);
        UpdateBar.IsOpen = update != null;
        var hasMiners = store.Miners.Count > 0;
        EmptyState.Visibility = hasMiners ? Visibility.Collapsed : Visibility.Visible;
        FleetSection.Visibility = MinerSection.Visibility = PoolSection.Visibility = hasMiners ? Visibility.Visible : Visibility.Collapsed;
        if (!hasMiners)
        {
            SoloSection.Visibility = Visibility.Collapsed;
            if (EmptyBoardHost.Child is not DeviceCard)
            {
                var board = new DeviceCard(160, showLed: false);
                board.Update(DeviceCardSpec.From("Bitaxe Gamma 601", null), ConnectionState.Checking, false);
                EmptyBoardHost.Child = board;
            }
            return;
        }

        var parts = new List<string> { string.Format(Strings.OfOnline, store.OnlineCount, store.Miners.Count) };
        var offline = store.Miners.Count - store.OnlineCount;
        if (offline > 0) parts.Add($"{offline} offline");
        if (store.LastGlobalRefresh is { } last) parts.Add(string.Format(Strings.Updated, DisplayFormat.Time(last)));
        MetaText.Text = string.Join(" · ", parts);
        UpdateSolo();

        var (value, unit) = MinerFormatting.SplitHashrate(store.TotalHashrateGhs);
        FleetRateValue.Text = value;
        FleetRateUnit.Text = unit;
        FleetRateSub.Text = string.Format(Strings.GHSMinersActive, store.TotalHashrateGhs, store.OnlineCount);
        FleetChart.SetPoints(store.FleetHistory.Select(s => (s.Timestamp, s.HashRateGhs)));

        // The three strongest online miners, the strongest in front
        var ranked = store.OnlineMiners.OrderByDescending(m => m.LatestInfo!.HashRate).ToList();
        _stack.Update((ranked.Count > 0 ? ranked : store.Miners).Take(3)
            .Select(m => (DeviceCardSpec.For(m), m.State, store.IsHot(m))).ToList());

        foreach (var line in _figureLines) line.Fill = Palette.Hairline;
        var accepted = store.TotalSharesAccepted;
        var rejected = store.TotalSharesRejected;
        var rejectRate = accepted + rejected > 0 ? rejected * 100.0 / (accepted + rejected) : 0;
        var latency = store.AveragePoolLatencyMs;
        SetFigure(0, DisplayFormat.Decimal(store.TotalPowerWatts, 1), "W",
            string.Format(Strings.KWhPerDay, DisplayFormat.Decimal(store.TotalPowerWatts * 24 / 1000, 2)));
        SetFigure(1, store.FleetEfficiencyJPerTh > 0 ? DisplayFormat.Decimal(store.FleetEfficiencyJPerTh, 1) : "—", "J/TH", Strings.EnergyPerTerahash);
        SetFigure(2, DisplayFormat.Integer(accepted), null,
            string.Format(Strings.Rejected, rejected, DisplayFormat.Percent(rejectRate, 2)));
        SetFigure(3, store.BestSessionInfo?.SessionBestDiffText ?? "—", null,
            string.Format(Strings.AllTime, store.BestDiffInfo?.BestDiffFormatted ?? "—"));
        SetFigure(4, latency is { } ms ? DisplayFormat.Decimal(ms, 0) : "—", latency != null ? "ms" : null, LatencyRating.Evaluate(latency).Label());

        MinerCountText.Text = store.Miners.Count.ToString();
        SyncCards();
        PoolTable.Update(store.Miners, store.IsHot);
    }

    /// <summary>One card per miner in the store's order; existing cards only update their texts.</summary>
    private void SyncCards()
    {
        var store = App.Store;
        var ids = store.Miners.Select(m => m.Id).ToHashSet();
        foreach (var gone in _cards.Keys.Where(id => !ids.Contains(id)).ToList()) _cards.Remove(gone);
        var cards = new List<MinerCard>();
        foreach (var miner in store.Miners)
        {
            if (!_cards.TryGetValue(miner.Id, out var card)) _cards[miner.Id] = card = new MinerCard();
            card.Update(miner);
            cards.Add(card);
        }
        if (!MinerGrid.Children.Cast<MinerCard>().SequenceEqual(cards))
        {
            MinerGrid.Children.Clear();
            foreach (var card in cards) MinerGrid.Children.Add(card);
        }
    }

    private async void Add_Click(object sender, RoutedEventArgs e) => await App.MainWindow.ShowAddMinerAsync();

    private async void Scan_Click(object sender, RoutedEventArgs e) => await App.MainWindow.ShowScanAsync();
}
