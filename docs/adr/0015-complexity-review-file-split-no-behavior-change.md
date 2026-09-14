# ADR-0015: Complexity review — pause "fix everything" review cycles, split ResourcePool.cs

## Status
Accepted

## Context
After five consecutive review rounds (ADR-0009 through ADR-0014), each following the pattern
"run 2-3 expert agents, fix every well-defined finding," the user asked directly whether the
project now has too much complexity, rather than requesting another round of bug-hunting.

A direct (non-delegated) inspection was done instead of another review-agent round, since the
question was about judgment/trade-offs, not about finding more bugs:

- `src/ConnectionPool.Core/ResourcePool.cs` had grown to 644 lines — by far the largest file in
  the solution (next largest: `MemberCircuitBreaker.cs` at 218 lines). 25 methods/fields in one
  class.
- `PoolOptions` has 6 knobs (`MaxSize`, `PrewarmCount`, `OnLeakDetected`, `CreateTimeout`,
  `MaxIdleLifetime`, `AcquireTimeout`) — each one traces directly to a specific, documented finding
  (not speculative future-proofing). For comparison, HikariCP (the reference implementation this
  project's design repeatedly cites) exposes on the order of 15-20 configuration properties, so 6
  is not excessive for a production-grade pool.
- Reading every method in `ResourcePool.cs` end to end: every branch, every extra `catch` clause,
  and every counter traces to a specific ADR and a specific test. There is no dead code, no
  speculative abstraction, and no unused configuration surface. The density comes from the domain
  itself (concurrent resource lifecycle management with timeouts, health signals, and leak
  detection), not from accidental complexity or premature generalization.
- However, the review-cycle *process* itself is showing real diminishing returns: ADR-0014 (round
  five) found a genuine regression that round four's own fix (ADR-0013) had introduced
  (`CreateTimeout`/`AcquireTimeout` misclassification). This is a signal, not a coincidence — a
  single 644-line file accumulating five rounds of concurrency-correctness patches is harder to
  hold in one's head all at once than five smaller, single-responsibility files would be, which
  raises the odds of the *next* round finding another self-inflicted regression rather than a
  genuinely new bug.

## Decision
1. **No further "3-agent review, fix everything found" rounds are planned by default.** The
   codebase has been reviewed five times by security, DB-pool-design, and distributed-systems
   experts; all well-defined, actionable findings have been fixed; the remaining open items in
   `TODO.md` are either explicit user-rejected scope (cross-process shared state) or require an
   architectural decision the user has not yet made (the residual `AcquireTimeout == null` probe
   race). Continuing to run full review rounds against an already-hardened, already-tested
   644-line concurrency file has a worsening cost/benefit ratio: the marginal bug found is
   decreasingly likely to be a *pre-existing* one and increasingly likely to be a regression
   introduced by the review process's own prior fix.
2. **Do not remove any configuration knob or feature.** Every knob and code path was individually
   justified by a specific, real finding, verified by a dedicated test, and there is no unused
   surface to cut. Removing any of them would reopen an already-fixed issue.
3. **Split `ResourcePool.cs` into two `partial class` files, purely for readability — zero
   behavior change:**
   - `ResourcePool.cs` — construction/validation, `WarmupAsync`, `AcquireAsync`, `GetStats`,
     `HealthChanges`, `ReturnAsync`, leak reporting, health-change publishing, disposal.
   - `ResourcePool.Recycling.cs` (new) — everything to do with creating and recycling a slot's
     underlying resource: `IsExpiredByIdleLifetime`, `RecycleInPlaceAsync`,
     `RecycleInBackgroundAsync`, `CreateNewSlotAsync`, `CreateThroughGateAsync`,
     `AbandonCreationAsync`, `SafeDisposeAsync`, `DisposeAbandonedSlotAsync`.

   This is a pure file reorganization (same class, same fields, same methods, same bodies, no
   logic changed) — done specifically because it is the lowest-risk kind of "simplification"
   available: it improves readability (each file now fits comfortably on screen and has a single
   clear responsibility) without touching any of the carefully-tuned concurrency logic that five
   review rounds have hardened. Verified via 3 consecutive full test runs post-split (90/90,
   unchanged from pre-split).

## Consequences
- `ResourcePool.cs`: 644 → 445 lines. `ResourcePool.Recycling.cs`: 205 lines (new). Neither file
  alone is trivial, but each is now scoped to one concern (lease/capacity lifecycle vs.
  creation/recycling), which should make future changes easier to reason about in isolation.
- No public API change, no behavior change, no test change required — the split is invisible to
  consumers and to the test suite (90/90 passing, unchanged pass count, confirmed stable across 3
  runs).
- Future review rounds (if any) should be scoped and targeted (e.g. "re-verify only the
  AcquireTimeout/CreateTimeout interaction" ) rather than a blanket "find anything" sweep across
  the whole pool, to avoid repeating the pattern where a broad fix in one round quietly breaks an
  adjacent invariant that the next round then has to rediscover.
- The one known, still-open architectural question (the residual time-based probe race when
  `AcquireTimeout` is `null`, see ADR-0014) remains explicitly deferred pending the user's input —
  it is a design trade-off, not a bug to "fix" reactively in another review round.

## Evidence
- File sizes before/after: see `wc -l src/**/*.cs` in the session transcript.
- `PoolOptions` knob count and justification: each of the 6 properties has a corresponding ADR
  (0002, 0007, 0012, 0007, 0007, 0012/0013) and dedicated tests.
- Build + 3x full test run after the split: 90/90 passing (Core 40, Dataverse 47, Polly 3), no
  flaky timing failures, no behavior change.
