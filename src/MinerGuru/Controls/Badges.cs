using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using MinerGuru.Core;
using MinerGuru.Core.Models;

namespace MinerGuru.Controls;

/// <summary>The quiet inline badges of the Mac app: a colored dot with mono text, nothing filled.</summary>
public static class Badges
{
    private static Ellipse Dot(Microsoft.UI.Xaml.Media.Brush brush) =>
        new() { Width = 6, Height = 6, Fill = brush, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Row(double spacing = 6) => new() { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };

    /// <summary><c>● BCH Bitcoin Cash</c>, or with the share in dual pool mode.</summary>
    public static StackPanel Coin(MinedCoin coin, int? percentage = null, bool showFullName = false)
    {
        var row = Row();
        row.Children.Add(Dot(Palette.CoinColor(coin)));
        var symbol = Palette.MonoText(coin.Symbol(), 11, Palette.TextSecondary);
        symbol.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        row.Children.Add(symbol);
        if (showFullName && coin != MinedCoin.Unknown) row.Children.Add(Palette.UiText(coin.FullName(), 11, Palette.TextTertiary));
        if (percentage is { } p) row.Children.Add(Palette.MonoText($"{p} %"));
        return row;
    }

    /// <summary>The coins a miner works on: one, or both pools with their share.</summary>
    public static StackPanel ActiveCoins(MinerSystemInfo info, bool compact = false)
    {
        if (info.IsDualPoolMining && info.PrimaryPoolCoin != info.SecondaryPoolCoin)
        {
            var row = Row(8);
            row.Children.Add(Coin(info.PrimaryPoolCoin, info.DualPoolPrimaryPercent));
            row.Children.Add(Coin(info.SecondaryPoolCoin, info.DualPoolSecondaryPercent));
            return row;
        }
        return Coin(info.IsDualPoolMining ? info.PrimaryPoolCoin : info.ActivePoolCoin, showFullName: !compact);
    }

    /// <summary><c>● 37 ms Exzellent</c>; compact without the rating.</summary>
    public static StackPanel Latency(double? ms, bool compact = false)
    {
        var quality = LatencyRating.Evaluate(ms);
        var row = Row();
        row.Children.Add(Dot(Palette.LatencyColor(quality)));
        row.Children.Add(Palette.MonoText(MinerFormatting.FormatLatency(ms), 11, Palette.TextSecondary));
        if (!compact && ms != null) row.Children.Add(Palette.UiText(quality.Label(), 11, Palette.TextTertiary));
        return row;
    }

    /// <summary>Which pool is in use, as quiet text; only the fallback pool gets a warning dot.</summary>
    public static StackPanel PoolMode(MinerSystemInfo info, bool compact = false)
    {
        var row = Row();
        if (info.IsDualPoolMining)
        {
            row.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Foreground = Palette.TextSecondary });
            row.Children.Add(Palette.UiText(compact
                ? $"Dual {info.DualPoolPrimaryPercent}/{info.DualPoolSecondaryPercent}"
                : $"Dual-Pool {info.DualPoolPrimaryPercent} / {info.DualPoolSecondaryPercent} %", 11, Palette.TextSecondary));
        }
        else if (info.IsUsingFallbackStratum)
        {
            row.Children.Add(Dot(Palette.Warning));
            row.Children.Add(Palette.UiText(compact ? "Fallback" : Strings.FallbackPoolActive, 11, Palette.TextSecondary));
        }
        else
        {
            row.Children.Add(Palette.UiText(compact ? Strings.Primary : Strings.PrimaryPoolActive, 11, Palette.TextTertiary));
        }
        return row;
    }
}
