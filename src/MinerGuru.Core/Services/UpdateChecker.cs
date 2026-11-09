using System.Net.Http.Headers;
using System.Reflection;
using MinerGuru.Core.Models;

namespace MinerGuru.Core.Services;

public enum UpdateStatus { Idle, Checking, UpToDate, Failed }

/// <summary>Asks GitHub once a day whether a newer version exists. It only tells the user: nothing is downloaded or installed.</summary>
public sealed class UpdateChecker(StoredSettings settings, Action save, string? currentVersion = null, Func<Task<string?>>? fetch = null)
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    /// <summary>How often <see cref="Start"/> looks whether a check is due. A failed check is repeated at this pace.</summary>
    private static readonly TimeSpan WakeInterval = TimeSpan.FromHours(1);

    private readonly string _currentVersion = currentVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0";
    private readonly Func<Task<string?>> _fetch = fetch ?? FetchLatestReleaseAsync;

    public event Action? Changed;

    public UpdateStatus Status { get; private set; }

    public string? LatestVersion => settings.LatestVersion;

    public bool IsEnabled
    {
        get => settings.UpdateCheckEnabled;
        set { settings.UpdateCheckEnabled = value; save(); Changed?.Invoke(); }
    }

    /// <summary>A version newer than this app, if the last answer named one.</summary>
    public string? AvailableVersion =>
        settings.LatestVersion is { } latest && UpdateCheck.IsNewer(latest, _currentVersion) ? latest : null;

    /// <summary>The version the overview announces.</summary>
    public string? BannerVersion =>
        IsEnabled && AvailableVersion is { } version && version != settings.DismissedUpdateVersion ? version : null;

    public void Start() => _ = LoopAsync();

    private async Task LoopAsync()
    {
        while (true)
        {
            await CheckIfDueAsync(DateTime.UtcNow);
            await Task.Delay(WakeInterval);
        }
    }

    public async Task CheckIfDueAsync(DateTime now)
    {
        if (!IsEnabled) return;
        if (settings.LastUpdateCheck is { } last && last <= now && now - last < CheckInterval) return;
        await CheckAsync(now);
    }

    public Task CheckAsync() => CheckAsync(DateTime.UtcNow);

    private async Task CheckAsync(DateTime now)
    {
        if (Status == UpdateStatus.Checking) return;
        Status = UpdateStatus.Checking;
        Changed?.Invoke();
        string? version = null;
        try
        {
            if (await _fetch() is { } body) version = UpdateCheck.VersionFromResponse(body);
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        if (version == null)
        {
            Status = UpdateStatus.Failed;
        }
        else
        {
            settings.LatestVersion = version;
            settings.LastUpdateCheck = now;
            save();
            Status = UpdateStatus.UpToDate;
        }
        Changed?.Invoke();
    }

    public void DismissBanner()
    {
        settings.DismissedUpdateVersion = AvailableVersion;
        save();
        Changed?.Invoke();
    }

    private static async Task<string?> FetchLatestReleaseAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var url = DebugSwitch.Get("MINERGURU_UPDATE_URL") ?? AppInfo.UpdateCheckUrl;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0";
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MinerGuru", version));
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
    }
}
