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
    /// Diagnostic-only (log-only), matching e.g. HikariCP's leak-detection behavior: the pool does
    /// NOT recycle or dispose the underlying resource on this signal, because the only evidence
    /// available is that the <see cref="PooledLease{T}"/> wrapper became unreachable - the resource
    /// it wraps may still be referenced and actively in use elsewhere (e.g. a caller that extracted
    /// <see cref="PooledLease{T}.Resource"/> into a local and then dropped the lease). A genuine
    /// leak therefore permanently reduces this pool's effective capacity by one slot until the
    /// process restarts - size <see cref="MaxSize"/> and monitor via this callback and
    /// <see cref="ResourcePool{T}.HealthChanges"/> with that trade-off in mind. See docs/adr/0012.
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

    /// <summary>
    /// Maximum time <see cref="ResourcePool{T}.AcquireAsync"/> will wait for a usable resource
    /// before giving up and throwing <see cref="PoolAcquireTimeoutException"/>. Null (default,
    /// backward-compatible) means wait indefinitely - the pre-ADR-0012 behavior. Mirrors the same
    /// bounded-wait pattern most database connection pools use (e.g. HikariCP's
    /// <c>connectionTimeout</c>, ADO.NET's <c>Connect Timeout</c>): a timeout on the wait itself,
    /// not a hard cap on how many callers may be waiting - a waiting caller is cheap (just a
    /// suspended <see cref="Task"/>), so the risk being bounded is caller pile-up/backpressure, not
    /// memory. This bounds the *entire* acquire - the initial capacity-gate wait AND any inline
    /// idle-lifetime recycle / serialized creation that follows getting a permit (docs/adr/0013) -
    /// not merely the first semaphore wait. The one residual gap: if the single in-flight
    /// <see cref="IPooledResourcePolicy{T}.CreateAsync"/> call your caller ends up waiting behind is
    /// itself neither cancellation-aware nor bounded by <see cref="CreateTimeout"/>, that specific
    /// call cannot be interrupted - configure <see cref="CreateTimeout"/> alongside this setting for
    /// a true worst-case bound. See docs/adr/0012, docs/adr/0013.
    /// </summary>
    public TimeSpan? AcquireTimeout { get; init; }
}
