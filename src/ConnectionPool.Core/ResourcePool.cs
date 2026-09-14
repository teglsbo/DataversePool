using System.Collections.Concurrent;

namespace ConnectionPool.Core;

/// <summary>
/// Generic, domain-agnostic resource pool. Bounds concurrently created+leased resources to
/// <see cref="PoolOptions.MaxSize"/>, serializes all resource creation (docs/adr/0002), and never
/// blocks other waiters while recycling a resource that was marked unhealthy or leaked
/// (docs/adr/0004). See docs/adr/0007 for the race-condition/timeout/shutdown hardening applied here.
/// </summary>
public sealed class ResourcePool<T> : IAsyncDisposable where T : notnull
{
    private readonly IPooledResourcePolicy<T> _policy;
    private readonly PoolOptions _options;

    // One permit per unit of pool capacity (created-or-creatable resource). Acquire waits for a
    // permit; Return/recycle-completion releases one. This naturally bounds total created slots to
    // MaxSize without any separate counter needing to be kept in lockstep.
    private readonly SemaphoreSlim _capacityGate;

    // Ensures policy.CreateAsync is never invoked concurrently, regardless of caller (warmup, lazy
    // acquire, or background recycle). See docs/adr/0002. May, in the rare case of a CreateTimeout
    // expiring, be released while the abandoned creation is still technically running - see
    // docs/adr/0007 (#4).
    private readonly SemaphoreSlim _creationGate = new(1, 1);

    private readonly ConcurrentStack<Slot<T>> _idle = new();
    private int _createdCount;
    private int _waitingCount;
    private int _unhealthyOrRecyclingCount;
    private int _consecutiveCreateFailures;
    private int _consecutiveOperationalFailures;
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

        _capacityGate = new SemaphoreSlim(_options.MaxSize, _options.MaxSize);
    }

    /// <summary>
    /// Sequentially creates up to <see cref="PoolOptions.PrewarmCount"/> resources ahead of time.
    /// Intended to be called once at startup (e.g. from an <c>IHostedService</c>). Creation is
    /// serialized regardless (docs/adr/0002); calling this simply moves that cost earlier.
    /// </summary>
    public async Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var count = Math.Min(_options.PrewarmCount, _options.MaxSize);
        for (var i = 0; i < count; i++)
        {
            await _capacityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var slot = await CreateNewSlotAsync(cancellationToken).ConfigureAwait(false);
                _idle.Push(slot);
            }
            finally
            {
                _capacityGate.Release();
            }
        }
    }

    public async Task<PooledLease<T>> AcquireAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        Interlocked.Increment(ref _waitingCount);
        try
        {
            if (_options.AcquireTimeout is { } acquireTimeout)
            {
                var acquired = await _capacityGate.WaitAsync(acquireTimeout, cancellationToken).ConfigureAwait(false);
                if (!acquired)
                {
                    // Snapshot stats before decrementing _waitingCount (the finally block below) so
                    // the exception reflects the state that actually caused the timeout.
                    throw new PoolAcquireTimeoutException(acquireTimeout, GetStats());
                }
            }
            else
            {
                await _capacityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waitingCount);
        }

        try
        {
            Slot<T> slot;
            while (true)
            {
                if (_idle.TryPop(out var candidate))
                {
                    if (IsExpiredByIdleLifetime(candidate))
                    {
                        var recycledForAge = await RecycleInPlaceAsync(candidate, cancellationToken).ConfigureAwait(false);
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
                    var recycled = await RecycleInPlaceAsync(candidate, cancellationToken).ConfigureAwait(false);
                    if (recycled)
                    {
                        slot = candidate;
                        break;
                    }

                    // Recycle failed: this slot's capacity permit is considered consumed/lost; loop
                    // around to either find another idle slot or create a fresh one below.
                    continue;
                }

                slot = await CreateNewSlotAsync(cancellationToken).ConfigureAwait(false);
                break;
            }

            slot.State = SlotState.Leased;
            return new PooledLease<T>(this, slot);
        }
        catch
        {
            _capacityGate.Release();
            throw;
        }
    }

    public PoolStats GetStats()
    {
        var idle = _idle.Count;
        var created = Volatile.Read(ref _createdCount);
        return new PoolStats(
            MaxSize: _options.MaxSize,
            CreatedCount: created,
            IdleCount: idle,
            LeasedCount: Math.Max(0, created - idle),
            UnhealthyOrRecyclingCount: Volatile.Read(ref _unhealthyOrRecyclingCount),
            WaitingCount: Volatile.Read(ref _waitingCount),
            ConsecutiveCreateFailures: Volatile.Read(ref _consecutiveCreateFailures),
            ConsecutiveOperationalFailures: Volatile.Read(ref _consecutiveOperationalFailures));
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
        slot.State = SlotState.Idle;
        slot.BecameIdleAt = DateTimeOffset.UtcNow;
        _policy.OnReturned(slot.Resource!); // scrub any per-lease state before next caller (ADR-0009)
        _idle.Push(slot);
        _capacityGate.Release();
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

    private bool IsExpiredByIdleLifetime(Slot<T> slot)
    {
        if (_options.MaxIdleLifetime is not { } maxLifetime)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - slot.BecameIdleAt > maxLifetime;
    }

    private async Task<bool> RecycleInPlaceAsync(Slot<T> slot, CancellationToken cancellationToken)
    {
        slot.State = SlotState.Recycling;
        Interlocked.Increment(ref _unhealthyOrRecyclingCount);
        PublishHealthChanged(SlotHealthState.RecyclingStarted, slot.LastIncident);
        await SafeDisposeAsync(slot.Resource).ConfigureAwait(false);

        try
        {
            slot.Resource = await CreateThroughGateAsync(cancellationToken).ConfigureAwait(false);
            slot.ClearIncident();
            slot.State = SlotState.Idle;
            slot.BecameIdleAt = DateTimeOffset.UtcNow;
            Interlocked.Exchange(ref _consecutiveCreateFailures, 0);
            Interlocked.Exchange(ref _consecutiveOperationalFailures, 0); // a fresh resource is presumed operationally healthy again
            PublishHealthChanged(SlotHealthState.Recovered, null);
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _consecutiveCreateFailures);
            PublishHealthChanged(SlotHealthState.RecoveryFailed, new PoolIncidentInfo(ex, DateTimeOffset.UtcNow));
            Interlocked.Decrement(ref _createdCount);
            return false;
        }
        finally
        {
            Interlocked.Decrement(ref _unhealthyOrRecyclingCount);
        }
    }

    private async Task RecycleInBackgroundAsync(Slot<T> slot)
    {
        var recycled = await RecycleInPlaceAsync(slot, CancellationToken.None).ConfigureAwait(false);

        if (Volatile.Read(ref _poolDisposed) == 1)
        {
            if (recycled)
            {
                await SafeDisposeAsync(slot.Resource).ConfigureAwait(false);
            }
        }
        else if (recycled)
        {
            _idle.Push(slot);
        }

        // Whether recovery succeeded or the slot's capacity was permanently given up, the permit is
        // released so waiters can proceed (either using the recycled slot, or creating a new one).
        _capacityGate.Release();
    }

    private async Task<Slot<T>> CreateNewSlotAsync(CancellationToken cancellationToken)
    {
        var resource = await CreateThroughGateAsync(cancellationToken).ConfigureAwait(false);
        return new Slot<T> { Resource = resource, State = SlotState.Idle, BecameIdleAt = DateTimeOffset.UtcNow };
    }

    /// <summary>
    /// Invokes <see cref="IPooledResourcePolicy{T}.CreateAsync"/> behind the serial creation gate,
    /// optionally bounded by <see cref="PoolOptions.CreateTimeout"/>. On timeout the gate is released
    /// immediately and the abandoned creation is left to finish (and be disposed) in the background.
    /// See docs/adr/0007 (#4).
    /// </summary>
    private async Task<T> CreateThroughGateAsync(CancellationToken cancellationToken)
    {
        await _creationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var gateReleased = false;
        try
        {
            var createTask = _policy.CreateAsync(cancellationToken);

            Task completed;
            if (_options.CreateTimeout is { } timeout)
            {
                completed = await Task.WhenAny(createTask, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
            }
            else
            {
                completed = createTask;
            }

            if (completed != createTask)
            {
                // Timed out. Release the gate now (see tradeoff in ADR-0007 #4) and let the
                // abandoned task finish on its own; dispose whatever it eventually produces.
                _creationGate.Release();
                gateReleased = true;
                _ = AbandonCreationAsync(createTask);
                Interlocked.Increment(ref _consecutiveCreateFailures);
                throw new TimeoutException(
                    $"Creating a pooled resource did not complete within {_options.CreateTimeout}.");
            }

            var resource = await createTask.ConfigureAwait(false);
            Interlocked.Increment(ref _createdCount);
            Interlocked.Exchange(ref _consecutiveCreateFailures, 0);
            return resource;
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch
        {
            Interlocked.Increment(ref _consecutiveCreateFailures);
            throw;
        }
        finally
        {
            if (!gateReleased)
            {
                _creationGate.Release();
            }
        }
    }

    private async Task AbandonCreationAsync(Task<T> createTask)
    {
        try
        {
            var resource = await createTask.ConfigureAwait(false);
            await SafeDisposeAsync(resource).ConfigureAwait(false);
        }
        catch
        {
            // The original caller already observed the timeout; nothing more to act on here.
        }
    }

    private async ValueTask SafeDisposeAsync(T? resource)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            await _policy.DisposeResourceAsync(resource).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: disposing an already-broken resource failing is not itself fatal.
        }
    }

    private async ValueTask DisposeAbandonedSlotAsync(Slot<T> slot)
    {
        await SafeDisposeAsync(slot.Resource).ConfigureAwait(false);
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
        for (var i = 0; i < _options.MaxSize; i++)
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

        for (var i = 0; i < acquiredPermits; i++)
        {
            _capacityGate.Release();
        }

        while (_idle.TryPop(out var slot))
        {
            await SafeDisposeAsync(slot.Resource).ConfigureAwait(false);
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
