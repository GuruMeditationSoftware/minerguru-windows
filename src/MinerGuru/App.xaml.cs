using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using MinerGuru.Controls;
using MinerGuru.Core.Services;

namespace MinerGuru;

public partial class App : Application
{
    private MainWindow? _window;
    private TrayIcon? _tray;
    private TrayFlyoutWindow? _flyout;

    public App()
    {
        InitializeComponent();
    }

    private const int AsfwAny = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>The miners and their polling; created on the UI thread, so its events arrive there.</summary>
    public static MinerStore Store { get; private set; } = null!;

    public static MainWindow MainWindow => ((App)Current)._window!;

    /// <summary>Ends the app for real; closing the window may only hide it.</summary>
    public static void Quit()
    {
        var app = (App)Current;
        Store.StopPolling();
        app._tray?.Dispose();
        app.Exit();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // One app per user: a second start brings the running one forward, which may be sitting in the tray.
        var instance = AppInstance.FindOrRegisterForKey("main");
        if (!instance.IsCurrent)
        {
            // This start may come to the front (a widget, the Start menu); the running app may take that over.
            AllowSetForegroundWindow(AsfwAny);
            await instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit();
            return;
        }
        var queue = DispatcherQueue.GetForCurrentThread();
        instance.Activated += (_, _) => queue.TryEnqueue(() => _window?.ShowFromBackground());

        Store = new MinerStore(MinerStore.DefaultDataDirectory);
        if (Store.Language != null) MinerGuru.Core.Lang.English = Store.Language == "en";
        DiagnosticLog.Shared.Activate(Store.DataDirectory);
        ChangeLog.Shared.Activate(Store.DataDirectory);
        DiagnosticLog.Shared.Write("app", $"start version={System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} os={Environment.OSVersion.Version}");
        // Unhandled exceptions end up in crash.log in the data folder; the event log only names the XAML module.
        UnhandledException += (_, e) =>
        {
            try { File.AppendAllText(Path.Combine(Store.DataDirectory, "crash.log"), $"{DateTime.Now:O}\n{e.Exception}\n\n"); }
            catch (IOException) { }
            DiagnosticLog.Shared.Write("crash", $"{e.Exception.GetType().FullName}: see crash.log");
        };
        _window = new MainWindow();
        _window.Activate();
        Store.Otp.Prompt = (name, problem) => Dialogs.OtpCodeWindow.AskAsync(name, problem, ((FrameworkElement)_window.Content).ActualTheme);
        Store.ConfirmWrite = (name, fields, restarts) => Dialogs.ConfirmWriteWindow.AskAsync(name, fields, restarts, ((FrameworkElement)_window.Content).ActualTheme);
        _tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        Store.Alerts.Notifier = new WindowsNotifier(_tray, queue);
        _flyout = new TrayFlyoutWindow();
        _flyout.AppWindow.Hide();
        _tray.Invoked += () => _flyout.Toggle(_tray);
        Store.Changed += () => _tray.Tooltip = $"MinerGuru · {Store.TraySummaryText}";
        // The package lookup takes a moment: off the UI thread. A registration from another folder or version is renewed.
        _ = Task.Run(async () =>
        {
            if (!Widgets.WidgetPackage.Registered) return;
            Store.WritesWidgetSnapshot = true;
            await Widgets.WidgetPackage.RefreshAsync();
        });
        Store.StartPolling();
        Store.Updates.Start();
        if (Store.Miners.Count == 0 && Store.AutoScanOnEmptyLaunch) Store.Scanner.Start();
        if (MinerGuru.Core.DebugSwitch.Get("MINERGURU_OPEN") == "tray") _flyout.Open(_tray);
    }
}
