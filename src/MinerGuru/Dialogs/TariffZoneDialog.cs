using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MinerGuru.Controls;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Dialogs;

/// <summary>Create or edit one tariff zone, as the Mac's TariffZoneEditorSheet: name, price, days, time.</summary>
public sealed partial class TariffZoneDialog : ContentDialog
{
    private readonly TariffZone _zone;
    private readonly TextBox _name;
    private readonly TextBox _price;

    /// <summary>The saved zone once the dialog was confirmed.</summary>
    public TariffZone? Result { get; private set; }

    /// <param name="zone">A copy: it is edited directly.</param>
    public TariffZoneDialog(TariffZone zone, bool isNew, string currencyCode)
    {
        _zone = zone;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 600.0;
        PrimaryButtonText = L("Sichern", "Save");
        CloseButtonText = L("Abbrechen", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        _name = new TextBox { Text = zone.Name, PlaceholderText = L("z. B. Nacht", "e.g. Night") };
        _name.TextChanged += (_, _) => Validate();
        _price = new TextBox { Text = PowerTariff.PriceText(zone.PricePerKwh), Width = 90, TextAlignment = TextAlignment.Right };
        _price.TextChanged += (_, _) => Validate();
        var priceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        priceRow.Children.Add(_price);
        var currency = Palette.UiText(currencyCode, 13, Palette.TextSecondary);
        currency.VerticalAlignment = VerticalAlignment.Center;
        priceRow.Children.Add(currency);
        var days = new WeekdayPicker(zone.Window.Weekdays);
        days.Changed += Validate;

        var form = new StackPanel { Width = 520, Spacing = 14 };
        var title = Palette.UiText(isNew ? L("Neue Tarifzone", "New tariff zone") : L("Tarifzone bearbeiten", "Edit tariff zone"), 15, Palette.TextPrimary);
        title.FontWeight = FontWeights.Medium;
        form.Children.Add(title);
        form.Children.Add(PowerForm.Row("Name", _name));
        form.Children.Add(PowerForm.Row(L("Preis pro kWh", "Price per kWh"), priceRow));
        form.Children.Add(PowerForm.Row(L("Tage", "Days"), days));
        form.Children.Add(PowerForm.Row(L("Uhrzeit", "Time"), new TimeWindowFields(zone.Window)));
        Content = form;
        PrimaryButtonClick += (_, _) =>
        {
            _zone.Name = _name.Text.Trim();
            _zone.PricePerKwh = PowerTariff.ParsePrice(_price.Text) ?? _zone.PricePerKwh;
            Result = _zone;
        };
        Validate();
    }

    private void Validate() => IsPrimaryButtonEnabled =
        _name.Text.Trim().Length > 0 && _zone.Window.Weekdays.Count > 0 && PowerTariff.ParsePrice(_price.Text) != null;
}
