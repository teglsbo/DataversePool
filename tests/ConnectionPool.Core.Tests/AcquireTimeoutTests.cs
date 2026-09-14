using ConnectionPool.Core;
using Xunit;

namespace ConnectionPool.Core.Tests;

/// <summary>
/// Verifies docs/adr/0012: PoolOptions.AcquireTimeout bounds how long AcquireAsync waits for
/// capacity before giving up, mirroring the bounded-wait pattern used by HikariCP/ADO.NET pools.
/// </summary>
public class AcquireTimeoutTests
{
    [Fact]
    public async Task AcquireAsync_ThrowsPoolAcquireTimeoutException_WhenNoCapacityBecomesAvailableInTime()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(
            policy, new PoolOptions { MaxSize = 1, AcquireTimeout = TimeSpan.FromMilliseconds(100) });

        await using var lease = await pool.AcquireAsync(); // consume the only slot, never returned in this test

        var ex = await Assert.ThrowsAsync<PoolAcquireTimeoutException>(() => pool.AcquireAsync());

        Assert.Equal(TimeSpan.FromMilliseconds(100), ex.Timeout);
        Assert.Equal(1, ex.Stats.MaxSize);
    }

    [Fact]
    public async Task AcquireAsync_Succeeds_WhenCapacityFreesUpBeforeTimeout()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(
            policy, new PoolOptions { MaxSize = 1, AcquireTimeout = TimeSpan.FromSeconds(5) });

        var lease = await pool.AcquireAsync();
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            await lease.DisposeAsync();
        });

        await using var lease2 = await pool.AcquireAsync();
        Assert.NotNull(lease2.Resource);
    }

    [Fact]
    public async Task AcquireAsync_WaitsIndefinitely_WhenAcquireTimeoutNotConfigured()
    {
        // Default (null) preserves pre-ADR-0012 behavior: no bound on the wait.
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1 });

        var lease = await pool.AcquireAsync();
        var acquireTask = pool.AcquireAsync();

        await Task.Delay(150);
        Assert.False(acquireTask.IsCompleted); // still waiting, no timeout configured

        await lease.DisposeAsync();
        await using var lease2 = await acquireTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(lease2.Resource);
    }
}
