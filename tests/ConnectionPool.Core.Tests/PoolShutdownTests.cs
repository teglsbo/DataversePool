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

    /// <summary>
    /// Regression test for a disposal race: <see cref="ResourcePool{T}.DisposeAsync"/> used to
    /// release whatever capacity permits it collected (from idle/unused capacity) back to
    /// <c>_capacityGate</c> *before* draining <c>_idle</c>. That let a concurrent
    /// <see cref="ResourcePool{T}.AcquireAsync"/> call - which had already passed the entry disposed
    /// check - win one of those transiently-released permits and pop a resource that was still
    /// sitting in <c>_idle</c> mid-teardown, handing out a lease from a pool that was disposing.
    /// Fixed by (a) draining <c>_idle</c> completely before releasing any collected permits, and
    /// (b) re-checking disposed state right after <see cref="ResourcePool{T}.AcquireAsync"/> wins a
    /// permit, so a concurrent acquirer that only gets a permit once disposal finishes still fails
    /// cleanly instead of receiving a resource.
    ///
    /// This test forces the interleaving deterministically rather than relying on real timing: a
    /// fake dispose delay pauses <see cref="ResourcePool{T}.DisposeAsync"/> mid-drain (after the
    /// first idle resource, before the second), and a concurrent acquire is issued while that pause
    /// is in effect. See docs/adr/0022.
    /// </summary>
    [Fact]
    public async Task AcquireAsync_ConcurrentWithDisposeAsync_NeverReturnsLeaseFromDisposedPool()
    {
        var policy = new FakePolicy();
        var disposePaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDisposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeCallsSeen = 0;

        policy.BeforeDisposeDelay = async _ =>
        {
            if (Interlocked.Increment(ref disposeCallsSeen) == 1)
            {
                firstDisposeStarted.TrySetResult();
                await disposePaused.Task; // held here until the test releases it below
            }
        };

        var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 2, PrewarmCount = 2 });
        await pool.WarmupAsync(); // two idle resources, both permits free, nothing leased

        // Starts disposal: collects both free permits (fixed code holds them until the drain below
        // is complete), then starts draining _idle - pops the first resource and blocks on our fake
        // dispose delay while still holding both permits.
        var disposeTask = pool.DisposeAsync().AsTask();
        await firstDisposeStarted.Task;

        // A concurrent acquirer issued while disposal is mid-drain must never obtain (or create) a
        // resource out of a disposing pool - whether it fails at the entry check or, had it managed
        // to win a permit mid-drain, at the post-permit re-check.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.AcquireAsync());

        disposePaused.SetResult(); // let DisposeAsync finish draining the rest of _idle
        await disposeTask;

        // Both idle resources must end up disposed exactly once - not leaked, not handed out.
        Assert.Equal(2, policy.DisposeCallCount);
    }
}
