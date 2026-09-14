using ConnectionPool.Core;
using Xunit;

namespace ConnectionPool.Core.Tests;

/// <summary>
/// Verifies docs/adr/0007 (#1, #5): late/misused MarkUnhealthy calls after dispose are ignored, and
/// idle resources older than MaxIdleLifetime are proactively recycled on checkout.
/// </summary>
public class DefensiveBehaviorTests
{
    [Fact]
    public async Task MarkUnhealthy_AfterDispose_DoesNotThrow_AndIsIgnored()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1 });

        var lease = await pool.AcquireAsync();
        await lease.DisposeAsync();

        // Should be a silent no-op rather than corrupting whichever slot state now exists.
        var ex = Record.Exception(() => lease.MarkUnhealthy(new InvalidOperationException("too late")));
        Assert.Null(ex);
    }

    [Fact]
    public async Task AcquireAsync_RecyclesIdleResource_OlderThanMaxIdleLifetime()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(
            policy,
            new PoolOptions { MaxSize = 1, MaxIdleLifetime = TimeSpan.FromMilliseconds(50) });

        var lease1 = await pool.AcquireAsync();
        var originalId = lease1.Resource.Id;
        await lease1.DisposeAsync();

        await Task.Delay(150); // exceed MaxIdleLifetime while idle

        await using var lease2 = await pool.AcquireAsync();

        Assert.NotEqual(originalId, lease2.Resource.Id);
        Assert.Equal(2, policy.CreateCallCount);
        Assert.Equal(1, policy.DisposeCallCount);
    }
}
