using ConnectionPool.Core;
using Xunit;

namespace ConnectionPool.Core.Tests;

/// <summary>
/// Verifies docs/adr/0007 (#2): pool shutdown does not leak leases that are still in flight, and
/// AcquireAsync correctly rejects use after disposal.
/// </summary>
public class PoolShutdownTests
{
    [Fact]
    public async Task AcquireAsync_Throws_AfterPoolDisposed()
    {
        var policy = new FakePolicy();
        var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1 });
        await pool.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.AcquireAsync());
    }

    [Fact]
    public async Task LeaseReturnedAfterPoolDisposed_IsDisposedDirectly_NotReIdled()
    {
        var policy = new FakePolicy();
        var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1 });

        var lease = await pool.AcquireAsync();
        var resource = lease.Resource;

        await pool.DisposeAsync(); // pool now marked disposed; drain window will not see this lease
        await lease.DisposeAsync(); // "late" return after shutdown

        Assert.True(resource.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_DisposesIdleResources()
    {
        var policy = new FakePolicy();
        var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1, PrewarmCount = 1 });
        await pool.WarmupAsync();

        await pool.DisposeAsync();

        Assert.Equal(1, policy.DisposeCallCount);
    }
}
