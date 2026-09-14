using ConnectionPool.Core;
using ConnectionPool.Dataverse;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Verifies docs/adr/0007 (#6): a permanently failing ("dead") member is skipped by the group
/// selection strategy rather than continuing to receive 1/N of traffic, while still allowing
/// recovery (half-open) and never permanently locking out the whole group.
/// </summary>
public class HealthAwareRoundRobinSlotSelectionStrategyTests
{
    private static PoolStats StatsWithFailures(int consecutiveFailures) =>
        new(MaxSize: 4, CreatedCount: 0, IdleCount: 0, LeasedCount: 0,
            UnhealthyOrRecyclingCount: 0, WaitingCount: 0, ConsecutiveCreateFailures: consecutiveFailures);

    [Fact]
    public void SelectNext_SkipsDeadMember_OnceFailureThresholdReached()
    {
        var members = new[]
        {
            new DataverseUserPool("healthy-a", "dummy-a"),
            new DataverseUserPool("dead-b", "dummy-b"),
            new DataverseUserPool("healthy-c", "dummy-c"),
        };
        var strategy = new HealthAwareRoundRobinSlotSelectionStrategy(failureThreshold: 3, cooldownPeriod: TimeSpan.FromMinutes(5));

        // "dead-b" has failed 5 times in a row (>= threshold) - should never be selected while open.
        var stats = new[] { StatsWithFailures(0), StatsWithFailures(5), StatsWithFailures(0) };

        var selections = Enumerable.Range(0, 10)
            .Select(_ => strategy.SelectNext(members, stats).Member.Name)
            .ToArray();

        Assert.DoesNotContain("dead-b", selections);
        Assert.All(selections, name => Assert.Contains(name, new[] { "healthy-a", "healthy-c" }));
    }

    [Fact]
    public void SelectNext_RetriesMember_AfterCooldown_HalfOpen()
    {
        var members = new[]
        {
            new DataverseUserPool("healthy-a", "dummy-a"),
            new DataverseUserPool("recovering-b", "dummy-b"),
        };
        var strategy = new HealthAwareRoundRobinSlotSelectionStrategy(
            failureThreshold: 2, cooldownPeriod: TimeSpan.FromMilliseconds(50));

        var openStats = new[] { StatsWithFailures(0), StatsWithFailures(5) };

        // First call observes the failing member and opens its circuit (not yet eligible).
        var first = strategy.SelectNext(members, openStats);
        Assert.Equal("healthy-a", first.Member.Name);

        Thread.Sleep(100); // exceed cooldown

        // After cooldown, the previously-open member should become eligible again (half-open probe).
        var afterCooldown = Enumerable.Range(0, 4)
            .Select(_ => strategy.SelectNext(members, openStats).Member.Name)
            .ToArray();

        Assert.Contains("recovering-b", afterCooldown);
    }

    [Fact]
    public void SelectNext_FailsOpen_WhenAllMembersAreDead()
    {
        var members = new[]
        {
            new DataverseUserPool("dead-a", "dummy-a"),
            new DataverseUserPool("dead-b", "dummy-b"),
        };
        var strategy = new HealthAwareRoundRobinSlotSelectionStrategy(failureThreshold: 2, cooldownPeriod: TimeSpan.FromMinutes(5));
        var stats = new[] { StatsWithFailures(10), StatsWithFailures(10) };

        // Even with every member circuit-open, a selection must still be returned (fail open),
        // never an exception - the caller's own retry/circuit-breaker (e.g. Polly) decides what to
        // do next, the group pool itself must not deadlock or throw here.
        var selection = strategy.SelectNext(members, stats);
        Assert.Contains(selection.Member.Name, new[] { "dead-a", "dead-b" });
        Assert.True(selection.AllMembersUnavailable);
    }

    [Fact]
    public void SelectNext_SkipsThrottledMember_UntilWindowExpires()
    {
        var members = new[]
        {
            new DataverseUserPool("healthy-a", "dummy-a"),
            new DataverseUserPool("throttled-b", "dummy-b"),
        };
        members[1].ReportThrottled(TimeSpan.FromMilliseconds(50));

        var strategy = new HealthAwareRoundRobinSlotSelectionStrategy();
        var stats = new[] { StatsWithFailures(0), StatsWithFailures(0) };

        var whileThrottled = Enumerable.Range(0, 6)
            .Select(_ => strategy.SelectNext(members, stats).Member.Name)
            .ToArray();
        Assert.DoesNotContain("throttled-b", whileThrottled);

        Thread.Sleep(100); // exceed throttle window

        var afterExpiry = Enumerable.Range(0, 6)
            .Select(_ => strategy.SelectNext(members, stats).Member.Name)
            .ToArray();
        Assert.Contains("throttled-b", afterExpiry);
    }

    [Fact]
    public void SelectNext_FailsOpen_WhenAllMembersAreThrottled()
    {
        var members = new[]
        {
            new DataverseUserPool("throttled-a", "dummy-a"),
            new DataverseUserPool("throttled-b", "dummy-b"),
        };
        foreach (var member in members)
        {
            member.ReportThrottled(TimeSpan.FromMinutes(5));
        }

        var strategy = new HealthAwareRoundRobinSlotSelectionStrategy();
        var stats = new[] { StatsWithFailures(0), StatsWithFailures(0) };

        // Even with every member throttled, a selection must still be returned (fail open) -
        // never an exception/deadlock. The caller decides whether to wait, reject, etc.
        var selection = strategy.SelectNext(members, stats);
        Assert.Contains(selection.Member.Name, new[] { "throttled-a", "throttled-b" });
        Assert.True(selection.AllMembersUnavailable);
    }
}
