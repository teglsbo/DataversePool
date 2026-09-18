# ADR-0022: Four concurrency leak/race fixes (disposal, retry delay, probe claims, non-ready base client)

## Status
Accepted

## Context
A closer look at the pool's concurrency-sensitive paths - shutdown, throttle-retry, half-open probe
selection, and base-client construction - turned up four related-but-distinct bugs, all sharing the
same shape: a resource, permit, or claim can be left in an inconsistent state (leaked, or briefly
handed out from a pool that's mid-teardown) under a specific interleaving that normal sequential
testing doesn't exercise.

### (A) `ResourcePool<T>.DisposeAsync` disposal race
`DisposeAsync` marks the pool disposed, then does a best-effort drain: it tries to collect up to
`MaxSize` capacity permits (5s timeout each, to give outstanding leases a window to be returned),
then previously released all of them back to `_capacityGate` *before* draining `_idle`. Releasing the
permits first meant a concurrent `AcquireAsync` call that had already passed the entry
`ThrowIfDisposed()` check (i.e. started just before disposal began) could win one of those permits
and pop/create a slot while `_idle` was still being drained - handing out a lease from (or racing the
disposal of) a pool that is mid-teardown. Compounding this, `AcquireAsync` only checked disposed state
once, at entry - never again after actually winning a permit.

### (B) `DataversePool.ExecuteWithThrottleRetryAsync` lease leak on canceled retry-wait
The same-member-retry path waits out the previously-reported throttle window
(`Task.Delay(previousRetryAfter, cancellationToken)`) before retrying against the same member. That
wait sat *before* the `try/finally` that disposes the acquired lease - if the wait was canceled, the
lease was never disposed/returned, permanently consuming a pool slot.

### (C) Probe-claim leak in both slot-selection strategies
`MemberCircuitBreaker.IsEligible` grants a one-shot half-open "probe" claim per member per cooldown
window (see ADR-0010/0014). Both `HealthAwareRoundRobinSlotSelectionStrategy` and
`LeastConnectionsSlotSelectionStrategy` call it once per half-open candidate while scanning for an
eligible member, but only the ultimately-selected candidate's claim was ever completed/abandoned
afterwards (via `ReportAcquireOutcome`/`ReportAcquireAbandoned`). Any other half-open candidate that
was scanned, won a claim, and then wasn't picked kept that claim - incorrectly blocking it from a
fresh probe attempt until `probeClaimTimeout` expired, even though nothing was ever actually
attempted against it.

### (D) Non-ready base client left stored on rejection
`DataverseServiceClientPolicy.GetOrCreateBaseClientAsync` correctly throws
`InvalidOperationException` when the base client (from either the connection-string path or a
factory) comes back non-null but not `IsReady`. It left the broken client sitting in `_baseClient`,
though - if the policy is never retried or disposed, that instance leaks.

## Decision
- **(A)** Reordered `DisposeAsync` to drain `_idle` fully *before* releasing any permits collected
  during the initial wait, so a concurrent acquirer can only win a permit once teardown of idle
  resources is actually complete. Added a second disposed-state check in `AcquireAsync`, immediately
  after winning a capacity permit (not just at entry), so an acquirer that only gets a permit once
  disposal has finished still fails cleanly with `ObjectDisposedException` instead of receiving (or
  creating) a resource.
- **(B)** Moved the same-member retry-wait `Task.Delay` call to inside the existing `try` block
  (as the first statement, before invoking `operation`), so the existing `finally { lease.DisposeAsync() }`
  now also covers a cancellation of that wait.
- **(C)** After picking the winning candidate, both strategies now loop over every other eligible
  candidate that also won a probe claim and call `MemberCircuitBreaker.AbandonProbe` for it, freeing
  the claim immediately instead of leaving it to expire via timeout.
- **(D)** `GetOrCreateBaseClientAsync` now disposes and clears `_baseClient` before throwing whenever
  the resulting client is non-null but not ready - matching the cleanup-before-throw pattern already
  used elsewhere in the same method.

## Consequences
- No behavioral change for the common case (healthy pool, healthy members) - all four fixes only
  change behavior in already-exceptional interleavings (concurrent-with-shutdown, canceled retry
  waits, multiple simultaneously-recovering members, and a factory/connection string producing a
  broken client).
- (A) and (C) are covered by deterministic regression tests (`PoolShutdownTests`,
  `HealthAwareRoundRobinSlotSelectionStrategyTests`, `LeastConnectionsSlotSelectionStrategyTests`)
  that force the relevant interleaving via controlled gates (a fake dispose delay, and a
  caller-supplied shared `MemberCircuitBreaker`) rather than relying on real-time timing races.
- (B) and (D) are not directly covered by a new automated test: both require a genuinely successful
  `ServiceClient` construction (a real lease, or a non-null-but-not-ready base client) to exercise,
  and `ServiceClient` cannot be faked or constructed outside a live Dataverse connection - the same
  constraint documented in ADR-0001 for this codebase's existing Dataverse test coverage. Both were
  verified by code inspection instead; the fixes themselves are small, structural, and follow the
  same pattern as existing, already-tested cleanup-before-throw/dispose-in-finally code nearby.
