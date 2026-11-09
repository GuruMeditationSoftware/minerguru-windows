using Microsoft.UI.Xaml.Controls;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

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
        BlockTimeText.Text = L($"Ø 1 Block alle {summary.ExpectedTimePerBlockFormatted}", $"Ø 1 block every {summary.ExpectedTimePerBlockFormatted}");

        RewardValue.Text = DisplayFormat.DecimalUpTo(summary.Coin.BlockSubsidyCoins(), 3);
        RewardUnit.Text = summary.Coin.Symbol();

        MinersText.Text = summary.ContributingMinersText;
        DifficultyText.Text = L($"Netz-Diff {MinerFormatting.FormatDifficulty(summary.NetworkDifficulty)}",
                                $"Network diff {MinerFormatting.FormatDifficulty(summary.NetworkDifficulty)}");
        ToolTipService.SetToolTip(DifficultyText, summary.IsLiveDifficulty
            ? L("Vom Pool gemeldet", "Reported by the pool")
            : L("Schätzwert, bis der Pool die Netz-Difficulty meldet", "Estimate until the pool reports the network difficulty"));
    }
}
