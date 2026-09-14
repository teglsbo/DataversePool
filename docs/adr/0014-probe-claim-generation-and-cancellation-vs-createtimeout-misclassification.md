# ADR-0014: Probe-claim generation correlation, and correct distinction between AcquireTimeout cancellation and real CreateTimeout

## Status
Accepted

## Context
The user asked for another (fifth) review round after ADR-0013 was committed (`5c6dd7d`). Security
+ DB pool design expert + distributed-systems expert, all asked to actively re-verify (not just
believe) ADR-0013's six fix claims against the committed code. The security review found nothing new
(CTS lifecycle, `DetectedLeakCount`, ADR-0009's CallerId fix, etc. confirmed unchanged/correct). The two
other experts found, partly independently, that several of ADR-0013's fixes were only **partially
effective**, along with a **new regression** introduced by the ADR-0013 work itself:

1. **`CreateTimeout`/`AcquireTimeout` confusion (new regression, blocking, found independently by both
   experts):** `CreateThroughGateAsync` races creation against
   `Task.WhenAny(createTask, Task.Delay(timeout, cancellationToken))`. If `cancellationToken`
   (ADR-0013's linked `AcquireTimeout` deadline token, or the caller's own token) expires *before*
   `CreateTimeout` itself does, `Task.Delay` enters the Canceled state — but `Task.WhenAny` still returns
   it as the "winner," and the code unconditionally classified this as a real
   `CreateTimeout` expiry: incremented `_consecutiveCreateFailures` and threw a raw
   `TimeoutException` instead of `OperationCanceledException`. Consequence: the outer
   `AcquireAsync` translation to `PoolAcquireTimeoutException` (ADR-0013 fix #1) was never hit,
   `DataverseGroupPool`'s new specific `catch (PoolAcquireTimeoutException)` (ADR-0013 fix #4)
   was therefore never hit either, and a pure capacity/cancellation event ended up in the general
   `catch`, which reported it as a **failed health probe** — exactly what ADR-0013 #4 was supposed
   to prevent. The same pattern existed in `RecycleInPlaceAsync`'s blanket `catch (Exception)`, which also
   swallowed cancellation as "recycle failed" (decremented `_createdCount`, incremented
   `_consecutiveCreateFailures`).
2. **Probe-claim race still genuinely open when `AcquireTimeout` is `null` (blocking,
   distributed-systems expert):** ADR-0013's "end-to-end `AcquireTimeout`" only makes the
   `probeClaimTimeout` race from ADR-0011 less likely when `AcquireTimeout` is actually
   configured and is shorter than `probeClaimTimeout`. Since `AcquireTimeout` is `null` (disabled)
   by default, the original race is **completely untouched** in the default configuration: a
   legitimately slow (not hung) caller A can lose its claim to `probeClaimTimeout` while a new
   caller B wins an overlapping probe.
3. **`AbandonProbe`/`CompleteProbe` without per-attempt identity (blocking/non-blocking, both
   experts):** these methods mutated "whatever is currently claimed" without knowing *which*
   attempt they were actually completing. A late/stale call from an already overtaken attempt A could
   therefore wrongly release or close a newer, still active claim belonging to B — thereby
   allowing a third caller C to win yet another overlapping probe, or incorrectly close/reopen the
   circuit based on a stale attempt.
4. **`WarmupAsync` still not race-free under concurrency (blocking, DB pool expert):**
   ADR-0013's before/after check of `_createdCount` made the check-and-create sequence idempotent for
   *sequential* repeated calls, but not atomic across *concurrent* `WarmupAsync` calls: two
   overlapping calls can both observe `CreatedCount` below target (idle resources do not hold a
   capacity permit, so `_capacityGate` alone does not prevent this), and both create — which
   together can exceed `MaxSize` with enough concurrent callers.

The user was not available to prioritize this round (autopilot). Findings #1 and #4 are
well-defined, low-risk bugs with a clear, correct fix — they are fixed. Finding #3 (claim correlation) is
tractable to fix partially (a generation/token per claim, without changing the selection
algorithm itself) and at the same time fixes the concrete stale-report corruption both experts
demonstrated — it is fixed. Finding #2 (the fundamental, time-based race when no real
"claim is still alive" signal exists) is an architectural trade-off, not a simple bug: a full
fix would either require never assigning a new claim before the old claim's outcome is *known* (risk:
a permanently hanging/crashed attempt blocks recovery forever — exactly why the
`probeClaimTimeout` fallback exists), or real cross-attempt cancellation of the old attempt
(unrealistic against a non-cooperative Dataverse SDK). This is documented as a known, **not fixed**
limitation that requires a conscious choice from the user the next time they are available — not something
that should be decided unilaterally under autopilot.

During implementation of finding #3 it was also discovered (but **not fixed** in this round) that
`HealthAwareRoundRobinSlotSelectionStrategy`/`LeastConnectionsSlotSelectionStrategy`'s
selection loop calls `MemberCircuitBreaker.IsEligible` for **all** candidates during
eligibility filtering — not only the one ultimately selected via round-robin/least-connections. Because
`IsEligible` claims a half-open probe as soon as it finds an available one, this means that a
half-open member that ends up *not* being selected in that round still has its single probe slot
"used up" until `probeClaimTimeout` expires — without any attempt ever actually being made against it.
This is a real, previously unreported bug that can delay real recovery under concurrent
load. A fix requires separating "is this candidate in principle eligible" (read-only) from
"claim the probe" (only for the actually selected candidate) — which in turn requires handling what
happens if the claim fails *after* the selection has been made (the candidate was available at peek,
but got claimed by another thread in the meantime). It is a real, but non-trivial algorithmic change that
risks introducing new concurrency bugs without thorough separate test coverage and
design consideration — deferred to a future round/ADR, documented here so it is not lost.

## Decision

### Fix for #1: Distinguish cancellation from real `CreateTimeout` expiry
`CreateThroughGateAsync`: after `Task.WhenAny` returns the delay task (not `createTask`),
`cancellationToken.IsCancellationRequested` is checked *before* it is classified as a `CreateTimeout`. If
true, `OperationCanceledException` is thrown (without incrementing `_consecutiveCreateFailures`); only
if the token is not canceled is it a real `CreateTimeout` expiry (unchanged behavior: increment the
counter, throw `TimeoutException`). Added `catch (OperationCanceledException) { throw; }` (before
the general `catch`) so cancellation never hits the general error-counter increment.

`RecycleInPlaceAsync`: added a dedicated `catch (OperationCanceledException)` that rethrows
without marking the recycle as failed (no `_consecutiveCreateFailures` increment, no
`RecoveryFailed` health event) — but still *decrements* `_createdCount`, because the old
resource was already disposed before the attempt to restore it, so the slot's capacity is genuinely lost
regardless of why the replacement was not completed.

In both places, this means that an `AcquireTimeout` deadline (or the caller's own token) that
expires while a creation is in progress — whether it is a fresh creation or an inline
idle-lifetime recycle — now correctly propagates as `OperationCanceledException`, which
`AcquireAsync`'s outer `catch` (ADR-0013) translates into `PoolAcquireTimeoutException`, which
`DataverseGroupPool`'s specific `catch` (ADR-0013 #4) then correctly handles via
`ReportAcquireAbandoned` instead of reporting a failed probe.

### Fix for #3: Claim-generation correlation in `MemberCircuitBreaker`
`CircuitState` got a `ClaimGeneration` field, incremented every time a new half-open probe is
claimed. New overload `IsEligible(member, stats, now, out long? claimGeneration)` — the original
3-argument method calls this one and discards the generation (100% backward-compatible, all existing
calls/tests unchanged). `claimGeneration` is only non-null when the call actually wins a
half-open probe (not when the circuit is simply closed, or the call is rejected).

`CompleteProbe`/`AbandonProbe` got corresponding new overloads with an optional `long? claimGeneration`
parameter. If a non-null generation is provided and it does not match the member's current
`ClaimGeneration`, the report is silently ignored — it is from an already overtaken attempt and must not
touch a newer, still active claim. `null` (the original 2-argument overloads) preserves the old,
uncorrelated behavior unchanged.

This generation is threaded through the entire chain: `SlotSelection` got a new, optional
`ProbeClaimGeneration` field (default `null`, so existing `new SlotSelection(member, bool)` calls
in tests remain unchanged); `ISlotSelectionStrategy.ReportAcquireOutcome`/`ReportAcquireAbandoned`
got corresponding new default overloads that receive the generation and forward it to
`MemberCircuitBreaker`; both circuit-breaker-aware strategies capture the generation from
`IsEligible` for the actually selected candidate and place it in `SlotSelection`;
`DataverseGroupPool.AcquireAsync` includes `selection.ProbeClaimGeneration` in all
`ReportAcquireOutcome`/`ReportAcquireAbandoned` calls.

This fixes the concrete corruption demonstrated by both experts: probe A is claimed and
abandoned, probe B wins a new claim, and A's delayed/double completion report now correctly affects
nothing (is ignored) because its generation no longer matches B's.

### Fix for #4: `WarmupAsync` concurrency-safe via dedicated warmup gate
New `SemaphoreSlim _warmupGate = new(1, 1)`, which serializes the entire
check-target/take-permit/create sequence across concurrent `WarmupAsync` calls. Only one
`WarmupAsync` caller can be inside the decision at a time; the internal double-check of
`_createdCount` (before and after `_capacityGate`) is retained as defense in depth against concurrent lazy
`AcquireAsync` creations (which do not go through `_warmupGate`, and do not need to — they are
not targeting the `PrewarmCount` target). The serialization adds no real extra
lock contention beyond what `_creationGate` already imposes on creation itself (`docs/adr/0002`).

### Not fixed (deliberately, requires the user's decision): #2, the fundamental probe race
`PoolOptions.AcquireTimeout`/`MemberCircuitBreaker` documentation is extended with an explicit,
honest warning: the single-probe guarantee is **only** race-free in practice when `AcquireTimeout` is
configured to a value shorter than `probeClaimTimeout` for any realistic creation time —
with the default (`AcquireTimeout = null`), the original time-based race from ADR-0011 is still
really possible (a legitimately slow, non-hung prober can lose its claim to
`probeClaimTimeout` while it is still active). A full fix requires an architectural choice between:
- **Strict only-one-in-flight semantics**: never assign a new claim before the previous claim's outcome is
  explicitly known (`CompleteProbe`/`AbandonProbe` called) — risk: a permanently hanging/crashed
  attempt (no `CreateTimeout` configured, `CreateAsync` does not respect cancellation) blocks
  recovery of that member forever.
- **Preserve the timeout fallback** (current model) — risk: rare overlapping double probes under
  unlucky timing, which this round has shown is genuinely possible, although rare in practice (requires that a
  real creation takes longer than `probeClaimTimeout`, which itself falls back to
  `cooldownPeriod` by default).

This is the same category of choice as the cross-process/cross-member trade-offs already
presented and deliberately deferred in ADR-0010/0012 — it requires a product decision about
availability vs. correctness, not a code fix, and awaits the user's input.

### Known, deferred finding (not fixed): probe claims "used up" by non-selected candidates
See the Context section above. Documented here as a concrete, scoped backlog item for a
future ADR: separate "is candidate in principle eligible" (read-only) from "claim the probe" (only for the
actually selected candidate), and define correct fallback behavior if the claim fails after selection.

## Consequences
- **Backward-compatible**: all new parameters are optional/default `null`; existing calls to
  `IsEligible`/`CompleteProbe`/`AbandonProbe`/`SlotSelection` construction and all existing
  `ISlotSelectionStrategy` implementations (including test doubles) continue unchanged.
- **Sharper error classification**: an `AcquireTimeout` or caller cancellation that hits in the middle of
  an ongoing creation (fresh or inline recycle) no longer incorrectly counts as a
  create failure or a failed health probe.
- **Stale probe reports can no longer corrupt a newer claim** on the same member.
- **`WarmupAsync` is now correctly atomic across concurrent calls**, not just idempotent for
  sequential repeated calls.
- **Two findings are deliberately not fixed** and are documented above with rationale: the fundamental
  probe race when `AcquireTimeout` is `null` (requires the user's product decision), and
  "probe claims used up by non-selected candidates" (requires a larger, isolated redesign effort).
- Test coverage: **90/90 passing** (Core 40 [+4], Dataverse 47 [+4], Polly 3), up from 82/82. Run 3x in
  a row without flaky timing failures. New tests: `CreateTimeout`/`AcquireTimeout` confusion via both
  fresh creation and inline recycle (2 tests), concurrent `WarmupAsync` race (2 tests),
  claim-generation behavior — non-null only on actually won claim, stale completion/abandon ignored
  correctly, newer claim remains intact, backward-compatible null behavior (5 tests).
