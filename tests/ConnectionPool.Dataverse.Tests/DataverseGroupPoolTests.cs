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

    [Fact]
    public async Task AcquireAsync_FailFast_IgnoresExpiredThrottleTicks_WhenComputingEarliestRetry()
    {
        // docs/adr/0011: ThrottledUntil ticks are never cleared after expiry, so a stale tick from a
        // long-past throttle must not be reported as if it were still a valid future retry hint.
        var a = new DataverseUserPool("user-a", "dummy-a");
        var b = new DataverseUserPool("user-b", "dummy-b");
        a.ReportThrottled(TimeSpan.FromMilliseconds(10)); // will have expired by the time we assert
        b.ReportThrottled(TimeSpan.FromSeconds(30)); // still in the future

        await Task.Delay(30); // let a's throttle window elapse

        await using var group = new DataverseGroupPool(
            new[] { a, b }, new FakeAllUnavailableStrategy(), GroupAllUnavailableBehavior.FailFast);

        var ex = await Assert.ThrowsAsync<DataverseGroupUnavailableException>(() => group.AcquireAsync());

        Assert.NotNull(ex.EarliestKnownRetryAt);
        Assert.Equal(b.ThrottledUntil, ex.EarliestKnownRetryAt); // a's stale/expired tick is ignored
    }

    [Fact]
    public async Task ExecuteWithThrottleRetryAsync_Throws_WhenOperationIsNull()
    {
        var a = new DataverseUserPool("user-a", "dummy-a");
        await using var group = new DataverseGroupPool(new[] { a });

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => group.ExecuteWithThrottleRetryAsync<int>(null!));
    }

    [Fact]
    public async Task ExecuteWithThrottleRetryAsync_Throws_WhenMaxAttemptsNotPositive()
    {
        var a = new DataverseUserPool("user-a", "dummy-a");
        await using var group = new DataverseGroupPool(new[] { a });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => group.ExecuteWithThrottleRetryAsync((client, ct) => Task.FromResult(1), maxAttempts: 0));
    }

    [Fact]
    public async Task ExecuteWithThrottleRetryAsync_PropagatesAcquireFailure_WithoutInvokingOperation()
    {
        // Dummy connection strings fail inside DataverseServiceClientPolicy.CreateAsync (no real
        // Dataverse to connect to) - AcquireAsync itself throws before a lease is ever produced.
        // This proves ExecuteWithThrottleRetryAsync does not swallow/retry acquire-level failures -
        // only failures from `operation`, once a lease was actually acquired, are eligible for retry.
        var a = new DataverseUserPool("user-a", "dummy-a");
        await using var group = new DataverseGroupPool(new[] { a });

        var operationInvoked = false;

        await Assert.ThrowsAsync<ArgumentException>(() => group.ExecuteWithThrottleRetryAsync<int>((client, ct) =>
        {
            operationInvoked = true;
            return Task.FromResult(1);
        }));

        Assert.False(operationInvoked);
    }
}

