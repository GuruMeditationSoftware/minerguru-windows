using Microsoft.UI.Xaml.Controls;
using MinerGuru.Core;
using MinerGuru.Core.Models;

namespace MinerGuru.Controls;

public sealed partial class CoinSoloChanceCard : UserControl
{
    private (FleetCoinMiningSummary Summary, SoloChanceTimeframe Timeframe)? _shown;

    public CoinSoloChanceCard()
    {
        InitializeComponent();
        // The coin dot is the only color set in code.
        ActualThemeChanged += (_, _) => { if (_shown is { } s) CoinDot.Fill = Palette.CoinColor(s.Summary.Coin); };
    }

    public void Update(FleetCoinMiningSummary summary, SoloChanceTimeframe timeframe)
    {
        _shown = (summary, timeframe);
        CoinDot.Fill = Palette.CoinColor(summary.Coin);
        CoinName.Text = summary.Coin.FullName();
        CoinSymbol.Text = summary.Coin.Symbol();
        RateText.Text = MinerFormatting.FormatHashrate(summary.TotalHashrateGhs);

        OddsText.Text = summary.FormattedOddsRatio(timeframe);
        PercentText.Text = summary.FormattedProbabilityPercent(timeframe);
        BlockTimeText.Text = string.Format(Strings.Average1BlockEvery, summary.ExpectedTimePerBlockFormatted);

        RewardValue.Text = DisplayFormat.DecimalUpTo(summary.Coin.BlockSubsidyCoins(), 3);
        RewardUnit.Text = summary.Coin.Symbol();

        MinersText.Text = summary.ContributingMinersText;
        DifficultyText.Text = string.Format(Strings.NetworkDiff, MinerFormatting.FormatDifficulty(summary.NetworkDifficulty));
        ToolTipService.SetToolTip(DifficultyText, summary.IsLiveDifficulty
            ? Strings.ReportedByThePool
            : Strings.EstimateUntilThePoolReports);
    }
}
