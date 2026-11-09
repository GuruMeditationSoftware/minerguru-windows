using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MinerGuru.Controls;

/// <summary>
/// One key figure of a miner, as the Mac's MetricCardView: quiet icon and title, the figure with its unit,
/// a mono subtitle. Icons and figures stay neutral; only a warning colors the card.
/// </summary>
public sealed partial class MetricCard : Grid
{
    private readonly Border _border = new();
    private readonly FontIcon _icon;
    private readonly TextBlock _title = Palette.UiText("", 12);
    private readonly FontIcon _warningIcon = new() { Glyph = "", FontSize = 11, Visibility = Visibility.Collapsed };
    private readonly TextBlock _value = new() { FontSize = 24, Style = (Style)Application.Current.Resources["FigureValueStyle"] };
    private readonly TextBlock _unit = Palette.UiText("", 12);
    private readonly TextBlock _subtitle = Palette.MonoText(" ");
    private bool _warning;

    public MetricCard(string title, string glyph)
    {
        _border.Style = (Style)Application.Current.Resources["CardBorderStyle"];
        _border.CornerRadius = new CornerRadius(12);
        _border.Padding = new Thickness(16);
        Children.Add(_border);
        _icon = new FontIcon { Glyph = glyph, FontSize = 12, Width = 16 };
        _title.Text = title;

        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(_warningIcon, 2);
        head.Children.Add(_icon);
        head.Children.Add(_title);
        head.Children.Add(_warningIcon);

        _unit.Margin = new Thickness(4, 0, 0, 3);
        _unit.VerticalAlignment = VerticalAlignment.Bottom;
        var figure = new StackPanel { Orientation = Orientation.Horizontal };
        figure.Children.Add(_value);
        figure.Children.Add(_unit);

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(head);
        stack.Children.Add(figure);
        stack.Children.Add(_subtitle);
        _border.Child = stack;
        Restyle();
    }

    public string Title
    {
        set => _title.Text = value;
    }

    public void Set(string value, string? unit, string? subtitle, bool warning = false)
    {
        _value.Text = value;
        _unit.Text = unit ?? "";
        _subtitle.Text = string.IsNullOrEmpty(subtitle) ? " " : subtitle.Replace(" • ", " · ");
        ToolTipService.SetToolTip(_subtitle, subtitle);
        _warning = warning;
        _warningIcon.Visibility = warning ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_warningIcon, warning ? Strings.Warning : null);
        Restyle();
    }

    private void Restyle()
    {
        _border.Background = Palette.Get("CardBrush");
        _border.BorderBrush = _warning ? Palette.Get("WarningStrokeBrush") : Palette.Hairline;
        _icon.Foreground = Palette.TextTertiary;
        _title.Foreground = Palette.TextSecondary;
        _warningIcon.Foreground = Palette.Warning;
        _value.Foreground = _warning ? Palette.Bad : Palette.TextPrimary;
        _unit.Foreground = Palette.TextSecondary;
        _subtitle.Foreground = Palette.TextTertiary;
    }
}
