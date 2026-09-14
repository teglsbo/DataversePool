namespace ConnectionPool.Core;

/// <summary>
/// Domain-specific lifecycle rules for the resource type <typeparamref name="T"/> pooled by
/// <see cref="ResourcePool{T}"/>. The pool engine itself has no knowledge of what <typeparamref name="T"/>
/// actually is (e.g. a Dataverse ServiceClient) - all creation, health, and disposal decisions are
/// delegated here. See docs/adr/0001-pool-core-domain-agnostic-via-policy.md.
/// </summary>
public interface IPooledResourcePolicy<T> where T : notnull
{
    /// <summary>
    /// Creates a new resource instance. The pool guarantees this is never invoked concurrently
    /// for the same pool (serial creation gate, see docs/adr/0002), regardless of whether the
    /// call originated from eager warmup or lazy on-demand creation.
    /// </summary>
    Task<T> CreateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Determines whether an idle resource is still fit to be handed out. <paramref name="lastIncident"/>
    /// is populated when the resource was previously marked unhealthy (e.g. via
    /// <see cref="PooledLease{T}.MarkUnhealthy"/> or a detected lease leak), allowing the policy to
    /// distinguish transient issues from ones that require discarding the resource.
    /// </summary>
    bool IsHealthy(T resource, PoolIncidentInfo? lastIncident);

    /// <summary>
    /// Releases/closes a resource that is being removed from the pool permanently.
    /// </summary>
    ValueTask DisposeResourceAsync(T resource);

    /// <summary>
    /// Called when a healthy resource is returned to the pool, before it is re-idled and made
    /// available to the next <c>AcquireAsync</c> caller. Default no-op. Use this to scrub any
    /// per-lease mutable state on <typeparamref name="T"/> that must never leak to the next,
    /// unrelated caller (e.g. an impersonation/"act as" identity set by the previous caller) -
    /// see docs/adr/0009-return-scrubbing-hook-caller-id-leak.md. The pool engine itself has no
    /// opinion on what "per-lease state" means for a given <typeparamref name="T"/>; that is
    /// exactly the kind of domain knowledge this policy interface exists to hold (ADR-0001).
    /// </summary>
    void OnReturned(T resource)
    {
        // Default: nothing to scrub. Most pooled resource types have no caller-mutable identity
        // state, so most policies never need to override this.
    }
}
