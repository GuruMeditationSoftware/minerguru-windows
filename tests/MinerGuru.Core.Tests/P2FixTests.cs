using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class CoinMismatchTests
{
    [Fact]
    public void ABchAddressOnABitcoinPoolWarns()
    {
        Assert.Equal((MinedCoin.Btc, MinedCoin.Bch), MinedCoinDetector.Mismatch("public-pool.io", "bitcoincash:qqqq.worker"));
        Assert.NotNull(MinedCoinDetector.MismatchWarning("public-pool.io", "bitcoincash:qqqq.worker"));
    }

    [Fact]
    public void ABitcoinAddressOnABchPoolWarns() =>
        Assert.Equal((MinedCoin.Bch, MinedCoin.Btc), MinedCoinDetector.Mismatch("bch.solo.ckpool.org", "bc1qabc.worker"));

    [Theory]
    [InlineData("public-pool.io", "bc1qabc.worker")]
    [InlineData("bch.solo.ckpool.org", "bitcoincash:qqqq.worker")]
    [InlineData("pool.example.com", "bitcoincash:qqqq.worker")]
    [InlineData("bch.solo.ckpool.org", "1BoatSLRHtKNngkdXEeobR76b53LETtpyT.worker")]
    [InlineData("public-pool.io", "3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy.worker")]
    public void FittingAndUnclearCombinationsStayQuiet(string host, string user) =>
        Assert.Null(MinedCoinDetector.Mismatch(host, user));
}

public class HistoryGapTests
{
    private static readonly DateTime Start = new(2026, 11, 5, 8, 0, 0);
    private static (DateTime, double) At(int minute) => (Start.AddMinutes(minute), 500);

    [Fact]
    public void AStretchWithoutRowsSplitsTheLine()
    {
        var runs = HistoryQuery.SplitAtGaps([At(0), At(1), At(10), At(11)], 60);
        Assert.Equal([2, 2], runs.Select(r => r.Count));
    }

    [Fact]
    public void SteadyRowsStayTogether() =>
        Assert.Single(HistoryQuery.SplitAtGaps([At(0), At(2), At(5)], 60));

    [Fact]
    public void NoValuesGiveNoRuns() => Assert.Empty(HistoryQuery.SplitAtGaps([], 60));
}

public class DamagedSettingsTests
{
    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mg-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    public void DamagedSettingsMoveAsideAndAreReported(string content)
    {
        var folder = Folder();
        var path = Path.Combine(folder, "settings.json");
        File.WriteAllText(path, content);

        var store = new MinerStore(folder);

        Assert.NotNull(store.DamagedSettingsBackup);
        Assert.Equal(content, File.ReadAllText(store.DamagedSettingsBackup));
        Assert.Empty(store.Miners);
    }

    [Fact]
    public void AMissingFileIsNoDamage() => Assert.Null(new MinerStore(Folder()).DamagedSettingsBackup);
}
