using System.Diagnostics;
using Microsoft.Win32;
using MinerGuru.Core;
using MinerGuru.Core.Services;
using Windows.ApplicationModel;
using Windows.Management.Deployment;


namespace MinerGuru.Widgets;

/// <summary>
/// The widgets need a package identity, and without a signature Windows grants one only in Developer Mode: the app
/// folder itself is registered as a loose package (AppxManifest.xml next to the exe). MinerGuru only reads whether
/// Developer Mode is on; switching it is the user's call in Windows settings.
/// </summary>
internal static class WidgetPackage
{
    public const string Name = "MinerGuru.Widgets";
    public const string Publisher = "CN=MinerGuru";

    private static string Manifest => Path.Combine(AppContext.BaseDirectory, "AppxManifest.xml");

    /// <summary>The icons and previews ship with the app; a build without them has no widgets.</summary>
    public static bool IsAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, "Widgets", "Assets", "Provider.png"));

    /// <summary>The manifest for this version and language: the widget picker shows its names.</summary>
    private static string ManifestText()
    {
        using var stream = typeof(WidgetPackage).Assembly.GetManifestResourceStream("MinerGuru.Widgets.AppxManifest.template.xml")!;
        var template = new StreamReader(stream).ReadToEnd();
        var version = typeof(WidgetPackage).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);
        // A data folder of its own (mock setup) goes along: Windows starts the service without the app's environment.
        var arguments = WidgetService.Argument;
        if (MinerGuru.Core.DebugSwitch.Get("MINERGURU_DATA_DIR") is { Length: > 0 } custom)
            arguments += $" {WidgetService.DataDirArgument}\"{custom}\"";
        var text = template
            .Replace("{{ARGUMENTS}}", System.Security.SecurityElement.Escape(arguments))
            .Replace("{{LANGUAGE}}", Lang.English ? "en-us" : "de-de")
            .Replace("{{FLEET}}", Strings.Fleet)
            .Replace("{{FLEET_DESCRIPTION}}", Strings.HashratePowerAndStateOf)
            .Replace("{{SOLO}}", Strings.SoloChance)
            .Replace("{{SOLO_DESCRIPTION}}", Strings.YourOddsOfABlock);
        // Windows registers a development package again only under another version: the last part follows the
        // manifest's content, so a new language or data folder counts as one (ForceUpdateFromAnyVersion allows lower).
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        var revision = BitConverter.ToUInt16(hash, 0) % 65535;
        return text.Replace("{{VERSION}}", $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}.{revision}");
    }

    private static bool ManifestCurrent()
    {
        try { return File.ReadAllText(Manifest) == ManifestText(); }
        catch (IOException) { return false; }
    }

    public static bool DeveloperModeOn
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
                return key?.GetValue("AllowDevelopmentWithoutDevLicense") is int on && on == 1;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    public static Package? Installed
    {
        get
        {
            try { return new PackageManager().FindPackagesForUser("", Name, Publisher).FirstOrDefault(); }
            catch (Exception) { return null; }
        }
    }

    public static bool Registered => Installed != null;

    /// <summary>Windows answers within a second or two; a hanging deployment must not leave the switch waiting.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>Registers the app folder; throws with Windows' message when it refuses.</summary>
    public static async Task RegisterAsync()
    {
        if (!ManifestCurrent()) await File.WriteAllTextAsync(Manifest, ManifestText());
        StopServices();
        await DeployAsync($"Add-AppxPackage -Register {Quote(Manifest)} -ForceUpdateFromAnyVersion");
    }

    public static async Task RemoveAsync()
    {
        if (!Registered) return;
        StopServices();
        await DeployAsync($"Get-AppxPackage {Name} | Remove-AppxPackage");
    }

    /// <summary>
    /// The deployment runs in a hidden PowerShell: started by MinerGuru itself through PackageManager, a removal (and a
    /// registration that shuts apps down) stalled in the VM after it was done, and the switch never heard back.
    /// </summary>
    private static async Task DeployAsync(string command)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " +
            $"try {{ {command} -ErrorAction Stop; exit 0 }} catch {{ [Console]::Out.Write($_.Exception.Message); exit 1 }}" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(Patience); }
        catch (TimeoutException)
        {
            process.Kill();
            throw new InvalidOperationException(Strings.WindowsDoesNotAnswer);
        }
        if (process.ExitCode != 0) throw new InvalidOperationException(Clean(await output));
    }

    private static string Quote(string path) => "'" + path.Replace("'", "''") + "'";

    /// <summary>
    /// A widget service of the old registration: every other MinerGuru from this folder, since the app itself runs
    /// once per user. Windows starts the service again when the board needs it.
    /// </summary>
    private static void StopServices()
    {
        var self = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("MinerGuru"))
            using (process)
            {
                try
                {
                    if (process.Id != self && string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                        process.Kill();
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
    }

    /// <summary>
    /// At start: a registration that points at another folder, or at the manifest of another version or language, is
    /// renewed, so the widget board starts the MinerGuru that is installed now. Errors stay quiet.
    /// </summary>
    public static async Task RefreshAsync()
    {
        try
        {
            if (!IsAvailable || Installed is not { } package) return;
            var here = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var there = Path.TrimEndingDirectorySeparator(package.InstalledPath);
            var stale = !string.Equals(here, there, StringComparison.OrdinalIgnoreCase) || !ManifestCurrent();
            if (stale && DeveloperModeOn) await RegisterAsync();
        }
        catch (Exception e)
        {
            DiagnosticLog.Shared.Write("widgets", "refresh failed: " + e.Message);
        }
    }

    private static string Clean(string text)
    {
        var message = text.Trim();
        return message.Length > 400 ? message[..400] + " …" : message;
    }
}
