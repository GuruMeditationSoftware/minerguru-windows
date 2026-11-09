using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class ParityTests
{
    [Fact]
    public void ScrambleKeepsTheShapeAndIsStable()
    {
        var scrambled = Privacy.Scramble("192.168.1.20");
        Assert.Equal(scrambled, Privacy.Scramble("192.168.1.20"));
        Assert.NotEqual("192.168.1.20", scrambled);
        Assert.Equal("...", new string(scrambled.Where(c => !char.IsDigit(c)).ToArray()));
    }

    [Fact]
    public void StratumUserKeepsTheWorker()
    {
        var user = Privacy.ScrambleStratumUser("bc1qexample.bitaxe");
        Assert.EndsWith(".bitaxe", user);
        Assert.False(user.StartsWith("bc1qexample"));
    }

    [Fact]
    public void FreeTextHidesAddressesOnly()
    {
        var line = Privacy.ScrambleFreeText("connected to 192.168.1.20 as bc1qexamplewalletaddressxxxxxxxxxxxx");
        Assert.StartsWith("connected to ", line);
        Assert.DoesNotContain("192.168.1.20", line);
        Assert.DoesNotContain("bc1qexample", line);
    }

    [Theory]
    [InlineData("I (123) asic_result: ID: 1, Nonce F0AE9D21 diff 1284.3 of 874.", 1284.3, 874.0, null)]
    [InlineData("I (123) asic_result: (Sec) Job ID: 78 diff 141114.6/3075/791M", 141114.6, 3075.0, 2)]
    public void ParsesShareLines(string line, double difficulty, double pool, int? slot)
    {
        var sample = ShareSample.Parse(line, DateTime.UnixEpoch)!;
        Assert.Equal(difficulty, sample.Difficulty);
        Assert.Equal(pool, sample.PoolDifficulty);
        Assert.Equal(slot, sample.Pool);
    }

    [Fact]
    public void IgnoresOtherLogLines() => Assert.Null(ShareSample.Parse("I (5) power: diff 12 of 3", DateTime.UnixEpoch));

    [Theory]
    [InlineData("E (1) wifi: lost", LogLevel.Error)]
    [InlineData("W (1) temp: high", LogLevel.Warning)]
    [InlineData("I (1) ok", LogLevel.Info)]
    [InlineData("plain text", LogLevel.Other)]
    public void ReadsTheLogLevel(string line, LogLevel level) => Assert.Equal(level, MinerLogStream.LevelOf(line));
}
