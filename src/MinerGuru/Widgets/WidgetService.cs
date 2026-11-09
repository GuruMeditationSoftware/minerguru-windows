using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using MinerGuru.Core.Widgets;
using WinRT;

namespace MinerGuru.Widgets;

/// <summary>
/// The widget service: a windowless MinerGuru that Windows starts as a COM server for the widget board
/// (see AppxManifest.xml). It draws from the snapshot the app writes; with no app running it asks the miners itself,
/// without alerts, history or schedules, which stay the app's business.
/// </summary>
internal static class WidgetService
{
    public const string Argument = "-widgetprovider";
    /// <summary>Followed by the data folder, when it is not the usual one.</summary>
    public const string DataDirArgument = "-datadir=";
    public const string ClassId = "5EE96375-2213-4F19-96F0-447483026F91";

    private static readonly ManualResetEventSlim Done = new();

    public static void Run(string[] args)
    {
        if (args.FirstOrDefault(a => a.StartsWith(DataDirArgument, StringComparison.OrdinalIgnoreCase)) is { } dir)
            Environment.SetEnvironmentVariable("MINERGURU_DATA_DIR", dir[DataDirArgument.Length..]);
        // COM calls from the widget board arrive on any thread: the class object lives in the multithreaded apartment.
        var thread = new Thread(Serve) { Name = "widgets" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
    }

    private static void Serve()
    {
        Log.Write($"start {Environment.ProcessId}");
        try
        {
            ComWrappersSupport.InitializeComWrappers();
            Marshal.ThrowExceptionForHR(CoRegisterClassObject(Guid.Parse(ClassId), new Factory(), ClsctxLocalServer, RegclsMultipleUse, out var cookie));
            Provider.Shared.Recover();
            while (!Done.Wait(TimeSpan.FromSeconds(30)))
                if (!Provider.Shared.Tick()) break;
            CoRevokeClassObject(cookie);
        }
        catch (Exception e)
        {
            Log.Write("error " + e);
        }
        Log.Write("end");
    }

    public static void Stop() => Done.Set();

    private const uint ClsctxLocalServer = 4;
    private const uint RegclsMultipleUse = 1;

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject([MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("00000001-0000-0000-C000-000000000046")]
    private interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr obj);
        [PreserveSig] int LockServer(bool fLock);
    }

    [ComVisible(true)]
    private sealed class Factory : IClassFactory
    {
        public int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr obj)
        {
            obj = IntPtr.Zero;
            if (outer != IntPtr.Zero) return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
            var unknown = MarshalInspectable<IWidgetProvider>.FromManaged(Provider.Shared);
            try { return Marshal.QueryInterface(unknown, in riid, out obj); }
            finally { Marshal.Release(unknown); }
        }

        public int LockServer(bool fLock) => 0;
    }
}

/// <summary>The widgets the board shows and what they draw.</summary>
internal sealed partial class Provider : IWidgetProvider
{
    public static readonly Provider Shared = new();

    /// <summary>The app's snapshot counts as live this long; after that the service asks the miners itself.</summary>
    private static readonly TimeSpan AppSnapshotFresh = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan OwnPollEvery = TimeSpan.FromSeconds(60);
    /// <summary>With every widget hidden for this long, the service ends; Windows starts it again when needed.</summary>
    private static readonly TimeSpan IdleExit = TimeSpan.FromMinutes(10);

    private sealed class Widget(string definition, string size)
    {
        public string Definition = definition;
        public string Size = size;
        public bool Active;
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Widget> _widgets = [];
    private WidgetSnapshot? _polled;
    private DateTime _polledAt = DateTime.MinValue;
    private DateTime _lastActive = DateTime.Now;
    private readonly string _dataDirectory = MinerStore.DefaultDataDirectory;

    /// <summary>Widgets placed before this start of the service.</summary>
    public void Recover()
    {
        foreach (var info in WidgetManager.GetDefault().GetWidgetInfos() ?? [])
            lock (_lock) _widgets[info.WidgetContext.Id] = new Widget(info.WidgetContext.DefinitionId, SizeName(info.WidgetContext.Size)) { Active = info.WidgetContext.IsActive };
        Log.Write($"recovered {_widgets.Count}");
        Redraw();
    }

    /// <summary>Every 30 s: fresh figures for the visible widgets. <c>false</c> ends the service.</summary>
    public bool Tick()
    {
        lock (_lock)
        {
            if (_widgets.Count == 0) return false;
            if (_widgets.Values.Any(w => w.Active)) _lastActive = DateTime.Now;
            else if (DateTime.Now - _lastActive > IdleExit) return false;
            else return true;
        }
        Redraw();
        return true;
    }

    public void CreateWidget(WidgetContext context)
    {
        lock (_lock) _widgets[context.Id] = new Widget(context.DefinitionId, SizeName(context.Size)) { Active = true };
        Log.Write($"create {context.DefinitionId} {context.Size}");
        Redraw(context.Id);
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        bool none;
        lock (_lock)
        {
            _widgets.Remove(widgetId);
            none = _widgets.Count == 0;
        }
        if (none) WidgetService.Stop();
    }

    /// <summary>The cards have no Action.Execute: a tap opens <see cref="WidgetCards.OpenUrl"/>.</summary>
    public void OnActionInvoked(WidgetActionInvokedArgs args) { }

    public void OnWidgetContextChanged(WidgetContextChangedArgs args)
    {
        lock (_lock)
            if (_widgets.TryGetValue(args.WidgetContext.Id, out var w)) w.Size = SizeName(args.WidgetContext.Size);
        Redraw(args.WidgetContext.Id);
    }

    public void Activate(WidgetContext context)
    {
        lock (_lock)
        {
            if (!_widgets.TryGetValue(context.Id, out var w)) _widgets[context.Id] = w = new Widget(context.DefinitionId, SizeName(context.Size));
            w.Active = true;
            _lastActive = DateTime.Now;
        }
        Redraw(context.Id);
    }

    public void Deactivate(string widgetId)
    {
        lock (_lock)
            if (_widgets.TryGetValue(widgetId, out var w)) w.Active = false;
    }

    private static string SizeName(WidgetSize size) => size switch
    {
        WidgetSize.Small => "small",
        WidgetSize.Large => "large",
        _ => "medium",
    };

    /// <summary>Draws one widget, or all visible ones.</summary>
    private void Redraw(string? only = null)
    {
        var snapshot = Current();
        List<(string Id, Widget W)> targets;
        lock (_lock)
            targets = _widgets.Where(p => only == null ? p.Value.Active : p.Key == only).Select(p => (p.Key, p.Value)).ToList();
        if (targets.Count == 0) return;
        lock (_lock)
        {
            if (snapshot != null) Lang.English = snapshot.Language == "en";
            foreach (var (id, w) in targets)
            {
                var card = WidgetCards.Build(w.Definition, w.Size, snapshot, DateTime.Now);
                try { WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(id) { Template = card, Data = "{}" }); }
                catch (Exception e) { Log.Write($"update {w.Definition}: {e.Message}"); }
            }
        }
    }

    /// <summary>The app's snapshot while it runs; otherwise the miners asked directly, at most once a minute.</summary>
    private WidgetSnapshot? Current()
    {
        var file = WidgetSnapshot.Load(_dataDirectory);
        var now = DateTime.Now;
        if (file != null && now - file.Time < AppSnapshotFresh) return file;
        if (now - _polledAt >= OwnPollEvery)
        {
            _polledAt = now;
            try { _polled = PollAsync(now).GetAwaiter().GetResult() ?? _polled; }
            catch (Exception e) { Log.Write("poll " + e.Message); }
        }
        return new[] { file, _polled }.Where(s => s != null).MaxBy(s => s!.Time);
    }

    private async Task<WidgetSnapshot?> PollAsync(DateTime now)
    {
        var settingsPath = Path.Combine(_dataDirectory, "settings.json");
        if (!File.Exists(settingsPath)) return null;
        var settings = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(settingsPath)) ?? new StoredSettings();
        Lang.English = settings.Language is { } language ? language == "en" : System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "de";
        var miners = settings.Miners.Select(c => new MinerDevice(c, ConnectionState.Checking)).ToList();
        await Task.WhenAll(miners.Select(async m =>
        {
            try
            {
                var (info, _) = await MinerApiClient.Shared.FetchSystemInfoAsync(m.IpAddress, TimeSpan.FromSeconds(5));
                m.LatestInfo = info;
                m.State = ConnectionState.Online;
            }
            catch (MinerApiException)
            {
                m.State = ConnectionState.Offline;
            }
        }));
        // Away from home no miner answers: the last real figures stay, marked with their time.
        if (miners.Count > 0 && miners.All(m => m.State == ConnectionState.Offline)) return null;
        Log.Write($"polled {miners.Count(m => m.IsOnline)}/{miners.Count}");
        return WidgetSnapshot.Of(miners, settings.TempWarningThresholdC, settings.SoloChanceTimeframe, now);
    }
}

/// <summary>A small log of its own in the data folder; the app's logs belong to the app's process.</summary>
internal static class Log
{
    private static readonly Lock Gate = new();
    private static readonly string FilePath = Path.Combine(MinerStore.DefaultDataDirectory, "widgets", "service.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 64 * 1024) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
