using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using MinerGuru.Controls;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru;

/// <summary>
/// The panel at the tray icon, as the Mac's MenuBarView: the fleet at a glance, one line per miner, and the way
/// back to the dashboard. A small borderless window that hides when it loses focus.
/// </summary>
public sealed partial class TrayFlyoutWindow : Window
{
    private const double PanelWidth = 340;
    private const double MaxPanelHeight = 620;
    private const int Gap = 12;

    private readonly Grid _root = new();
    private readonly Border _frame = new();
    private readonly StackPanel _content = new() { Spacing = 12, Padding = new Thickness(16) };
    private readonly ScrollViewer _scroll = new() { MaxHeight = MaxPanelHeight };
    private readonly nint _handle;
    private DateTime _hiddenAt;

    public TrayFlyoutWindow()
    {
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _scroll.Content = _content;
        _frame.Child = _scroll;
        _root.Children.Add(_frame);
        Content = _root;
        Title = "MinerGuru";
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        var round = 2;
        DwmSetWindowAttribute(_handle, 33, ref round, sizeof(int));   // DWMWA_WINDOW_CORNER_PREFERENCE: round
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) HidePanel();
        };
        App.Store.Changed += () =>
        {
            if (IsOpen) Render();
        };
    }

    private bool IsOpen => AppWindow.IsVisible;

    /// <summary>Opens it at the tray icon, or closes it. A click on the icon first takes the focus away, which already hid the panel.</summary>
    public void Toggle(TrayIcon tray)
    {
        if (IsOpen || DateTime.Now - _hiddenAt < TimeSpan.FromMilliseconds(300))
        {
            HidePanel();
            return;
        }
        Open(tray);
    }

    public void Open(TrayIcon tray)
    {
        Render();
        var scale = GetDpiForWindow(_handle) / 96.0;
        _root.Measure(new Windows.Foundation.Size(PanelWidth, MaxPanelHeight));
        var width = (int)(PanelWidth * scale);
        var height = (int)Math.Ceiling(Math.Min(_root.DesiredSize.Height, MaxPanelHeight) * scale);
        var (x, y) = Position(tray, width, height, (int)(Gap * scale));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        AppWindow.Show();
        Activate();
        SetForegroundWindow(_handle);
    }

    private void HidePanel()
    {
        if (!IsOpen) return;
        _hiddenAt = DateTime.Now;
        AppWindow.Hide();
    }

    /// <summary>Above the icon, or below it with the taskbar on top; always inside the work area.</summary>
    private (int X, int Y) Position(TrayIcon tray, int width, int height, int gap)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var bounds = tray.Bounds;
        var centerX = bounds is { } b ? (b.Left + b.Right) / 2 : area.X + area.Width;
        var x = Math.Clamp(centerX - width / 2, area.X + gap, area.X + area.Width - width - gap);
        var onTop = bounds is { } r && r.Top < area.Y + area.Height / 2;
        return (x, onTop ? area.Y + gap : area.Y + area.Height - height - gap);
    }

    private void Render()
    {
        var store = App.Store;
        _root.RequestedTheme = (App.MainWindow.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
        _frame.Background = Palette.Get("CardBrush");
        // The window's own border is the hairline; Windows would draw it white.
        var border = _root.ActualTheme == ElementTheme.Light ? 0x00DCDCDC : 0x00343434;
        DwmSetWindowAttribute(_handle, 34, ref border, sizeof(int));   // DWMWA_BORDER_COLOR
        _content.Children.Clear();
        _content.Children.Add(Header());
        if (store.Miners.Count > 0)
        {
            _content.Children.Add(Summary());
            _content.Children.Add(Line());
            var rows = new StackPanel { Spacing = 8 };
            foreach (var miner in store.Miners) rows.Children.Add(MinerRow(miner));
            _content.Children.Add(rows);
        }
        else
        {
            _content.Children.Add(Palette.UiText(L("Noch keine Miner eingerichtet.", "No miners set up yet."), 12, Palette.TextSecondary));
        }
        _content.Children.Add(Line());
        _content.Children.Add(Actions());
    }

    private static Rectangle Line() => new() { Height = 1, Fill = Palette.Hairline };

    private static Grid Header()
    {
        var store = App.Store;
        var header = new Grid();
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        name.Children.Add(new MinerGuruMark(14) { VerticalAlignment = VerticalAlignment.Center });
        var title = Palette.UiText("MinerGuru", 15, Palette.TextPrimary);
        title.FontWeight = FontWeights.Medium;
        name.Children.Add(title);
        header.Children.Add(name);

        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        var block = new StatusBlock(7) { VerticalAlignment = VerticalAlignment.Center };
        block.Apply(store.OnlineCount > 0 ? ConnectionState.Online : ConnectionState.Offline, false);
        status.Children.Add(block);
        var count = Palette.UiText($"{store.OnlineCount}/{store.Miners.Count} Online", 12, Palette.TextSecondary);
        count.VerticalAlignment = VerticalAlignment.Center;
        status.Children.Add(count);
        header.Children.Add(status);
        return header;
    }

    private static Border Summary()
    {
        var store = App.Store;
        (string Label, string Value)[] figures =
        [
            ("Hashrate", MinerFormatting.FormatHashrate(store.TotalHashrateGhs)),
            (L("Leistung", "Power"), $"{store.TotalPowerWatts.ToString("0.0", Culture)} W"),
            (L("Ø Ping", "Ø Ping"), MinerFormatting.FormatLatency(store.AveragePoolLatencyMs)),
            ("Best Diff", store.BestSessionInfo?.SessionBestDiffText ?? "—"),
        ];
        var row = new Grid { ColumnSpacing = 10 };
        for (var i = 0; i < figures.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var figure = new StackPanel { Spacing = 2 };
            figure.Children.Add(Palette.UiText(figures[i].Label, 11, Palette.TextSecondary));
            var value = Palette.UiText(figures[i].Value, 13, Palette.TextPrimary);
            value.FontWeight = FontWeights.Medium;
            figure.Children.Add(value);
            Grid.SetColumn(figure, row.ColumnDefinitions.Count - 1);
            row.Children.Add(figure);
            if (i == figures.Length - 1) break;
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var line = new Rectangle { Width = 1, Height = 24, Fill = Palette.Hairline, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(line, row.ColumnDefinitions.Count - 1);
            row.Children.Add(line);
        }
        var box = new Border { Padding = new Thickness(12), Child = row, Style = (Style)Application.Current.Resources["InsetBorderStyle"] };
        ToolTipService.SetToolTip(box, L("Best Diff: höchste Difficulty seit dem letzten Neustart der Miner", "Best diff: highest difficulty since the miners last restarted"));
        return box;
    }

    private static Grid MinerRow(MinerDevice miner)
    {
        var info = miner.LatestInfo;
        var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var block = new StatusBlock(8) { VerticalAlignment = VerticalAlignment.Center };
        block.Apply(miner.State, App.Store.IsHot(miner), miner.OfflineReason);
        row.Children.Add(block);

        var text = new StackPanel { Spacing = 1 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var name = Palette.UiText(miner.DisplayName, 12, Palette.TextPrimary);
        name.FontWeight = FontWeights.Medium;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.MaxWidth = 130;
        title.Children.Add(name);
        if (info != null)
        {
            var dual = info.IsDualPoolMining && info.PrimaryPoolCoin != info.SecondaryPoolCoin;
            var dot = new Ellipse { Width = 6, Height = 6, Fill = Palette.CoinColor(info.ActivePoolCoin), VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(dot);
            var coin = Palette.MonoText(dual ? $"{info.PrimaryPoolCoin.Symbol()}+{info.SecondaryPoolCoin.Symbol()}" : info.ActivePoolCoin.Symbol(), 11, Palette.TextSecondary);
            coin.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(coin);
        }
        text.Children.Add(title);
        var address = Palette.MonoText($"{miner.HardwareTitle} · {MinerGuru.Core.Privacy.Ip(miner.IpAddress)}", 11, Palette.TextTertiary);
        address.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(address);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        FrameworkElement figures;
        if (info != null && miner.IsOnline)
        {
            var stack = new StackPanel { Spacing = 1 };
            var hashrate = Palette.UiText(MinerFormatting.FormatHashrate(info.HashRate), 12, Palette.TextPrimary);
            hashrate.FontWeight = FontWeights.Medium;
            hashrate.HorizontalAlignment = HorizontalAlignment.Right;
            stack.Children.Add(hashrate);
            var ping = miner.ActivePoolLatencyMs is { } ms ? $" • {ms:0}ms" : "";
            var temp = Palette.UiText($"{info.Temp:0}°C{ping}", 11, Palette.TextSecondary);
            temp.HorizontalAlignment = HorizontalAlignment.Right;
            stack.Children.Add(temp);
            var best = Palette.UiText($"Best {info.SessionBestDiffText}", 11, Palette.TextSecondary);
            best.HorizontalAlignment = HorizontalAlignment.Right;
            stack.Children.Add(best);
            figures = stack;
        }
        else
        {
            figures = Palette.MonoText("Offline", 11, Palette.Bad);
            figures.VerticalAlignment = VerticalAlignment.Center;
        }
        Grid.SetColumn(figures, 2);
        row.Children.Add(figures);
        return row;
    }

    private Grid Actions()
    {
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var open = new Button { Content = L("Dashboard öffnen", "Open dashboard"), HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["SmallAccentButtonStyle"] };
        open.Click += (_, _) =>
        {
            HidePanel();
            App.MainWindow.ShowFromBackground();
        };
        actions.Children.Add(open);

        var refresh = new Button { Content = new FontIcon { Glyph = "\uE72C", FontSize = 13 }, Style = (Style)Application.Current.Resources["SmallButtonStyle"] };
        ToolTipService.SetToolTip(refresh, L("Jetzt aktualisieren", "Refresh now"));
        AutomationProperties.SetName(refresh, L("Jetzt aktualisieren", "Refresh now"));
        refresh.Click += async (_, _) => await App.Store.RefreshAllAsync();
        Grid.SetColumn(refresh, 1);
        actions.Children.Add(refresh);

        var quit = new Button { Content = new FontIcon { Glyph = "\uE7E8", FontSize = 13 }, Style = (Style)Application.Current.Resources["SmallButtonStyle"] };
        ToolTipService.SetToolTip(quit, L("MinerGuru beenden", "Quit MinerGuru"));
        AutomationProperties.SetName(quit, L("MinerGuru beenden", "Quit MinerGuru"));
        quit.Click += (_, _) => App.Quit();
        Grid.SetColumn(quit, 2);
        actions.Children.Add(quit);
        return actions;
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
