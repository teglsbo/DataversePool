using ConnectionPool.Core;
using Xunit;

namespace ConnectionPool.Core.Tests;

/// <summary>
/// Verifies docs/adr/0013: ResourcePool's constructor validates PoolOptions duration settings up
/// front, rather than letting an invalid value fail confusingly later (e.g. a negative
/// AcquireTimeout making every acquire time out immediately, or Task.Delay throwing on a negative
/// CreateTimeout deep inside CreateThroughGateAsync).
/// </summary>
public class PoolOptionsValidationTests
{
    [Fact]
    public void Constructor_Throws_WhenMaxSizeNotPositive()
    {
        var policy = new FakePolicy();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 0 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_Throws_WhenAcquireTimeoutNotPositive(int seconds)
    {
        var policy = new FakePolicy();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResourcePool<FakeResource>(policy, new PoolOptions { AcquireTimeout = TimeSpan.FromSeconds(seconds) }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_Throws_WhenCreateTimeoutNotPositive(int seconds)
    {
        var policy = new FakePolicy();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResourcePool<FakeResource>(policy, new PoolOptions { CreateTimeout = TimeSpan.FromSeconds(seconds) }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_Throws_WhenMaxIdleLifetimeNotPositive(int seconds)
    {
        var policy = new FakePolicy();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResourcePool<FakeResource>(policy, new PoolOptions { MaxIdleLifetime = TimeSpan.FromSeconds(seconds) }));
    }

    [Fact]
    public async Task Constructor_Succeeds_WhenAllDurationsAreNull()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions());
        Assert.NotNull(pool);
    }

    [Fact]
    public void Defaults_BoundAcquireAndCreate_RatherThanWaitingForever()
    {
        // A pool whose defaults are all "wait forever" turns a saturated or hung dependency into
        // callers blocked indefinitely with no exception and no signal. Both knobs therefore now
        // default to a finite (deliberately generous) duration; null remains available as an
        // explicit opt-out.
        var options = new PoolOptions();

        Assert.Equal(PoolOptions.DefaultAcquireTimeout, options.AcquireTimeout);
        Assert.Equal(PoolOptions.DefaultCreateTimeout, options.CreateTimeout);
        Assert.True(options.AcquireTimeout > TimeSpan.Zero);
        Assert.True(options.CreateTimeout > TimeSpan.Zero);
    }

    [Fact]
    public async Task Defaults_CanStillBeOptedOutOf_ByExplicitNull()
    {
        var options = new PoolOptions { AcquireTimeout = null, CreateTimeout = null };

        Assert.Null(options.AcquireTimeout);
        Assert.Null(options.CreateTimeout);

        // Null must remain constructible (it means "unbounded", not "invalid").
        await using var _ = new ResourcePool<FakeResource>(new FakePolicy(), options);
    }
}
