# Screenshot of the MinerGuru window (or the whole screen if it is not running), in physical pixels.
# -Steps names a file of input steps run first, one per line, coordinates in window pixels of a shot:
#   click X Y | type TEXT | key enter|tab|escape|down|up|ctrlA|del | wheel X Y N | wait MS
# With -Desktop all of it refers to the whole screen and the app stays where it is (tray, notifications).
param([string]$Out = "$env:USERPROFILE\src\shot.png", [int]$Scroll = 0, [string]$Steps = "", [switch]$Desktop)
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
[void][Win]::SetProcessDpiAwarenessContext([IntPtr]-4)   # per monitor v2
$app = Get-Process MinerGuru -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($app -or $Desktop) {
    if ($Desktop) {
        $x = 0; $y = 0; $w = [Win]::GetSystemMetrics(0); $h = [Win]::GetSystemMetrics(1)
    } else {
    [void][Win]::ShowWindow($app.MainWindowHandle, 9)
    [void][Win]::SetForegroundWindow($app.MainWindowHandle)
    Start-Sleep -Milliseconds 700
    $r = New-Object Win+RECT
    [void][Win]::GetWindowRect($app.MainWindowHandle, [ref]$r)
    $x = $r.Left; $y = $r.Top; $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    }
    if ($Steps -and (Test-Path $Steps)) {
        Add-Type -AssemblyName System.Windows.Forms
        foreach ($line in Get-Content $Steps) {
            $p = $line.Trim() -split ' ', 2
            $a = if ($p.Count -gt 1) { $p[1] } else { '' }
            switch ($p[0]) {
                'click' { $c = $a -split ' '; [void][Win]::SetCursorPos($x + [int]$c[0], $y + [int]$c[1]); Start-Sleep -Milliseconds 350
                          [Win]::mouse_event(0x02, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 80; [Win]::mouse_event(0x04, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 500 }
                'wheel' { $c = $a -split ' '; [void][Win]::SetCursorPos($x + [int]$c[0], $y + [int]$c[1])
                          for ($i = 0; $i -lt [Math]::Abs([int]$c[2]); $i++) { [Win]::mouse_event(0x0800, 0, 0, $(if ([int]$c[2] -gt 0) { -120 } else { 120 }), [UIntPtr]::Zero); Start-Sleep -Milliseconds 40 }
                          Start-Sleep -Milliseconds 700 }
                'type'  { [System.Windows.Forms.SendKeys]::SendWait(($a -replace '([+^%~(){}\[\]])', '{$1}')); Start-Sleep -Milliseconds 300 }
                'key'   { $k = @{ enter = '{ENTER}'; tab = '{TAB}'; escape = '{ESC}'; down = '{DOWN}'; up = '{UP}'; ctrlA = '^a'; del = '{DEL}' }[$a]
                          [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 400 }
                'wait'  { Start-Sleep -Milliseconds ([int]$a) }
            }
        }
        Remove-Item $Steps
    }
    if ($Scroll -ne 0) {
        # Wheel over the page (right of the sidebar); positive scrolls down.
        [void][Win]::SetCursorPos($x + [int]($w * 0.6), $y + [int]($h * 0.5))
        for ($i = 0; $i -lt [Math]::Abs($Scroll); $i++) {
            [Win]::mouse_event(0x0800, 0, 0, $(if ($Scroll -gt 0) { -120 } else { 120 }), [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 40
        }
        Start-Sleep -Milliseconds 900
        [void][Win]::SetCursorPos($x + $w - 4, $y + [int]($h * 0.5))
        Start-Sleep -Milliseconds 300
    }
} else {
    $x = 0; $y = 0; $w = [Win]::GetSystemMetrics(0); $h = [Win]::GetSystemMetrics(1)
}
$img = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($img)
$g.CopyFromScreen($x, $y, 0, 0, $img.Size)
$img.Save($Out)
