using ConnectionPool.Core;

namespace ConnectionPool.Core.Tests;

/// <summary>
/// Fake resource + policy used across Core tests. Deliberately has no dependency on any real
/// pooled resource type (e.g. ServiceClient) - Core is tested entirely with fakes, per
/// docs/adr/0001 and the session's agreed test strategy.
/// </summary>
public sealed class FakeResource
{
    public int Id { get; }
    public bool Disposed { get; set; }

    public FakeResource(int id) => Id = id;
}

public sealed class FakePolicy : IPooledResourcePolicy<FakeResource>
{
    private int _nextId;
    private int _concurrentCreations;

    public int CreateCallCount;
    public int DisposeCallCount;
    public int OnReturnedCallCount;
    public int MaxObservedConcurrentCreations;
    public Func<FakeResource, PoolIncidentInfo?, bool>? HealthOverride;
    public Func<Task>? BeforeCreateDelay;
    public Func<FakeResource, Task>? BeforeDisposeDelay;
    public bool FailNextCreate;

    public async Task<FakeResource> CreateAsync(CancellationToken cancellationToken)
    {
        var concurrent = Interlocked.Increment(ref _concurrentCreations);
        InterlockedMax(ref MaxObservedConcurrentCreations, concurrent);
        Interlocked.Increment(ref CreateCallCount);
        try
        {
            if (BeforeCreateDelay is not null)
            {
                await BeforeCreateDelay().ConfigureAwait(false);
            }

            if (FailNextCreate)
            {
                FailNextCreate = false;
                throw new InvalidOperationException("Simulated creation failure.");
            }

            return new FakeResource(Interlocked.Increment(ref _nextId));
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentCreations);
        }
    }

    public bool IsHealthy(FakeResource resource, PoolIncidentInfo? lastIncident)
    {
        if (HealthOverride is not null)
        {
            return HealthOverride(resource, lastIncident);
        }

        return lastIncident is null;
    }

    public async ValueTask DisposeResourceAsync(FakeResource resource)
    {
        if (BeforeDisposeDelay is not null)
        {
            await BeforeDisposeDelay(resource).ConfigureAwait(false);
        }

        resource.Disposed = true;
        Interlocked.Increment(ref DisposeCallCount);
    }

    public void OnReturned(FakeResource resource) => Interlocked.Increment(ref OnReturnedCallCount);

    private static void InterlockedMax(ref int target, int value)
    {
        int initial, computed;
        do
        {
            initial = target;
            computed = Math.Max(initial, value);
        } while (Interlocked.CompareExchange(ref target, computed, initial) != initial);
    }
}
