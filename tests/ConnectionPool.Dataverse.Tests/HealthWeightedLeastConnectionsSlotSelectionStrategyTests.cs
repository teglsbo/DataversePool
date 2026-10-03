using ConnectionPool.Core;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// docs/research/autoscaling.md §9.6 items 1-2: headroom ranking and the post-429 slow-start ramp.
/// The dop_hint cap needs a live ServiceClient response and is covered by the pure
/// <see cref="HealthWeightedLeastConnectionsSlotSelectionStrategy.ComputeRampCap"/> tests plus
/// the live strategy tests, not here.
/// </summary>
public class HealthWeightedLeastConnectionsSlotSelectionStrategyTests
{
    private static PoolStats Stats(int leased, int maxSize = 8, int consecutiveFailures = 0) =>
        new(MaxSize: maxSize, CreatedCount: 0, IdleCount: 0, LeasedCount: leased,
            UnhealthyOrRecyclingCount: 0, WaitingCount: 0, ConsecutiveCreateFailures: consecutiveFailures);

    [Fact]
    public void SelectNext_RanksByHeadroom_NotRawLeaseCount()
    {
        var members = new[] { new DataverseUserPool("big", "dummy-a"), new DataverseUserPool("small", "dummy-b") };
        var strategy = new HealthWeightedLeastConnectionsSlotSelectionStrategy();
        // big: 3/8 leased (0.625 headroom) vs small: 2/4 leased (0.5) - fewer leases is NOT better here.
        var stats = new[] { Stats(leased: 3, maxSize: 8), Stats(leased: 2, maxSize: 4) };

        var picks = Enumerable.Range(0, 5).Select(_ => strategy.SelectNext(members, stats).Member.Name);

        Assert.All(picks, name => Assert.Equal("big", name));
    }

    [Fact]
    public void SelectNext_BreaksTiesRoundRobin()
    {
        var members = new[] { new DataverseUserPool("a", "dummy-a"), new DataverseUserPool("b", "dummy-b") };
        var strategy = new HealthWeightedLeastConnectionsSlotSelectionStrategy();
        var stats = new[] { Stats(2), Stats(2) };

        var picks = Enumerable.Range(0, 4).Select(_ => strategy.SelectNext(members, stats).Member.Name).ToArray();

        Assert.Contains("a", picks);
        Assert.Contains("b", picks);
    }

    [Fact]
    public void SelectNext_SkipsThrottledMember()
    {
        var throttled = new DataverseUserPool("throttled", "dummy-a");
        throttled.ReportThrottled(TimeSpan.FromMinutes(5));
        var members = new[] { throttled, new DataverseUserPool("ok", "dummy-b") };
        var strategy = new HealthWeightedLeastConnectionsSlotSelectionStrategy();
        var stats = new[] { Stats(0), Stats(7) };

        var selection = strategy.SelectNext(members, stats);

        Assert.Equal("ok", selection.Member.Name);
        Assert.False(selection.AllMembersUnavailable);
    }

    [Fact]
    public void SelectNext_SkipsCircuitOpenMember()
    {
        var members = new[] { new DataverseUserPool("dead", "dummy-a"), new DataverseUserPool("ok", "dummy-b") };
        var strategy = new HealthWeightedLeastConnectionsSlotSelectionStrategy(
            failureThreshold: 2, cooldownPeriod: TimeSpan.FromMinutes(5));
        var stats = new[] { Stats(0, consecutiveFailures: 5), Stats(6) };

        var picks = Enumerable.Range(0, 3).Select(_ => strategy.SelectNext(members, stats).Member.Name);

        Assert.All(picks, name => Assert.Equal("ok", name));
    }

    [Fact]
    public void SelectNext_FailsOpen_WhenEveryMemberUnavailable()
    {
        var a = new DataverseUserPool("a", "dummy-a");
        var b = new DataverseUserPool("b", "dummy-b");
        a.ReportThrottled(TimeSpan.FromMinutes(5));
        b.ReportThrottled(TimeSpan.FromMinutes(5));
        var strategy = new HealthWeightedLeastConnectionsSlotSelectionStrategy();

        var selection = strategy.SelectNext(new[] { a, b }, new[] { Stats(5), Stats(1) });

        Assert.True(selection.AllMembersUnavailable);
        Assert.Equal("b", selection.Member.Name); // most headroom
    }

    [Fact]
    public void SelectNext_RampingMemberJustAfterThrottle_IsDeprioritisedVersusAHealthyOne()
    {
        // 'recovering' was throttled and its window just ended: cap = rampStart (2), already holding
        // 2 leases -> headroom 0. 'steady' has 4/8 -> headroom 0.5. Plain least-connections would
        // pick 'recovering' (2 < 4); the ramp must send traffic to 'steady' instead.
        var recovering = new DataverseUserPool("recovering", "dummy-a");
        recovering.ReportThrottled(TimeSpan.Zero);
        var steady = new DataverseUserPool("steady", "dummy-b");
        var strategy = new HealthWeightedLeastConnectionsSlotSelectionStrategy(
            rampStart: 2, rampStepInterval: TimeSpan.FromHours(1));
        var stats = new[] { Stats(2), Stats(4) };

        var picks = Enumerable.Range(0, 4)
            .Select(_ => strategy.SelectNext(new[] { recovering, steady }, stats).Member.Name);

        Assert.All(picks, name => Assert.Equal("steady", name));
    }

    [Fact]
    public void SelectNext_NeverThrottledMember_IsNotRamped()
    {
        var members = new[] { new DataverseUserPool("a", "dummy-a"), new DataverseUserPool("b", "dummy-b") };
        var strategy = new HealthWeightedLeastConnectionsSlotSelectionStrategy(rampStart: 1);
        var stats = new[] { Stats(5), Stats(1) };

        Assert.Equal("b", strategy.SelectNext(members, stats).Member.Name);
        Assert.Equal(8, strategy.EffectiveCap(members[0], stats[0], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ComputeRampCap_NeverThrottled_ReturnsFullCap()
    {
        Assert.Equal(8, HealthWeightedLeastConnectionsSlotSelectionStrategy.ComputeRampCap(
            null, DateTimeOffset.UtcNow, 2, TimeSpan.FromSeconds(5), 8));
    }

    [Fact]
    public void ComputeRampCap_StillInsideWindow_ReturnsFullCap()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(8, HealthWeightedLeastConnectionsSlotSelectionStrategy.ComputeRampCap(
            now.AddSeconds(10), now, 2, TimeSpan.FromSeconds(5), 8));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(12, 4)]
    [InlineData(30, 8)]
    [InlineData(3600, 8)]
    public void ComputeRampCap_GrowsOnePerStep_AndCapsAtFull(int secondsSinceRecovery, int expected)
    {
        var now = DateTimeOffset.UtcNow;
        var recoveredAt = now.AddSeconds(-secondsSinceRecovery);

        Assert.Equal(expected, HealthWeightedLeastConnectionsSlotSelectionStrategy.ComputeRampCap(
            recoveredAt, now, rampStart: 2, step: TimeSpan.FromSeconds(5), fullCap: 8));
    }

    [Fact]
    public void ComputeRampCap_NeverExceedsFullCap_WhenRampStartIsAboveIt()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(3, HealthWeightedLeastConnectionsSlotSelectionStrategy.ComputeRampCap(
            now.AddSeconds(-1), now, rampStart: 10, step: TimeSpan.FromSeconds(5), fullCap: 3));
    }

    [Fact]
    public void Breaker_IsExposedForMetrics()
    {
        var strategy = new HealthWeightedLeastConnectionsSlotSelectionStrategy();

        Assert.NotNull(strategy.Breaker);
    }

    [Fact]
    public void Constructor_Throws_OnInvalidRampSettings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HealthWeightedLeastConnectionsSlotSelectionStrategy(rampStart: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HealthWeightedLeastConnectionsSlotSelectionStrategy(rampStepInterval: TimeSpan.Zero));
    }
}
