using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using MinerGuru.Controls;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Dialogs;
using MinerGuru.Pages;
using static MinerGuru.Core.Lang;

namespace MinerGuru;

/// <summary>A page that names itself in the title bar, like the Mac's navigation title and subtitle.</summary>
public interface IHeaderPage
{
    (string Title, string Subtitle) Header { get; }
}

public sealed partial class MainWindow : Window
{
    private readonly SidebarNavRow _fleetRow = new(L("Flotte", "Fleet"), "");
    private readonly SidebarNavRow _poolsRow = new("Pools", "");
    private readonly SidebarNavRow _powerRow = new(L("Strom", "Power"), "");
    private readonly SidebarNavRow _maintenanceRow = new(L("Wartung", "Maintenance"), "\uE90F");
    private readonly SidebarNavRow _reportsRow = new(L("Berichte", "Reports"), "\uE9F9");
    /// <summary>The sidebar row of each miner, kept so a refresh only updates the texts.</summary>
    private readonly Dictionary<Guid, SidebarMinerRow> _minerRows = [];
    private readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    /// <summary>"fleet", "pools", "power", "settings" or a miner id.</summary>
    private object _selection = "fleet";

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        SizeForDisplay();

        MarkHost.Child = new MinerGuruMark(18);
        SyncPrivacyButton();
        _fleetRow.Click += (_, _) => Select("fleet");
        _poolsRow.Click += (_, _) => Select("pools");
        _powerRow.Click += (_, _) => Select("power");
        SectionRows.Children.Add(_fleetRow);
        SectionRows.Children.Add(_poolsRow);
        _maintenanceRow.Click += (_, _) => Select("maintenance");
        _reportsRow.Click += (_, _) => Select("reports");
        SectionRows.Children.Add(_powerRow);
        SectionRows.Children.Add(_maintenanceRow);
        SectionRows.Children.Add(_reportsRow);

        Root.ActualThemeChanged += (_, _) => OnThemeChanged();
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer.Stop();
            Banner.Visibility = Visibility.Collapsed;
        };
        ContentFrame.Navigated += (_, _) => UpdateHeader();
        App.Store.Changed += OnStoreChanged;
        App.Store.Maintenance.Changed += OnStoreChanged;
        App.Store.Banner += ShowBanner;
        App.Store.History.Damaged += () => DispatcherQueue.TryEnqueue(ShowHistoryDamaged);
        if (App.Store.History.DamagedBackup != null) ShowHistoryDamaged();
        if (App.Store.DamagedSettingsBackup != null)
        {
            ShowBanner(L(
                "Die Einstellungen waren beschädigt, MinerGuru startet leer. Die alte Datei liegt im Datenordner.",
                "The settings were damaged, MinerGuru starts empty. The old file stays in the data folder."), true);
            // Appears right at the start, when a short notice is easily missed.
            _bannerTimer.Stop();
        }
        AppWindow.Closing += (_, args) =>
        {
            if (App.Store.KeepRunningInTray)
            {
                args.Cancel = true;
                AppWindow.Hide();
            }
            else App.Quit();
        };

        // Test switches for the VM, debug builds only (DebugSwitch): MINERGURU_THEME=light|dark overrides the Windows setting,
        // MINERGURU_OPEN=<address> starts on that miner's page, MINERGURU_OPEN=pools|power|settings on that page, =tray opens the tray panel (see App).
        Root.RequestedTheme = MinerGuru.Core.DebugSwitch.Get("MINERGURU_THEME") switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ApplyCaptionColors();
        SyncSidebar();
        var open = MinerGuru.Core.DebugSwitch.Get("MINERGURU_OPEN");
        var start = string.IsNullOrEmpty(open) ? null : App.Store.Miners.FirstOrDefault(m => m.IpAddress.EndsWith(open, StringComparison.OrdinalIgnoreCase));
        if (open is "pools" or "power" or "maintenance" or "reports" or "settings") Select(open);
        else if (start != null) Select(start.Id);
        else Select("fleet");
    }

    /// <summary>About 1280 × 820 at 100 %, scaled to the display and kept inside its work area.</summary>
    private void SizeForDisplay()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1280 * scale), area.Width - 40);
        var height = Math.Min((int)(820 * scale), area.Height - 40);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(1000 * scale);
            presenter.PreferredMinimumHeight = (int)(620 * scale);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    /// <summary>Caption buttons without their own background, in the text color of the theme.</summary>
    private void ApplyCaptionColors()
    {
        var light = Root.ActualTheme == ElementTheme.Light;
        byte tone = light ? (byte)0 : (byte)255;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = Windows.UI.Color.FromArgb(0xD9, tone, tone, tone);
        bar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(0xD9, tone, tone, tone);
        bar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(0xD9, tone, tone, tone);
        bar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0x60, tone, tone, tone);
        bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x14, tone, tone, tone);
        bar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x24, tone, tone, tone);
    }

    private void OnThemeChanged()
    {
        ApplyCaptionColors();
        MarkHost.Child = new MinerGuruMark(18);
        SyncSidebar();
    }

    // Navigation

    private void Select(object selection)
    {
        _selection = selection;
        var transition = new EntranceNavigationTransitionInfo();
        switch (selection)
        {
            case "fleet": ContentFrame.Navigate(typeof(FleetPage), null, transition); break;
            case "pools": ContentFrame.Navigate(typeof(PoolsPage), null, transition); break;
            case "power": ContentFrame.Navigate(typeof(PowerPage), null, transition); break;
            case "maintenance": ContentFrame.Navigate(typeof(MaintenancePage), null, transition); break;
            case "reports": ContentFrame.Navigate(typeof(ReportsPage), null, transition); break;
            case "settings": ContentFrame.Navigate(typeof(SettingsPage), null, transition); break;
            case Guid id: ContentFrame.Navigate(typeof(MinerPage), id, transition); break;
        }
        SyncSelection();
    }

    public void ShowMiner(Guid id) => Select(id);

    /// <summary>Back from the tray or a notification, optionally on a miner's page.</summary>
    public void ShowFromBackground(Guid? miner = null)
    {
        if (miner is { } id && App.Store.Find(id) != null) Select(id);
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    public void ShowFleet() => Select("fleet");

    public void ShowSettings() => Select("settings");

    private void Privacy_Click(object sender, RoutedEventArgs e)
    {
        App.Store.HideSensitiveData = !App.Store.HideSensitiveData;
        SyncPrivacyButton();
    }

    public void SyncPrivacyButton()
    {
        var hidden = App.Store.HideSensitiveData;
        PrivacyIcon.Glyph = hidden ? "\uED1A" : "\uE7B3";
        var tip = hidden
            ? L("IP-, Wallet- und MAC-Adressen wieder anzeigen (Strg+Umschalt+H)", "Show IP, wallet and MAC addresses again (Ctrl+Shift+H)")
            : L("IP-, Wallet- und MAC-Adressen für Screenshots verschleiern (Strg+Umschalt+H)", "Hide IP, wallet and MAC addresses for screenshots (Ctrl+Shift+H)");
        ToolTipService.SetToolTip(PrivacyButton, tip);
        AutomationProperties.SetName(PrivacyButton, tip);
    }

    public void ShowPools() => Select("pools");

    private void SyncSelection()
    {
        _fleetRow.IsSelected = _selection is "fleet";
        _poolsRow.IsSelected = _selection is "pools";
        _powerRow.IsSelected = _selection is "power";
        _maintenanceRow.IsSelected = _selection is "maintenance";
        _reportsRow.IsSelected = _selection is "reports";
        foreach (var (id, row) in _minerRows) row.IsSelected = _selection is Guid selected && selected == id;
    }

    private void UpdateHeader()
    {
        var (title, subtitle) = ContentFrame.Content is IHeaderPage page ? page.Header : ("MinerGuru", "");
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
    }

    // Sidebar

    private void OnStoreChanged()
    {
        SyncSidebar();
        UpdateHeader();
    }

    private void SyncSidebar()
    {
        var store = App.Store;
        var online = store.OnlineCount > 0;
        _fleetRow.Trailing = online ? MinerFormatting.FormatHashrate(store.TotalHashrateGhs) : null;
        _powerRow.Trailing = online ? $"{store.TotalPowerWatts:0} W" : null;
        _maintenanceRow.Trailing = store.Maintenance.DueCount(DateTime.Now) is > 0 and var due ? due.ToString() : null;
        RefreshAllButton.IsEnabled = store.Miners.Count > 0 && !store.IsRefreshingAll;

        var ids = store.Miners.Select(m => m.Id).ToHashSet();
        foreach (var gone in _minerRows.Keys.Where(id => !ids.Contains(id)).ToList()) _minerRows.Remove(gone);

        // Rows in the store's order (by address)
        var rows = new List<SidebarMinerRow>();
        foreach (var miner in store.Miners)
        {
            if (!_minerRows.TryGetValue(miner.Id, out var row))
            {
                row = new SidebarMinerRow();
                var id = miner.Id;
                row.Click += (_, _) => Select(id);
                row.ContextFlyout = MinerMenu(miner);
                _minerRows[miner.Id] = row;
            }
            row.Update(miner, store.IsHot(miner));
            rows.Add(row);
        }
        if (!MinerRows.Children.Cast<SidebarMinerRow>().SequenceEqual(rows))
        {
            MinerRows.Children.Clear();
            foreach (var row in rows) MinerRows.Children.Add(row);
        }

        var hasMiners = store.Miners.Count > 0;
        MinersDivider.Visibility = MinersHeader.Visibility = hasMiners ? Visibility.Visible : Visibility.Collapsed;
        MinerCountText.Text = store.Miners.Count.ToString();
        if (_selection is Guid selected && !ids.Contains(selected)) ShowFleet();
        SyncSelection();
    }

    private MenuFlyout MinerMenu(MinerDevice miner)
    {
        var menu = new MenuFlyout();
        var browser = new MenuFlyoutItem { Text = L("Im Browser öffnen", "Open in browser"), Icon = new FontIcon { Glyph = "" } };
        browser.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri($"http://{miner.IpAddress}"));
        var refresh = new MenuFlyoutItem { Text = L("Jetzt aktualisieren", "Refresh now"), Icon = new FontIcon { Glyph = "" } };
        refresh.Click += async (_, _) => await App.Store.RefreshMinerAsync(miner.Id);
        var remove = new MenuFlyoutItem { Text = L("Miner entfernen", "Remove miner"), Icon = new FontIcon { Glyph = "" } };
        remove.Click += async (_, _) => await ConfirmRemoveAsync(miner);
        menu.Items.Add(browser);
        menu.Items.Add(refresh);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(remove);
        return menu;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => Select("settings");

    private async void AddIp_Click(object sender, RoutedEventArgs e) => await ShowAddMinerAsync();

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ShowScanAsync();

    private async void RefreshAll_Click(object sender, RoutedEventArgs e) => await App.Store.RefreshAllAsync();

    // Dialogs and messages

    public async Task ShowAddMinerAsync()
    {
        var dialog = new AddMinerDialog { XamlRoot = Content.XamlRoot, RequestedTheme = Root.ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.AddedMiner is { } miner)
        {
            ShowBanner(L($"{miner.DisplayName} hinzugefügt.", $"{miner.DisplayName} added."), false);
            ShowMiner(miner.Id);
        }
    }

    public async Task ShowScanAsync() =>
        await new NetworkScanDialog { XamlRoot = Content.XamlRoot, RequestedTheme = Root.ActualTheme }.ShowAsync();

    public async Task ConfirmRemoveAsync(MinerDevice miner)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = L($"{miner.DisplayName} entfernen?", $"Remove {miner.DisplayName}?"),
            Content = L("Der Miner läuft weiter, MinerGuru zeigt ihn nur nicht mehr an. Der Verlauf geht verloren.",
                        "The miner keeps running, MinerGuru just stops showing it. Its history is lost."),
            PrimaryButtonText = L("Entfernen", "Remove"),
            CloseButtonText = L("Abbrechen", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) App.Store.RemoveMiner(miner.Id);
    }

    private void ShowHistoryDamaged()
    {
        ShowBanner(L(
            "Der Verlauf war beschädigt und beginnt neu. Die alte Datei liegt im Datenordner.",
            "The history was damaged and starts anew. The old file stays in the data folder."), true);
        // Appears right at the start, when a short notice is easily missed.
        _bannerTimer.Stop();
        _bannerTimer.Interval = TimeSpan.FromSeconds(30);
        _bannerTimer.Start();
    }

    public void ShowBanner(string message, bool isError)
    {
        _bannerTimer.Interval = TimeSpan.FromSeconds(4);
        BannerIcon.Glyph = isError ? "" : "";
        BannerIcon.Foreground = isError ? Palette.Warning : Palette.Good;
        BannerText.Text = message;
        Banner.Visibility = Visibility.Visible;
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }
}
