using ConnectionPool.Core;
using Xunit;

namespace ConnectionPool.Core.Tests;

/// <summary>
/// Verifies docs/adr/0002: policy.CreateAsync is never invoked concurrently, even under heavy
/// concurrent acquire pressure with an empty pool.
/// </summary>
public class SerialCreationGateTests
{
    [Fact]
    public async Task ConcurrentAcquires_NeverCallCreateAsync_Concurrently()
    {
        var policy = new FakePolicy
        {
            BeforeCreateDelay = () => Task.Delay(15),
        };
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 20 });

        var acquireTasks = Enumerable.Range(0, 20)
            .Select(_ => pool.AcquireAsync())
            .ToArray();

        var leases = await Task.WhenAll(acquireTasks);

        Assert.Equal(1, policy.MaxObservedConcurrentCreations);
        Assert.Equal(20, policy.CreateCallCount);

        foreach (var lease in leases)
        {
            await lease.DisposeAsync();
        }
    }
}
