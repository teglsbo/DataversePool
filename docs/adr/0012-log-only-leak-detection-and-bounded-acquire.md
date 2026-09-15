# ADR-0012: Bounded acquire (timeout), operational-failure-aware circuit breaker, log-only leak detection

## Status
Accepted

**Update (ADR-0013):** `AcquireTimeout`, as described here, originally bounded only
`_capacityGate.WaitAsync` — not the rest of the acquire operation (recycle/creation after permit).
ADR-0013 extends it to bound the entire acquire operation end-to-end. Likewise,
`ConsecutiveOperationalFailures` (introduced here) was originally reset by every successful recycle, which
ADR-0013 fixes. See ADR-0013 for details; this ADR's other decisions (log-only
leak detection, #3/#5 accepted unchanged) remain valid as described.

## Context
After ADR-0011 was committed, the user was presented with the remaining, deliberately deferred
backlog from the second review round (the DB pool design expert + distributed-systems re-review) and made
explicit decisions on each item:

1. **Bounded acquire queue/deadline** — assessed by both experts as the highest remaining
   priority: `ResourcePool<T>.AcquireAsync` currently waits forever for capacity, with no way to
   provide real backpressure. The user's response: "sounds like a good idea to have it bounded" + asked
   what typical DB pool practice is.
2. **Operational-failure-aware circuit breaker** — the circuit in `MemberCircuitBreaker` reacted only
   to creation failures (`ConsecutiveCreateFailures`), not to operational failures reported via
   `PooledLease.MarkUnhealthy` on an already-created resource. The user's response: "yes - necessary,
   so you do not keep pushing a bad one around to everyone."
3. **Cross-member concurrent `CreateAsync` in `DataversePool`** — the DB pool expert flagged this
   as a possible reintroduction of ADR-0002's lock-contention problem, but also recommended empirical
   verification of whether the SDK's internal lock is per instance or process-global before building a
   gate. The user's response: "probably just per instance; it only costs a little extra time to create if
   you do not serialize." → accepted as a known, small, insignificant cost; NO code changed.
4. **Log-only leak detection** — the DB pool expert recommended (like HikariCP) making
   leak detection purely diagnostic instead of recycling/disposing a potentially-still-in-use
   resource. ADR-0011 deliberately rejected this without the user's explicit input. This time, the user's response
   was: "log only." → implemented.
5. **Cross-process coordination** — still explicitly rejected ("agree" with leaving it alone). No
   change.

Typical practice for point 1, which the user asked about: HikariCP's `connectionTimeout` (default 30s,
throws `SQLTransientConnectionException` on expiry), Npgsql's `Timeout`, and SqlClient's
`Connect Timeout` all use a **timeout on the waiting time itself** for an available connection — not a
hard limit on the number of waiting callers. The reasoning is: a waiting caller is cheap (just a
suspended `Task`/`await`), so the risk that needs bounding is caller pile-up/lack of backpressure,
not memory usage. The same pattern is chosen here.

## Decision

### Fix for #1: `PoolOptions.AcquireTimeout`
New, optional (`null` default, backward-compatible) `TimeSpan? AcquireTimeout`. When set,
`ResourcePool<T>.AcquireAsync` uses `SemaphoreSlim.WaitAsync(timeout, cancellationToken)` on the
capacity gate instead of an unbounded `WaitAsync(cancellationToken)`. On expiry, it throws a new,
public `PoolAcquireTimeoutException` (inherits `TimeoutException`, like `CreateTimeout`'s existing
exception, for consistent error handling) with a `PoolStats` snapshot for diagnostics (how
many are waiting, how many are idle/created, etc.). No change to default behavior — existing
calls without `AcquireTimeout` set still wait forever, as before.

### Fix for #2: `PoolStats.ConsecutiveOperationalFailures` + `MemberCircuitBreaker` reacts to both signals
- New counter in `ResourcePool<T>`: `_consecutiveOperationalFailures`, incremented by a new internal
  `ReportOperationalFailure()` method, called from `PooledLease.MarkUnhealthy` (i.e., every time a
  user reports that an *already handed-out* resource failed operationally — not during
  creation).
- Reset by any subsequent healthy `ReturnAsync` (a successful use is evidence of
  recovery) and by successful `RecycleInPlaceAsync` (a fresh resource is assumed operationally
  healthy again) — the same pattern `ConsecutiveCreateFailures` already used.
- Exposed on `PoolStats` as `ConsecutiveOperationalFailures`.
- `MemberCircuitBreaker.IsEligible` now opens the circuit if **either** `ConsecutiveCreateFailures`
  **or** `ConsecutiveOperationalFailures` reaches the threshold — a member that creates fine, but whose
  resources consistently fail in actual use, is now treated just as seriously as a member that cannot
  be created at all, and is rotated away from instead of continuing to receive traffic
  ("pushing a bad one around to everyone," as the user put it).

### Fix for #4: Log-only leak detection
`ResourcePool<T>.ReportLeakedLease`/`CompleteLeakReport` have been rewritten to be **purely diagnostic**:
- On a GC-detected leaked lease, only incident metadata is recorded (for `OnLeakDetected`/
  `HealthChanges`) — the pool NO LONGER calls `RecycleInBackgroundAsync`, does NOT dispose
  the resource, and does NOT release the capacity permit.
- New `SlotHealthState.LeakDetected` value (separate from `MarkedUnhealthy`, which still means "will
  be recycled") makes it possible for observability code to distinguish "we suspect a leak, purely
  informational" from "this slot is actively being recovered now."
- **Consequence, explicitly accepted by the user:** a real leak now reduces the pool's effective
  capacity permanently by one slot until the process restarts — exactly the same real
  operational experience HikariCP's log-only leak detection gives in practice. `OnLeakDetected`/
  `HealthChanges` are therefore no longer "nice to have" telemetry, but the only way you can
  discover that this happened and act on it (alert, restart the process, inspect the source code for
  the missing `DisposeAsync` call).
- The finalizer thread safety from ADR-0011 (dispatch via `ThreadPool.QueueUserWorkItem`, try/catch around
  the callback and each observer) is preserved unchanged — only *what* happens after reporting has
  changed, not *how* it is reported safely.
- `PooledLease<T>` and `PoolOptions.OnLeakDetected`'s XML docs have been updated to describe the new
  behavior. ADR-0003 (which originally described leak tracking as "evacuates/restores the slot") is
  annotated with a reference to this ADR instead of being rewritten, in accordance with `docs/adr/README.md`'s
  convention of not editing an accepted ADR's decision.

### #3 and #5: no code change
- **#3** (cross-member concurrent creation): the user accepted the presumed small extra
  clone cost of not serializing across group members instead of building an unverified gate. No code changed; remains documented as a known, accepted trade-off.
- **#5** (cross-process coordination): still explicitly rejected. No change.

## Consequences
- **Backward-compatible for #1/#2**: `AcquireTimeout` is `null` by default (unbounded wait,
  unchanged behavior); `ConsecutiveOperationalFailures` is a new, additive `PoolStats` property with
  default `0`.
- **Behavior change for #4 (intentional, not backward-compatible in practice)**: any existing user
  who implicitly assumed that a leak would be "repaired by the pool itself" now instead experiences a
  permanent reduction in effective capacity upon a real leak. This is an intentional
  product decision made explicitly by the user, not a regression.
- `PoolAcquireTimeoutException` is a new public type (inherits `TimeoutException`, and is therefore also caught
  by existing `catch (TimeoutException)` code that already handles `CreateTimeout`).
- Test coverage: 66/66 passing after this change (Core 21 [+4: 3 new `AcquireTimeoutTests` and net
  +1 in leak tests after rewriting from 2 to 3 log-only tests], Dataverse 42 [+1: operational failure
  opens the circuit], Polly 3), up from 61/61. `LeakReportingSafetyTests` from ADR-0011 were rewritten
  to verify the new log-only behavior (never disposed, capacity permanently lost, still
  no crash/stranded capacity with failing callback/observer) instead of the old
  recycle behavior.
