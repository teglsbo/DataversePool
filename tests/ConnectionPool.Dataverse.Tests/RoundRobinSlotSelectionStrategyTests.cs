using ConnectionPool.Dataverse;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// These tests deliberately never call AcquireAsync/WarmupAsync(PrewarmCount > 0) on a real
/// DataverseUserPool, since that would attempt a genuine Dataverse connection. Constructing a
/// DataverseUserPool is cheap and side-effect free (connection is only established lazily on first
/// CreateAsync), so it is safe to use as a selection-target stand-in here.
/// </summary>
public class RoundRobinSlotSelectionStrategyTests
{
    [Fact]
    public void SelectNext_CyclesThroughMembers_InOrder()
    {
        var members = new[]
        {
            new DataverseUserPool("user-a", "dummy-connection-a"),
            new DataverseUserPool("user-b", "dummy-connection-b"),
            new DataverseUserPool("user-c", "dummy-connection-c"),
        };
        var stats = members.Select(m => m.GetStats()).ToArray();
        var strategy = new RoundRobinSlotSelectionStrategy();

        var selections = Enumerable.Range(0, 6)
            .Select(_ => strategy.SelectNext(members, stats).Member.Name)
            .ToArray();

        Assert.Equal(new[] { "user-a", "user-b", "user-c", "user-a", "user-b", "user-c" }, selections);
    }

    [Fact]
    public void SelectNext_Throws_WhenNoMembers()
    {
        var strategy = new RoundRobinSlotSelectionStrategy();
        Assert.Throws<InvalidOperationException>(
            () => strategy.SelectNext(Array.Empty<DataverseUserPool>(), Array.Empty<ConnectionPool.Core.PoolStats>()));
    }

    /// <summary>
    /// Unlike <see cref="LeastConnectionsSlotSelectionStrategy"/> and
    /// <see cref="HealthAwareRoundRobinSlotSelectionStrategy"/>, plain round-robin has no concept of
    /// a Dataverse throttle report at all (see docs/adr/0008) - it keeps cycling through every
    /// member strictly in order, including one that was just reported throttled. This is the
    /// documented trade-off: round-robin will keep sending 1/N of new traffic to a member that is
    /// currently over its own Dataverse request budget.
    /// </summary>
    [Fact]
    public void SelectNext_KeepsSelectingAThrottledMember_UnlikeThrottleAwareStrategies()
    {
        var members = new[]
        {
            new DataverseUserPool("user-a", "dummy-connection-a"),
            new DataverseUserPool("user-b", "dummy-connection-b"),
        };
        members[0].ReportThrottled(TimeSpan.FromMinutes(5));

        var stats = members.Select(m => m.GetStats()).ToArray();
        var strategy = new RoundRobinSlotSelectionStrategy();

        var selections = Enumerable.Range(0, 4)
            .Select(_ => strategy.SelectNext(members, stats).Member.Name)
            .ToArray();

        Assert.Equal(new[] { "user-a", "user-b", "user-a", "user-b" }, selections);
    }
}
