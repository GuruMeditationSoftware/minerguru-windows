using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using MinerGuru.Core;
using MinerGuru.Core.Models;

namespace MinerGuru.Controls;

/// <summary>
/// A row of the sidebar as on the Mac: a surface step with a hairline when selected, a faint tint on hover.
/// A button underneath, so it takes focus and works with the keyboard.
/// </summary>
public partial class SidebarRow : Button
{
    private static readonly ControlTemplate PlainTemplate = (ControlTemplate)XamlReader.Load(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
        "<ContentPresenter Content='{TemplateBinding Content}' /></ControlTemplate>");

    protected readonly Border Surface = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(8, 0, 8, 0) };
    private bool _selected;
    private bool _hovered;

    public SidebarRow(double height)
    {
        Template = PlainTemplate;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Surface.Height = height;
        Content = Surface;
        PointerEntered += (_, _) => { _hovered = true; Restyle(); };
        PointerExited += (_, _) => { _hovered = false; Restyle(); };
        ActualThemeChanged += (_, _) => Restyle();
        Restyle();
    }

    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; Restyle(); }
    }

    protected virtual void Restyle()
    {
        Surface.Background = _selected ? Palette.Get("RaisedBrush") : _hovered ? Palette.Get("HoverBrush") : new SolidColorBrush(Palette.Transparent);
        Surface.BorderBrush = _selected ? Palette.Get("SelectedStrokeBrush") : new SolidColorBrush(Palette.Transparent);
    }
}

/// <summary>Fleet, Pools, Power: icon, title and a mono figure on the right.</summary>
public sealed partial class SidebarNavRow : SidebarRow
{
    private readonly FontIcon _icon;
    private readonly TextBlock _title;
    private readonly TextBlock _trailing = Palette.MonoText();

    public SidebarNavRow(string title, string glyph) : base(32)
    {
        _icon = new FontIcon { Glyph = glyph, FontSize = 14, Width = 30 };
        _title = Palette.UiText(title, 13);
        AutomationProperties.SetName(this, title);
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _title.VerticalAlignment = _icon.VerticalAlignment = _trailing.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(_trailing, 2);
        grid.Children.Add(_icon);
        grid.Children.Add(_title);
        grid.Children.Add(_trailing);
        Surface.Child = grid;
        Restyle();
    }

    public string? Trailing
    {
        set => _trailing.Text = value ?? "";
    }

    protected override void Restyle()
    {
        base.Restyle();
        if (_title == null) return;
        var brush = IsSelected ? Palette.TextPrimary : Palette.TextSecondary;
        _icon.Foreground = brush;
        _title.Foreground = brush;
        _trailing.Foreground = Palette.TextTertiary;
    }
}

/// <summary>A miner: its board as a small card with the status LED, name and <c>model · coin</c>, hashrate and temperature.</summary>
public sealed partial class SidebarMinerRow : SidebarRow
{
    private readonly DeviceCard _card = new(30);
    private readonly TextBlock _name = Palette.UiText("", 13);
    private readonly TextBlock _meta = Palette.MonoText();
    private readonly TextBlock _rate = Palette.MonoText();
    private readonly TextBlock _temp = Palette.MonoText();
    private MinerDevice? _miner;
    private bool _hot;

    public SidebarMinerRow() : base(44)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _card.VerticalAlignment = VerticalAlignment.Center;
        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_name);
        left.Children.Add(_meta);
        var right = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        _rate.HorizontalAlignment = _temp.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(_rate);
        right.Children.Add(_temp);
        Grid.SetColumn(left, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(_card);
        grid.Children.Add(left);
        grid.Children.Add(right);
        Surface.Child = grid;
    }

    public void Update(MinerDevice miner, bool hot)
    {
        _miner = miner;
        _hot = hot;
        _card.Update(miner, hot);
        _name.Text = miner.DisplayName;
        _meta.Text = miner.SidebarMetaLine();
        if (miner.LatestInfo is { } info && miner.IsOnline)
        {
            _rate.Text = MinerFormatting.FormatHashrate(info.HashRate);
            _temp.Text = $"{info.Temp:0} °C";
            _temp.Visibility = Visibility.Visible;
        }
        else
        {
            _rate.Text = miner.State == ConnectionState.Offline ? "Offline" : "…";
            _temp.Visibility = Visibility.Collapsed;
        }
        ToolTipService.SetToolTip(this, $"{miner.DisplayName} · {MinerGuru.Core.Privacy.Ip(miner.IpAddress)}");
        AutomationProperties.SetName(this, $"{miner.DisplayName}, {_rate.Text}");
        Restyle();
    }

    protected override void Restyle()
    {
        base.Restyle();
        if (_name == null) return;
        _name.Foreground = IsSelected ? Palette.TextPrimary : Palette.TextSecondary;
        _meta.Foreground = Palette.TextTertiary;
        _rate.Foreground = _miner?.IsOnline == true ? Palette.TextSecondary : Palette.TextTertiary;
        _temp.Foreground = _hot ? Palette.Warning : Palette.TextTertiary;
        if (_miner != null) _card.Update(_miner, _hot);
    }
}
