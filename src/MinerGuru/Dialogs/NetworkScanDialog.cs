using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using MinerGuru.Controls;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Dialogs;

/// <summary>Search the local subnet for miners and pick the ones to add, as the Mac's NetworkScannerSheet.</summary>
public sealed partial class NetworkScanDialog : ContentDialog
{
    private readonly TextBox _prefix = new() { Width = 130, FontFamily = Palette.Mono, PlaceholderText = NetworkScanner.FallbackPrefix };
    private readonly Button _startStop = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _progress = new() { Spacing = 6 };
    private readonly ProgressBar _bar = new() { Minimum = 0 };
    private readonly TextBlock _progressText = Palette.UiText("", 12, Palette.TextSecondary);
    private readonly TextBlock _foundText = Palette.MonoText("", 11, Palette.TextSecondary);
    private readonly TextBlock _listTitle = Palette.UiText("", 13, Palette.TextPrimary);
    private readonly Button _addAll;
    private readonly StackPanel _empty = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _emptyIcon = new() { FontSize = 32, Foreground = Palette.TextSecondary };
    private readonly TextBlock _emptyTitle = Palette.UiText("", 15, Palette.TextPrimary);
    private readonly TextBlock _emptyText = Palette.UiText("", 12, Palette.TextSecondary);
    private readonly ScrollViewer _listScroll = new();
    private readonly StackPanel _list = new() { Spacing = 8 };
    /// <summary>What the rows show, so a progress tick does not rebuild them.</summary>
    private string? _renderedRows;

    private static NetworkScanStore Scanner => App.Store.Scanner;

    public NetworkScanDialog()
    {
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 700.0;
        CloseButtonText = L("Fertig", "Done");

        _prefix.Text = App.Store.ScanSubnetPrefix;
        _prefix.TextChanged += (_, _) =>
        {
            if (NetworkScanner.IsValidPrefix(_prefix.Text)) App.Store.ScanSubnetPrefix = NetworkScanner.NormalizePrefix(_prefix.Text);
            _startStop.IsEnabled = Scanner.IsScanning || NetworkScanner.IsValidPrefix(_prefix.Text);
        };
        _startStop.Click += (_, _) =>
        {
            if (Scanner.IsScanning) Scanner.Stop();
            else Scanner.Start();
        };
        _addAll = new Button { Content = L("Alle neuen hinzufügen", "Add all new"), Style = (Style)Application.Current.Resources["SmallAccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
        _addAll.Click += (_, _) => Scanner.AddAllDiscovered();

        Content = BuildContent();
        Scanner.Changed += Sync;
        Opened += (_, _) =>
        {
            if (!Scanner.IsScanning && Scanner.Discovered.Count == 0 && NetworkScanner.IsValidPrefix(App.Store.ScanSubnetPrefix)) Scanner.Start();
            Sync();
        };
        Closed += (_, _) =>
        {
            Scanner.Changed -= Sync;
            Scanner.Stop();
        };
        Sync();
    }

    private Grid BuildContent()
    {
        var root = new Grid { Width = 640, Height = 430, RowSpacing = 14 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(new FontIcon { Glyph = "\uE701", FontSize = 18, Foreground = Palette.TextSecondary });
        var titles = new StackPanel { Spacing = 2 };
        var title = Palette.UiText(L("Lokales Netzwerk nach Minern durchsuchen", "Search the local network for miners"), 15, Palette.TextPrimary);
        title.FontWeight = FontWeights.Medium;
        titles.Children.Add(title);
        var subtitle = Palette.UiText(L("Erkennt automatisch Bitaxe (Gamma 601, Supra, Ultra) & NerdQAxe++ über /api/system/info", "Finds Bitaxe (Gamma 601, Supra, Ultra) & NerdQAxe++ through /api/system/info"), 12, Palette.TextSecondary);
        subtitle.TextWrapping = TextWrapping.Wrap;
        titles.Children.Add(subtitle);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        root.Children.Add(header);

        var line = new Rectangle { Height = 1, Fill = Palette.Hairline };
        Grid.SetRow(line, 1);
        root.Children.Add(line);

        var subnet = new Grid { ColumnSpacing = 12 };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            subnet.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var label = Palette.UiText(L("Subnetz-Präfix:", "Subnet prefix:"), 13, Palette.TextPrimary);
        label.VerticalAlignment = VerticalAlignment.Center;
        subnet.Children.Add(label);
        Grid.SetColumn(_prefix, 1);
        subnet.Children.Add(_prefix);
        var range = Palette.UiText(L(".1 bis .254", ".1 to .254"), 12, Palette.TextSecondary);
        range.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(range, 2);
        subnet.Children.Add(range);
        Grid.SetColumn(_startStop, 4);
        subnet.Children.Add(_startStop);
        Grid.SetRow(subnet, 2);
        root.Children.Add(subnet);

        _progress.Children.Add(_bar);
        var progressLine = new Grid();
        progressLine.Children.Add(_progressText);
        _foundText.HorizontalAlignment = HorizontalAlignment.Right;
        progressLine.Children.Add(_foundText);
        _progress.Children.Add(progressLine);
        Grid.SetRow(_progress, 3);
        root.Children.Add(_progress);

        var listHeader = new Grid();
        _listTitle.FontWeight = FontWeights.Medium;
        _listTitle.VerticalAlignment = VerticalAlignment.Center;
        listHeader.Children.Add(_listTitle);
        listHeader.Children.Add(_addAll);
        Grid.SetRow(listHeader, 4);
        root.Children.Add(listHeader);

        _emptyIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyTitle.FontWeight = FontWeights.Medium;
        _emptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyText.TextAlignment = TextAlignment.Center;
        _emptyText.TextWrapping = TextWrapping.Wrap;
        _empty.Children.Add(_emptyIcon);
        _empty.Children.Add(_emptyTitle);
        _empty.Children.Add(_emptyText);
        Grid.SetRow(_empty, 5);
        root.Children.Add(_empty);

        _listScroll.Content = _list;
        Grid.SetRow(_listScroll, 5);
        root.Children.Add(_listScroll);
        return root;
    }

    private void Sync()
    {
        var scanner = Scanner;
        var store = App.Store;
        var prefix = NetworkScanner.NormalizePrefix(store.ScanSubnetPrefix);
        var scanning = scanner.IsScanning;
        var found = scanner.Discovered;

        _prefix.IsEnabled = !scanning;
        _startStop.Style = (Style)Application.Current.Resources[scanning ? "DefaultButtonStyle" : "AccentButtonStyle"];
        _startStop.Content = scanning ? L("Scan abbrechen", "Stop scan") : L("Scan starten", "Start scan");
        _startStop.IsEnabled = scanning || NetworkScanner.IsValidPrefix(_prefix.Text);

        _progress.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        _bar.Maximum = Math.Max(1, scanner.TotalCount);
        _bar.Value = scanner.CompletedCount;
        _progressText.Text = L($"Scanne {prefix}.1–254… ({scanner.CompletedCount} / {scanner.TotalCount} IPs geprüft)", $"Scanning {prefix}.1–254… ({scanner.CompletedCount} / {scanner.TotalCount} IPs checked)");
        _foundText.Text = L($"{found.Count} Miner gefunden", $"{found.Count} miners found");

        _listTitle.Text = L($"Gefundene Miner im Netzwerk ({found.Count})", $"Miners found on the network ({found.Count})");
        _addAll.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _addAll.IsEnabled = found.Any(m => !store.IsAlreadyAdded(m.IpAddress));

        _empty.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _listScroll.Visibility = found.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _emptyIcon.Glyph = scanning ? "\uE701" : "\uE968";
        _emptyTitle.Text = scanning ? L("Suche läuft…", "Searching…") : L("Noch keine Miner im Scan-Ergebnis", "No miners in the scan result yet");
        _emptyText.Text = scanning
            ? L("Antwortende Bitaxe- und NerdQAxe++-Miner erscheinen sofort hier.", "Bitaxe and NerdQAxe++ miners that answer appear here right away.")
            : L($"Klicke auf „Scan starten“, um das Subnetz {prefix}.1–254 zu durchsuchen.", $"Click \"Start scan\" to search the subnet {prefix}.1–254.");

        var rows = string.Join('|', found.Select(m => $"{m.IpAddress} {store.IsAlreadyAdded(m.IpAddress)}"));
        if (rows == _renderedRows) return;
        _renderedRows = rows;
        _list.Children.Clear();
        foreach (var miner in found) _list.Children.Add(Row(miner));
    }

    private static Border Row(DiscoveredMiner miner)
    {
        var info = miner.Info;
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = info.DetectedHardwareTitle();
        var board = new DeviceCard(52, showLed: false) { VerticalAlignment = VerticalAlignment.Center };
        board.Update(DeviceCardSpec.From(title, info), ConnectionState.Online, false);
        grid.Children.Add(board);

        var text = new StackPanel { Spacing = 2 };
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var hostname = Palette.UiText(info.Hostname, 13, Palette.TextPrimary);
        hostname.FontWeight = FontWeights.Medium;
        name.Children.Add(hostname);
        var hardware = Palette.MonoText(title, 11, Palette.TextSecondary);
        hardware.VerticalAlignment = VerticalAlignment.Center;
        name.Children.Add(hardware);
        text.Children.Add(name);
        text.Children.Add(Palette.MonoText($"{miner.IpAddress}   {MinerFormatting.FormatHashrate(info.HashRate)}   {info.Temp.ToString("0.0", Culture)} °C   {info.Power.ToString("0.0", Culture)} W", 11, Palette.TextSecondary));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        FrameworkElement action;
        if (App.Store.IsAlreadyAdded(miner.IpAddress))
        {
            var added = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            added.Children.Add(new FontIcon { Glyph = "\uE73E", FontSize = 12, Foreground = Palette.Good });
            added.Children.Add(Palette.UiText(L("Bereits vorhanden", "Already added"), 12, Palette.Good));
            action = added;
        }
        else
        {
            var add = new Button { Content = L("Hinzufügen", "Add"), Style = (Style)Application.Current.Resources["SmallAccentButtonStyle"] };
            add.Click += (_, _) => Scanner.Add(miner);
            action = add;
        }
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);

        return new Border { Padding = new Thickness(12), Child = grid, Style = (Style)Application.Current.Resources["CardBorderStyle"], CornerRadius = new CornerRadius(12) };
    }
}
