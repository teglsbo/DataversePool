using ConnectionPool.Core;
using Xunit;

namespace ConnectionPool.Core.Tests;

/// <summary>
/// Verifies docs/adr/0011: a leaked (GC'd, undisposed) lease is reported off the CLR finalizer
/// thread, and a faulty OnLeakDetected callback / HealthChanges observer can never (a) crash the
/// process via an unhandled exception on the finalizer thread, or (b) prevent the slot from still
/// being recycled.
/// </summary>
public class LeakReportingSafetyTests
{
    [Fact]
    public async Task LeakedLease_IsStillRecycled_EvenWhenOnLeakDetectedCallbackThrows()
    {
        var policy = new FakePolicy();
        var options = new PoolOptions
        {
            MaxSize = 1,
            OnLeakDetected = _ => throw new InvalidOperationException("faulty subscriber"),
        };
        await using var pool = new ResourcePool<FakeResource>(policy, options);

        var originalId = await LeakALeaseAsync(pool);

        ForceFinalization();

        FakeResource? newResource = null;
        for (var i = 0; i < 100 && newResource is null; i++)
        {
            await using var probe = await pool.AcquireAsync();
            if (probe.Resource.Id != originalId)
            {
                newResource = probe.Resource;
            }
            else
            {
                await Task.Delay(20);
            }
        }

        Assert.NotNull(newResource); // recycled despite the throwing callback - no capacity stranded
    }

    [Fact]
    public async Task LeakedLease_IsStillRecycled_EvenWhenHealthChangesObserverThrows()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1 });
        using var subscription = pool.HealthChanges.Subscribe(new ThrowingObserver());

        var originalId = await LeakALeaseAsync(pool);

        ForceFinalization();

        FakeResource? newResource = null;
        for (var i = 0; i < 100 && newResource is null; i++)
        {
            await using var probe = await pool.AcquireAsync();
            if (probe.Resource.Id != originalId)
            {
                newResource = probe.Resource;
            }
            else
            {
                await Task.Delay(20);
            }
        }

        Assert.NotNull(newResource); // recycled despite the throwing observer - no capacity stranded
    }

    // Isolated in its own method so the JIT doesn't keep the lease rooted for the rest of the
    // caller's stack frame - required for deterministic GC-triggered finalization in a test.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<int> LeakALeaseAsync(ResourcePool<FakeResource> pool)
    {
        var lease = await pool.AcquireAsync();
        return lease.Resource.Id; // deliberately never disposed - simulates a caller dropping the lease
    }

    private static void ForceFinalization()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class ThrowingObserver : IObserver<SlotHealthChanged>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(SlotHealthChanged value) => throw new InvalidOperationException("faulty observer");
    }
}
