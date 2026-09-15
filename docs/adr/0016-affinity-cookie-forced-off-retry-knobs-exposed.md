# ADR-0016: Force `EnableAffinityCookie=false` in code; expose retry/throttle knobs as optional overrides

## Status
Accepted

## Context
A review of Microsoft's own documented `ServiceClient` performance guidance (server affinity,
connection reuse via `Clone()`, service-protection throttling/retry, degree-of-parallelism) against
what this library already does turned up two gaps:

1. **`ServiceClient.EnableAffinityCookie`** defaults to `true`. It pins every request from one
   client instance to a single Dataverse backend node - good for a single interactive session
   (cache locality), actively counter-productive for a pool whose entire purpose is spreading
   concurrent requests across many leased clients. This was previously only *documented* as a
   connection-string recommendation, which relies on every caller remembering to add it (and can be
   silently overridden by a connection string that sets it `true`).

2. **`ServiceClient.MaxRetryCount`/`RetryPauseTime`** (SDK defaults: 10 retries, 5s pause) govern the
   SDK's own internal retry loop for transient (non-429) errors. That loop runs entirely inside a
   single leased `ServiceClient` call, before anything is thrown to caller code. With the defaults,
   one call hitting a transient error can block for up to ~50 seconds before an exception ever
   reaches this library's throttle detection (docs/adr/0008) or a circuit breaker built on top of
   it - the pool has no way to distinguish "stuck" from "merely slow" until the SDK gives up.

`MaxRetryCount` is not limited to non-429 errors, though - it also governs HTTP 429
(service-protection/throttling) retries. A third related property,
`UseExponentialRetryDelayForConcurrencyThrottle` (SDK default: `false`), controls whether repeated
429 retries back off exponentially or just keep honoring Dataverse's raw `Retry-After` header each
time.

Both are real, settable properties (confirmed via reflection against the SDK's actual DLL, not
assumed from docs), so both are fixable without reflecting into private SDK internals.

## Decision
- **`EnableAffinityCookie` is forced to `false` in code**, unconditionally, on both the base client
  and every clone `DataverseServiceClientPolicy` produces - not left to the connection string. There
  is one correct answer for a pool, so there is no override knob for this.
- **`MaxRetryCount`/`RetryPauseTime`/`UseExponentialRetryDelayForConcurrencyThrottle` are exposed as
  optional overrides** via a new `DataverseClientOptions` type, passed through `DataverseUserPool`'s
  constructor to `DataverseServiceClientPolicy`, applied to both the base client and every clone.
  Unlike the affinity cookie, there is no single correct value here - the right choice depends
  entirely on how long a caller is willing to block per operation vs. how much resilience to
  transient/throttling errors they want the SDK to absorb silently. Left `null` (default), the SDK's
  own defaults apply - fully backward compatible.
- Validation (`MaxRetryCount >= 0`, `RetryPauseTime >= TimeSpan.Zero`) happens eagerly in
  `DataverseServiceClientPolicy`'s constructor, not lazily on first use, so a misconfiguration fails
  fast at startup rather than on the first `AcquireAsync`.
- Recommended pattern for making 429s pool-visible immediately: set `MaxRetryCount = 0`. This makes
  the SDK never retry internally on *any* error, including 429, and throw right away - letting
  `DataverseThrottleDetector`/the circuit breaker (docs/adr/0008) own backoff decisions instead of
  the SDK silently absorbing them.

## Consequences
- No behavior change for existing callers that don't pass `DataverseClientOptions` (affinity cookie
  fix aside, which is an unconditional correctness improvement for pooled usage).
- Callers with a tight end-to-end timeout budget (e.g. driven by `PoolOptions.AcquireTimeout` or
  their own operation timeout) can now lower `MaxRetryCount`/`RetryPauseTime` so SDK-internal retries
  don't silently eat most of that budget before the pool/circuit breaker ever sees a failure.
- **Does `MaxRetryCount=0` still give the pool a usable backoff signal on 429?** Yes, by reasoning
  (not directly verified against a live connection - see the testing note below):
  `DataverseThrottleDetector` reads `Retry-After` from `HttpOperationException.Response.Headers`,
  which mirrors the actual server response Dataverse sent back. That header reflects what the
  server returned on the very first 429, not something synthesized only once the SDK's retry budget
  is exhausted. So `MaxRetryCount=0` only changes *whether the SDK retries before throwing* - not
  what's attached to the resulting exception - and the throttle signal is preserved. This is the
  same exception path already verified via reflection for the exhausted-retry case in docs/adr/0008,
  applied here by analogy to the immediate/zero-retry case.
- Not solved: proactively reading Dataverse's `x-ms-ratelimit-*`/`x-ms-dop-hint` response headers
  before a call fails - still not possible, per docs/adr/0008, because `ServiceClient` does not
  surface headers for successful calls.
- Testing: `ServiceClient` cannot be constructed without a live Dataverse connection (its
  parameterless constructor is private, `IsReady` is non-virtual, so it can't be subclassed/faked),
  so the actual override *application* to a real client, and the `MaxRetryCount=0` 429 reasoning
  above, are not unit-testable. What is tested: `DataverseClientOptions.Validate()` range checks,
  and that `DataverseServiceClientPolicy`'s constructor runs that validation eagerly (throws
  `ArgumentOutOfRangeException` before any network attempt) - see `DataverseClientOptionsTests`.
