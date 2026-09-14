using ConnectionPool.Core;
using ConnectionPool.Dataverse;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Verifies the load-aware alternative to plain/health-aware round-robin: a member with fewer
/// currently-leased connections is preferred over one with more, and circuit-breaking behavior for
/// a persistently dead member matches HealthAwareRoundRobinSlotSelectionStrategy.
/// </summary>
public class LeastConnectionsSlotSelectionStrategyTests
{
    private static PoolStats Stats(int leasedCount, int consecutiveFailures = 0) =>
        new(MaxSize: 8, CreatedCount: 0, IdleCount: 0, LeasedCount: leasedCount,
            UnhealthyOrRecyclingCount: 0, WaitingCount: 0, ConsecutiveCreateFailures: consecutiveFailures);

    [Fact]
    public void SelectNext_PrefersMemberWithFewestLeasedConnections()
    {
        var members = new[]
        {
            new DataverseUserPool("busy-a", "dummy-a"),
            new DataverseUserPool("idle-b", "dummy-b"),
            new DataverseUserPool("medium-c", "dummy-c"),
        };
        var strategy = new LeastConnectionsSlotSelectionStrategy();
        var stats = new[] { Stats(leasedCount: 6), Stats(leasedCount: 1), Stats(leasedCount: 3) };

        var selections = Enumerable.Range(0, 5)
            .Select(_ => strategy.SelectNext(members, stats).Member.Name)
            .ToArray();

        Assert.All(selections, name => Assert.Equal("idle-b", name));
    }

    [Fact]
    public void SelectNext_BreaksTiesRoundRobin()
    {
        var members = new[]
        {
            new DataverseUserPool("a", "dummy-a"),
            new DataverseUserPool("b", "dummy-b"),
        };
        var strategy = new LeastConnectionsSlotSelectionStrategy();
        var stats = new[] { Stats(leasedCount: 2), Stats(leasedCount: 2) }; // tied load

        var selections = Enumerable.Range(0, 4)
            .Select(_ => strategy.SelectNext(members, stats).Member.Name)
            .ToArray();

        // Tied members should still alternate rather than always picking the same one.
        Assert.Contains("a", selections);
        Assert.Contains("b", selections);
    }

    [Fact]
    public void SelectNext_SkipsDeadMember_EvenIfItHasFewestLeasedConnections()
    {
        var members = new[]
        {
            new DataverseUserPool("dead-but-idle", "dummy-a"),
            new DataverseUserPool("healthy-busy", "dummy-b"),
        };
        var strategy = new LeastConnectionsSlotSelectionStrategy(failureThreshold: 3, cooldownPeriod: TimeSpan.FromMinutes(5));

        // dead-but-idle has 0 leased connections (would win on load alone) but is circuit-open.
        var stats = new[] { Stats(leasedCount: 0, consecutiveFailures: 5), Stats(leasedCount: 4) };

        var selections = Enumerable.Range(0, 5)
            .Select(_ => strategy.SelectNext(members, stats).Member.Name)
            .ToArray();

        Assert.All(selections, name => Assert.Equal("healthy-busy", name));
    }

    [Fact]
    public void SelectNext_FailsOpen_WhenAllMembersAreDead()
    {
        var members = new[]
        {
            new DataverseUserPool("dead-a", "dummy-a"),
            new DataverseUserPool("dead-b", "dummy-b"),
        };
        var strategy = new LeastConnectionsSlotSelectionStrategy(failureThreshold: 2, cooldownPeriod: TimeSpan.FromMinutes(5));
        var stats = new[] { Stats(leasedCount: 0, consecutiveFailures: 10), Stats(leasedCount: 0, consecutiveFailures: 10) };

        var selection = strategy.SelectNext(members, stats);
        Assert.Contains(selection.Member.Name, new[] { "dead-a", "dead-b" });
        Assert.True(selection.AllMembersUnavailable);
    }

    [Fact]
    public void SelectNext_SkipsThrottledMember_EvenIfItHasFewestLeasedConnections()
    {
        var members = new[]
        {
            new DataverseUserPool("throttled-but-idle", "dummy-a"),
            new DataverseUserPool("healthy-busy", "dummy-b"),
        };
        members[0].ReportThrottled(TimeSpan.FromMinutes(5));

        var strategy = new LeastConnectionsSlotSelectionStrategy();
        var stats = new[] { Stats(leasedCount: 0), Stats(leasedCount: 4) };

        var selections = Enumerable.Range(0, 5)
            .Select(_ => strategy.SelectNext(members, stats).Member.Name)
            .ToArray();

        Assert.All(selections, name => Assert.Equal("healthy-busy", name));
    }
}
