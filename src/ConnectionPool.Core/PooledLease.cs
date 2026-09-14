namespace ConnectionPool.Core;

/// <summary>
/// An exclusively-owned handle to a pooled resource. Isolation is a dispose contract, not a runtime
/// enforcement: the resource must not be shared across threads/operations concurrently, and
/// <see cref="DisposeAsync"/> must always be called (typically via <c>await using</c>) so the
/// underlying slot returns to the pool. See docs/adr/0003-lease-isolation-contract-not-enforced-runtime.md.
///
/// If a lease is garbage-collected without being disposed, the pool detects this (leak tracking) but
/// only reports it diagnostically - it does NOT recycle/dispose the underlying resource, since the
/// resource may still be referenced and in use elsewhere even though this wrapper became
/// unreachable. See docs/adr/0012-log-only-leak-detection-and-bounded-acquire.md.
/// </summary>
public sealed class PooledLease<T> : IAsyncDisposable where T : notnull
{
    private readonly ResourcePool<T> _pool;
    private readonly Slot<T> _slot;
    private int _disposed;

    internal PooledLease(ResourcePool<T> pool, Slot<T> slot)
    {
        _pool = pool;
        _slot = slot;
    }

    /// <summary>The leased resource. Throws if accessed after disposal.</summary>
    public T Resource => _disposed == 0
        ? _slot.Resource ?? throw new InvalidOperationException("Slot has no resource.")
        : throw new ObjectDisposedException(nameof(PooledLease<T>));

    /// <summary>
    /// Signals that the caller observed a failure on <see cref="Resource"/>. The pool will not hand out
    /// this resource again; it is recycled (disposed + replaced) in the background without blocking
    /// other waiting acquires. Cheap to call - see docs/adr/0004.
    /// </summary>
    public void MarkUnhealthy(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Volatile.Read(ref _disposed) == 1)
        {
            // Lease already returned/disposed - ignore rather than risk mutating a slot that may
            // now be idle or leased to someone else. See docs/adr/0007 (#1) and docs/adr/0003:
            // full protection still relies on callers never using a lease concurrently with/after
            // disposing it.
            return;
        }

        _slot.State = SlotState.Unhealthy;
        _slot.LastIncidentException = exception;
        _slot.LastIncidentAt = DateTimeOffset.UtcNow;
        _slot.LastIncidentWasLeak = false;
        _pool.ReportOperationalFailure(); // feeds circuit-breaker-aware strategies - see docs/adr/0012
        _pool.PublishHealthChanged(SlotHealthState.MarkedUnhealthy, _slot.LastIncident);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        GC.SuppressFinalize(this);
        return _pool.ReturnAsync(_slot);
    }

    ~PooledLease()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _pool.ReportLeakedLease(_slot);
        }
    }
}
