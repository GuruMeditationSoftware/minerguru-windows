using System.Runtime.InteropServices;

namespace MinerGuru.Controls;

/// <summary>
/// The icon in the notification area. WinUI has none, so this is the Win32 call with a hidden window for its
/// messages. Created on the UI thread, whose message loop serves that window.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8001;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;
    private const int NIN_BALLOONUSERCLICK = 0x0405;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NIIF_USER = 4, NIIF_NOSOUND = 0x10;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint IconId = 1;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;

    private readonly WndProc _wndProc;      // kept, the window calls it
    private readonly nint _window;
    private readonly nint _icon;
    private readonly uint _taskbarCreated;
    private string _tooltip = "MinerGuru";

    /// <summary>Left or right click, or Enter on the icon.</summary>
    public event Action? Invoked;

    /// <summary>The user clicked a notification.</summary>
    public event Action? NotificationClicked;

    public TrayIcon(string iconPath)
    {
        _wndProc = OnMessage;
        var module = GetModuleHandleW(null);
        var windowClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = module,
            lpszClassName = "MinerGuruTray",
        };
        RegisterClassExW(ref windowClass);
        // A hidden top-level window: a message-only one would miss the restart of Explorer.
        _window = CreateWindowExW(0, windowClass.lpszClassName, "", 0, 0, 0, 0, 0, 0, 0, module, 0);
        _icon = LoadImageW(0, iconPath, IMAGE_ICON, GetSystemMetrics(49), GetSystemMetrics(50), LR_LOADFROMFILE);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        Add();
    }

    public string Tooltip
    {
        get => _tooltip;
        set
        {
            if (value == _tooltip) return;
            _tooltip = value;
            var data = Data();
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
    }

    public void Notify(string title, string body, bool silent)
    {
        var data = Data();
        data.uFlags |= NIF_INFO;
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = body.Length > 255 ? body[..255] : body;
        data.dwInfoFlags = NIIF_USER | (silent ? NIIF_NOSOUND : 0);
        data.hBalloonIcon = _icon;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>The icon on screen in physical pixels, <c>null</c> if Windows does not tell (it sits in the overflow).</summary>
    public (int Left, int Top, int Right, int Bottom)? Bounds
    {
        get
        {
            var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = _window, uID = IconId };
            if (Shell_NotifyIconGetRect(ref id, out var rect) != 0) return null;
            return (rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
    }

    public void Dispose()
    {
        var data = Data();
        Shell_NotifyIconW(NIM_DELETE, ref data);
        DestroyWindow(_window);
    }

    private void Add()
    {
        var data = Data();
        Shell_NotifyIconW(NIM_ADD, ref data);
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref data);
    }

    private NOTIFYICONDATAW Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window,
        uID = IconId,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
    };

    private nint OnMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == WM_APP_TRAY)
        {
            var trayEvent = (int)(lParam & 0xFFFF);
            if (trayEvent is NIN_SELECT or NIN_KEYSELECT or WM_CONTEXTMENU) Invoked?.Invoke();
            else if (trayEvent == NIN_BALLOONUSERCLICK) NotificationClicked?.Invoke();
            return 0;
        }
        if (message == _taskbarCreated)
        {
            Add();
            return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImageW(nint instance, string name, uint type, int width, int height, uint load);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
