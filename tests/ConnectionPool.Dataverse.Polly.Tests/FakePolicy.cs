using ConnectionPool.Core;

namespace ConnectionPool.Dataverse.Polly.Tests;

/// <summary>Minimal fake resource/policy, mirroring ConnectionPool.Core.Tests' FakePolicy, so this
/// project can obtain a real PooledLease{T} without any Dataverse/network dependency.</summary>
public sealed class FakeResource
{
    public bool Disposed { get; set; }
}

public sealed class FakePolicy : IPooledResourcePolicy<FakeResource>
{
    public Task<FakeResource> CreateAsync(CancellationToken cancellationToken) => Task.FromResult(new FakeResource());

    public bool IsHealthy(FakeResource resource, PoolIncidentInfo? lastIncident) => lastIncident is null;

    public ValueTask DisposeResourceAsync(FakeResource resource)
    {
        resource.Disposed = true;
        return ValueTask.CompletedTask;
    }
}
