using ConnectionPool.Dataverse;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Verifies docs/adr/0008's throttle-state bookkeeping on <see cref="DataverseUserPool"/>:
/// extend-never-shorten semantics, and that throttling never touches pool health/recycling.
/// Deliberately never calls AcquireAsync/WarmupAsync(PrewarmCount > 0) - see
/// RoundRobinSlotSelectionStrategyTests for why (no real Dataverse connection in unit tests).
/// </summary>
public class DataverseUserPoolThrottleStateTests
{
    [Fact]
    public void IsThrottled_False_ByDefault()
    {
        var pool = new DataverseUserPool("user-a", "dummy");

        Assert.False(pool.IsThrottled);
        Assert.Null(pool.ThrottledUntil);
    }

    [Fact]
    public void ReportThrottled_SetsIsThrottledTrue_UntilWindowExpires()
    {
        var pool = new DataverseUserPool("user-a", "dummy");

        pool.ReportThrottled(TimeSpan.FromMilliseconds(50));

        Assert.True(pool.IsThrottled);
        Assert.NotNull(pool.ThrottledUntil);
        Assert.True(pool.ThrottledUntil > DateTimeOffset.UtcNow);

        Thread.Sleep(100);

        Assert.False(pool.IsThrottled);
    }

    [Fact]
    public void ReportThrottled_ExtendsWindow_NeverShortensIt()
    {
        var pool = new DataverseUserPool("user-a", "dummy");

        pool.ReportThrottled(TimeSpan.FromSeconds(30));
        var firstUntil = pool.ThrottledUntil;

        // A shorter report arriving later (e.g. a stale/racing signal) must not shorten the window.
        pool.ReportThrottled(TimeSpan.FromSeconds(1));

        Assert.Equal(firstUntil, pool.ThrottledUntil);

        // A longer report must extend it.
        pool.ReportThrottled(TimeSpan.FromMinutes(5));
        Assert.True(pool.ThrottledUntil > firstUntil);
    }

    [Fact]
    public void ReportThrottled_DoesNotAffectPoolStats()
    {
        var pool = new DataverseUserPool("user-a", "dummy");
        var statsBefore = pool.GetStats();

        pool.ReportThrottled(TimeSpan.FromSeconds(30));

        var statsAfter = pool.GetStats();
        Assert.Equal(statsBefore, statsAfter); // throttling never recycles/marks unhealthy anything
    }
}
