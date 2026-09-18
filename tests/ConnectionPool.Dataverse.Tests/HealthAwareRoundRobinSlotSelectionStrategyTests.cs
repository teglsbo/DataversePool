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
        var strategy = new HealthAwareRoundRobinSlotSelectionStrategy(breaker);
        var stats = new[] { StatsWithFailures(5), StatsWithFailures(5), StatsWithFailures(5) };

        // First pass: opens all three circuits (not yet eligible - just opened this round).
        strategy.SelectNext(members, stats);
        Thread.Sleep(100); // exceed cooldown - all three are now half-open/eligible

        // Second pass: all three are eligible and each wins its own probe claim inside IsEligible,
        // but only one is ultimately selected.
        var selection = strategy.SelectNext(members, stats);
        var now = DateTimeOffset.UtcNow;

        foreach (var member in members)
        {
            var stillEligible = breaker.IsEligible(member, StatsWithFailures(5), now);
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
