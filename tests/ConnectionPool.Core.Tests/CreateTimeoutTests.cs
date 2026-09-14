using ConnectionPool.Core;
using Xunit;

namespace ConnectionPool.Core.Tests;

/// <summary>
/// Verifies docs/adr/0007 (#4): a hung/very-slow CreateAsync is bounded by PoolOptions.CreateTimeout
/// and does not permanently wedge the pool's serial creation gate for other callers.
/// </summary>
public class CreateTimeoutTests
{
    [Fact]
    public async Task AcquireAsync_ThrowsTimeoutException_WhenCreateAsyncHangs()
    {
        var policy = new FakePolicy
        {
            BeforeCreateDelay = () => Task.Delay(TimeSpan.FromSeconds(10)),
        };
        await using var pool = new ResourcePool<FakeResource>(
            policy,
            new PoolOptions { MaxSize = 1, CreateTimeout = TimeSpan.FromMilliseconds(100) });

        await Assert.ThrowsAsync<TimeoutException>(() => pool.AcquireAsync());
    }

    [Fact]
    public async Task AcquireAsync_SucceedsForOtherCallers_AfterOneCreateAsyncTimesOut()
    {
        var callCount = 0;
        var policy = new FakePolicy
        {
            BeforeCreateDelay = () =>
            {
                var thisCall = Interlocked.Increment(ref callCount);
                // Only the first call hangs; subsequent calls (after the gate is released on
                // timeout) complete quickly.
                return thisCall == 1 ? Task.Delay(TimeSpan.FromSeconds(10)) : Task.CompletedTask;
            },
        };
        await using var pool = new ResourcePool<FakeResource>(
            policy,
            new PoolOptions { MaxSize = 2, CreateTimeout = TimeSpan.FromMilliseconds(100) });

        await Assert.ThrowsAsync<TimeoutException>(() => pool.AcquireAsync());

        // The creation gate must have been released despite the first (abandoned) creation still
        // technically running in the background - otherwise this would hang too.
        await using var lease = await pool.AcquireAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(lease.Resource);
    }
}
