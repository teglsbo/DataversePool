using ConnectionPool.Core;
using Xunit;

namespace ConnectionPool.Core.Tests;

/// <summary>Runtime <see cref="ResourcePool{T}.SetMaxSize"/> - see docs/adr/0025.</summary>
public class RuntimeResizeTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    private static ResourcePool<FakeResource> NewPool(FakePolicy policy, int maxSize) =>
        new(policy, new PoolOptions { MaxSize = maxSize, AcquireTimeout = null });

    [Fact]
    public async Task MaxSize_StartsAtOptionsValue_AndIsReflectedInStats()
    {
        await using var pool = NewPool(new FakePolicy(), 3);
        Assert.Equal(3, pool.MaxSize);

        pool.SetMaxSize(5);

        Assert.Equal(5, pool.MaxSize);
        Assert.Equal(5, pool.GetStats().MaxSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SetMaxSize_RejectsNonPositive(int value)
    {
        await using var pool = NewPool(new FakePolicy(), 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.SetMaxSize(value));
        Assert.Equal(2, pool.MaxSize);
    }

    [Fact]
    public async Task SetMaxSize_Throws_AfterDispose()
    {
        var pool = NewPool(new FakePolicy(), 2);
        await pool.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => pool.SetMaxSize(3));
    }

    [Fact]
    public async Task Grow_ReleasesBlockedWaiterImmediately()
    {
        await using var pool = NewPool(new FakePolicy(), 1);
        await using var held = await pool.AcquireAsync();

        var waiter = pool.AcquireAsync();
        await Task.Delay(Short);
        Assert.False(waiter.IsCompleted);

        pool.SetMaxSize(2);

        await using var second = await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, pool.GetStats().LeasedCount);
    }

    [Fact]
    public async Task Shrink_WithFreeCapacity_TakesEffectImmediately()
    {
        await using var pool = NewPool(new FakePolicy(), 3);
        pool.SetMaxSize(1);

        await using var only = await pool.AcquireAsync();
        var blocked = pool.AcquireAsync();
        await Task.Delay(Short);

        Assert.False(blocked.IsCompleted);
        await only.DisposeAsync();
        await using var next = await blocked.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Shrink_BelowInFlight_DoesNotCancelLeases_AndRetiresThemOnReturn()
    {
        var policy = new FakePolicy();
        await using var pool = NewPool(policy, 3);
        var leases = new[] { await pool.AcquireAsync(), await pool.AcquireAsync(), await pool.AcquireAsync() };
        var resources = leases.Select(l => l.Resource).ToArray(); // Resource is inaccessible after dispose

        pool.SetMaxSize(1);

        // All three stay usable; a new caller must wait until the pool is back under its new size.
        Assert.All(resources, r => Assert.False(r.Disposed));
        var waiter = pool.AcquireAsync();

        await leases[0].DisposeAsync();
        await Task.Delay(Short);
        Assert.False(waiter.IsCompleted); // 2 still out, limit is 1

        await leases[1].DisposeAsync();
        await Task.Delay(Short);
        Assert.False(waiter.IsCompleted); // 1 still out, limit is 1

        Assert.True(resources[0].Disposed); // retired, not re-idled
        Assert.True(resources[1].Disposed);

        await leases[2].DisposeAsync();
        await using var next = await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(resources[2], next.Resource); // the survivor is reused, not recreated
        Assert.Equal(1, pool.GetStats().CreatedCount);
    }

    [Fact]
    public async Task Shrink_TrimsIdleResourcesAboveNewSize()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 4, PrewarmCount = 4 });
        await pool.WarmupAsync();
        Assert.Equal(4, pool.GetStats().IdleCount);

        pool.SetMaxSize(1);

        await WaitUntil(() => pool.GetStats().CreatedCount == 1);
        Assert.Equal(1, pool.GetStats().IdleCount);
        Assert.Equal(3, policy.DisposeCallCount);
    }

    [Fact]
    public async Task GrowAfterShrink_ForgivesOutstandingDebt_InsteadOfOvershooting()
    {
        await using var pool = NewPool(new FakePolicy(), 2);
        var a = await pool.AcquireAsync();
        var b = await pool.AcquireAsync();

        pool.SetMaxSize(1); // owes 1 permit
        pool.SetMaxSize(2); // forgives it: nothing released, nothing owed

        var waiter = pool.AcquireAsync();
        await Task.Delay(Short);
        Assert.False(waiter.IsCompleted); // still 2 out of 2

        await a.DisposeAsync();
        await using var c = await waiter.WaitAsync(TimeSpan.FromSeconds(5));

        // Exactly 2 at a time: a fourth caller waits.
        var fourth = pool.AcquireAsync();
        await Task.Delay(Short);
        Assert.False(fourth.IsCompleted);
        await b.DisposeAsync();
        await using var d = await fourth.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConcurrentLoad_WithRandomResizes_NeverExceedsLimitOnceSettled()
    {
        var policy = new FakePolicy();
        await using var pool = NewPool(policy, 4);
        var inFlight = 0;
        var maxObservedAfterSettle = 0;
        var settled = 0;
        using var stop = new CancellationTokenSource();

        async Task Worker()
        {
            while (!stop.IsCancellationRequested)
            {
                await using var lease = await pool.AcquireAsync();
                var now = Interlocked.Increment(ref inFlight);
                if (Volatile.Read(ref settled) == 1)
                {
                    InterlockedMax(ref maxObservedAfterSettle, now);
                }

                await Task.Yield();
                Interlocked.Decrement(ref inFlight);
            }
        }

        var workers = Enumerable.Range(0, 16).Select(_ => Task.Run(Worker)).ToArray();
        var random = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            pool.SetMaxSize(random.Next(1, 9));
            await Task.Yield();
        }

        pool.SetMaxSize(3);
        await Task.Delay(200); // let pre-shrink leases drain
        Volatile.Write(ref settled, 1);
        await Task.Delay(300);
        stop.Cancel();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.InRange(maxObservedAfterSettle, 1, 3);
        Assert.True(pool.GetStats().CreatedCount <= 3);
    }

    [Fact]
    public async Task SuccessfulRecycle_DoesNotInflateCreatedCount()
    {
        // Regression: a successful recycle used to count the replacement without un-counting the
        // disposed original, so CreatedCount crept up by one per recycle.
        await using var pool = NewPool(new FakePolicy(), 1);
        for (var i = 0; i < 3; i++)
        {
            var lease = await pool.AcquireAsync();
            lease.MarkUnhealthy(new InvalidOperationException("boom"));
            await lease.DisposeAsync();
            await using var next = await pool.AcquireAsync(); // waits for the background recycle
        }

        Assert.Equal(1, pool.GetStats().CreatedCount);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(10);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int initial;
        do
        {
            initial = target;
            if (value <= initial)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref target, value, initial) != initial);
    }
}
