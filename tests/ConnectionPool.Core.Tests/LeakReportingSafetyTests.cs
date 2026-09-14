using ConnectionPool.Core;
using Xunit;

namespace ConnectionPool.Core.Tests;

/// <summary>
/// Verifies docs/adr/0012: leak detection is diagnostic-only (log-only, like HikariCP). A leaked
/// (GC'd, undisposed) lease's resource is neither recycled nor disposed - the pool only reports it
/// via <see cref="PoolOptions.OnLeakDetected"/>/<see cref="ResourcePool{T}.HealthChanges"/>, off the
/// CLR finalizer thread, and a faulty callback/observer can neither crash the process nor prevent
/// the pool from continuing to serve other, still-healthy capacity.
/// </summary>
public class LeakReportingSafetyTests
{
    [Fact]
    public async Task LeakedLease_IsNeverDisposedOrRecycled_ButOtherCapacityRemainsUsable()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 2 });

        await LeakALeaseAsync(pool);
        ForceFinalization();
        await Task.Delay(50); // let the thread-pool dispatched leak report run

        // The leaked slot's capacity permit is gone forever by design - but the pool's other slot
        // is unaffected and still usable.
        await using var lease = await pool.AcquireAsync();
        Assert.NotNull(lease.Resource);

        Assert.Equal(0, policy.DisposeCallCount); // never disposed - would be unsafe if still in use elsewhere
    }

    [Fact]
    public async Task LeakedLease_DoesNotCrashProcess_EvenWhenOnLeakDetectedCallbackThrows()
    {
        var policy = new FakePolicy();
        var callbackInvoked = new TaskCompletionSource();
        var options = new PoolOptions
        {
            MaxSize = 2,
            OnLeakDetected = _ =>
            {
                callbackInvoked.TrySetResult();
                throw new InvalidOperationException("faulty subscriber");
            },
        };
        await using var pool = new ResourcePool<FakeResource>(policy, options);

        await LeakALeaseAsync(pool);
        ForceFinalization();

        // Wait (bounded) for the thread-pool-dispatched callback to run. If the throwing callback
        // had been invoked on the finalizer thread, an unhandled exception there would have crashed
        // this test process - reaching this line at all is part of the proof.
        var completed = await Task.WhenAny(callbackInvoked.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(callbackInvoked.Task, completed);

        // Pool must still be usable afterwards - the throwing callback did not corrupt pool state.
        await using var lease = await pool.AcquireAsync();
        Assert.NotNull(lease.Resource);
    }

    [Fact]
    public async Task LeakedLease_PublishesLeakDetectedHealthEvent_EvenWhenObserverThrows()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 2 });
        var received = new List<SlotHealthChanged>();
        using var subscription = pool.HealthChanges.Subscribe(new RecordingThenThrowingObserver(received));

        await LeakALeaseAsync(pool);
        ForceFinalization();

        for (var i = 0; i < 50 && received.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Contains(received, e => e.State == SlotHealthState.LeakDetected);

        await using var lease = await pool.AcquireAsync(); // pool still usable afterwards
        Assert.NotNull(lease.Resource);
    }

    [Fact]
    public async Task LeakedLease_IncrementsDetectedLeakCount_SynchronouslyVisibleInStats()
    {
        // docs/adr/0013: DetectedLeakCount must be durably visible via GetStats() even if nobody
        // subscribed to OnLeakDetected/HealthChanges at the time - it is incremented synchronously,
        // not only as part of the (best-effort) thread-pool dispatched callback/event.
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 2 });

        Assert.Equal(0, pool.GetStats().DetectedLeakCount);

        await LeakALeaseAsync(pool);
        ForceFinalization();

        // The increment itself is synchronous (happens in ReportLeakedLease, before the callback is
        // dispatched), so no delay/poll should be needed - but allow a brief window since
        // finalization scheduling itself is not instantaneous.
        int leakCount = 0;
        for (var i = 0; i < 50 && leakCount == 0; i++)
        {
            leakCount = pool.GetStats().DetectedLeakCount;
            if (leakCount == 0)
            {
                await Task.Delay(20);
            }
        }

        Assert.Equal(1, leakCount);
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

    private sealed class RecordingThenThrowingObserver : IObserver<SlotHealthChanged>
    {
        private readonly List<SlotHealthChanged> _target;

        public RecordingThenThrowingObserver(List<SlotHealthChanged> target) => _target = target;

        public void OnCompleted() { }
        public void OnError(Exception error) { }

        public void OnNext(SlotHealthChanged value)
        {
            _target.Add(value);
            throw new InvalidOperationException("faulty observer");
        }
    }
}
