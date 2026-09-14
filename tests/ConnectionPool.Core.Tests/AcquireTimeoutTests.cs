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
    public async Task AcquireAsync_ThrowsPoolAcquireTimeoutException_WhenStuckBehindSerializedCreation()
    {
        // Regression test for docs/adr/0013: AcquireTimeout must bound the *entire* acquire, not
        // just the initial capacity-gate wait. With MaxSize=2, both callers can immediately obtain a
        // capacity permit - but resource creation is always serialized (docs/adr/0002), so the
        // second caller ends up waiting on the *creation* gate behind the first caller's slow
        // create. That wait must also be bounded by AcquireTimeout, not unbounded.
        var creationStarted = new SemaphoreSlim(0);
        var releaseCreation = new SemaphoreSlim(0);
        var policy = new FakePolicy
        {
            BeforeCreateDelay = async () =>
            {
                creationStarted.Release();
                await releaseCreation.WaitAsync(TimeSpan.FromSeconds(5));
            },
        };
        await using var pool = new ResourcePool<FakeResource>(
            policy, new PoolOptions { MaxSize = 2, AcquireTimeout = TimeSpan.FromMilliseconds(150) });

        var firstAcquireTask = pool.AcquireAsync(); // occupies the serial creation gate
        await creationStarted.WaitAsync(TimeSpan.FromSeconds(5)); // ensure it is inside CreateAsync

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<PoolAcquireTimeoutException>(() => pool.AcquireAsync());
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Expected the second acquire to time out near AcquireTimeout while waiting behind " +
            $"serialized creation, but took {stopwatch.Elapsed}.");

        releaseCreation.Release(); // let the first creation finish so pool teardown doesn't hang
        await using var firstLease = await firstAcquireTask;
        Assert.NotNull(firstLease.Resource);
    }

    [Fact]
    public async Task AcquireAsync_RespectsCallerCancellation_NotJustAcquireTimeout()
    {
        // A caller-cancelled token should surface as OperationCanceledException, not be
        // misreported as a PoolAcquireTimeoutException.
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(
            policy, new PoolOptions { MaxSize = 1, AcquireTimeout = TimeSpan.FromSeconds(30) });

        await using var lease = await pool.AcquireAsync(); // consume the only slot

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<OperationCanceledException>(() => pool.AcquireAsync(cts.Token));
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

    [Fact]
    public async Task AcquireAsync_ThrowsPoolAcquireTimeoutException_NotCreateTimeoutException_WhenAcquireDeadlineFiresDuringCreate()
    {
        // Regression test for a round-5 review finding: when both AcquireTimeout and CreateTimeout
        // are configured, Task.WhenAny(createTask, Task.Delay(createTimeout, cancellationToken)) was
        // "won" by the delay task whenever *either* CreateTimeout genuinely elapsed OR
        // cancellationToken itself fired first (e.g. AcquireTimeout expiring while creation was
        // still in flight) - both were misclassified as a create timeout, incrementing
        // ConsecutiveCreateFailures and throwing a raw TimeoutException instead of letting
        // AcquireAsync translate the cancellation into PoolAcquireTimeoutException. See docs/adr/0014.
        var neverReleased = new SemaphoreSlim(0);
        var policy = new FakePolicy
        {
            BeforeCreateDelay = () => neverReleased.WaitAsync(TimeSpan.FromSeconds(30)),
        };
        await using var pool = new ResourcePool<FakeResource>(
            policy,
            new PoolOptions
            {
                MaxSize = 1,
                AcquireTimeout = TimeSpan.FromMilliseconds(100),
                CreateTimeout = TimeSpan.FromSeconds(30), // much longer than AcquireTimeout
            });

        var ex = await Assert.ThrowsAsync<PoolAcquireTimeoutException>(() => pool.AcquireAsync());

        Assert.Equal(TimeSpan.FromMilliseconds(100), ex.Timeout);
        Assert.Equal(0, pool.GetStats().ConsecutiveCreateFailures);

        neverReleased.Release(); // let the abandoned create finish so pool teardown doesn't hang
    }

    [Fact]
    public async Task AcquireAsync_ThrowsPoolAcquireTimeoutException_WhenAcquireDeadlineFiresDuringInlineRecycle()
    {
        // Same misclassification, but via the idle-lifetime-triggered inline recycle path
        // (RecycleInPlaceAsync -> CreateThroughGateAsync). Before the fix, cancellation here was
        // swallowed by a blanket `catch (Exception)` that reported it as a failed recycle -
        // incrementing ConsecutiveCreateFailures for what was really just a caller giving up
        // waiting. See docs/adr/0014.
        var hangForever = new SemaphoreSlim(0);
        var hangNextCreate = false;
        var policy = new FakePolicy
        {
            BeforeCreateDelay = async () =>
            {
                if (hangNextCreate)
                {
                    await hangForever.WaitAsync(TimeSpan.FromSeconds(30));
                }
            },
        };
        await using var pool = new ResourcePool<FakeResource>(
            policy,
            new PoolOptions
            {
                MaxSize = 1,
                MaxIdleLifetime = TimeSpan.FromMilliseconds(1),
                AcquireTimeout = TimeSpan.FromMilliseconds(100),
                CreateTimeout = TimeSpan.FromSeconds(30),
            });

        var lease = await pool.AcquireAsync();
        await lease.DisposeAsync(); // becomes idle
        await Task.Delay(20); // exceed MaxIdleLifetime

        hangNextCreate = true;
        await Assert.ThrowsAsync<PoolAcquireTimeoutException>(() => pool.AcquireAsync());

        Assert.Equal(0, pool.GetStats().ConsecutiveCreateFailures);

        hangForever.Release(); // let the abandoned recycle-create finish so teardown doesn't hang
    }
}
