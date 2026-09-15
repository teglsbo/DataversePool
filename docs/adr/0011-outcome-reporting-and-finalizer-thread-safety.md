# ADR-0011: Outcome-based probe completion + finalizer thread safety

## Status
Accepted (partially — see "Deliberately not resolved here" for what remains open)

## Context
After ADR-0009/0010 was committed, another round of three parallel reviews was run against the entire
codebase: security, a DB connection-pool design expert, and a distributed-systems expert
(who specifically re-verified the ADR-0009/0010 fixes instead of taking them at face value).

**Security**: no new findings. CallerId scrubbing (ADR-0009) was confirmed correct and complete;
`MemberCircuitBreaker`'s lock was assessed as race-free for the data structure itself.

**Distributed-systems re-review** found that ADR-0010's single-probe fix **was not complete**:
1. `probeClaimTimeout` is a blind timer, not a signal of the probe's real outcome. If the
   original prober is still hanging in `CreateAsync` (unbounded without `PoolOptions.CreateTimeout`)
   when the timeout expires, a *new* probe can be assigned concurrently with the old one — exactly the
   thundering-herd bug ADR-0010 was supposed to fix.
2. `MemberCircuitBreaker`'s constructor did not validate that `cooldownPeriod`/`probeClaimTimeout` are
   positive — `TimeSpan.Zero` would let all concurrent callers win the probe at once.
3. (Confirmed still open, expected): the circuit still measures only creation failures, not operational failures.
4. (Confirmed still open, expected): no bounded acquire queue/deadline in `ResourcePool`.
5. (Non-blocking): `EarliestKnownRetryAt` in `DataversePoolUnavailableException` could report
   an already expired throttle timestamp because throttle ticks were never cleared after expiry.

**The DB pool design expert** found three "blocking"-level findings:
1. **Finalizer-based leak reclaim can dispose a resource that is actually still in use** — if
   a caller only loses its reference to the `PooledLease<T>` itself (while still using
   `lease.Resource` somewhere), GC can finalize the lease and trigger recycle/dispose while the
   resource is simultaneously being used. Unlike, for example, HikariCP, which by default only *logs*
   suspected leaks without acting on them.
2. **User callbacks (`OnLeakDetected`, `HealthChanges` observers) are invoked synchronously on the
   finalizer thread itself.** An unhandled exception there is process-fatal, and a slow/blocking
   subscriber would delay finalization of everything else.
3. **The serialized creation gate (ADR-0002) applies only *within* one `DataverseUserPool`'s own
   `ResourcePool`** — under normal load (not just warmup), `DataversePool` can absolutely trigger
   concurrent `CreateAsync` calls across *different* members, which could potentially hit the same
   SDK-internal lock contention (1-3.2s) identified by ADR-0002 — but this is unverified without empirical
   measurement of whether the SDK's internal lock is per instance or process-global.

## Decision

### Fix: `MemberCircuitBreaker.CompleteProbe(member, succeeded)` — explicit outcome reporting
Instead of relying exclusively on `probeClaimTimeout` expiring, callers can/should now
report the actual outcome of an acquire attempt:
- **Success** clears all breaker bookkeeping for the member immediately (closing the circuit without
  waiting for the next stats snapshot or for `probeClaimTimeout` to expire).
- **Failure** restarts the cooldown window from now and releases the claim immediately, so the *next*
  cooldown's probe is not unnecessarily blocked by an already decided attempt.

`DataversePool.AcquireAsync` now calls `_strategy.ReportAcquireOutcome(member, succeeded)`
after each attempt (in a try/catch around the actual `AcquireAsync` call on the selected member) —
`OperationCanceledException` from the caller's own cancellation token is deliberately NOT reported as an
outcome (cancellation is not a health signal about the member). `ISlotSelectionStrategy` got a new
default no-op method `ReportAcquireOutcome`, so strategies without circuit breaking (ordinary
round-robin) do not need to change.

**This does not close the entire hole, and we do not claim that it does:** if the acquire attempt itself
hangs forever without `PoolOptions.CreateTimeout` set, no outcome is reported, and the
`probeClaimTimeout` fallback is still what eventually allows a new probe — the
original race from the review can still theoretically occur in that case. Likewise, if a
half-open attempt happens to hit an already-created idle resource (no `CreateAsync`
is called at all), a "success" does not prove that the member can actually create connections again —
it is still an approximation. The recommendation to operators is therefore explicit: **set
`PoolOptions.CreateTimeout`** to bound this residual case. Documented directly in the
XML doc on `CompleteProbe`.

### Fix: input validation in `MemberCircuitBreaker`'s constructor
`cooldownPeriod <= TimeSpan.Zero` and `probeClaimTimeout <= TimeSpan.Zero` now throw
`ArgumentOutOfRangeException` instead of silently accepting values that would destroy the
single-probe guarantee.

### Fix: finalizer thread safety in `ResourcePool<T>`
`ReportLeakedLease` (called directly from `PooledLease<T>`'s finalizer) is now limited to cheap,
exception-free field updates. The actual callback invocation (`OnLeakDetected`) and
observer notification (`PublishHealthChanged`/`HealthChanges`) are now dispatched via
`ThreadPool.QueueUserWorkItem` to an ordinary thread-pool thread — not the finalizer thread. Both
`OnLeakDetected` invocation and each individual observer's `OnNext` are also now wrapped in try/catch: a
failing subscriber can neither (a) crash the process via an unhandled exception on the
finalizer thread, (b) block other observers from being notified, nor (c) prevent the
slot from still being sent to recycling afterward.

**We deliberately did NOT change the fundamental architectural decision** (ADR-0003/0004) that a
leaked lease triggers recycle/dispose of the underlying resource. The DB pool expert's stronger
recommendation — make leak detection purely diagnostic (log only, never dispose, like HikariCP) — was
considered, but rejected for now: it would reverse an already made, documented
design decision without the user's explicit input, and it removes a safety-net property
(a truly forgotten/leaked resource is never reused by a future caller in an unknown
state). The *real* bug here was not "recycling is a bad idea," but that
callback execution happened unsafely on the finalizer thread — that is fixed. The risk of
disposing a technically still-in-use resource (because only the lease wrapper, not the resource itself,
lost all references) is an inherent consequence of the leak-detection design itself and remains
presented as a known trade-off, not a bug to "fix" without changing the entire model.

### Fix: stale throttle tick in `DataversePool.BuildUnavailableException`
`EarliestKnownRetryAt` calculation now filters `ThrottledUntil` values down to only those still
in the future (`> DateTimeOffset.UtcNow`) before `Min()` is computed — an already expired throttle tick
(which is never proactively cleared) is no longer incorrectly reported as a valid future
retry time.

## Deliberately not resolved here
The following findings from this review round are **confirmed real, but deliberately not fixed** in this ADR —
either because they require a larger architectural shift, empirical verification, or an explicit
product decision from the user, which was not available when this work was performed:

- **Bounded acquire queue/deadline** (`AcquireTimeout`/`MaxWaiters` in `ResourcePool.AcquireAsync`) —
  assessed by both experts as the highest remaining priority. Not built here because it is a
  non-trivial, potentially breaking API extension (new option, new exception type for
  queue timeout/overflow) that deserves its own dedicated round.
- **Outcome-driven circuit breaker for operational failures** (not only creation failures) — requires that
  `PooledLease.MarkUnhealthy` (or a new mechanism) feeds the same `ConsecutiveCreateFailures`-like
  signal that the group strategies read, which is a larger change to `PoolStats`/`ResourcePool`'s
  responsibility split.
- **Cross-member concurrent `CreateAsync` in `DataversePool`** — the DB pool expert himself
  recommended empirical verification (measure whether the SDK's lock contention is per instance or
  process-global) BEFORE building a global gate across group members, so as not to
  introduce unnecessary serialization that does not solve a real problem.
- **Leak detection as purely diagnostic (log-only)** — see the rationale above; a deliberate deviation
  from the DB pool expert's recommendation, not an error.
- **Cross-process budget coordination** (ADR-0009/0010 point #1) — still explicitly rejected by the
  user ("no shared state between processes").

## Consequences
- **Not breaking**: `CompleteProbe` and `ReportAcquireOutcome` are additive APIs
  (`ISlotSelectionStrategy.ReportAcquireOutcome` has a default no-op implementation). Existing
  custom strategies compile unchanged.
- `MemberCircuitBreaker`'s constructor now throws for previously accepted (but meaningless)
  `TimeSpan.Zero`/negative inputs — technically breaking for anyone who (incorrectly) used these, but
  judged desirable: that kind of configuration was always a bug.
- Test coverage: 61/61 passing after this change (Core 17 [+2], Dataverse 41 [+7], Polly 3),
  including new tests for TimeSpan validation, `CompleteProbe` success/failure behavior, stale
  throttle-tick filtering, and GC-triggered leak reporting with an intentionally failing
  callback/observer (verifies no exception escapes, and that the slot is still recycled).
