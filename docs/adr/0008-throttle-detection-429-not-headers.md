# ADR-0008: Throttle detection via HTTP 429/exception, not proactive rate-limit headers

## Status
Accepted

## Context
Question: should the group pool's throttle awareness (i.e., avoiding sending traffic to a member that is
close to its Dataverse service-protection limit) be based on (a) Dataverse's proactive
`x-ms-ratelimit-*` response headers, or (b) the reactive HTTP 429 response (`Retry-After`)?

In general, for Dataverse's Web API itself, the answer is clearly **headers are better**: Dataverse sends
`x-ms-ratelimit-burst-remaining-xrm-requests`, `x-ms-ratelimit-time-remaining-xrm-requests` (and
the corresponding headers for the execution-time budget) on **every single** response — successful or not. It is
a *leading* signal (react before you get throttled), whereas a 429 is a *lagging* signal (react only
after Dataverse has already rejected a call).

But this pool does not wrap Dataverse's raw Web API — it wraps
`Microsoft.PowerPlatform.Dataverse.Client.ServiceClient`. We investigated via reflection whether
`ServiceClient` (and its `ConnectionOptions`) expose these headers for a *successful* call:

- No public property, event, or hook on `ServiceClient` or `ConnectionOptions` provides access
  to response headers for a successful call.
- `ServiceClient` has its own internal retry logic for throttling
  (`MaxRetryCount`/`RetryPauseTime`/`UseExponentialRetryDelayForConcurrencyThrottle`) — it
  absorbs 429s internally and retries on its own before anything even reaches user code.
- **When** the SDK's own retry budget is exhausted, a
  `Microsoft.PowerPlatform.Dataverse.Client.Exceptions.HttpOperationException` is thrown whose
  `Response` property *does in fact* expose `StatusCode` (verified: `429`) and `Headers`
  (verified: `IDictionary<string, IEnumerable<string>>`, including `Retry-After`) — confirmed via
  reflection against the actual SDK DLL, not assumed.

Conclusion: for **this SDK**, the 429/exception path is not a preference among two equally good
options — it is the only actually available structure short of reflecting into
`ServiceClient`'s private HTTP pipeline (fragile, unsupported by Microsoft, and a
maintenance nightmare during SDK upgrades). It was therefore deliberately rejected.

## Decision
- `DataverseThrottleDetector.TryGetRetryAfter(Exception?, out TimeSpan)` walks the exception chain,
  finds a `HttpOperationException` with `Response.StatusCode == 429`, and parses the
  `Retry-After` header (seconds or HTTP date) into a `TimeSpan`. If a 429 is found but
  `Retry-After` is missing/cannot be parsed, a conservative default (5s) is used — still better than
  ignoring the signal.
- `DataverseUserPool` gets `ThrottledUntil`/`IsThrottled`/`ReportThrottled(TimeSpan)` — throttling
  does **not** mark the resource unhealthy/for recycling (the connection is fine, the user is just temporarily
  over its own budget). This is deliberately a separate mechanism from `MarkUnhealthy`
  (ADR-0004/0007), which is for genuinely defective connections.
- `DataversePool.AcquireAsync()` now returns `DataverseLease` (not a raw
  `PooledLease<ServiceClient>`) specifically so a consumer can report a 429 back to the
  **correct** member — the pool itself otherwise does not know which member was selected for a given
  call without this reference. `DataverseLease.ReportIfThrottled(exception)` is the convenience method
  that combines detection + reporting in one call.
- Both selection strategies (`HealthAwareRoundRobinSlotSelectionStrategy`,
  `LeastConnectionsSlotSelectionStrategy`) now also skip throttled members, with the same
  "fail open if all are down" guarantee as for circuit-open members (ADR-0007 #6).

## Consequences
- **Requires explicit reporting from the consumer.** The pool cannot detect throttling on its own — it
  does not see what you do with a leased `ServiceClient` after `AcquireAsync()`. If a consumer
  does not call `ReportIfThrottled`/`ReportThrottled` in its error-handling flow, throttle awareness
  is a no-op. This is documented as a hard boundary, not a future "todo".
- **Only 429s that actually escape the SDK's own retry are detected.** Most transient
  throttling events are already absorbed internally by `ServiceClient` (that is the whole point of its own
  retry logic) — our signal is effectively only for persistent/repeated throttling that survives
  the SDK's internal retry budget. That is still valuable (it is exactly the situation where the group's
  round-robin would otherwise keep hammering the same member), but it is not a general
  telemetry source for how close a member is to its limit during normal operation.
- **`DataversePool.AcquireAsync()`'s return type changed** from `PooledLease<ServiceClient>` to
  `DataverseLease` — an intentional API break, acceptable because the library is still
  pre-1.0/preview. `DataverseLease` keeps the same usage pattern (`.Resource`,
  `await using`/`DisposeAsync`), so migration is minimal.
- If a future SDK version exposes response headers for successful calls (or we switch
  to calling the Web API directly instead of through `ServiceClient`), a proactive
  header-based strategy can be added as a new, separate signal without changing the contract established
  here.

## Addendum (2026-10): SOAP path, 429 vs. failure, and how members recover
Confirmed live and implemented since the original decision (PLAN-2026-09-30.md Phases 2-3):

- **The SOAP transport has no HTTP 429.** The Dataverse SDK may use SOAP under the hood, and there a
  service-protection rejection surfaces as `FaultException<OrganizationServiceFault>` with one of
  three error codes: `NumberOfRequests` (`0x80072322`), `ExecutionTime` (`0x80072321`) or
  `ConcurrentRequests` (`0x80072326`), with the back-off in `ErrorDetails["Retry-After"]` (observed as
  a boxed `TimeSpan`). The original detector only knew `HttpOperationException`, so SOAP throttles
  went unnoticed. `DataverseThrottleDetector` now recognizes both. Confirmed live for
  `ConcurrentRequests` and `NumberOfRequests`; `ExecutionTime` could not be provoked in four
  attempts (see `docs/research/autoscaling.md` §2.2), so that code rests on documentation alone.
- **A 429 is not a failure.** It never feeds the circuit breaker (`MemberCircuitBreaker`) and never
  recycles the connection. It only sets `ThrottledUntil` (capped at `DefaultMaxRetryAfter`, ADR-0017).
  Real failures take a separate path: connection-level faults call `MarkUnhealthy`
  (`DataverseLease.ReportIfConnectionFault`), and known non-self-healing connect errors (revoked
  secret, disabled app user, 401) quarantine the member (`DataverseFailureClassifier`).
- **Recovery is a ramp, not a probe.** A throttled member is skipped until `ThrottledUntil`.
  `HealthWeightedLeastConnectionsSlotSelectionStrategy` then lets it back in gradually
  (start at 2 concurrent, +1 per 5 s) instead of releasing the whole queue onto it at once. The
  same ramp applies after a half-open probe succeeds for a circuit-open member.
- **Throttle state is per application user, not per server.** Dataverse limits are per user and per
  web server, but `DataverseUserPool.IsThrottled` has no per-frontend granularity (the SDK exposes no
  stable server identity), so the pool cannot steer around a single throttled frontend.
- Operators can see all of this through the `dataversepool.member.*` gauges
  (`throttled`, `throttled_until_seconds`, `dop_hint`, `breaker_state`, `quarantined`).
  See `docs/research/autoscaling.md` §9.6.
