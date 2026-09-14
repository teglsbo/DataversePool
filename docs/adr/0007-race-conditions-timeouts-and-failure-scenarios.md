# ADR-0007: Race conditions, timeouts, and failure scenarios — identified gaps and fixes

## Status
Accepted (fixes bugs in the existing ConnectionPool.Core/Dataverse implementation)

## Context
During a systematic review of race conditions, timing, and failure scenarios in the existing
implementation, the following were identified:

### 1. `MarkUnhealthy` after `DisposeAsync` (use-after-dispose race)
`PooledLease<T>.MarkUnhealthy` did not check the `_disposed` flag. If user code (incorrectly) calls
`MarkUnhealthy` from a background thread after the lease has already been disposed, it would mutate a slot that
is either already back on the idle stack or leased out to *another* leaser — data corruption.

**Fix:** `MarkUnhealthy` checks `_disposed` and becomes a no-op if the lease is already disposed.
This is still not 100% race-free (TOCTOU between check and mutation), but it reduces the window
significantly. Full safety requires the user code to comply with the isolation contract from ADR-0003
(never call `MarkUnhealthy` concurrently with/after `DisposeAsync` from another thread).

### 2. Pool shutdown while leases are still "in flight"
`ResourcePool<T>.DisposeAsync` only drained the `_idle` stack. It neither prevented new
`AcquireAsync` calls nor handled leases returned *after* the pool was disposed —
these were simply pushed back onto `_idle` and never drained again. Real scenario: application shutdown
while requests are in flight → leaked `ServiceClient` connections/handles.

**Fix:**
- `AcquireAsync` throws `ObjectDisposedException` if the pool is disposed.
- `DisposeAsync` attempts a best-effort drain (waits briefly, per permit, for outstanding
  leases to be returned) before it drains `_idle`.
- `ReturnAsync`/background recycle checks `_poolDisposed` and disposes the resource directly instead
  of reinserting it into `_idle`, if a lease is returned after shutdown has started.

### 3. `PoolStats.UnhealthyOrRecyclingCount` was hardcoded to 0
This made it impossible for a future throttle/health-aware selection strategy (or telemetry) to see
how many slots were actually in the process of being recovered.

**Fix:** It is now counted correctly with `Interlocked` counters, incremented when a slot is marked
unhealthy/leaked, decremented when recycling finishes (success or permanently abandoned).

### 4. Hanging/very slow `CreateAsync` blocks the entire pool's creation gate indefinitely
The serial creation gate (ADR-0002) is necessary to avoid lock contention during parallel cloning,
but it has a downside: if one call to `policy.CreateAsync` hangs (network partition, DNS timeout,
an SDK call that never returns), the *entire* pool's ability to create new resources is frozen
indefinitely — including for other users/threads that are simply waiting for any free resource
and do not themselves hit the creation path.

**Fix:** New optional `PoolOptions.CreateTimeout`. Creation is wrapped in a `Task.WhenAny` against a
timer. On timeout:
- The creation gate is released immediately (the pool can continue), and a `TimeoutException` is thrown to
  the calling `AcquireAsync`.
- The original, now "abandoned" `CreateAsync` call may still complete in the background (it cannot be
  cooperatively cancelled) — the result is disposed automatically when/if it eventually returns.
- **Intentional tradeoff:** in rare cases (only on an actual timeout), this can allow two real
  clone attempts to overlap in time — this is an accepted deviation from the strict ADR-0002 guarantee,
  because the alternative (a permanently deadlocked pool) is worse.

### 5. No max lifetime for idle resources (equivalent to ADO.NET's "Connection Lifetime")
A resource that has been idle for a long time may have silently lost its connection (token expired without
`IsReady` necessarily detecting it proactively) — it is first handed out as "healthy" and then fails during
actual use. Today this is only detected reactively (`MarkUnhealthy` from user code), which is an
accepted v1 behavior (ADR-0004), but it can be improved proactively.

**Fix (partial, follow-up):** `PoolOptions.MaxIdleLifetime` (optional) — if set, the age of an
idle slot is checked at checkout, and it is recycled inline (same code path as an unhealthy slot) if it has
been idle longer than the limit. Not a background timer in v1 (avoids complexity from a separate
sweep thread) — checked lazily on the next `AcquireAsync`.

### 6. "One user in a group is dead" — round-robin still sends 1/N traffic to a permanently failing user
If one member in a `DataverseGroupPool` is permanently unavailable (blocked app user, incorrect
secret, blocked IP), pure round-robin will keep sending every Nth request there, where it will
either hang (until `CreateTimeout`) or fail repeatedly — poor tail latency and wasted
capacity, without the other members compensating.

**Fix:** New `PoolStats.ConsecutiveCreateFailures` counter per member pool. A new
`HealthAwareRoundRobinSlotSelectionStrategy` skips members where this counter exceeds a
configurable threshold ("circuit open"), with cooldown-based "half-open" retry logic (after X
time, the member is tried again for one request; if it succeeds, the counter is reset). If *all* members are
circuit-open at the same time (for example a temporary total network failure), one member is still selected
round-robin (fail open) rather than throwing an exception without fallback — an intentional choice to avoid the
group "locking itself out" permanently during a short-lived shared failure.

## Consequences
- Core and the Dataverse adapter are now robust against the most important identified race conditions and
  hanging scenarios, verified with new tests (see `TimeoutAndShutdownTests`,
  `HealthAwareRoundRobinSlotSelectionStrategyTests`).
- `CreateTimeout` and `MaxIdleLifetime` are opt-in (default: no timeout/lifetime limit) so as not to
  change default behavior for existing users of the library.
- Still open: full protection against the `MarkUnhealthy` race requires user discipline (ADR-0003); a
  background sweep for idle max lifetime (instead of only lazy-at-checkout) is postponed to v2 if
  the need arises.
