# ADR-0001: Generic pool core decoupled from Dataverse via `IPooledResourcePolicy<T>`

## Status
Accepted

## Context
We need to build connection pooling for Dataverse `ServiceClient`, but we do not want to lock
the entire design to Dataverse-specific knowledge (tokens, discovery, `CallerId`). Other
resource types (for example other SDK clients) can benefit from the same pool engine.

## Decision
`ConnectionPool.Core` only knows the generic type `T` and a policy interface:

```csharp
public interface IPooledResourcePolicy<T>
{
    Task<T> CreateAsync(CancellationToken ct);
    bool IsHealthy(T resource, PoolIncidentInfo? lastIncident);
    ValueTask DisposeResourceAsync(T resource);
}
```

The naming is intentionally borrowed from `Microsoft.Extensions.ObjectPool.PooledObjectPolicy<T>`
for recognizability. "Policy" rather than "Factory", because the contract covers the entire
resource lifecycle (creation, health evaluation, disposal) — not only creation.

`ConnectionPool.Dataverse` implements `DataverseServiceClientPolicy : IPooledResourcePolicy<ServiceClient>`
and is the only place in the solution that knows Dataverse-specific types.

## Consequences
- Core can be unit tested 100% with fakes, without ever creating a `ServiceClient`.
- Future resource types (other SDKs) can reuse `ConnectionPool.Core` without changes.
- All domain logic (when a Dataverse connection is "unhealthy") lives in the adapter, not the core.
