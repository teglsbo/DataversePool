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

    // Serializes the "check current count against target, then create the shortfall" decision
    // across concurrent WarmupAsync callers. Without this, two overlapping WarmupAsync calls can
    // both observe CreatedCount below target (idle resources don't hold a capacity permit, so the
    // capacity gate alone doesn't prevent this), both proceed to create, and jointly overshoot
    // MaxSize - see docs/adr/0014.
    private readonly SemaphoreSlim _warmupGate = new(1, 1);

    private readonly ConcurrentStack<Slot<T>> _idle = new();
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

        _capacityGate = new SemaphoreSlim(_options.MaxSize, _options.MaxSize);
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
        var target = Math.Min(_options.PrewarmCount, _options.MaxSize);

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
                    _idle.Push(slot);
                }
                finally
                {
                    // Matches the same permit lifecycle as every other idle resource in this pool: the
                    // permit represents "an acquire is actively in progress against this unit of
                    // capacity," not "a resource physically exists" - idle resources sit in _idle with
                    // their permit already released, to be re-consumed by whichever AcquireAsync next
                    // claims them. See docs/adr/0013.
                    _capacityGate.Release();
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
                _capacityGate.Release();
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
            MaxSize: _options.MaxSize,
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
            // Deliberately do NOT reset _consecutiveOperationalFailures here: successfully cloning a
            // replacement resource is evidence the member can be *created*, not that it can serve a
            // real operation successfully. Resetting on recycle-success let a member whose every
            // operation fails (but whose ServiceClient.Clone keeps succeeding) cycle
            // fail -> recycle -> reset forever without ever reaching the breaker's threshold - see
            // docs/adr/0013. Only a genuinely healthy lease return (ReturnAsync) resets this counter.
            PublishHealthChanged(SlotHealthState.Recovered, null);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Cancellation (caller-initiated, or an AcquireTimeout deadline) must propagate so the
            // caller (AcquireAsync) can translate it correctly - it is not evidence this member is
            // unhealthy, so it must NOT be counted as a create failure (that would incorrectly help
            // trip the circuit breaker for what was really just a caller giving up waiting). The old
            // resource was already disposed above and no replacement was created, so the slot's
            // capacity genuinely is gone; reflect that in _createdCount without touching the
            // create-failure counter or publishing a health incident. See docs/adr/0014.
            Interlocked.Decrement(ref _createdCount);
            throw;
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
                // The delay "won" the race - but that can happen for two different reasons that
                // must not be conflated: either CreateTimeout genuinely elapsed (a signal about
                // *this member's* health), or cancellationToken itself fired first (caller
                // cancellation, or - via AcquireAsync's linked token - PoolOptions.AcquireTimeout
                // expiring while creation was still in flight) and Task.Delay observed that instead.
                // A cancellation is not evidence of a create failure and must propagate as
                // OperationCanceledException so AcquireAsync can translate it correctly (e.g. into
                // PoolAcquireTimeoutException) rather than being misreported as a timed-out create -
                // see docs/adr/0014.
                _creationGate.Release();
                gateReleased = true;
                _ = AbandonCreationAsync(createTask);

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

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
        catch (OperationCanceledException)
        {
            // Cancellation (caller-initiated, or an AcquireTimeout deadline flowing through the
            // linked token) is not evidence of a create failure - don't let it trip/extend the
            // circuit. See docs/adr/0014.
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
