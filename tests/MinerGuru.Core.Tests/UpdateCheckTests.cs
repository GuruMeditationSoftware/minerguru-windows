using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class UpdateCheckTests
{
    private static string Json(string tag, bool prerelease = false) =>
        $$"""{"tag_name":"{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}}}""";

    private static UpdateChecker Checker(StoredSettings settings, string? body) =>
        new(settings, () => { }, "0.2.1", () => Task.FromResult(body));

    [Theory]
    [InlineData("v0.3.0", "0.3.0")]
    [InlineData("2.0", "2.0")]
    public void ReadsTheVersionOfARelease(string tag, string version) =>
        Assert.Equal(version, UpdateCheck.VersionFromResponse(Json(tag)));

    [Fact]
    public void IgnoresPrereleasesAndOtherTags()
    {
        Assert.Null(UpdateCheck.VersionFromResponse(Json("v2.0.0", prerelease: true)));
        Assert.Null(UpdateCheck.VersionFromResponse(Json("v2.0.0-beta")));
        Assert.Null(UpdateCheck.VersionFromResponse(Json("nightly")));
        Assert.Null(UpdateCheck.VersionFromResponse("not json"));
    }

    [Theory]
    [InlineData("1.10.0", "1.9.9", true)]
    [InlineData("0.2.2", "0.2.1", true)]
    [InlineData("0.3", "0.2.9", true)]
    [InlineData("0.2.1", "0.2.1", false)]
    [InlineData("0.2", "0.2.0", false)]
    [InlineData("0.1.9", "0.2.0", false)]
    public void ComparesNumbersNotText(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateCheck.IsNewer(candidate, current));

    [Fact]
    public async Task AnnouncesANewerVersionUntilDismissed()
    {
        var updates = Checker(new StoredSettings(), Json("v0.3.0"));
        await updates.CheckAsync();
        Assert.Equal("0.3.0", updates.BannerVersion);
        updates.DismissBanner();
        Assert.Null(updates.BannerVersion);
        Assert.Equal("0.3.0", updates.AvailableVersion);
    }

    [Fact]
    public async Task ALaterVersionShowsAgainAfterDismissing()
    {
        var settings = new StoredSettings();
        var first = Checker(settings, Json("v0.3.0"));
        await first.CheckAsync();
        first.DismissBanner();
        var second = Checker(settings, Json("v0.3.1"));
        await second.CheckAsync();
        Assert.Equal("0.3.1", second.BannerVersion);
    }

    [Fact]
    public async Task StaysQuietWhenCurrentOrTurnedOff()
    {
        var current = Checker(new StoredSettings(), Json("v0.2.1"));
        await current.CheckAsync();
        Assert.Null(current.AvailableVersion);
        var off = Checker(new StoredSettings { UpdateCheckEnabled = false }, Json("v0.3.0"));
        await off.CheckIfDueAsync(DateTime.UtcNow);
        Assert.Null(off.LatestVersion);
    }

    [Fact]
    public async Task AFailedCheckIsRetried()
    {
        var settings = new StoredSettings();
        var missing = Checker(settings, null);
        await missing.CheckIfDueAsync(DateTime.UtcNow);
        Assert.Equal(UpdateStatus.Failed, missing.Status);
        var next = Checker(settings, Json("v0.3.0"));
        await next.CheckIfDueAsync(DateTime.UtcNow);
        Assert.Equal("0.3.0", next.LatestVersion);
    }

    [Fact]
    public async Task AsksAtMostOnceADay()
    {
        var settings = new StoredSettings();
        var start = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        await Checker(settings, Json("v0.3.0")).CheckIfDueAsync(start);
        var later = Checker(settings, Json("v1.0.0"));
        await later.CheckIfDueAsync(start.AddHours(1));
        Assert.Equal("0.3.0", later.LatestVersion);
        await later.CheckIfDueAsync(start.Add(UpdateChecker.CheckInterval).AddMinutes(1));
        Assert.Equal("1.0.0", later.LatestVersion);
    }
}
