using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MinerGuru.Core.Models;
using Windows.UI;

namespace MinerGuru.Controls;

/// <summary>The brushes and text styles of Theme.xaml for code-built views, in the theme the window shows.</summary>
public static class Palette
{
    public static bool IsLight =>
        App.MainWindow?.Content is FrameworkElement root ? root.ActualTheme == ElementTheme.Light : Application.Current.RequestedTheme == ApplicationTheme.Light;

    /// <summary>A brush of Theme.xaml in the current light or dark palette; code-built views read it again when the theme changes.</summary>
    public static Brush Get(string key)
    {
        var resources = Application.Current.Resources;
        var theme = IsLight ? "Light" : "Dark";
        foreach (var dictionary in resources.MergedDictionaries)
            if (dictionary.ThemeDictionaries.TryGetValue(theme, out var themed) && themed is ResourceDictionary d && d.TryGetValue(key, out var brush))
                return (Brush)brush;
        return (Brush)resources[key];
    }

    public static Brush TextPrimary => Get("TextPrimaryBrush");
    public static Brush TextSecondary => Get("TextSecondaryBrush");
    public static Brush TextTertiary => Get("TextTertiaryBrush");
    public static Brush Hairline => Get("HairlineBrush");
    public static Brush Accent => Get("AccentBrush");
    public static Brush Good => Get("SignalGoodBrush");
    public static Brush Warning => Get("SignalWarningBrush");
    public static Brush Bad => Get("SignalBadBrush");
    public static Brush Idle => Get("SignalIdleBrush");

    public static FontFamily Mono => (FontFamily)Application.Current.Resources["MonoFont"];
    public static FontFamily Figure => (FontFamily)Application.Current.Resources["FigureFont"];

    public static SolidColorBrush Rgb(byte r, byte g, byte b, double opacity = 1) =>
        new(Color.FromArgb((byte)Math.Round(opacity * 255), r, g, b));

    public static SolidColorBrush White(double opacity) => Rgb(255, 255, 255, opacity);
    public static SolidColorBrush Black(double opacity) => Rgb(0, 0, 0, opacity);

    /// <summary>Mac <c>Font.mono(size)</c> as a text block.</summary>
    public static TextBlock MonoText(string text = "", double size = 11, Brush? brush = null) => new()
    {
        Text = text,
        FontFamily = Mono,
        FontSize = size,
        Foreground = brush ?? TextTertiary,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
    };

    public static TextBlock UiText(string text = "", double size = 13, Brush? brush = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush ?? TextPrimary,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
    };

    /// <summary>Each coin keeps its color, shown only as a small dot next to its symbol.</summary>
    public static Brush CoinColor(MinedCoin coin)
    {
        var (light, dark) = coin switch
        {
            MinedCoin.Btc => (0xd9780au, 0xf0a03cu),
            MinedCoin.Bch => (0x14a06cu, 0x3ecf8eu),
            MinedCoin.Dgb => (0x2a78d6u, 0x5aa2f5u),
            MinedCoin.Xec => (0x0e9f9fu, 0x3cc9c9u),
            MinedCoin.Bsv => (0xb58a06u, 0xf5c04au),
            MinedCoin.Ppc => (0x4f9a2au, 0x7ccf5au),
            MinedCoin.Nmc => (0x5b6ad0u, 0x8d98f0u),
            MinedCoin.Fb => (0x7c5ce0u, 0xa48af5u),
            _ => (0u, 0u),
        };
        if (light == 0) return Idle;
        var hex = IsLight ? light : dark;
        return Rgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
    }

    public static Brush LatencyColor(LatencyQuality quality) => quality switch
    {
        LatencyQuality.Excellent or LatencyQuality.Good => Good,
        LatencyQuality.Fair => Warning,
        LatencyQuality.Poor => Bad,
        _ => Idle,
    };

    public static Color Transparent => Colors.Transparent;
}
