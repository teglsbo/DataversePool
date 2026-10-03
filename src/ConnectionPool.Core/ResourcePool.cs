using System.Collections.Concurrent;

namespace ConnectionPool.Core;

/// <summary>
/// Generic, domain-agnostic resource pool. Bounds concurrently created+leased resources to
/// <see cref="PoolOptions.MaxSize"/>, serializes all resource creation (docs/adr/0002), and never
/// blocks other waiters while recycling a resource that was marked unhealthy or leaked
/// (docs/adr/0004). See docs/adr/0007 for the race-condition/timeout/shutdown hardening applied here.
/// </summary>
public sealed partial class ResourcePool<T> : IAsyncDisposable where T : notnull
{
    private readonly IPooledResourcePolicy<T> _policy;
    private readonly PoolOptions _options;

    // One permit per unit of pool capacity (created-or-creatable resource). Acquire waits for a
    // permit; Return/recycle-completion releases one (via ReleasePermit). This naturally bounds total
    // created slots to the current max size without any separate counter needing to be kept in
    // lockstep. Its own maxCount is unbounded because SetMaxSize can grow it at runtime.
    private readonly SemaphoreSlim _capacityGate;

    // Current concurrency limit. Starts at PoolOptions.MaxSize; changed only by SetMaxSize.
    private int _maxSize;

    // Permits still owed after a shrink that could not take them back immediately because they were
    // held by in-flight leases/creations. ReleasePermit pays this down instead of returning the
    // permit to _capacityGate, so a shrink takes effect as leases come back - nothing in flight is
    // cancelled. See docs/adr/0025.
    private int _permitDebt;
    private readonly object _resizeLock = new();

    // Ensures policy.CreateAsync is never invoked concurrently, regardless of caller (warmup, lazy
    // acquire, or background recycle). See docs/adr/0002. May, in the rare case of a CreateTimeout
    // expiring, be released while the abandoned creation is still technically running - see
    // docs/adr/0007 (#4).
    private readonly SemaphoreSlim _creationGate = new(1, 1);

    // Serializes the "check current count against target, then create the shortfall" decision
    // across concurrent WarmupAsync callers. Without this, two overlapping WarmupAsync calls can
    // both observe CreatedCount below target (idle resources don't hold a capacity permit, so the
    // capacity gate alone doesn't prevent this), both proceed to create, and jointly overshoot
    // MaxSize - see docs/adr/0014.
    private readonly SemaphoreSlim _warmupGate = new(1, 1);

    private readonly ConcurrentStack<Slot<T>> _idle = new();
    private readonly object _idlePushLock = new();
    private int _createdCount;
    private int _waitingCount;
    private int _unhealthyOrRecyclingCount;
    private int _consecutiveCreateFailures;
    private int _consecutiveOperationalFailures;
    private int _detectedLeakCount;
    private int _poolDisposed;

    private readonly object _observersLock = new();
    private readonly List<IObserver<SlotHealthChanged>> _observers = new();

    public ResourcePool(IPooledResourcePolicy<T> policy, PoolOptions? options = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _options = options ?? new PoolOptions();
        if (_options.MaxSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxSize must be positive.");
        }

        // Fail fast at construction rather than surfacing a confusing failure later (e.g. Task.Delay
        // throwing on a negative TimeSpan, or a negative AcquireTimeout making every acquire time out
        // immediately). null continues to mean "disabled/unbounded" for all three. See docs/adr/0013.
        if (_options.AcquireTimeout is { } acquireTimeout && acquireTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "AcquireTimeout must be a positive duration when set.");
        }

        if (_options.CreateTimeout is { } createTimeout && createTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "CreateTimeout must be a positive duration when set.");
        }

        if (_options.MaxIdleLifetime is { } maxIdleLifetime && maxIdleLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxIdleLifetime must be a positive duration when set.");
        }

        _maxSize = _options.MaxSize;
        _capacityGate = new SemaphoreSlim(_maxSize, int.MaxValue);
    }

    /// <summary>The current concurrency limit: <see cref="PoolOptions.MaxSize"/> until changed by
    /// <see cref="SetMaxSize"/>.</summary>
    public int MaxSize => Volatile.Read(ref _maxSize);

    /// <summary>
    /// Changes the maximum number of concurrently created/leased resources at runtime, e.g. to follow
    /// Dataverse's <c>RecommendedDegreesOfParallelism</c> or an operator's decision. Takes effect for
    /// subsequent acquires; never cancels or waits for anything in flight. See docs/adr/0025.
    /// </summary>
    /// <remarks>
    /// <para><b>Growing</b> releases the extra capacity immediately, so waiting callers proceed at once.</para>
    /// <para><b>Shrinking</b> immediately takes back as much unused capacity as is free; the rest is
    /// taken back as in-flight leases are returned. Until then, more than <paramref name="maxSize"/>
    /// leases can briefly be outstanding - <see cref="PoolStats.LeasedCount"/> shows the real number.
    /// Idle resources above the new limit are disposed in the background, and returned ones are
    /// disposed rather than re-idled while the pool is over its new size.</para>
    /// <para>Thread-safe; concurrent calls are applied in some serial order and the last one wins.</para>
    /// </remarks>
    /// <param name="maxSize">The new limit. Must be positive.</param>
    public void SetMaxSize(int maxSize)
    {
        if (maxSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSize), maxSize, "MaxSize must be positive.");
        }

        ThrowIfDisposed();

        lock (_resizeLock)
        {
            var delta = maxSize - _maxSize;
            Volatile.Write(ref _maxSize, maxSize);

            if (delta > 0)
            {
                // Cancel outstanding debt first: those permits are still circulating and simply no
                // longer need to be destroyed when they come back.
                var toRelease = delta;
                while (toRelease > 0)
                {
                    var debt = Volatile.Read(ref _permitDebt);
                    if (debt <= 0)
                    {
                        break;
                    }

                    var forgiven = Math.Min(debt, toRelease);
                    if (Interlocked.CompareExchange(ref _permitDebt, debt - forgiven, debt) == debt)
                    {
                        toRelease -= forgiven;
                    }
                }

                if (toRelease > 0)
                {
                    _capacityGate.Release(toRelease);
                }
            }
            else if (delta < 0)
            {
                var toTake = -delta;
                while (toTake > 0 && _capacityGate.Wait(0))
                {
                    toTake--;
                }

                if (toTake > 0)
                {
                    Interlocked.Add(ref _permitDebt, toTake);
                }

                _ = TrimExcessIdleAsync();
            }
        }
    }

    /// <summary>Returns one capacity permit - unless a shrink still owes one, in which case the
    /// permit is retired instead. Every permit release in this pool goes through here.</summary>
    private void ReleasePermit()
    {
        while (true)
        {
            var debt = Volatile.Read(ref _permitDebt);
            if (debt <= 0)
            {
                _capacityGate.Release();
                return;
            }

            if (Interlocked.CompareExchange(ref _permitDebt, debt - 1, debt) == debt)
            {
                return;
            }
        }
    }

    private bool IsOverSize => Volatile.Read(ref _createdCount) > Volatile.Read(ref _maxSize);

    private async Task TrimExcessIdleAsync()
    {
        while (IsOverSize && Volatile.Read(ref _poolDisposed) == 0 && _idle.TryPop(out var slot))
        {
            Interlocked.Decrement(ref _createdCount);
            await SafeDisposeAsync(slot.Resource).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ensures at least <c>min(</c><see cref="PoolOptions.PrewarmCount"/><c>, </c>
    /// <see cref="PoolOptions.MaxSize"/><c>)</c> resources exist, creating sequentially only the
    /// shortfall. Intended to be called once at startup (e.g. from an <c>IHostedService</c>), but is
    /// idempotent and safe to call repeatedly (e.g. a retried startup hook) - it tops up existing
    /// supply based on <see cref="PoolStats.CreatedCount"/> rather than unconditionally creating
    /// <see cref="PoolOptions.PrewarmCount"/> *more* resources every call, which would silently
    /// create more slots than <see cref="PoolOptions.MaxSize"/> allows. Creation is serialized
    /// regardless (docs/adr/0002); calling this simply moves that cost earlier. See docs/adr/0013.
    /// </summary>
    public async Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var target = Math.Min(_options.PrewarmCount, MaxSize);

        // _warmupGate makes the whole "check current count against target, then create the
        // shortfall" decision a single atomic step across concurrent WarmupAsync callers - see
        // docs/adr/0014. It intentionally does NOT serialize against lazy AcquireAsync-triggered
        // creation (that's fine: those aren't trying to hit `target`, so at worst this call creates
        // one fewer resource than it otherwise would, never more than MaxSize thanks to the capacity
        // gate below).
        await _warmupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                if (Volatile.Read(ref _createdCount) >= target)
                {
                    return; // already at (or above) target - idempotent no-op, no permit taken
                }

                await _capacityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // Re-check now that we hold a permit: a concurrent lazy AcquireAsync create may
                    // have already reached the target while this call was waiting.
                    if (Volatile.Read(ref _createdCount) >= target)
                    {
                        return;
                    }

                    var slot = await CreateNewSlotAsync(cancellationToken).ConfigureAwait(false);
                    if (!TryPushIdle(slot))
                    {
                        Interlocked.Decrement(ref _createdCount);
                        await SafeDisposeAsync(slot.Resource).ConfigureAwait(false);
                        throw new ObjectDisposedException(nameof(ResourcePool<T>));
                    }
                }
                finally
                {
                    // Matches the same permit lifecycle as every other idle resource in this pool: the
                    // permit represents "an acquire is actively in progress against this unit of
                    // capacity," not "a resource physically exists" - idle resources sit in _idle with
                    // their permit already released, to be re-consumed by whichever AcquireAsync next
                    // claims them. See docs/adr/0013.
                    ReleasePermit();
                }
            }
        }
        finally
        {
            _warmupGate.Release();
        }
    }

    public async Task<PooledLease<T>> AcquireAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // AcquireTimeout bounds the *entire* acquire operation (capacity wait + any inline
        // recycle/create that follows getting a permit), not just the initial semaphore wait -
        // otherwise a caller could still block well past the configured timeout behind serialized
        // creation/recycle work. A linked CancellationTokenSource ticking down from `now` gives us
        // "remaining time" for every subsequent await for free. See docs/adr/0013.
        var acquireTimeout = _options.AcquireTimeout;
        using var timeoutCts = acquireTimeout is { } timeout ? new CancellationTokenSource(timeout) : null;
        using var linkedCts = timeoutCts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
            : null;
        var effectiveToken = linkedCts?.Token ?? cancellationToken;

        try
        {
            Interlocked.Increment(ref _waitingCount);
            try
            {
                await _capacityGate.WaitAsync(effectiveToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _waitingCount);
            }

            try
            {
                // A concurrent DisposeAsync may have started (and can even transiently release
                // capacity permits back to _capacityGate while shutting down, see DisposeAsync)
                // after this acquire already passed the entry ThrowIfDisposed() check above. Re-check
                // immediately after winning a permit so such a caller fails cleanly instead of being
                // handed a resource out of a pool that is mid-teardown. See docs/adr/0022.
                if (Volatile.Read(ref _poolDisposed) == 1)
                {
                    throw new ObjectDisposedException(nameof(ResourcePool<T>));
                }

                Slot<T> slot;
                while (true)
                {
                    if (_idle.TryPop(out var candidate))
                    {
                        if (IsExpiredByIdleLifetime(candidate))
                        {
                            var recycledForAge = await RecycleInPlaceAsync(candidate, effectiveToken).ConfigureAwait(false);
                            if (recycledForAge)
                            {
                                slot = candidate;
                                break;
                            }

                            continue;
                        }

                        if (_policy.IsHealthy(candidate.Resource!, candidate.LastIncident))
                        {
                            slot = candidate;
                            break;
                        }

                        // Needs recycling right now - the caller is already waiting for a resource, so this
                        // is done inline (unlike the background recycle path used on Return()).
                        var recycled = await RecycleInPlaceAsync(candidate, effectiveToken).ConfigureAwait(false);
                        if (recycled)
                        {
                            slot = candidate;
                            break;
                        }

                        // Recycle failed: this slot's capacity permit is considered consumed/lost; loop
                        // around to either find another idle slot or create a fresh one below.
                        continue;
                    }

                    slot = await CreateNewSlotAsync(effectiveToken).ConfigureAwait(false);
                    break;
                }

                slot.State = SlotState.Leased;
                return new PooledLease<T>(this, slot);
            }
            catch
            {
                ReleasePermit();
                throw;
            }
        }
        catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The deadline (not the caller's own token) is what fired - report it as a bounded
            // acquire timeout rather than letting an OperationCanceledException leak out, which
            // would be indistinguishable from caller-initiated cancellation.
            throw new PoolAcquireTimeoutException(acquireTimeout!.Value, GetStats());
        }
    }

    public PoolStats GetStats()
    {
        var idle = _idle.Count;
        var created = Volatile.Read(ref _createdCount);
        return new PoolStats(
            MaxSize: MaxSize,
            CreatedCount: created,
            IdleCount: idle,
            LeasedCount: Math.Max(0, created - idle),
            UnhealthyOrRecyclingCount: Volatile.Read(ref _unhealthyOrRecyclingCount),
            WaitingCount: Volatile.Read(ref _waitingCount),
            ConsecutiveCreateFailures: Volatile.Read(ref _consecutiveCreateFailures),
            ConsecutiveOperationalFailures: Volatile.Read(ref _consecutiveOperationalFailures),
            DetectedLeakCount: Volatile.Read(ref _detectedLeakCount));
    }

    public IObservable<SlotHealthChanged> HealthChanges => new HealthChangesObservable(this);

    /// <summary>
    /// Records that a caller reported an operational failure via <see cref="PooledLease{T}.MarkUnhealthy"/>
    /// (as opposed to a creation failure). Feeds <see cref="PoolStats.ConsecutiveOperationalFailures"/>
    /// so circuit-breaker-aware group strategies can react to a member whose resources keep failing
    /// in actual use, not only ones that fail to be created. See docs/adr/0012.
    /// </summary>
    internal void ReportOperationalFailure() => Interlocked.Increment(ref _consecutiveOperationalFailures);

    internal ValueTask ReturnAsync(Slot<T> slot)
    {
        if (Volatile.Read(ref _poolDisposed) == 1)
        {
            // Pool is shutting down: don't re-idle into a stack nobody will drain again.
            // See docs/adr/0007 (#2).
            return DisposeAbandonedSlotAsync(slot);
        }

        if (slot.State == SlotState.Unhealthy)
        {
            // Non-blocking: recycle in the background and release capacity only once a replacement
            // resource is ready (or permanently given up on failure). Other waiters are not blocked
            // by this slot's recycle - they simply draw from remaining idle/creatable capacity.
            _ = RecycleInBackgroundAsync(slot);
            return ValueTask.CompletedTask;
        }

        // A successful, healthy return is evidence the member is operationally fine again - reset
        // the counter immediately rather than waiting for it to decay some other way.
        Interlocked.Exchange(ref _consecutiveOperationalFailures, 0);

        if (IsOverSize)
        {
            // Shrunk by SetMaxSize while this lease was out: retire the resource instead of
            // re-idling it. See docs/adr/0025.
            Interlocked.Decrement(ref _createdCount);
            ReleasePermit();
            return SafeDisposeAsync(slot.Resource);
        }

        try
        {
            _policy.OnReturned(slot.Resource!); // scrub any per-lease state before next caller (ADR-0009)
        }
        catch (Exception exception)
        {
            slot.State = SlotState.Unhealthy;
            slot.LastIncidentException = exception;
            slot.LastIncidentAt = DateTimeOffset.UtcNow;
            slot.LastIncidentWasLeak = false;
            PublishHealthChanged(SlotHealthState.MarkedUnhealthy, slot.LastIncident);
            _ = RecycleInBackgroundAsync(slot);
            return ValueTask.CompletedTask;
        }

        slot.State = SlotState.Idle;
        slot.BecameIdleAt = DateTimeOffset.UtcNow;
        if (!TryPushIdle(slot))
        {
            return DisposeAbandonedSlotAsync(slot);
        }

        ReleasePermit();
        return ValueTask.CompletedTask;
    }

    internal void ReportLeakedLease(Slot<T> slot)
    {
        // Called directly from PooledLease<T>'s finalizer thread. Per docs/adr/0012 this is
        // diagnostic-only (log-only leak detection, matching e.g. HikariCP): the pool does NOT
        // recycle or dispose the resource, and does NOT release its capacity permit. The only
        // evidence available is that the *lease wrapper* became unreachable - the underlying
        // resource may still be referenced and actively in use elsewhere (e.g. a caller that
        // extracted lease.Resource into a local and then dropped the lease); disposing it on that
        // assumption risks corrupting an in-flight operation, which is worse than a leaked slot.
        // The practical consequence: a genuine leak permanently reduces this pool's effective
        // capacity by one until the process restarts. Keep this method itself limited to cheap,
        // exception-free field writes; the user callback/observer notification below is dispatched
        // to the thread pool so an unhandled exception from a subscriber can never terminate the
        // process (an unhandled exception on the finalizer thread is process-fatal) and so a slow
        // subscriber never stalls finalization of other objects.
        slot.LastIncidentException = null;
        slot.LastIncidentAt = DateTimeOffset.UtcNow;
        slot.LastIncidentWasLeak = true;
        // Incremented synchronously (not from the thread-pool dispatch below) so it is durably
        // visible via GetStats()/PoolAcquireTimeoutException even if no OnLeakDetected callback or
        // HealthChanges subscriber was ever attached - see docs/adr/0013.
        Interlocked.Increment(ref _detectedLeakCount);

        var incident = slot.LastIncident;
        ThreadPool.QueueUserWorkItem(
            static state => state.pool.CompleteLeakReport(state.incident),
            (pool: this, incident),
            preferLocal: false);
    }

    private void CompleteLeakReport(PoolIncidentInfo? incident)
    {
        try
        {
            _options.OnLeakDetected?.Invoke(incident!);
        }
        catch
        {
            // A faulty leak-detection callback must not propagate onto the thread-pool worker.
            // See docs/adr/0012.
        }

        PublishHealthChanged(SlotHealthState.LeakDetected, incident);
    }

    internal void PublishHealthChanged(SlotHealthState state, PoolIncidentInfo? incident)
    {
        var change = new SlotHealthChanged(state, incident);
        IObserver<SlotHealthChanged>[] observersSnapshot;
        lock (_observersLock)
        {
            if (_observers.Count == 0)
            {
                return;
            }

            observersSnapshot = _observers.ToArray();
        }

        foreach (var observer in observersSnapshot)
        {
            try
            {
                observer.OnNext(change);
            }
            catch
            {
                // A faulty observer must not prevent other observers from being notified, or (when
                // this is reached from the leak-reporting path) prevent slot recycling from being
                // scheduled afterwards. See docs/adr/0011.
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _poolDisposed) == 1)
        {
            throw new ObjectDisposedException(nameof(ResourcePool<T>));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _poolDisposed, 1) == 1)
        {
            return;
        }

        // Best-effort drain: give outstanding leases a short window to be returned so we don't leak
        // them. Any lease returned after this point is disposed directly by ReturnAsync/
        // RecycleInBackgroundAsync instead of being re-idled. See docs/adr/0007 (#2).
        var acquiredPermits = 0;
        for (var i = 0; i < MaxSize; i++)
        {
            if (await _capacityGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            {
                acquiredPermits++;
            }
            else
            {
                break;
            }
        }

        // Drain _idle while still holding every permit collected above, so a concurrent
        // AcquireAsync cannot win one of these permits (once released, below) and pop/create a
        // slot while this loop is in the middle of disposing idle resources. See docs/adr/0022.
        List<Slot<T>> idleSlots = new();
        lock (_idlePushLock)
        {
            while (_idle.TryPop(out var slot))
            {
                idleSlots.Add(slot);
            }
        }

        foreach (var slot in idleSlots)
        {
            await SafeDisposeAsync(slot.Resource).ConfigureAwait(false);
        }

        for (var i = 0; i < acquiredPermits; i++)
        {
            _capacityGate.Release();
        }

        List<IObserver<SlotHealthChanged>> observersSnapshot;
        lock (_observersLock)
        {
            observersSnapshot = new List<IObserver<SlotHealthChanged>>(_observers);
            _observers.Clear();
        }

        foreach (var observer in observersSnapshot)
        {
            observer.OnCompleted();
        }
    }

    private bool TryPushIdle(Slot<T> slot)
    {
        lock (_idlePushLock)
        {
            if (Volatile.Read(ref _poolDisposed) == 1)
            {
                return false;
            }

            _idle.Push(slot);
            return true;
        }
    }

    private sealed class HealthChangesObservable : IObservable<SlotHealthChanged>
    {
        private readonly ResourcePool<T> _pool;

        public HealthChangesObservable(ResourcePool<T> pool) => _pool = pool;

        public IDisposable Subscribe(IObserver<SlotHealthChanged> observer)
        {
            lock (_pool._observersLock)
            {
                _pool._observers.Add(observer);
            }

            return new Unsubscriber(_pool, observer);
        }

        private sealed class Unsubscriber : IDisposable
        {
            private readonly ResourcePool<T> _pool;
            private readonly IObserver<SlotHealthChanged> _observer;

            public Unsubscriber(ResourcePool<T> pool, IObserver<SlotHealthChanged> observer)
            {
                _pool = pool;
                _observer = observer;
            }

            public void Dispose()
            {
                lock (_pool._observersLock)
                {
                    _pool._observers.Remove(_observer);
                }
            }
        }
    }
}
