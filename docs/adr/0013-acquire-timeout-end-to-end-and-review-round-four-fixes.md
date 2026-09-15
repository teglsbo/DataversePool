# ADR-0013: End-to-end AcquireTimeout, fair operational-failure counting, idempotent warmup, correct probe-outcome reporting, durable leak visibility

## Status
Accepted. **Partially superseded by [ADR-0014](0014-probe-claim-generation-and-cancellation-vs-createtimeout-misclassification.md):**
a fifth review round found that fix #1 (end-to-end `AcquireTimeout`) and fix #4 (`AbandonProbe`)
were only partially effective — a new regression (an `AcquireTimeout` cancellation that hits during an
ongoing `CreateAsync` was incorrectly classified as a `CreateTimeout` and therefore a failed
health probe) bypassed both. ADR-0014 fixes this regression and also adds claim-generation
correlation to `MemberCircuitBreaker`. Fixes #2, #3, #5, #6 below remain unaffected and valid.

## Context
After ADR-0012 was committed (`0c35c39`), the user asked for yet another (fourth) review round: security
+ DB pool design expert + distributed-systems expert, all aimed at the just-committed code.
The security review found nothing new. The other two independently found essentially the **same
root cause** from two different angles, along with additional real, well-defined bugs:

1. **`AcquireTimeout` bounds only `_capacityGate.WaitAsync`, not the entire acquire path** — both experts
   found this. DB pool expert: once a permit is obtained, recycle/creation after that can take
   unlimited time with no `AcquireTimeout` boundary. Distributed-systems expert: it was
   *also* the reason the probe-claim race from ADR-0011 was still genuinely open — the first
   half-open prober can get stuck in this unbounded phase while `probeClaimTimeout` expires and a new
   prober wins, even though the first is still legitimately waiting (not hung).
2. **`ConsecutiveOperationalFailures` resets too aggressively** — any successful
   `RecycleInPlaceAsync` (i.e., a fresh clone after an operational failure) reset the counter. A
   member whose operations consistently fail, but whose `ServiceClient.Clone` still succeeds, would
   therefore never reach the threshold: fail → recycle succeeds → reset → fail → recycle succeeds → reset...
   This undermined the entire purpose of ADR-0012's operational circuit signal.
3. **`WarmupAsync` is not idempotent** — each call creates `min(PrewarmCount, MaxSize)` *more*
   resources, regardless of how many already exist. A repeated call (e.g., a retried
   startup hook) can therefore exceed `MaxSize`.
4. **A capacity timeout is incorrectly reported as a failed circuit probe** —
   `PoolAcquireTimeoutException` is caught by `DataversePool.AcquireAsync`'s general `catch` and
   reported to `MemberCircuitBreaker` as `succeeded: false`. A pure load/capacity timeout is
   not evidence that the *member* is unhealthy, but it could unnecessarily extend a recovering member's
   cooldown.
5. **Log-only leaks are invisible in `PoolStats`** — the only evidence of a permanently lost slot
   (ADR-0012's deliberate trade-off) was a transient callback/event; a late or missing subscriber
   cannot see it afterward, and a generic `PoolAcquireTimeoutException` looks identical whether the
   cause is real load or leaked capacity.
6. **`PoolOptions` durations were unvalidated** in `ResourcePool`'s constructor (only `MaxSize` was
   checked) — a negative `AcquireTimeout`/`CreateTimeout`/`MaxIdleLifetime` would fail late and
   confusingly instead of immediately.

The user was not available to prioritize this round (autopilot); I therefore chose to
fix all six findings, because they are all well-defined bugs/robustness improvements without
architectural reversal or new scope, consistent with the pattern from earlier rounds.

## Decision

### Fix for #1: End-to-end `AcquireTimeout`
`ResourcePool<T>.AcquireAsync` now creates, when `PoolOptions.AcquireTimeout` is set, a
`CancellationTokenSource` with that duration, linked with the caller's own token via
`CancellationTokenSource.CreateLinkedTokenSource`. This linked token (`effectiveToken`) is used
for **all** subsequent work: `_capacityGate.WaitAsync`, idle-lifetime recycle
(`RecycleInPlaceAsync`), and new creation (`CreateNewSlotAsync` → `CreateThroughGateAsync`, including
its own `_creationGate.WaitAsync`). If the deadline expires (and not the caller's own token),
`OperationCanceledException` is caught and translated into `PoolAcquireTimeoutException` — not a raw
cancellation that would otherwise be impossible to distinguish from caller-initiated cancellation.

**Important, documented residual limitation:** this is *cooperative* cancellation. If the specific,
in-progress `CreateAsync` call neither respects `CancellationToken` itself nor is bounded by
`PoolOptions.CreateTimeout`, that particular call cannot be interrupted by `AcquireTimeout` alone — only
the waiting time *in front of* that call (e.g., behind `_creationGate` while another serialized call runs) is
guaranteed bounded. `AcquireTimeout`'s XML doc has been updated to explicitly recommend configuring
`CreateTimeout` together with `AcquireTimeout` for a real worst-case bound. This is the same category
of residual risk as the already documented "hanging `CreateAsync` without `CreateTimeout`" limitation
from ADR-0011's `probeClaimTimeout` fallback.

A new regression test proves the concrete, originally reported bug is fixed: with `MaxSize=2`
both callers can immediately obtain a capacity permit, but creation is always serialized
(`docs/adr/0002`) — the second caller, which previously would wait forever behind the first caller's slow
creation, now correctly times out near `AcquireTimeout`.

### Fix for #2: The operational-failure counter resets only on a truly healthy return
Removed: `Interlocked.Exchange(ref _consecutiveOperationalFailures, 0)` from
`RecycleInPlaceAsync`'s success branch. Successfully cloning a replacement resource is evidence that the
member *can be created*, not that it can perform a real operation successfully — only a
subsequent, actually healthy `ReturnAsync` (i.e., no `MarkUnhealthy` was called on the lease) now resets
 the counter. A member whose operations consistently fail will therefore correctly accumulate
`ConsecutiveOperationalFailures` across repeated recycles and eventually reach the threshold.

*Known, not fixed nuance (documented, not a blocking finding):* a single, long-running, lucky
lease that returns healthy after several newer leases have already failed can still reset the
counter "too early" because of concurrent traffic — a fully time-window/rate-based
outcome tracker would solve this generally, but is a larger architectural change than this round
addresses; the current "consecutive" model is an approximation, just as before.

### Fix for #3: `WarmupAsync` is now idempotent
Rewritten from "unconditionally create `min(PrewarmCount, MaxSize)` *more*" to "ensure that
`_createdCount` reaches at least `min(PrewarmCount, MaxSize)` by creating only the difference." Checks
`_createdCount` before *and* after taking a capacity permit (to handle races with
concurrent `WarmupAsync`/lazy `AcquireAsync` creation), and only tops up by the actual
shortfall. The permit lifecycle is unchanged from before (permit is taken, resource is created and placed in
`_idle`, permit is released again — idle resources never hold a permit themselves in this pool's model;
only an active lease/creation does). Repeated calls with the same `PrewarmCount` are now no-ops.

### Fix for #4: Capacity timeout is no longer reported as a failed probe
New `MemberCircuitBreaker.AbandonProbe(member)`: releases a claimed half-open probe **without**
restarting the cooldown window (unlike `CompleteProbe(succeeded: false)`). New default no-op
`ISlotSelectionStrategy.ReportAcquireAbandoned(member)`, implemented by both
circuit-breaker-aware strategies to call `_breaker.AbandonProbe`.
`DataversePool.AcquireAsync` now catches `PoolAcquireTimeoutException` specifically, *before* the
general `catch`, and calls `ReportAcquireAbandoned` instead of `ReportAcquireOutcome(false)`.

### Fix for #5: `PoolStats.DetectedLeakCount` — durable leak visibility
New, monotonically increasing `PoolStats.DetectedLeakCount`, incremented **synchronously** in
`ReportLeakedLease` (before callback/event dispatch to `ThreadPool`), so it is reliably visible via
`GetStats()` even if no `OnLeakDetected`/`HealthChanges` subscriber was ever attached.
`PoolAcquireTimeoutException`'s message now includes `DetectedLeakCount` and, if > 0, an explicit
note that rising leak count together with timeouts indicates lost capacity, not only load.

### Fix for #6: `PoolOptions` durations are validated in the constructor
`ResourcePool<T>`'s constructor now throws `ArgumentOutOfRangeException` if `AcquireTimeout`,
`CreateTimeout`, or `MaxIdleLifetime` is set to a non-positive duration. `null` remains the
only way to signal "disabled/unbounded" for all three.

## Consequences
- **Backward-compatible** for all six fixes: no public default behavior changes for code that
  does not use the affected features (`AcquireTimeout`/`CreateTimeout`/`MaxIdleLifetime` remain
  `null` by default; `DetectedLeakCount` is a new, additive `PoolStats` property with default `0`;
  `ReportAcquireAbandoned` is a new default no-op interface method).
- **Sharper error classification in `DataversePool`**: a pure capacity/load timeout no longer
  incorrectly affects a member's circuit cooldown — only real create/operational failures do.
- **`ConsecutiveOperationalFailures` is now a reliable signal** for the originally intended
  use case (a member whose operations consistently fail, but whose cloning succeeds) — the known
  concurrency nuance (a single late, lucky return can still reset under mixed traffic) is
  documented as an accepted approximation, not fixed in this round.
- **`WarmupAsync` can now safely be called multiple times** (e.g., from a retried hosted-service hook) without
  risking exceeding `MaxSize`.
- Test coverage: **82/82 passing** (Core 36 [+15], Dataverse 43 [+1], Polly 3), up from 66/66. Run 3x
  in a row without flaky timing failures. New tests: end-to-end `AcquireTimeout` (behind serialized creation +
  respect for caller cancellation), `WarmupAsync` idempotence (3 tests), operational-failure accumulation
  across recycles + correct reset on truly healthy return (2 tests), `AbandonProbe` (1 test),
  `DetectedLeakCount` visibility (1 test), `PoolOptions` duration validation (6 tests).
