using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Controls;

/// <summary>
/// The block a miner is hashing on right now, as far as the coinbase shows it: height, pool tag, reward and who
/// gets it. Comes from the firmware (AxeOS 2.13+) or, one card per pool, from the Stratum probe, which runs while
/// the page is open. With the probe off, a short hint where to switch it on.
/// </summary>
public sealed partial class BlockSection : UserControl
{
    private readonly StackPanel _cards = new() { Spacing = 16 };
    private MinerDevice? _miner;
    private string? _shown;

    public BlockSection()
    {
        Content = _cards;
        ActualThemeChanged += (_, _) =>
        {
            _shown = null;
            if (_miner != null) Update(_miner);
        };
    }

    /// <summary>Rebuilds only when something changed, so a selection in the card survives the polling.</summary>
    public void Update(MinerDevice miner)
    {
        _miner = miner;
        if (miner.LatestInfo is not { } info) return;
        var probes = App.Store.BlockProbes;
        var block = BlockTemplateSummary.From(info);
        var targets = BlockProbeTarget.TargetsFor(info);
        if (block == null && App.Store.StratumProbeEnabled && App.MainWindow.AppWindow.IsVisible)
            foreach (var target in targets) _ = probes.RefreshAsync(target); // the store throttles

        var state = block != null ? Key(block)
            : !App.Store.StratumProbeEnabled ? $"off{targets.Count}"
            : string.Join(";", targets.Select(t => $"{t}|{probes.IsRunning(t)}|{Key(probes.EntryFor(t))}"));
        if (state == _shown) return;
        _shown = state;

        _cards.Children.Clear();
        if (block != null) _cards.Children.Add(BlockCard(block, miner));
        else if (targets.Count == 0) { }
        else if (App.Store.StratumProbeEnabled)
            foreach (var target in targets) _cards.Children.Add(ProbeCard(target, targets.Count));
        else _cards.Children.Add(ProbeOffHint());
        Visibility = _cards.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Key(BlockProbeStore.Entry? entry) =>
        entry == null ? "" : entry.Block != null ? Key(entry.Block) : entry.Error ?? "loading";

    private static string Key(BlockTemplateSummary b) =>
        $"{b.Height}|{b.PoolTag}|{b.TotalSats}|{b.UserSats}|{b.PayoutState}|{b.OthersCount}|{b.OthersValueSats}|{b.ProbedAt}|" +
        string.Join(",", b.Signals) + "|" + string.Join(",", b.Outputs.Select(o => $"{o.Address}={o.ValueSats}={o.IsUserOutput}"));

    // Cards

    private static Border Card(UIElement content, bool alarm = false)
    {
        var card = new Border { Child = content, Padding = new Thickness(24), CornerRadius = new CornerRadius(16), Style = Style("CardBorderStyle") };
        if (alarm) card.BorderBrush = Palette.Bad;
        return card;
    }

    private static Style Style(string key) => (Style)Application.Current.Resources[key];

    private static Rectangle Line() => new() { Height = 1, Style = Style("HairlineStyle") };

    private static StackPanel Title(string? poolLabel)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new FontIcon { Glyph = "\uE7B8", FontSize = 14, Foreground = Palette.TextPrimary, VerticalAlignment = VerticalAlignment.Center });
        var text = Palette.UiText(L("Aktueller Block", "Current block"), 15, Palette.TextPrimary);
        text.FontWeight = FontWeights.Medium;
        title.Children.Add(text);
        if (poolLabel != null) title.Children.Add(Palette.UiText($"· {poolLabel}", 15, Palette.TextSecondary));
        return title;
    }

    private static TextBlock Caption(string text)
    {
        var caption = Palette.UiText(text, 12, Palette.TextSecondary);
        caption.TextWrapping = TextWrapping.Wrap;
        return caption;
    }

    private Border BlockCard(BlockTemplateSummary block, MinerDevice miner, string? poolLabel = null, BlockProbeTarget? target = null)
    {
        var body = new StackPanel { Spacing = 14 };

        var header = new Grid();
        header.Children.Add(Title(poolLabel));
        header.Children.Add(PayoutBadge(block.PayoutState));
        var top = new StackPanel { Spacing = 6 };
        top.Children.Add(header);
        top.Children.Add(Caption(block.ProbedAt == null
            ? L("Die Vorlage des Pools, an der dein Miner gerade rechnet. Findet er den Block, zahlt die Coinbase so aus wie hier gezeigt. Die übrigen Transaktionen kennt nur der Pool.",
                "The pool's template your miner is working on right now. If it finds the block, the coinbase pays out as shown here. Only the pool knows the other transactions.")
            : L("Die Vorlage, die der Pool gerade für deine Adresse baut. Der Miner meldet sie nicht, deshalb hat MinerGuru sie selbst beim Pool abgefragt, ohne mitzurechnen. Findet dein Miner den Block, zahlt die Coinbase so aus wie hier gezeigt.",
                "The template the pool is building for your address right now. The miner does not report it, so MinerGuru asked the pool itself, without mining along. If your miner finds the block, the coinbase pays out as shown here.")));
        body.Children.Add(top);
        body.Children.Add(Line());

        string Coins(double? sats) => sats is { } value ? BlockTemplateSummary.FormatCoins(value, block.Coin) : "—";
        var pairs = new AdaptiveGrid { MinColumnWidth = 180, Spacing = 16 };
        pairs.Children.Add(Pair(L("Blockhöhe", "Block height"), block.Height.ToString("N0", Culture)));
        pairs.Children.Add(Pair(L("Pool-Kennung", "Pool tag"), block.PoolTag ?? "—"));
        pairs.Children.Add(Pair(L("Belohnung", "Reward"), Coins(block.TotalSats)));
        pairs.Children.Add(Pair(L("davon Gebühren", "of which fees"), Coins(block.FeesSats)));
        pairs.Children.Add(Pair(L("An deine Adresse", "To your address"), UserShareText(block)));
        body.Children.Add(pairs);

        if (block.TransactionRange is var (lower, upper))
        {
            var range = lower == upper ? lower.ToString("N0", Culture) : $"{lower.ToString("N0", Culture)}–{upper.ToString("N0", Culture)}";
            var transactions = Caption(L($"Transaktionen im Block: {range}", $"Transactions in the block: {range}"));
            ToolTipService.SetToolTip(transactions, L("Aus der Tiefe des Merkle-Baums: Der Pool nennt nicht die genaue Zahl, nur die Größenordnung.",
                "From the depth of the merkle tree: the pool does not state the exact number, only the order of magnitude."));
            body.Children.Add(transactions);
        }
        if (block.Signals.Count > 0) body.Children.Add(SignalsRow(block));
        body.Children.Add(Line());
        body.Children.Add(OutputsSection(block, miner));
        if (block.ProbedAt is { } probedAt && target != null) body.Children.Add(ProbeFooter(probedAt, target));
        return Card(body, block.PayoutState == PayoutState.NotFound);
    }

    private static string UserShareText(BlockTemplateSummary block)
    {
        if (block.UserSats is not > 0) return "—";
        var amount = BlockTemplateSummary.FormatCoins(block.UserSats.Value, block.Coin);
        return block.UserShare is { } share ? $"{amount} ({share.ToString("P1", Culture)})" : amount;
    }

    private static StackPanel Pair(string label, string value)
    {
        var pair = new StackPanel { Spacing = 2 };
        pair.Children.Add(Palette.UiText(label, 11, Palette.TextSecondary));
        var text = Palette.MonoText(value, 12, Palette.TextPrimary);
        text.FontWeight = FontWeights.Medium;
        text.TextWrapping = TextWrapping.Wrap;
        text.IsTextSelectionEnabled = true;
        pair.Children.Add(text);
        return pair;
    }

    private static StackPanel SignalsRow(BlockTemplateSummary block)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var label = Caption(L("Signalisiert:", "Signals:"));
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        foreach (var signal in block.Signals)
        {
            var chip = new Border { Padding = new Thickness(6, 2, 6, 2), Style = Style("InsetBorderStyle"), Child = Palette.MonoText(signal, 11, Palette.TextPrimary) };
            ToolTipService.SetToolTip(chip, signal switch
            {
                "BIP-54" => L("BIP-54 (Consensus Cleanup): Der Pool zeigt Unterstützung für diese Soft-Fork.", "BIP-54 (Consensus Cleanup): the pool shows support for this soft fork."),
                "BIP-110" => L("BIP-110 (Reduced Data): Der Pool zeigt Unterstützung für diese Soft-Fork.", "BIP-110 (Reduced Data): the pool shows support for this soft fork."),
                _ => L("Der Pool zeigt in diesem Block Unterstützung für eine Protokolländerung.", "In this block the pool shows support for a protocol change."),
            });
            row.Children.Add(chip);
        }
        return row;
    }

    private static StackPanel OutputsSection(BlockTemplateSummary block, MinerDevice miner)
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(Palette.UiText(L("Empfänger der Belohnung", "Reward recipients"), 12, Palette.TextSecondary));
        if (block.PayoutState == PayoutState.DecodingOff) section.Children.Add(DecodingOffHint(miner));
        else if (block.Outputs.Count == 0) section.Children.Add(Caption(L("Der Miner hat noch keine Empfänger gemeldet.", "The miner has not reported any recipients yet.")));
        else
        {
            var rows = new StackPanel { Spacing = 6 };
            foreach (var output in block.Outputs) rows.Children.Add(OutputRow(output, block));
            if (block.OthersCount > 0) rows.Children.Add(OthersRow(block));
            section.Children.Add(rows);
        }
        return section;
    }

    private static Border Row(string glyph, Brush iconBrush, UIElement text, double amountSats, BlockTemplateSummary block)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12, Foreground = iconBrush, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn((FrameworkElement)text, 1);
        row.Children.Add(text);
        if (amountSats > 0)
        {
            var amount = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var coins = Palette.MonoText(BlockTemplateSummary.FormatCoins(amountSats, block.Coin), 11, Palette.TextPrimary);
            coins.FontWeight = FontWeights.Medium;
            coins.HorizontalAlignment = HorizontalAlignment.Right;
            amount.Children.Add(coins);
            if (block.TotalSats is > 0 and var total)
            {
                var share = Palette.UiText((amountSats / total).ToString("P1", Culture), 11, Palette.TextTertiary);
                share.HorizontalAlignment = HorizontalAlignment.Right;
                amount.Children.Add(share);
            }
            Grid.SetColumn(amount, 2);
            row.Children.Add(amount);
        }
        return new Border { Padding = new Thickness(10, 7, 10, 7), Style = Style("InsetBorderStyle"), Child = row };
    }

    private static Border OutputRow(CoinbaseOutput output, BlockTemplateSummary block)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var display = output.IsWitnessCommitment ? "OP_RETURN"
            : output.IsDataOutput ? (output.DataText is { } data ? $"OP_RETURN {data}" : "OP_RETURN")
            : MinerGuru.Core.Privacy.Address(output.Address);
        var address = Palette.MonoText(display, 11, Palette.TextPrimary);
        address.TextTrimming = TextTrimming.CharacterEllipsis;
        address.IsTextSelectionEnabled = true;
        ToolTipService.SetToolTip(address, display);
        text.Children.Add(address);
        if (output.IsUserOutput) text.Children.Add(Palette.UiText(L("Deine Adresse", "Your address"), 11, Palette.Good));
        else if (output.IsWitnessCommitment) text.Children.Add(Palette.UiText(L("SegWit-Prüfsumme, keine Auszahlung", "SegWit commitment, no payout"), 11, Palette.TextTertiary));
        else if (output.IsDataOutput) text.Children.Add(Palette.UiText(L("Daten, keine Auszahlung", "Data, no payout"), 11, Palette.TextTertiary));
        var glyph = output.IsUserOutput ? "\uE77B" : output.IsDataOutput ? "\uE8E4" : "\uE825";
        return Row(glyph, output.IsUserOutput ? Palette.Good : Palette.TextSecondary, text, output.ValueSats, block);
    }

    private static Border OthersRow(BlockTemplateSummary block)
    {
        var text = Caption(L($"{block.OthersCount} weitere Empfänger", $"{block.OthersCount} more recipients"));
        text.VerticalAlignment = VerticalAlignment.Center;
        return Row("\uE712", Palette.TextSecondary, text, block.OthersValueSats, block);
    }

    private static StackPanel PayoutBadge(PayoutState state)
    {
        var (title, help) = state switch
        {
            PayoutState.Verified => (L("Deine Adresse ist im Block", "Your address is in the block"),
                L("Die Coinbase zahlt an die Adresse aus deinem Pool-User.", "The coinbase pays the address in your pool user.")),
            PayoutState.NotFound => (L("Deine Adresse fehlt", "Your address is missing"),
                L("Im Pool-User steht eine Adresse, aber kein Empfänger im Block passt dazu. Findet dein Miner diesen Block, geht die Belohnung an andere.",
                  "Your pool user holds an address, but no recipient in the block matches it. If your miner finds this block, the reward goes to others.")),
            PayoutState.NotApplicable => (L("Konto-Pool", "Account pool"),
                L("Im Pool-User steht keine Adresse, sondern ein Kontoname. Der Pool zahlt dann selbst aus, im Block steht nur seine Adresse.",
                  "Your pool user holds an account name, not an address. The pool then pays out itself; only its own address is in the block.")),
            PayoutState.DecodingOff => (L("Nicht geprüft", "Not checked"),
                L("Coinbase-Dekodierung ist aus, der Miner prüft die Auszahlung nicht.", "Coinbase decoding is off, the miner does not check the payout.")),
            _ => (L("Noch keine Daten", "No data yet"), L("Der Miner hat noch keinen Auftrag dekodiert.", "The miner has not decoded a job yet.")),
        };
        var dot = state switch { PayoutState.Verified => Palette.Good, PayoutState.NotFound => Palette.Bad, _ => Palette.Idle };
        var badge = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        badge.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = dot, VerticalAlignment = VerticalAlignment.Center });
        var text = Palette.UiText(title, 11, state == PayoutState.NotFound ? Palette.Bad : Palette.TextSecondary);
        if (state == PayoutState.NotFound) text.FontWeight = FontWeights.Medium;
        badge.Children.Add(text);
        ToolTipService.SetToolTip(badge, help);
        return badge;
    }

    private static Border DecodingOffHint(MinerDevice miner)
    {
        var text = new StackPanel { Spacing = 6 };
        var title = Palette.UiText(L("Coinbase-Dekodierung ist für den aktiven Pool aus.", "Coinbase decoding is off for the active pool."), 13, Palette.TextPrimary);
        title.FontWeight = FontWeights.Medium;
        title.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(title);
        text.Children.Add(Caption(L("Ohne sie meldet der Miner nicht, wer die Belohnung bekommt, und prüft deine Adresse nicht. Du kannst sie in der Weboberfläche des Miners bei den Pool-Einstellungen einschalten.",
            "Without it the miner does not report who gets the reward and does not check your address. You can switch it on in the miner's web interface, in the pool settings.")));
        var open = new Button { Content = L("Weboberfläche öffnen", "Open web interface"), Style = Style("SmallButtonStyle") };
        open.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri($"http://{miner.IpAddress}"));
        text.Children.Add(open);

        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new FontIcon { Glyph = "\uE946", FontSize = 14, Foreground = Palette.TextSecondary, VerticalAlignment = VerticalAlignment.Top });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return new Border { Padding = new Thickness(12), Style = Style("InsetBorderStyle"), Child = row };
    }

    // Stratum probe

    private static string? PoolLabel(BlockProbeTarget target, int count)
    {
        if (count <= 1 && target.SharePercent == null) return null;
        var number = target.IsPrimary ? 1 : 2;
        return target.SharePercent is { } share ? $"Pool {number} · {share} %" : $"Pool {number}";
    }

    private FrameworkElement RefreshAction(BlockProbeTarget target, string label)
    {
        if (App.Store.BlockProbes.IsRunning(target)) return new ProgressRing { Width = 14, Height = 14, IsActive = true };
        var button = new Button { Content = label, Style = Style("SmallButtonStyle") };
        button.Click += async (_, _) => await App.Store.BlockProbes.RefreshAsync(target, force: true);
        return button;
    }

    private Border ProbeCard(BlockProbeTarget target, int count)
    {
        var label = PoolLabel(target, count);
        var entry = App.Store.BlockProbes.EntryFor(target);
        if (entry?.Block is { } block) return BlockCard(block, _miner!, label, target);

        var header = new Grid();
        header.Children.Add(Title(label));
        var host = Palette.MonoText(target.Host, 11, Palette.TextTertiary);
        host.HorizontalAlignment = HorizontalAlignment.Right;
        host.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(host);

        var status = new Grid { ColumnSpacing = 8 };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition());
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = Caption(entry?.Error ?? L("MinerGuru fragt den Pool nach seiner Vorlage …", "MinerGuru is asking the pool for its template …"));
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        status.Children.Add(text);
        if (entry?.Error != null)
        {
            status.Children.Add(new FontIcon { Glyph = "\uE7BA", FontSize = 13, Foreground = Palette.Warning, VerticalAlignment = VerticalAlignment.Center });
            var retry = RefreshAction(target, L("Erneut versuchen", "Try again"));
            Grid.SetColumn(retry, 2);
            status.Children.Add(retry);
        }
        else status.Children.Add(new ProgressRing { Width = 14, Height = 14, IsActive = true });

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(header);
        body.Children.Add(status);
        return Card(body);
    }

    private Grid ProbeFooter(DateTime probedAt, BlockProbeTarget target)
    {
        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(new FontIcon { Glyph = "\uE968", FontSize = 11, Foreground = Palette.TextTertiary, VerticalAlignment = VerticalAlignment.Center });
        var time = probedAt.ToString("HH:mm", Culture);
        var text = Palette.UiText(L($"Von diesem PC beim Pool abgefragt, {time} Uhr", $"Asked the pool from this PC, {time}"), 11, Palette.TextTertiary);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        footer.Children.Add(text);
        var action = RefreshAction(target, L("Neu abfragen", "Ask again"));
        Grid.SetColumn(action, 2);
        footer.Children.Add(action);
        return footer;
    }

    private static Border ProbeOffHint()
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon { Glyph = "\uE7B8", FontSize = 15, Foreground = Palette.TextSecondary, VerticalAlignment = VerticalAlignment.Top });
        var text = new StackPanel { Spacing = 4 };
        var title = Palette.UiText(L("Blockansicht", "Block view"), 13, Palette.TextPrimary);
        title.FontWeight = FontWeights.Medium;
        text.Children.Add(title);
        text.Children.Add(Caption(L("Dieser Miner meldet nicht, an welchem Block er rechnet. MinerGuru kann die Vorlage selbst beim Pool abfragen und prüfen, ob deine Adresse darin steht.",
            "This miner does not report which block it is working on. MinerGuru can ask the pool for the template itself and check whether your address is in it.")));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var open = new Button { Content = L("In den Einstellungen einschalten", "Switch on in settings"), Style = Style("SmallButtonStyle"), VerticalAlignment = VerticalAlignment.Center };
        open.Click += (_, _) => App.MainWindow.ShowSettings();
        Grid.SetColumn(open, 2);
        row.Children.Add(open);
        return Card(row);
    }
}
