using Microsoft.UI.Dispatching;
using MinerGuru.Controls;
using MinerGuru.Core.Services;

namespace MinerGuru;

/// <summary>
/// Alerts as notifications of the tray icon, which also work where the Windows App SDK's refuse: it has none for
/// elevated processes, and with UAC off every process is. A new one replaces the one on screen, so the alerts of
/// one poll go out together. A click opens the dashboard, on the miner if it is about one.
/// </summary>
internal sealed class WindowsNotifier : IAlertNotifier
{
    private readonly TrayIcon _tray;
    private readonly DispatcherQueue _queue;
    private readonly List<AlertEvent> _pending = [];
    private Guid? _shownMiner;

    public WindowsNotifier(TrayIcon tray, DispatcherQueue queue)
    {
        _tray = tray;
        _queue = queue;
        tray.NotificationClicked += () => App.MainWindow.ShowFromBackground(_shownMiner);
    }

    public void Deliver(AlertEvent alert)
    {
        _pending.Add(alert);
        if (_pending.Count == 1) _queue.TryEnqueue(Flush);
    }

    private void Flush()
    {
        foreach (var message in AlertCenter.Combine(_pending))
        {
            _shownMiner = message.MinerId == Guid.Empty ? null : message.MinerId;
            _tray.Notify(message.Title, message.Body, silent: message.IsRecovery);
        }
        _pending.Clear();
    }
}
