using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class AlertCenterTests : IDisposable
{
    public AlertCenterTests() => Lang.English = false;

    private readonly string _folder = Directory.CreateTempSubdirectory("mg-alerts").FullName;
    private readonly DateTime _start = new(2026, 11, 3, 12, 0, 0);
    private readonly Guid _id = Guid.NewGuid();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class Recorder : IAlertNotifier
    {
        public List<AlertEvent> Delivered { get; } = [];
        public void Deliver(AlertEvent alert) => Delivered.Add(alert);
    }

    private readonly Recorder _notifier = new();

    private AlertCenter Center(bool enabled = true)
    {
        var center = new AlertCenter(_folder) { Notifier = _notifier };
        center.IsEnabled = enabled;
        return center;
    }

    private MinerDevice Miner(ConnectionState state) => new(new PersistedMinerConfig { Id = _id, IpAddress = "192.168.1.50", CustomName = "Gamma" }, state);

    /// <summary>Polls every <paramref name="step"/> seconds with the given states and returns all alerts sent.</summary>
    private List<AlertEvent> Poll(AlertCenter center, ConnectionState[] states, int step = 60, int from = 0)
    {
        var sent = new List<AlertEvent>();
        for (var i = 0; i < states.Length; i++) sent.AddRange(center.Evaluate([Miner(states[i])], _start.AddSeconds(from + i * step)));
        return sent;
    }

    private const ConnectionState Off = ConnectionState.Offline;
    private const ConnectionState On = ConnectionState.Online;

    [Fact]
    public void OfflineAfterThreePollsAndRecovery()
    {
        var sent = Poll(Center(), [On, Off, Off, Off, On]);
        Assert.Equal([false, true], sent.Select(e => e.IsRecovery));
        Assert.Equal(2, _notifier.Delivered.Count);
        Assert.Equal("Gamma ist offline", sent[0].Title);
        Assert.Contains("seit 2 min", sent[0].Body);
    }

    [Fact]
    public void ShortOutageFromRestartIsIgnored() => Assert.Empty(Poll(Center(), [On, Off, Off, Off, Off, On], step: 5));

    [Fact]
    public void CheckingStateDoesNotCount() => Assert.Empty(Poll(Center(), [Off, ConnectionState.Checking, ConnectionState.Checking, Off]));

    [Fact]
    public void FlappingIsThrottled()
    {
        var center = Center();
        Assert.Equal(2, Poll(center, [Off, Off, Off, On]).Count);
        Assert.Empty(Poll(center, [Off, Off, Off, On], from: 300));
    }

    [Fact]
    public void AnOutageAfterTheRepeatIntervalAlertsAgain()
    {
        var center = Center();
        Assert.Equal(2, Poll(center, [Off, Off, Off, On]).Count);
        Assert.Equal(2, Poll(center, [Off, Off, Off, On], from: 2000).Count);
    }

    [Fact]
    public void MutedMinerAndDisabledKindAreSilent()
    {
        var center = Center();
        center.SetMuted(true, _id);
        Assert.Empty(Poll(center, [Off, Off, Off]));
        center.SetMuted(false, _id);
        center.Forget(_id);
        center.SetKind(AlertKind.Offline, false);
        Assert.Empty(Poll(center, [Off, Off, Off]));
        Assert.Empty(_notifier.Delivered);
    }

    [Fact]
    public void DisabledCenterSendsNothing() => Assert.Empty(Poll(Center(enabled: false), [Off, Off, Off, On]));

    [Fact]
    public void AlertsStartSwitchedOff() => Assert.False(new AlertCenter(_folder).IsEnabled);

    [Fact]
    public void SettingsArePersisted()
    {
        var center = Center();
        center.SetMuted(true, _id);
        center.SetKind(AlertKind.Offline, false);
        var reloaded = new AlertCenter(_folder);
        Assert.True(reloaded.IsEnabled);
        Assert.True(reloaded.IsMuted(_id));
        Assert.False(reloaded.IsKindEnabled(AlertKind.Offline));
    }

    [Fact]
    public void ForgettingAMinerClearsItsMute()
    {
        var center = Center();
        center.SetMuted(true, _id);
        center.Forget(_id);
        Assert.False(center.IsMuted(_id));
    }

    [Fact]
    public void WhatHappenedToOneMinerIsNotFoldedIntoAnAlarm()
    {
        var id = Guid.NewGuid();
        var alarm = new AlertEvent(AlertKind.Flatline, id, "alarm", "", false, "GT");
        var skipped = new AlertEvent(AlertKind.Flatline, id, "skipped", "", false, "GT", Alone: true);
        Assert.Equal([alarm, skipped], AlertCenter.Combine([alarm, skipped]));
    }

    [Fact]
    public void ConfirmationUsesWordsNotFieldNames()
    {
        foreach (var key in new[] { "overclockEnabled", "poolMode", "poolBalance", "primaryPoolIndex", "secondaryPoolIndex", "temptarget", "pidTargetTemp", "pools.1.stratumTLS", "pools.1.stratumProtocol" })
        {
            Assert.NotEqual(key, ChangeLog.Title(key));
            Assert.DoesNotContain("stratum", ChangeLog.Title(key));
        }
        Assert.Equal("Dual-Pool", ChangeLog.Shown("1", "poolMode"));
        Assert.Equal("1", ChangeLog.Shown("0", "primaryPoolIndex"));
        Assert.Equal("600", ChangeLog.Shown("600", "frequency"));
        Assert.EndsWith("–  → x".Replace("  ", " "), new ChangeField("frequency", null, "x").Summary);
    }

    [Fact]
    public void AlertsOfOnePollCombinePerDirection()
    {
        AlertEvent Alert(string name, bool recovery) => new(AlertKind.Offline, Guid.NewGuid(), name, "text", recovery, name);
        var single = Alert("A", false);
        Assert.Equal([single], AlertCenter.Combine([single]));

        var combined = AlertCenter.Combine([Alert("A", false), Alert("B", false), Alert("C", true)]);
        Assert.Equal(2, combined.Count);
        Assert.Equal("2 Miner sind offline", combined[0].Title);
        Assert.Equal("A, B", combined[0].Body);
        Assert.Equal(Guid.Empty, combined[0].MinerId);
        Assert.Equal("C", combined[1].Title);
    }

    [Fact]
    public void TraySummaryFollowsTheFleet()
    {
        var store = new MinerStore(_folder);
        Assert.Equal("MinerGuru", store.TraySummaryText);
        store.AddMiner("192.168.1.50");
        Assert.Equal("Offline", store.TraySummaryText);
    }
}
