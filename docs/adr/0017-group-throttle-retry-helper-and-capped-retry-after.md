# ADR-0017: Group-level throttle retry helper + capped `Retry-After`

## Status
Accepted

## Context
Two related gaps surfaced from a caller's perspective:

1. **"Retry against a different group member when throttled" was entirely caller-written.**
   `DataversePool.AcquireAsync()` and `DataverseLease.ReportIfThrottled` already made this
   *possible* (a throttled member is skipped by the selection strategy on the next acquire), but
   there was no helper that actually performed the acquire-execute-detect-retry loop. Every caller
   wanting this had to hand-write it.

2. **Dataverse's `Retry-After` header is not bounded to something small.** Dataverse's documented
   service-protection limits include up to a 20-minute execution-time budget per 5-minute sliding
   window, and real-world 429 responses have been observed reporting `Retry-After` values as high as
   ~17 minutes. `DataverseThrottleDetector.TryGetRetryAfter` previously honored whatever value
   Dataverse reported, verbatim. Combined with (1)'s retry loop, or even just
   `DataverseLease.ReportIfThrottled` used directly, an unbounded value would exclude a member
   from the group's rotation for a very long time from a single signal - disproportionate for most
   applications, and risky with few members (the rest absorb all traffic for that whole window).

## Decision
- **`DataversePool.ExecuteWithThrottleRetryAsync<T>(operation, maxAttempts?, maxRetryAfter?, ct)`**:
  acquires a lease, runs `operation`, and on a recognized 429/throttling signal reports it (so the
  member is excluded going forward) and retries against a freshly-acquired lease - naturally routed
  to a different member by the group's throttle-aware selection strategy. Only throttling signals
  are retried; any other exception from `operation` propagates immediately, unretried - this is
  intentionally narrow in scope (closing the specific "retry on a different member" gap), not a
  general-purpose resilience pipeline (that remains the optional Polly adapter's job, per ADR-0005).
  `AcquireAsync` failures themselves (e.g. `DataversePoolUnavailableException`) are not retried by
  this method - only failures from `operation`, once a lease was actually acquired, are eligible.
  Defaults `maxAttempts` to the member count (each member gets at most one attempt by default).
- **`DataverseThrottleDetector.DefaultMaxRetryAfter = 80 seconds`**, applied by default everywhere a
  `Retry-After` value is translated into a duration (`TryGetRetryAfter`,
  `DataverseLease.ReportIfThrottled`, and now `ExecuteWithThrottleRetryAsync`). All three accept
  an explicit override (`maxRetryAfter`/an overload taking a `TimeSpan`) for callers who want a
  different cap - including `TimeSpan.MaxValue` to opt back into honoring Dataverse's value
  verbatim. 80 seconds was chosen as a deliberately conservative default: long enough to matter,
  short enough that one over-reported window doesn't sideline a member for most of a work session.
  `DataverseUserPool.ReportThrottled(TimeSpan)` itself is deliberately NOT capped - it's a generic,
  trusted primitive that already existed and is exercised directly in tests with arbitrary
  durations; the cap only applies where Dataverse's own signal is being translated, not to an
  explicit caller-supplied duration.

## Consequences
- No behavior change for existing callers that don't use the new method or don't hit 429s with a
  `Retry-After` above 80 seconds (the overwhelming common case, since most throttling windows are
  far shorter than the documented worst case).
- Callers who legitimately want to honor Dataverse's value verbatim (e.g. because they run with very
  few members and prefer a long, certain wait over cycling through members that will all end up
  throttled anyway) can pass `TimeSpan.MaxValue` explicitly.
- Testing: `ExecuteWithThrottleRetryAsync`'s happy path (successful lease, throttled operation,
  successful retry against another member) is not unit-testable without a live Dataverse connection,
  same constraint as `DataverseServiceClientPolicy` (see ADR-0016). What is tested: argument
  validation, that `AcquireAsync` failures propagate untouched without invoking `operation` (proving
  this method's retry scope is limited to post-acquire failures), and the full cap behavior of
  `DataverseThrottleDetector.TryGetRetryAfter` (default cap applied, values under the cap untouched,
  explicit override honored, invalid override rejected) using the same real-SDK-exception-type
  fixture as the rest of `DataverseThrottleDetectorTests`.
