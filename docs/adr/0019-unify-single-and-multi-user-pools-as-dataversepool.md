# ADR-0019: Unify single-user and multi-user pools as `DataversePool`; wait only when no alternative exists

## Status
Accepted

## Context
ADR-0006 introduced two distinct public types: `DataverseUserPool` (one connection string) and
`DataverseGroupPool` (composes several `DataverseUserPool` instances, round-robin). This session's
review of `dvpool.stress`'s findings (a sibling load-test repo; see its README's Finding 6) surfaced
a real gap: `DataverseGroupPool.ExecuteWithThrottleRetryAsync` (ADR-0017) retries immediately on a
recognized throttle - correct when the selection strategy routes the retry to a different, healthy
member, but not when it doesn't (a single-member "group", or a multi-member group where every
member is currently over budget and `GroupAllUnavailableBehavior.FailOpen` hands one back anyway).
In both of those cases, an immediate retry just re-hits the same still-throttled connection for no
benefit.

Working through a fix surfaced a bigger question from the user: should single-user and multi-user
pooling really be two different public types the caller has to choose between and migrate across,
or should scaling from one Dataverse application user to several be a pure configuration change?
Since `DataverseGroupPool` already composed `DataverseUserPool` instances and its selection strategy
already handled a one-member collection correctly (round-robin over one item always returns that
item; circuit breaking doesn't care about member count), the missing piece was purely ergonomic and
naming, not architectural - `DataverseGroupPool` could already serve as the universal front door.

## Decision
- **Renamed `DataverseGroupPool` → `DataversePool`, `DataverseGroupLease` → `DataverseLease`,
  `DataverseGroupUnavailableException` → `DataversePoolUnavailableException`,
  `GroupAllUnavailableBehavior` → `AllUnavailableBehavior`, `AddDataverseGroupPool` (DI extension) →
  `AddDataversePool`.** "Group" specifically implied more than one member; that stopped being
  accurate once this type became the recommended entry point regardless of count. No behavior
  change from the rename alone - it's the same composition-over-`DataverseUserPool` design from
  ADR-0006, just renamed to reflect that it's not exclusively for the multi-member case anymore.
  `DataverseUserPool` itself is unchanged in name and role: the internal per-connection-string
  building block, still directly usable standalone for the narrowest "never scaling, want zero
  selection-strategy overhead" case (ADR-0006's original zero-overhead rationale still applies to
  that specific choice).
- **New single-member convenience constructor**: `new DataversePool(DataverseUserPool member, ...)`,
  equivalent to `new DataversePool(new[] { member }, ...)`. This is now the recommended way to start
  even with exactly one application user, if there's any chance of adding more later - scaling out
  is then purely "construct another `DataverseUserPool` and add it to the collection," never a
  change to call sites using `AcquireAsync`/`ExecuteWithThrottleRetryAsync`/`DataverseLease`.
- **`ExecuteWithThrottleRetryAsync` now waits out the capped `Retry-After` only when the
  freshly-acquired lease after a throttle report comes from the *same* member that was just
  throttled** - tracked via reference-equality on the previously-returned `DataverseLease.Member`.
  If a different member came back (a healthy alternative was available), the retry still happens
  immediately, unchanged from ADR-0017's original behavior. This one code path is now correct for
  every member count: a single-member pool always gets the same member back, so it always waits
  (the exact fix a single Dataverse user needs); a multi-member pool with a healthy alternative never
  waits (today's behavior preserved); a multi-member pool where every member is currently throttled
  and `AllUnavailableBehavior.FailOpen` returns one anyway now also correctly waits, closing a latent
  gap in ADR-0017's original implementation that had gone unnoticed because it was only reasoned
  about, not exercised, for the all-throttled case.
- **Default `maxAttempts` changed from `member count` to `Math.Max(member count, 3)`.** A plain
  member-count default gives a single-member pool exactly one attempt (i.e. no retry at all) by
  default, which would silently defeat the entire point of adding this wait-based retry path for
  that case. `Math.Max(_, 3)` preserves the old default for pools with 3+ members and gives smaller
  pools (including the single-member case) a minimum of three total attempts.
- **`DataverseLease.ReportIfThrottled` gained an `out TimeSpan retryAfter` overload** (the original
  bool-returning overload now delegates to it) so `ExecuteWithThrottleRetryAsync` can capture the
  actual capped duration to wait, while preserving the existing side-effect-ordering idiom (the
  exception filter's left operand always runs and records the throttle - including on the final,
  non-retried attempt - regardless of whether the right operand's attempt-count check passes).

## Consequences
- **Breaking rename**, but the library is pre-1.0/preview (`Directory.Build.props` version
  `0.1.0-preview`) with no NuGet-published releases yet, so this was done directly rather than via
  an obsolete/deprecated shim. Existing ADRs (0006-0018) were updated in place to use the new names
  throughout, rather than left referring to types that no longer exist - the substance of every past
  decision is unchanged, only the identifiers referenced are current. This ADR is the one place that
  explicitly documents the rename itself as a decision.
- `dvpool.stress`'s own Finding 6 (the SDK's own `Retry-After` handling has no cap; the only way to
  get a capped, self-owned wait is to disable the SDK's internal retries and implement it in the
  caller) remains the reason `MaxRetryCount=0` (ADR-0016) plus this method's own capped wait exist at
  all - this ADR does not change that recommendation, only fixes the case where "retry" previously
  meant "immediately, no wait" even when there was nowhere better to route to.
- Same testing constraint as ADR-0017: the happy path (real throttle → real wait → real retry
  against the same member) is not unit-testable without a live Dataverse connection
  (`ServiceClient` cannot be constructed standalone). What is tested: the new single-member
  constructor (argument validation and that it produces an equivalent one-element `Members`
  collection) and all pre-existing argument-validation/acquire-failure-propagation tests, renamed
  but otherwise unchanged, continue to pass.
