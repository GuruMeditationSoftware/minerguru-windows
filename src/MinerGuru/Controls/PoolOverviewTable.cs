using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using MinerGuru.Core;
using MinerGuru.Core.Models;

namespace MinerGuru.Controls;

/// <summary>
/// Pool, worker, session best and latency of every miner side by side, as the Mac's PoolOverviewTable:
/// a header and one row per miner with alternating backgrounds; dual-pool miners get two lines.
/// A click opens the miner.
/// </summary>
public sealed partial class PoolOverviewTable : Grid
{
    /// <summary>Relative column widths, from the Mac table's ideal widths.</summary>
    internal static readonly double[] ColumnWeights = [140, 260, 220, 120, 110, 110];

    private readonly Border _frame = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
    private readonly StackPanel _rows = new();
    private readonly Grid _header;
    private readonly Dictionary<Guid, PoolRow> _rowById = [];

    public PoolOverviewTable()
    {
        Children.Add(_frame);
        _header = PoolRow.Columns();
        _header.Height = 28;
        _header.Padding = new Thickness(12, 0, 12, 0);
        string[] titles =
        [
            "Miner", Strings.ActivePoolServer, "Worker / Wallet",
            "Best Diff (Session)", Strings.Mode, Strings.PoolLatency,
        ];
        for (var i = 0; i < titles.Length; i++)
        {
            var title = Palette.UiText(titles[i], 11, Palette.TextSecondary);
            title.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(title, i);
            _header.Children.Add(title);
        }

        var stack = new StackPanel();
        stack.Children.Add(_header);
        stack.Children.Add(_rows);
        _frame.Child = stack;
        ActualThemeChanged += (_, _) => Restyle();
        Restyle();
    }

    public void Update(IReadOnlyList<MinerDevice> miners, Func<MinerDevice, bool> isHot)
    {
        var ids = miners.Select(m => m.Id).ToHashSet();
        foreach (var gone in _rowById.Keys.Where(id => !ids.Contains(id)).ToList()) _rowById.Remove(gone);
        var rows = new List<PoolRow>();
        foreach (var miner in miners)
        {
            if (!_rowById.TryGetValue(miner.Id, out var row)) _rowById[miner.Id] = row = new PoolRow();
            row.Update(miner, isHot(miner));
            rows.Add(row);
        }
        if (!_rows.Children.Cast<PoolRow>().SequenceEqual(rows))
        {
            _rows.Children.Clear();
            foreach (var row in rows) _rows.Children.Add(row);
        }
        for (var i = 0; i < rows.Count; i++) rows[i].IsAlternate = i % 2 == 1;
    }

    private void Restyle()
    {
        _frame.Background = Palette.Get("CardBrush");
        _frame.BorderBrush = Palette.Hairline;
        _header.Background = Palette.Get("InsetBrush");
        foreach (var title in _header.Children.OfType<TextBlock>()) title.Foreground = Palette.TextSecondary;
    }
}

/// <summary>One miner in the pool table.</summary>
internal sealed partial class PoolRow : Button
{
    private static readonly ControlTemplate PlainTemplate = (ControlTemplate)XamlReader.Load(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
        "<ContentPresenter Content='{TemplateBinding Content}' /></ControlTemplate>");

    private readonly Grid _grid = Columns();
    private MinerDevice? _miner;
    private bool _hot;
    private bool _alternate;
    private bool _hovered;

    public PoolRow()
    {
        Template = PlainTemplate;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _grid.Padding = new Thickness(12, 8, 12, 8);
        _grid.MinHeight = 37;
        Content = _grid;
        Click += (_, _) => { if (_miner != null) App.MainWindow.ShowMiner(_miner.Id); };
        PointerEntered += (_, _) => { _hovered = true; Restyle(); };
        PointerExited += (_, _) => { _hovered = false; Restyle(); };
        ActualThemeChanged += (_, _) => { if (_miner != null) Update(_miner, _hot); };
        Restyle();
    }

    /// <summary>The six columns shared by header and rows.</summary>
    internal static Grid Columns()
    {
        var grid = new Grid { ColumnSpacing = 12 };
        foreach (var weight in PoolOverviewTable.ColumnWeights)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
        return grid;
    }

    public bool IsAlternate
    {
        set { _alternate = value; Restyle(); }
    }

    private void Restyle() =>
        _grid.Background = _hovered ? Palette.Get("HoverBrush") : _alternate ? Palette.Get("InsetBrush") : new SolidColorBrush(Palette.Transparent);

    public void Update(MinerDevice miner, bool hot)
    {
        _miner = miner;
        _hot = hot;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, miner.DisplayName);
        _grid.Children.Clear();
        var info = miner.IsOnline ? miner.LatestInfo : null;
        var dual = info?.IsDualPoolMining == true;

        // Miner: status, name, address
        var status = new StatusBlock();
        status.Apply(miner, hot);
        status.VerticalAlignment = VerticalAlignment.Center;
        var name = Palette.UiText(miner.DisplayName, 13, Palette.TextPrimary);
        name.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var address = Palette.MonoText(MinerGuru.Core.Privacy.Ip(miner.IpAddress), 11, Palette.TextSecondary);
        var names = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(name);
        names.Children.Add(address);
        var minerCell = new Grid { ColumnSpacing = 8 };
        minerCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        minerCell.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(names, 1);
        minerCell.Children.Add(status);
        minerCell.Children.Add(names);
        Add(minerCell, 0);

        if (info == null)
        {
            for (var column = 1; column <= 5; column++)
                Add(Palette.UiText(column == 4 ? "Offline" : "—", 12, Palette.TextSecondary), column);
            return;
        }

        // Pool server, worker and session best: one line per pool in dual pool mode
        Add(dual
            ? Lines(Endpoint(info.PrimaryPoolCoin, info.DualPoolPrimaryPercent, info.PrimaryPoolEndpoint),
                    Endpoint(info.SecondaryPoolCoin, info.DualPoolSecondaryPercent, info.FallbackPoolEndpoint))
            : Endpoint(info.ActivePoolCoin, null, info.ActivePoolEndpoint), 1);
        Add(dual ? Lines(User(info.StratumUser), User(info.FallbackStratumUser)) : User(info.ActivePoolUser), 2);
        Add(dual
            ? Lines(Best(info.PrimaryPoolSessionBestDiff), Best(info.SecondaryPoolSessionBestDiff))
            : Best(info.BestSessionDiffNumeric > 0 ? info.BestSessionDiffNumeric : null), 3);
        Add(Badges.PoolMode(info, compact: true), 4);

        var latency = Badges.Latency(miner.ActivePoolLatencyMs, compact: true);
        if (dual)
        {
            static string Ms(double? v) => v is > 0 ? v.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "—";
            var split = Palette.MonoText($"P1 {Ms(info.PrimaryPoolStatus?.EffectiveLatencyMs)} • P2 {Ms(info.SecondaryPoolStatus?.EffectiveLatencyMs)} ms", 10, Palette.TextSecondary);
            Add(Lines(latency, split), 5);
        }
        else Add(latency, 5);
    }

    private void Add(FrameworkElement cell, int column)
    {
        cell.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(cell, column);
        _grid.Children.Add(cell);
    }

    private static StackPanel Lines(params FrameworkElement[] lines)
    {
        var panel = new StackPanel { Spacing = 3 };
        foreach (var line in lines) panel.Children.Add(line);
        return panel;
    }

    private static FrameworkElement Endpoint(MinedCoin coin, int? percentage, string endpoint)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var badge = Badges.Coin(coin, percentage);
        var text = Palette.MonoText(endpoint, 11, Palette.TextPrimary);
        text.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(text, endpoint);
        Grid.SetColumn(text, 1);
        row.Children.Add(badge);
        row.Children.Add(text);
        return row;
    }

    private static TextBlock User(string user)
    {
        user = MinerGuru.Core.Privacy.StratumUser(user);
        var text = Palette.MonoText(user.Length == 0 ? "—" : user, 11, Palette.TextSecondary);
        if (user.Length > 0) ToolTipService.SetToolTip(text, user);
        return text;
    }

    private static TextBlock Best(double? value)
    {
        var text = Palette.MonoText(value is > 0 ? MinerFormatting.FormatDifficulty(value.Value) : "—", 11, Palette.TextPrimary);
        text.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        return text;
    }
}
