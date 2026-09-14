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
}
