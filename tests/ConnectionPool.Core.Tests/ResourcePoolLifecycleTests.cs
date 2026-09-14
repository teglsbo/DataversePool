using ConnectionPool.Core;
using Xunit;

namespace ConnectionPool.Core.Tests;

public class ResourcePoolLifecycleTests
{
    [Fact]
    public async Task AcquireAsync_CreatesResourceLazily_WhenPoolEmpty()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 2 });

        await using var lease = await pool.AcquireAsync();

        Assert.NotNull(lease.Resource);
        Assert.Equal(1, policy.CreateCallCount);
    }

    [Fact]
    public async Task DisposeAsync_ReturnsResourceForReuse_ByNextAcquire()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1 });

        var lease1 = await pool.AcquireAsync();
        var reusedId = lease1.Resource.Id;
        await lease1.DisposeAsync();

        await using var lease2 = await pool.AcquireAsync();

        Assert.Equal(reusedId, lease2.Resource.Id);
        Assert.Equal(1, policy.CreateCallCount); // still only ever created once
    }

    [Fact]
    public async Task DisposeAsync_InvokesPolicyOnReturned_BeforeResourceIsReIdled()
    {
        // See docs/adr/0009: this hook exists so a policy can scrub per-lease mutable state
        // (e.g. Dataverse's CallerId impersonation field) before the next, unrelated caller
        // acquires the same underlying resource.
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1 });

        var lease = await pool.AcquireAsync();
        Assert.Equal(0, policy.OnReturnedCallCount);

        await lease.DisposeAsync();

        Assert.Equal(1, policy.OnReturnedCallCount);
    }

    [Fact]
    public async Task AcquireAsync_BlocksBeyondMaxSize_UntilReleased()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1 });

        var lease1 = await pool.AcquireAsync();

        var secondAcquireTask = pool.AcquireAsync();
        await Task.Delay(50);
        Assert.False(secondAcquireTask.IsCompleted, "Second acquire should block while MaxSize=1 is exhausted.");

        await lease1.DisposeAsync();

        var lease2 = await secondAcquireTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(lease2.Resource);
        await lease2.DisposeAsync();
    }

    [Fact]
    public async Task WarmupAsync_CreatesConfiguredNumberOfResources_UpFront()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 5, PrewarmCount = 3 });

        await pool.WarmupAsync();

        Assert.Equal(3, policy.CreateCallCount);
        var stats = pool.GetStats();
        Assert.Equal(3, stats.IdleCount);
        Assert.Equal(3, stats.CreatedCount);
    }
}
