# ADR-0010: Configurable fail-fast (instead of only fail-open) + true single-probe half-open

## Status
Accepted

## Context
A distributed-systems-focused review identified three related problems in the
group selection strategies (`HealthAwareRoundRobinSlotSelectionStrategy`,
`LeastConnectionsSlotSelectionStrategy`):

1. **Missing shared budget coordination across processes.** See ADR-0009 — this remains a
   documented production limitation ("single process per service-principal set"), NOT resolved here.
   The user explicitly rejected introducing shared state across processes (no external
   coordinator/Redis/or similar) — any solution here must therefore remain strictly per process.
2. **Unconditional fail-open can amplify a real outage.** When all members of the group are
   circuit-open/throttled, the strategies previously always selected a member anyway (ADR-0007 #6,
   to avoid deadlock). During a tenant-wide degradation, this means the group keeps
   sending traffic to something that is certain to fail/be throttled, instead of providing
   backpressure.
3. **Half-open was not atomic.** When a member's cooldown expired, *all* concurrent
   `SelectNext` calls at that moment could consider the member "eligible" — i.e., an entire bundle of
   concurrent callers could stampede toward precisely the connection that was just recovering, which in
   practice is a local thundering herd on the weakest link.

## Decision

### Fix for #3: `MemberCircuitBreaker` (new shared type, still per process only)
The circuit-breaking logic (which was previously duplicated identically in both strategies) has been extracted into a
new public type, `MemberCircuitBreaker`, which:
- Preserves the closed/open/half-open semantics from ADR-0007 #6 (opens when
  `ConsecutiveCreateFailures >= failureThreshold`, retries after `cooldownPeriod`).
- Adds an **atomic single-probe layer**: when a member becomes half-open, only the *first*
  caller (within a `probeClaimTimeout` window, default = `cooldownPeriod`) is allowed to
  treat it as eligible. All other concurrent callers remain directed at other members
  until the probe outcome is visible (the member's `ConsecutiveCreateFailures` falls below the threshold —
  success — or the probe claim itself expires, after which a new probe can be assigned).
- Is still **purely per process, no shared state** — an in-memory `Dictionary`+`lock`, exactly as
  before. This is a deliberate boundary: making probe atomicity itself correct *within* one process
  requires no external coordinator, and the user explicitly rejected introducing one for this
  work.
- Tested: `MemberCircuitBreakerTests` includes a concurrency test with 50 concurrent calls via
  `Parallel.For`, which verifies exactly 1 winner of the probe slot (not 0, not >1).

Both strategies now use this shared type instead of each having its own private `Dictionary`-based
bookkeeping — removing duplicated, tricky concurrency code and fixing the bug at the same time.

### Fix for #2: `GroupAllUnavailableBehavior` (configurable, default unchanged)
`DataverseGroupPool` gets a new constructor parameter,
`GroupAllUnavailableBehavior allUnavailableBehavior = GroupAllUnavailableBehavior.FailOpen`:
- **`FailOpen`** (default, backward-compatible): unchanged behavior — select a member anyway, even when
  all are circuit-open/throttled. Sensible when a resilience layer higher up (Polly, custom retry)
  already handles the failure/throttling, and you would rather try than reject.
- **`FailFast`**: throw `DataverseGroupUnavailableException` (with member names and — if known —
  the earliest throttle window across members) **before** any `AcquireAsync` call is even
  attempted against a member you already know is unavailable. Sensible when you do not want to add
  load to (or prolong) an outage you already know about, and would rather have
  backpressure immediately.

To make this possible without changing `ISlotSelectionStrategy`'s existing
return-type signature to something nullable/throwing, the interface was instead extended with a
`readonly record struct SlotSelection(DataverseUserPool Member, bool AllMembersUnavailable)` — a
small, additive API change (breaking in the technical sense, but accepted because of pre-1.0 status, for the same
reasoning as `DataverseGroupLease` in ADR-0008). `RoundRobinSlotSelectionStrategy` (no
health awareness) always reports `AllMembersUnavailable: false`.

### #1 deliberately remains unresolved here
No code in this ADR attempts to coordinate budget/state across processes — it is still
presented as a production limitation in the README (ADR-0009). Resolving #1 "correctly" (globally correct
budget enforcement) by definition requires shared state somewhere (an external coordinator, database,
distributed lock, etc.), which the user explicitly opted out of for this work. The only
indirect improvement #1 gets here is that `FailFast` + `DataverseGroupUnavailableException` make it
significantly easier for an operator to *observe* that the group is under pressure (instead of silently
continuing to send traffic), which is a step toward better operations without requiring shared
state.

## Consequences
- **Backward-compatible**: the default behavior (`FailOpen`) is identical to before this ADR. Existing
  consumers of `DataverseGroupPool`/the strategies are unaffected unless they themselves opt into
  `FailFast`.
- **`ISlotSelectionStrategy.SelectNext`'s return type changed** from `DataverseUserPool` to
  `SlotSelection` — affects any custom strategy implementation (none are known to exist in
  this repo beyond the three built-ins + tests, all updated).
- `MemberCircuitBreaker` is public, so a custom strategy can reuse it instead of
  re-implementing half-open logic (and the associated single-probe fix) itself.
- Test coverage: 52/52 passing after this change (Core 15, Dataverse 34, Polly 3), including 4 new
  `MemberCircuitBreakerTests` and 3 new `DataverseGroupPoolTests` (default fail-open, fail-fast,
  fail-fast with correct "earliest retry" calculation from throttle windows).
