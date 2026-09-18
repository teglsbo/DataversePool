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

    [Fact]
    public void SelectNext_AbandonsUnselectedProbeClaims_SoTheyAreNotBlockedUntilTimeout()
    {
        // Regression test: IsEligible is called once per half-open candidate scanned during
        // selection, and previously only the ultimately-selected candidate's claim was ever
        // completed/abandoned via ReportAcquireOutcome/ReportAcquireAbandoned - any other half-open
        // candidate that also won a probe claim but wasn't picked leaked its claim until
        // probeClaimTimeout expired, incorrectly blocking it from a fresh probe even though nothing
        // was actually in flight for it. Fixed by abandoning every non-selected candidate's claim
        // immediately after picking the winner. See docs/adr/0022.
        var members = new[]
        {
            new DataverseUserPool("half-open-a", "dummy-a"),
            new DataverseUserPool("half-open-b", "dummy-b"),
            new DataverseUserPool("half-open-c", "dummy-c"),
        };
        var breaker = new MemberCircuitBreaker(
            failureThreshold: 2, cooldownPeriod: TimeSpan.FromMilliseconds(50), probeClaimTimeout: TimeSpan.FromMinutes(5));
        var strategy = new LeastConnectionsSlotSelectionStrategy(breaker);
        var stats = new[] { Stats(leasedCount: 0, consecutiveFailures: 5), Stats(leasedCount: 0, consecutiveFailures: 5), Stats(leasedCount: 0, consecutiveFailures: 5) };

        // First pass: opens all three circuits (not yet eligible - just opened this round).
        strategy.SelectNext(members, stats);
        Thread.Sleep(100); // exceed cooldown - all three are now half-open/eligible, tied on load

        // Second pass: all three are eligible and each wins its own probe claim inside IsEligible,
        // but only one is ultimately selected.
        var selection = strategy.SelectNext(members, stats);
        var now = DateTimeOffset.UtcNow;

        foreach (var member in members)
        {
            var stillEligible = breaker.IsEligible(member, Stats(leasedCount: 0, consecutiveFailures: 5), now);
            if (member == selection.Member)
            {
                // The winner's claim is real and still held - a fresh probe must not be handed
                // out for the same member while its outcome is unresolved.
                Assert.False(stillEligible);
            }
            else
            {
                // Every non-selected half-open candidate must have had its claim abandoned, so a
                // fresh probe is immediately available again - not blocked for probeClaimTimeout.
                Assert.True(stillEligible, $"{member.Name} should not still be holding a leaked probe claim.");
            }
        }
    }
}
