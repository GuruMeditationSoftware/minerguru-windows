using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;

namespace MinerGuru.Controls;

/// <summary>Whom the pool slots of a miner pay. Writing the expected address is always the user's decision.</summary>
public sealed partial class PayoutCard : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 10 };
    private string _shown = "";
    private string? _failure;

    public PayoutCard()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(PageParts.Title(Strings.Payout));
        panel.Children.Add(_rows);
        Content = PageParts.Card(panel);
    }

    public void Update(MinerDevice miner, MinerSystemInfo info)
    {
        var slots = PayoutCheck.Slots(info, App.Store.Alerts.ExpectedAddresses);
        Visibility = slots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var state = string.Join('|', slots.Select(s => s.Key + s.Address + s.Verdict)) + info.CoinbasePayoutStatus + Privacy.IsActive + _failure;
        if (state == _shown) return;
        _shown = state;
        _rows.Children.Clear();
        foreach (var slot in slots) _rows.Children.Add(Row(miner, slot));
        var status = info.CoinbasePayoutStatus switch
        {
            "verified" => Strings.TheCurrentBlockPaysThis,
            "not_found" => Strings.TheCurrentBlockDoesNot,
            _ => null,
        };
        if (status != null) _rows.Children.Add(Palette.UiText(status, 12, Palette.TextSecondary));
        if (_failure != null) _rows.Children.Add(Palette.UiText(_failure, 12, Palette.Bad));
    }

    private Grid Row(MinerDevice miner, PayoutSlot slot)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Fill = Color(slot.Verdict) };
        row.Children.Add(dot);
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(Palette.UiText($"{slot.Title} · {slot.Coin.Symbol()}", 13, Palette.TextPrimary));
        var address = Palette.MonoText(slot.Address.Length == 0 ? "–" : Privacy.Address(slot.Address), 11, Palette.TextSecondary);
        address.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(address);
        text.Children.Add(Palette.UiText(slot.Verdict.Description(), 12, Palette.TextSecondary));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var offerable = slot.Verdict.NeedsAttention() || slot.Verdict == PayoutVerdict.Account;
        if (offerable && App.Store.Alerts.ExpectedAddresses.TryGetValue(slot.Coin.Symbol(), out var target))
        {
            var button = PageParts.TextButton("", Strings.EnterExpectedAddress, () => _ = ApplyAsync(miner, slot, target));
            Grid.SetColumn(button, 2);
            row.Children.Add(button);
        }
        return row;
    }

    private async Task ApplyAsync(MinerDevice miner, PayoutSlot slot, string target)
    {
        _failure = null;
        try
        {
            await App.Store.ApplyExpectedAddressAsync(target, slot.Key, miner.Id);
        }
        catch (WriteDeclinedException)
        {
            // Nothing happened.
        }
        catch (MinerApiException e)
        {
            _failure = e.Message;
        }
    }

    private static Brush Color(PayoutVerdict verdict) => verdict switch
    {
        PayoutVerdict.Expected => Palette.Good,
        PayoutVerdict.Foreign or PayoutVerdict.FactoryDefault => Palette.Bad,
        PayoutVerdict.Account => Palette.Warning,
        _ => Palette.Idle,
    };
}
