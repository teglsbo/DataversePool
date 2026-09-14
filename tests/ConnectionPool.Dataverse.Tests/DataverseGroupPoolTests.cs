using ConnectionPool.Dataverse;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

public class DataverseGroupPoolTests
{
    [Fact]
    public void Constructor_Throws_WhenMembersEmpty()
    {
        Assert.Throws<ArgumentException>(() => new DataverseGroupPool(Array.Empty<DataverseUserPool>()));
    }

    [Fact]
    public async Task Members_ExposesAllConfiguredUserPools()
    {
        var a = new DataverseUserPool("user-a", "dummy-a");
        var b = new DataverseUserPool("user-b", "dummy-b");
        await using var group = new DataverseGroupPool(new[] { a, b });

        Assert.Equal(new[] { "user-a", "user-b" }, group.Members.Select(m => m.Name));

        await group.DisposeAsync(); // idempotent-safe double dispose exercised deliberately
    }

    [Fact]
    public async Task AcquireAsync_DefaultsToFailOpen_WhenAllMembersUnavailable()
    {
        // Default behavior (docs/adr/0007 #6, preserved by docs/adr/0010): a strategy reporting
        // "all unavailable" does NOT throw by default - the group pool still attempts the acquire
        // via whichever member the strategy picked.
        var a = new DataverseUserPool("user-a", "dummy-a");
        await using var group = new DataverseGroupPool(new[] { a }, new FakeAllUnavailableStrategy());

        // Acquiring from a dummy connection string will itself fail (no real Dataverse), but it
        // must fail via DataverseUserPool's own creation path, NOT via DataverseGroupUnavailableException -
        // proving the group pool did not short-circuit before attempting the acquire.
        await Assert.ThrowsAsync<ArgumentException>(() => group.AcquireAsync()); // dummy conn string fails inside DataverseServiceClientPolicy.CreateAsync, proving no short-circuit
    }

    [Fact]
    public async Task AcquireAsync_ThrowsGroupUnavailable_WhenFailFastConfigured_AndAllMembersUnavailable()
    {
        var a = new DataverseUserPool("user-a", "dummy-a");
        var b = new DataverseUserPool("user-b", "dummy-b");
        await using var group = new DataverseGroupPool(
            new[] { a, b }, new FakeAllUnavailableStrategy(), GroupAllUnavailableBehavior.FailFast);

        var ex = await Assert.ThrowsAsync<DataverseGroupUnavailableException>(() => group.AcquireAsync());

        Assert.Equal(new[] { "user-a", "user-b" }, ex.MemberNames);
        // No member is throttled in this scenario (only circuit-open, conceptually), so there is no
        // fixed earliest-retry instant to report.
        Assert.Null(ex.EarliestKnownRetryAt);
    }

    [Fact]
    public async Task AcquireAsync_FailFast_ReportsEarliestThrottleWindow_WhenMembersAreThrottled()
    {
        var a = new DataverseUserPool("user-a", "dummy-a");
        var b = new DataverseUserPool("user-b", "dummy-b");
        a.ReportThrottled(TimeSpan.FromSeconds(30));
        b.ReportThrottled(TimeSpan.FromSeconds(5)); // shorter window - this one should win as "earliest"

        await using var group = new DataverseGroupPool(
            new[] { a, b }, new FakeAllUnavailableStrategy(), GroupAllUnavailableBehavior.FailFast);

        var ex = await Assert.ThrowsAsync<DataverseGroupUnavailableException>(() => group.AcquireAsync());

        Assert.NotNull(ex.EarliestKnownRetryAt);
        Assert.Equal(b.ThrottledUntil, ex.EarliestKnownRetryAt);
    }
}
