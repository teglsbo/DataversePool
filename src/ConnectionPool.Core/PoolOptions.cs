namespace ConnectionPool.Core;

/// <summary>
/// Configuration for a <see cref="ResourcePool{T}"/>.
/// </summary>
public sealed class PoolOptions
{
    /// <summary>
    /// Maximum number of resources concurrently created/leased by this pool. Acquire calls beyond
    /// this size will wait for a resource to be returned.
    /// </summary>
    public int MaxSize { get; init; } = 8;

    /// <summary>
    /// Number of resources to eagerly create, sequentially, when <see cref="ResourcePool{T}.WarmupAsync"/>
    /// is invoked (e.g. from a hosted service at startup). Creation is always serialized regardless of
    /// this setting - see docs/adr/0002-serial-creation-gate-no-parallel-cloning.md.
    /// </summary>
    public int PrewarmCount { get; init; } = 0;

    /// <summary>
    /// Optional callback invoked whenever a lease is garbage-collected without having been disposed.
    /// Intended for logging/telemetry - the pool independently reclaims and recycles the underlying
    /// slot regardless of whether a callback is supplied.
    /// </summary>
    public Action<PoolIncidentInfo>? OnLeakDetected { get; init; }

    /// <summary>
    /// Maximum time to wait for <see cref="IPooledResourcePolicy{T}.CreateAsync"/> to complete before
    /// abandoning that attempt. Guards against a hung/very slow creation permanently wedging the
    /// pool's serial creation gate. See docs/adr/0007. Null (default) means no timeout is applied.
    /// </summary>
    public TimeSpan? CreateTimeout { get; init; }

    /// <summary>
    /// Maximum time a resource may sit idle in the pool before it is proactively recycled on next
    /// checkout, analogous to ADO.NET's "Connection Lifetime". Null (default) disables this check;
    /// staleness is then only detected reactively via <see cref="PooledLease{T}.MarkUnhealthy"/>.
    /// See docs/adr/0007.
    /// </summary>
    public TimeSpan? MaxIdleLifetime { get; init; }
}
