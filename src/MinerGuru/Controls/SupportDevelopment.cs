using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MinerGuru.Core.Models;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;

namespace MinerGuru.Controls;

/// <summary>The quiet line on the fleet page and in the settings that opens the donation dialog.</summary>
public sealed partial class SupportDevelopmentButton : HyperlinkButton
{
    private readonly FontIcon _icon = new() { Glyph = "", FontSize = 12 };

    public SupportDevelopmentButton()
    {
        var text = Palette.UiText(Strings.SupportThisProject, 12, Palette.TextSecondary);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _icon.Foreground = Palette.TextSecondary;
        content.Children.Add(_icon);
        content.Children.Add(text);
        Content = content;
        Padding = new Thickness(6, 2, 6, 2);
        ActualThemeChanged += (_, _) => text.Foreground = _icon.Foreground = Palette.TextSecondary;
        Click += async (_, _) => await new SupportDevelopmentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme }.ShowAsync();
    }
}

/// <summary>Why donate, and for each coin its QR code and address.</summary>
public sealed partial class SupportDevelopmentDialog : ContentDialog
{
    public SupportDevelopmentDialog()
    {
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Title = Strings.SupportThisProject;
        CloseButtonText = Strings.Close;
        var form = new StackPanel { Width = 460, Spacing = 16 };
        var intro = Palette.UiText(Strings.MinerGuruIsFreeAndHas, 13, Palette.TextSecondary);
        intro.TextWrapping = TextWrapping.Wrap;
        form.Children.Add(intro);
        var coins = new Grid { ColumnSpacing = 16 };
        for (var i = 0; i < DonationCoin.All.Count; i++)
        {
            coins.ColumnDefinitions.Add(new ColumnDefinition());
            var coin = CoinCard(DonationCoin.All[i]);
            Grid.SetColumn(coin, i);
            coins.Children.Add(coin);
        }
        form.Children.Add(coins);
        Content = form;
    }

    private static StackPanel CoinCard(DonationCoin coin)
    {
        var card = new StackPanel { Spacing = 10 };
        var name = Palette.UiText(coin.Name, 13, Palette.TextPrimary);
        name.FontWeight = FontWeights.Medium;
        card.Children.Add(name);
        if (!coin.IsConfigured)
        {
            card.Children.Add(Palette.UiText(Strings.AddressNotSetYet, 12, Palette.TextSecondary));
            return card;
        }
        var image = new Image { Width = 180, Height = 180 };
        _ = ShowCodeAsync(image, coin.PaymentUri);
        // Scanners want the dark modules on white in either theme.
        card.Children.Add(new Border { Child = image, Background = Palette.Rgb(255, 255, 255), Padding = new Thickness(8), CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left });
        var address = Palette.MonoText(coin.Address, 11, Palette.TextSecondary);
        address.TextWrapping = TextWrapping.Wrap;
        address.IsTextSelectionEnabled = true;
        card.Children.Add(address);
        var copy = new Button { Content = Strings.CopyAddress, Style = (Style)Application.Current.Resources["SmallButtonStyle"] };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(coin.Address);
            Clipboard.SetContent(package);
            copy.Content = Strings.Copied;
        };
        card.Children.Add(copy);
        return card;
    }

    private static async Task ShowCodeAsync(Image image, string text)
    {
        var png = PngByteQRCodeHelper.GetQRCode(text, QRCodeGenerator.ECCLevel.M, 10);
        using var stream = new MemoryStream(png);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        image.Source = bitmap;
    }
}
