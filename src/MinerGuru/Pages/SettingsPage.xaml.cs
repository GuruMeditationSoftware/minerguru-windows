using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using MinerGuru.Dialogs;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Pages;

public sealed partial class SettingsPage : Page, IHeaderPage
{
    public (string Title, string Subtitle) Header => (Core.Lang.L("Einstellungen", "Settings"), Core.Lang.L("Abfrage, Warnungen, Infobereich, Daten", "Polling, warnings, notification area, data"));

    private readonly bool _ready;

    public SettingsPage()
    {
        InitializeComponent();
        var store = App.Store;
        LanguageBox.Items.Add(L("Wie Windows", "Same as Windows"));
        LanguageBox.Items.Add("Deutsch");
        LanguageBox.Items.Add("English");
        LanguageBox.SelectedIndex = store.Language switch { "de" => 1, "en" => 2, _ => 0 };
        PrivacySwitch.IsOn = store.HideSensitiveData;
        IntervalBox.Value = store.PollingIntervalSeconds;
        TempBox.Value = store.TempWarningThresholdC;
        SubnetBox.Text = store.ScanSubnetPrefix;
        AutoScanSwitch.IsOn = store.AutoScanOnEmptyLaunch;
        StratumProbeSwitch.IsOn = store.StratumProbeEnabled;
        UpdateCheckSwitch.IsOn = store.Updates.IsEnabled;
        ShowUpdateStatus();
        store.Updates.Changed += ShowUpdateStatus;
        Unloaded += (_, _) => store.Updates.Changed -= ShowUpdateStatus;
        AlertsSwitch.IsOn = store.Alerts.IsEnabled;
        OfflineAlertText.Text = AlertKind.Offline.Title();
        AutomationProperties.SetName(OfflineAlertSwitch, OfflineAlertText.Text);
        OfflineAlertSwitch.IsOn = store.Alerts.IsKindEnabled(AlertKind.Offline);
        MaintenanceAlertText.Text = AlertKind.Maintenance.Title();
        AutomationProperties.SetName(MaintenanceAlertSwitch, MaintenanceAlertText.Text);
        MaintenanceAlertSwitch.IsOn = store.Alerts.IsKindEnabled(AlertKind.Maintenance);
        OverheatAlertText.Text = AlertKind.Overheat.Title();
        AutomationProperties.SetName(OverheatAlertSwitch, OverheatAlertText.Text);
        OverheatAlertSwitch.IsOn = store.Alerts.IsKindEnabled(AlertKind.Overheat);
        FlatlineAlertText.Text = AlertKind.Flatline.Title();
        AutomationProperties.SetName(FlatlineAlertSwitch, FlatlineAlertText.Text);
        FlatlineAlertSwitch.IsOn = store.Alerts.IsKindEnabled(AlertKind.Flatline);
        FallbackAlertText.Text = AlertKind.Fallback.Title();
        AutomationProperties.SetName(FallbackAlertSwitch, FallbackAlertText.Text);
        FallbackAlertSwitch.IsOn = store.Alerts.IsKindEnabled(AlertKind.Fallback);
        RestartAlertText.Text = AlertKind.Restart.Title();
        AutomationProperties.SetName(RestartAlertSwitch, RestartAlertText.Text);
        RestartAlertSwitch.IsOn = store.Alerts.IsKindEnabled(AlertKind.Restart);
        PayoutAlertText.Text = AlertKind.Payout.Title();
        AutomationProperties.SetName(PayoutAlertSwitch, PayoutAlertText.Text);
        PayoutAlertSwitch.IsOn = store.Alerts.IsKindEnabled(AlertKind.Payout);
        ReadOnlySwitch.IsOn = ChangeLog.Shared.IsReadOnly;
        RestartFlatlineSwitch.IsOn = store.Alerts.RestartsOnFlatline;
        RestartPrimarySwitch.IsOn = store.Alerts.RestartsToPrimary;
        BtcAddressBox.Text = store.Alerts.ExpectedAddresses.GetValueOrDefault("BTC", "");
        BchAddressBox.Text = store.Alerts.ExpectedAddresses.GetValueOrDefault("BCH", "");
        DgbAddressBox.Text = store.Alerts.ExpectedAddresses.GetValueOrDefault("DGB", "");
        XecAddressBox.Text = store.Alerts.ExpectedAddresses.GetValueOrDefault("XEC", "");
        ShowEmailStatus();
        KeepRunningSwitch.IsOn = store.KeepRunningInTray;
        WidgetsSection.Visibility = Widgets.WidgetPackage.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
        ShowWidgetState();
        // Developer Mode is switched in Windows settings: coming back to MinerGuru shows it at once.
        // (On Loaded: a start on this page builds it before the window exists.)
        Loaded += (_, _) => App.MainWindow.Activated += MainWindow_Activated;
        Unloaded += (_, _) => App.MainWindow.Activated -= MainWindow_Activated;
        SyncAlertCards();
        DataFolderText.Text = store.DataDirectory;
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = L($"Version {version?.ToString(3)} · Windows-Vorschau", $"Version {version?.ToString(3)} · Windows preview");
        _ready = true;
    }

    private void IntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_ready && !double.IsNaN(args.NewValue)) App.Store.PollingIntervalSeconds = (int)Math.Round(args.NewValue);
    }

    private void TempBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_ready && !double.IsNaN(args.NewValue)) App.Store.TempWarningThresholdC = Math.Round(args.NewValue);
    }

    private void SubnetBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var prefix = NetworkScanner.NormalizePrefix(SubnetBox.Text);
        if (NetworkScanner.IsValidPrefix(prefix) && prefix != App.Store.ScanSubnetPrefix) App.Store.ScanSubnetPrefix = prefix;
        SubnetBox.Text = App.Store.ScanSubnetPrefix;
    }

    private void AutoScanSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) App.Store.AutoScanOnEmptyLaunch = AutoScanSwitch.IsOn;
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) App.Store.Language = LanguageBox.SelectedIndex switch { 1 => "de", 2 => "en", _ => null };
    }

    private void PrivacySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        App.Store.HideSensitiveData = PrivacySwitch.IsOn;
        App.MainWindow.SyncPrivacyButton();
    }

    private void UpdateCheckSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) App.Store.Updates.IsEnabled = UpdateCheckSwitch.IsOn;
    }

    private async void UpdateCheckButton_Click(object sender, RoutedEventArgs e) => await App.Store.Updates.CheckAsync();

    private async void UpdateDownload_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri(MinerGuru.Core.Models.AppInfo.ReleasesUrl));

    private void ShowUpdateStatus()
    {
        var updates = App.Store.Updates;
        var available = updates.AvailableVersion;
        UpdateStatusText.Text = updates.Status switch
        {
            UpdateStatus.Checking => L("Suche läuft …", "Checking …"),
            UpdateStatus.Failed => L("GitHub ist gerade nicht erreichbar.", "GitHub cannot be reached right now."),
            _ when available != null => L($"MinerGuru {available} ist verfügbar", $"MinerGuru {available} is available"),
            UpdateStatus.UpToDate => L("MinerGuru ist aktuell.", "MinerGuru is up to date."),
            _ => L("Noch nicht gesucht", "Not checked yet"),
        };
        UpdateDownloadLink.Visibility = available != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateCheckButton.IsEnabled = updates.Status != UpdateStatus.Checking;
    }

    private void StratumProbeSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) App.Store.StratumProbeEnabled = StratumProbeSwitch.IsOn;
    }

    private void AlertsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) App.Store.Alerts.IsEnabled = AlertsSwitch.IsOn;
        SyncAlertCards();
    }

    private void OfflineAlertSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) App.Store.Alerts.SetKind(AlertKind.Offline, OfflineAlertSwitch.IsOn);
    }

    private void MaintenanceAlertSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) App.Store.Alerts.SetKind(AlertKind.Maintenance, MaintenanceAlertSwitch.IsOn);
    }

    private void OverheatAlertSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) App.Store.Alerts.SetKind(AlertKind.Overheat, OverheatAlertSwitch.IsOn);
    }

    private void FlatlineAlertSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) App.Store.Alerts.SetKind(AlertKind.Flatline, FlatlineAlertSwitch.IsOn);
    }

    private void FallbackAlertSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) App.Store.Alerts.SetKind(AlertKind.Fallback, FallbackAlertSwitch.IsOn);
    }

    private void RestartAlertSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) App.Store.Alerts.SetKind(AlertKind.Restart, RestartAlertSwitch.IsOn);
    }

    private void PayoutAlertSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) App.Store.Alerts.SetKind(AlertKind.Payout, PayoutAlertSwitch.IsOn);
    }

    private void ReadOnlySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) ChangeLog.Shared.IsReadOnly = ReadOnlySwitch.IsOn;
    }

    private void RestartFlatlineSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) App.Store.Alerts.RestartsOnFlatline = RestartFlatlineSwitch.IsOn;
    }

    private void RestartPrimarySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) App.Store.Alerts.RestartsToPrimary = RestartPrimarySwitch.IsOn;
    }

    private void AddressBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not TextBox box) return;
        var coin = box == BtcAddressBox ? MinedCoin.Btc : box == BchAddressBox ? MinedCoin.Bch : box == DgbAddressBox ? MinedCoin.Dgb : MinedCoin.Xec;
        App.Store.Alerts.SetExpectedAddress(coin, box.Text);
        var invalid = new[] { BtcAddressBox, BchAddressBox, DgbAddressBox, XecAddressBox }
            .Any(b => b.Text.Trim().Length > 0 && CoinAddress.ScriptPubKey(b.Text.Trim()) == null);
        AddressWarning.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ChangeLog_Click(object sender, RoutedEventArgs e) => await Dialogs.ChangeLogDialog.ShowForAsync(XamlRoot);

    private async void EmailAlerts_Click(object sender, RoutedEventArgs e)
    {
        await new EmailAlertsDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme }.ShowAsync();
        ShowEmailStatus();
    }

    private void ShowEmailStatus()
    {
        var email = App.Store.Email.Settings;
        EmailStatusText.Text = email.IsEnabled && email.IsComplete ? email.Recipients : L("Aus", "Off");
    }

    private void KeepRunningSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) App.Store.KeepRunningInTray = KeepRunningSwitch.IsOn;
    }

    // Widgets

    private bool _widgetsBusy;
    /// <summary>The switch follows Windows' state: no toggle handling then.</summary>
    private bool _widgetsSyncing;

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated) ShowWidgetState();
    }

    /// <summary>
    /// Without Developer Mode Windows grants no package identity to an unsigned app, so the switch waits for it.
    /// Already registered widgets can always be switched off.
    /// </summary>
    private async void ShowWidgetState(string? error = null)
    {
        if (_widgetsBusy || !Widgets.WidgetPackage.IsAvailable) return;
        var (developer, registered) = await Task.Run(() => (Widgets.WidgetPackage.DeveloperModeOn, Widgets.WidgetPackage.Registered));
        if (_widgetsBusy) return;
        _widgetsSyncing = true;
        WidgetsSwitch.IsOn = registered;
        _widgetsSyncing = false;
        WidgetsSwitch.IsEnabled = developer || registered;
        WidgetsTitle.Opacity = WidgetsSwitch.IsEnabled ? 1 : 0.6;
        DeveloperSettingsLink.Visibility = developer ? Visibility.Collapsed : Visibility.Visible;
        WidgetsHint.Text = error ?? (developer, registered) switch
        {
            (true, true) => L("Flotte und Solo-Chance im Widget-Board (Windows-Taste + W, dann „+“).",
                "Fleet and solo chance on the widget board (Windows key + W, then “+”)."),
            (true, false) => L("Zeigt Flotte und Solo-Chance im Widget-Board von Windows.",
                "Shows fleet and solo chance on the Windows widget board."),
            (false, true) => L("Der Entwicklermodus ist aus: Windows zeigt die Widgets erst wieder, wenn er an ist. Ausschalten geht trotzdem.",
                "Developer Mode is off: Windows shows the widgets again once it is on. Switching them off still works."),
            _ => L("Nur mit dem Entwicklermodus von Windows möglich: MinerGuru ist nicht signiert, und nur so lässt Windows Widgets einer solchen App zu. Er wird unter System › Für Entwickler eingeschaltet.",
                "Only possible with Windows Developer Mode: MinerGuru is not signed, and only then does Windows allow widgets from such an app. Turn it on under System › For developers."),
        };
    }

    private async void WidgetsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _widgetsSyncing || _widgetsBusy) return;
        var on = WidgetsSwitch.IsOn;
        _widgetsBusy = true;
        WidgetsSwitch.IsEnabled = false;
        WidgetsHint.Text = on ? L("Wird eingerichtet …", "Setting up …") : L("Wird entfernt …", "Removing …");
        string? error = null;
        try
        {
            if (on) await Widgets.WidgetPackage.RegisterAsync();
            else await Widgets.WidgetPackage.RemoveAsync();
            App.Store.WritesWidgetSnapshot = on;
            DiagnosticLog.Shared.Write("widgets", on ? "registered" : "removed");
        }
        catch (Exception ex)
        {
            error = L("Windows hat abgelehnt: ", "Windows refused: ") + ex.Message;
            DiagnosticLog.Shared.Write("widgets", (on ? "register" : "remove") + " failed: " + ex.Message);
        }
        _widgetsBusy = false;
        ShowWidgetState(error);
    }

    private async void DeveloperSettings_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:developers"));

    private void SyncAlertCards() => OfflineAlertCard.Visibility = AlertsSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;

    private async void ExportLog_Click(object sender, RoutedEventArgs e) =>
        await Controls.PageParts.SaveFileAsync("minerguru-log.txt", L("Textdatei", "Text file"), ".txt", System.Text.Encoding.UTF8.GetBytes(DiagnosticLog.Shared.ExportText()));

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{App.Store.DataDirectory}\"") { UseShellExecute = true });

    private async void Feedback_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri(MinerGuru.Core.Models.AppInfo.FeedbackUrl));

    private async void License_Click(object sender, RoutedEventArgs e) =>
        await new MinerGuru.Dialogs.LicenseDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme }.ShowAsync();
}
