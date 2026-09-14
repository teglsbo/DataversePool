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
}
