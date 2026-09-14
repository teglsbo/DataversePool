namespace ConnectionPool.Core;

// Creation and recycling concerns for ResourcePool<T>, split out purely for readability
// (no behavior change) - the fields, gates, and counters below are declared in ResourcePool.cs.
public sealed partial class ResourcePool<T> where T : notnull
{
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
}
