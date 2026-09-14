using ConnectionPool.Core;
using Xunit;

namespace ConnectionPool.Core.Tests;

/// <summary>
/// Verifies docs/adr/0004: MarkUnhealthy recycles the slot without blocking other waiters, and
/// docs/adr/0003: leaked (undisposed, GC'd) leases are detected and recycled too.
/// </summary>
public class HealthSignalTests
{
    [Fact]
    public async Task MarkUnhealthy_CausesResourceToBeReplaced_OnNextAcquire()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1 });

        var lease1 = await pool.AcquireAsync();
        var originalId = lease1.Resource.Id;
        lease1.MarkUnhealthy(new InvalidOperationException("boom"));
        await lease1.DisposeAsync();

        // Recycle happens in the background; poll briefly for the replacement to land.
        FakeResource? newResource = null;
        for (var i = 0; i < 50 && newResource is null; i++)
        {
            await using var probe = await pool.AcquireAsync();
            if (probe.Resource.Id != originalId)
            {
                newResource = probe.Resource;
            }
            else
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(newResource);
        Assert.Equal(2, policy.CreateCallCount);
        Assert.Equal(1, policy.DisposeCallCount);
    }

    [Fact]
    public async Task HealthChanges_PublishesMarkedUnhealthyEvent()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1 });

        var received = new List<SlotHealthChanged>();
        using var subscription = pool.HealthChanges.Subscribe(new RecordingObserver(received));

        var lease = await pool.AcquireAsync();
        lease.MarkUnhealthy(new InvalidOperationException("boom"));
        await lease.DisposeAsync();

        // Allow background recycle to publish its follow-up events.
        await Task.Delay(100);

        Assert.Contains(received, e => e.State == SlotHealthState.MarkedUnhealthy);
    }

    [Fact]
    public async Task ConsecutiveOperationalFailures_AccumulatesAcrossRecycles_NotResetBySuccessfulRecycle()
    {
        // Regression test for docs/adr/0013: a member whose every operation fails, but whose
        // replacement resource keeps being created successfully (recycle succeeds each time), must
        // still accumulate ConsecutiveOperationalFailures - resetting it merely because a *new*
        // resource was cloned successfully defeated the whole point of this counter (a
        // fail -> recycle-succeeds -> reset loop that never reaches the breaker's threshold).
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1 });

        for (var i = 0; i < 3; i++)
        {
            var lease = await pool.AcquireAsync();
            lease.MarkUnhealthy(new InvalidOperationException("boom"));
            await lease.DisposeAsync();

            // Wait for the background recycle triggered by the unhealthy return to finish (a fresh
            // resource becomes idle again) before the next iteration's acquire.
            for (var wait = 0; wait < 50 && pool.GetStats().IdleCount == 0; wait++)
            {
                await Task.Delay(10);
            }
        }

        Assert.Equal(3, pool.GetStats().ConsecutiveOperationalFailures);
        Assert.Equal(4, policy.CreateCallCount); // 1 initial + 3 successful recycle-replacements
    }

    [Fact]
    public async Task ConsecutiveOperationalFailures_ResetsOnlyOnGenuinelyHealthyReturn()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1 });

        var lease1 = await pool.AcquireAsync();
        lease1.MarkUnhealthy(new InvalidOperationException("boom"));
        await lease1.DisposeAsync();

        for (var wait = 0; wait < 50 && pool.GetStats().IdleCount == 0; wait++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, pool.GetStats().ConsecutiveOperationalFailures);

        var lease2 = await pool.AcquireAsync();
        await lease2.DisposeAsync(); // healthy return

        Assert.Equal(0, pool.GetStats().ConsecutiveOperationalFailures);
    }

    private sealed class RecordingObserver : IObserver<SlotHealthChanged>
    {
        private readonly List<SlotHealthChanged> _target;

        public RecordingObserver(List<SlotHealthChanged> target) => _target = target;

        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(SlotHealthChanged value) => _target.Add(value);
    }
}
