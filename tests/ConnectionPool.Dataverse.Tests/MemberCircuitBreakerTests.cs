using ConnectionPool.Core;
using ConnectionPool.Dataverse;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Verifies docs/adr/0010: only a single concurrent caller wins the half-open "probe" slot for a
/// given member per cooldown window - everyone else is treated as still-ineligible until that
/// probe's outcome is observable, instead of every waiting caller piling onto a just-recovering
/// member at once (the bug in the pre-ADR-0010 per-strategy implementation).
/// </summary>
public class MemberCircuitBreakerTests
{
    private static PoolStats StatsWithFailures(int consecutiveFailures) =>
        new(MaxSize: 4, CreatedCount: 0, IdleCount: 0, LeasedCount: 0,
            UnhealthyOrRecyclingCount: 0, WaitingCount: 0, ConsecutiveCreateFailures: consecutiveFailures);

    private static PoolStats StatsWithOperationalFailures(int consecutiveOperationalFailures) =>
        new(MaxSize: 4, CreatedCount: 0, IdleCount: 0, LeasedCount: 0,
            UnhealthyOrRecyclingCount: 0, WaitingCount: 0, ConsecutiveCreateFailures: 0,
            ConsecutiveOperationalFailures: consecutiveOperationalFailures);

    [Fact]
    public void IsEligible_False_WhenOperationalFailuresReachThreshold()
    {
        // docs/adr/0012: a member that creates fine but keeps failing operationally
        // (PooledLease.MarkUnhealthy) must also trip the circuit, not just creation failures.
        var breaker = new MemberCircuitBreaker(failureThreshold: 3, cooldownPeriod: TimeSpan.FromSeconds(30));
        var member = new DataverseUserPool("a", "dummy-a");

        Assert.False(breaker.IsEligible(member, StatsWithOperationalFailures(5), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsEligible_True_WhenCircuitClosed()
    {
        var breaker = new MemberCircuitBreaker(failureThreshold: 3, cooldownPeriod: TimeSpan.FromSeconds(30));
        var member = new DataverseUserPool("a", "dummy-a");

        Assert.True(breaker.IsEligible(member, StatsWithFailures(0), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsEligible_False_WhenJustOpened()
    {
        var breaker = new MemberCircuitBreaker(failureThreshold: 3, cooldownPeriod: TimeSpan.FromSeconds(30));
        var member = new DataverseUserPool("a", "dummy-a");

        Assert.False(breaker.IsEligible(member, StatsWithFailures(5), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsEligible_OnlyOneCallerWinsTheProbe_WhenManyCallersRaceAfterCooldown()
    {
        var breaker = new MemberCircuitBreaker(failureThreshold: 2, cooldownPeriod: TimeSpan.FromMilliseconds(20));
        var member = new DataverseUserPool("a", "dummy-a");
        var stats = StatsWithFailures(5);

        breaker.IsEligible(member, stats, DateTimeOffset.UtcNow); // opens the circuit
        Thread.Sleep(50); // exceed cooldown - member is now half-open

        var now = DateTimeOffset.UtcNow;
        var winners = 0;
        Parallel.For(0, 50, _ =>
        {
            if (breaker.IsEligible(member, stats, now))
            {
                Interlocked.Increment(ref winners);
            }
        });

        // This is the core fix: previously every one of these 50 concurrent callers would have
        // seen the half-open member as eligible simultaneously (thundering herd onto a connection
        // that's still trying to recover). Now exactly one wins the probe slot.
        Assert.Equal(1, winners);
    }

    [Fact]
    public void IsEligible_AllowsNewProbe_AfterProbeClaimTimeoutElapses()
    {
        var breaker = new MemberCircuitBreaker(
            failureThreshold: 2, cooldownPeriod: TimeSpan.FromMilliseconds(20), probeClaimTimeout: TimeSpan.FromMilliseconds(30));
        var member = new DataverseUserPool("a", "dummy-a");
        var stats = StatsWithFailures(5);

        breaker.IsEligible(member, stats, DateTimeOffset.UtcNow); // opens
        Thread.Sleep(30); // exceed cooldown
        Assert.True(breaker.IsEligible(member, stats, DateTimeOffset.UtcNow)); // first probe wins
        Assert.False(breaker.IsEligible(member, stats, DateTimeOffset.UtcNow)); // second caller denied

        Thread.Sleep(40); // exceed probe-claim timeout - the stale probe claim is abandoned

        Assert.True(breaker.IsEligible(member, stats, DateTimeOffset.UtcNow)); // fresh probe allowed
    }

    [Fact]
    public void IsEligible_ClosesCircuitImmediately_OnceFailuresDropBelowThreshold()
    {
        var breaker = new MemberCircuitBreaker(failureThreshold: 3, cooldownPeriod: TimeSpan.FromMinutes(5));
        var member = new DataverseUserPool("a", "dummy-a");

        breaker.IsEligible(member, StatsWithFailures(5), DateTimeOffset.UtcNow); // opens, long cooldown

        // Simulate the underlying pool recovering (e.g. a successful create resets the counter)
        // without waiting out the cooldown - the breaker should reflect that immediately.
        Assert.True(breaker.IsEligible(member, StatsWithFailures(0), DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_Throws_WhenCooldownPeriodNotPositive(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemberCircuitBreaker(failureThreshold: 3, cooldownPeriod: TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_Throws_WhenProbeClaimTimeoutNotPositive(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemberCircuitBreaker(failureThreshold: 3, cooldownPeriod: TimeSpan.FromSeconds(30),
                probeClaimTimeout: TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void CompleteProbe_Success_ClosesCircuitImmediately_WithoutWaitingForProbeClaimTimeout()
    {
        // docs/adr/0011: a real outcome should close the circuit right away, not rely on the next
        // stats snapshot or on the probe-claim timeout ever elapsing.
        var breaker = new MemberCircuitBreaker(failureThreshold: 2, cooldownPeriod: TimeSpan.FromMilliseconds(10),
            probeClaimTimeout: TimeSpan.FromMinutes(5));
        var member = new DataverseUserPool("a", "dummy-a");
        var stats = StatsWithFailures(5);

        breaker.IsEligible(member, stats, DateTimeOffset.UtcNow); // opens
        Thread.Sleep(20);
        Assert.True(breaker.IsEligible(member, stats, DateTimeOffset.UtcNow)); // wins the probe

        breaker.CompleteProbe(member, succeeded: true);

        // Even though memberStats still reports failures (a stale snapshot) and the long
        // probeClaimTimeout hasn't elapsed, the explicit success outcome should already have
        // cleared the breaker's bookkeeping so a fresh evaluation of the (now recovered) real stats
        // is trusted immediately.
        Assert.True(breaker.IsEligible(member, StatsWithFailures(0), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CompleteProbe_Failure_RestartsCooldownAndReleasesClaim_WithoutWaitingForProbeClaimTimeout()
    {
        // docs/adr/0011: a failed real attempt should re-arm a fresh cooldown/claim immediately,
        // closing the window where a stale claim would otherwise block a new probe until
        // probeClaimTimeout elapses.
        var cooldown = TimeSpan.FromMilliseconds(20);
        var breaker = new MemberCircuitBreaker(failureThreshold: 2, cooldownPeriod: cooldown,
            probeClaimTimeout: TimeSpan.FromMinutes(5));
        var member = new DataverseUserPool("a", "dummy-a");
        var stats = StatsWithFailures(5);

        breaker.IsEligible(member, stats, DateTimeOffset.UtcNow); // opens
        Thread.Sleep(30);
        Assert.True(breaker.IsEligible(member, stats, DateTimeOffset.UtcNow)); // wins the probe

        breaker.CompleteProbe(member, succeeded: false);

        Assert.False(breaker.IsEligible(member, stats, DateTimeOffset.UtcNow)); // fresh cooldown just started

        Thread.Sleep(30); // exceed the fresh cooldown
        Assert.True(breaker.IsEligible(member, stats, DateTimeOffset.UtcNow)); // new probe allowed promptly
    }

    [Fact]
    public void AbandonProbe_ReleasesClaim_WithoutRestartingCooldown()
    {
        // docs/adr/0013: unlike CompleteProbe(succeeded: false), abandoning a probe (e.g. because the
        // acquire hit a pool-wide capacity timeout unrelated to this member's health) must free the
        // claim so a fresh probe can be won immediately, WITHOUT restarting/extending the cooldown -
        // a capacity timeout is not evidence the member is unhealthy.
        var cooldown = TimeSpan.FromMilliseconds(20);
        var breaker = new MemberCircuitBreaker(failureThreshold: 2, cooldownPeriod: cooldown,
            probeClaimTimeout: TimeSpan.FromMinutes(5));
        var member = new DataverseUserPool("a", "dummy-a");
        var stats = StatsWithFailures(5);

        breaker.IsEligible(member, stats, DateTimeOffset.UtcNow); // opens
        Thread.Sleep(30);
        var firstProbeAt = DateTimeOffset.UtcNow;
        Assert.True(breaker.IsEligible(member, stats, firstProbeAt)); // wins the probe
        Assert.False(breaker.IsEligible(member, stats, firstProbeAt)); // second caller denied

        breaker.AbandonProbe(member);

        // A fresh probe can be won right away - no need to wait out another full cooldown window,
        // because AbandonProbe does not touch OpenedAt.
        Assert.True(breaker.IsEligible(member, stats, firstProbeAt));
    }
}
