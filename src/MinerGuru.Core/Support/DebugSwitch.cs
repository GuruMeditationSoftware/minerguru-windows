namespace MinerGuru.Core;

/// <summary>Test switches from the environment for the mock setup and the screenshots in the VM (MINERGURU_DATA_DIR,
/// MINERGURU_SCAN_EXTRA, MINERGURU_UPDATE_URL, MINERGURU_THEME, MINERGURU_OPEN). Only debug builds read them: in a release
/// build nothing in the environment redirects the data folder, the scan, the update check or the start page.</summary>
public static class DebugSwitch
{
    public static string? Get(string name)
    {
#if DEBUG
        return Environment.GetEnvironmentVariable(name);
#else
        return null;
#endif
    }

    /// <summary>Whether this build reads the switches at all.</summary>
    public static bool Enabled =>
#if DEBUG
        true;
#else
        false;
#endif
}
