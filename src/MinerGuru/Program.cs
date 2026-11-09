using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MinerGuru;

/// <summary>
/// The entry point, ahead of the generated XAML start (DISABLE_XAML_GENERATED_MAIN), because the same exe has two
/// more jobs once the widgets are on (see Widgets/AppxManifest.template.xml):
/// <list type="bullet">
/// <item><c>-widgetprovider</c>: Windows starts the widget service.</item>
/// <item>With the widget package's identity: a tap on a widget opened <c>minerguru://open</c>. That start only hands
/// on to the normal MinerGuru, which must run without the package (its own single instance, its own data folder).</item>
/// </list>
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Any(a => a.Equals(Widgets.WidgetService.Argument, StringComparison.OrdinalIgnoreCase)))
            Widgets.WidgetService.Run(args);
        else if (HasPackageIdentity())
            HandOn();
        else
            XamlGeneratedProgram.XamlGeneratedMain();
    }

    /// <summary>
    /// Through Explorer, so the new process has no package identity (Explorer passes no arguments on, so MinerGuru
    /// opens as it is). The board gave this start the foreground; whichever MinerGuru opens may take it over.
    /// </summary>
    private static void HandOn()
    {
        AllowSetForegroundWindow(-1);
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Environment.ProcessPath}\"") { UseShellExecute = false });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static bool HasPackageIdentity()
    {
        var length = 0u;
        // APPMODEL_ERROR_NO_PACKAGE without identity, ERROR_INSUFFICIENT_BUFFER with one.
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
