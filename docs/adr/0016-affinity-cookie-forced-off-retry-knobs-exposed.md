# ADR-0016: Force `EnableAffinityCookie=false` in code; expose `MaxRetryCount`/`RetryPauseTime` as optional overrides

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

Both are real, settable properties (confirmed via reflection against the SDK's actual DLL, not
assumed from docs), so both are fixable without reflecting into private SDK internals.

## Decision
- **`EnableAffinityCookie` is forced to `false` in code**, unconditionally, on both the base client
  and every clone `DataverseServiceClientPolicy` produces - not left to the connection string. There
  is one correct answer for a pool, so there is no override knob for this.
- **`MaxRetryCount`/`RetryPauseTime` are exposed as optional overrides** via a new
  `DataverseClientOptions` type, passed through `DataverseUserPool`'s constructor to
  `DataverseServiceClientPolicy`, applied to both the base client and every clone. Unlike the
  affinity cookie, there is no single correct value here - the right choice depends entirely on how
  long a caller is willing to block per operation vs. how much resilience to transient errors they
  want the SDK to absorb silently. Left `null` (default), the SDK's own defaults apply -
  fully backward compatible.
- Validation (`MaxRetryCount >= 0`, `RetryPauseTime >= TimeSpan.Zero`) happens eagerly in
  `DataverseServiceClientPolicy`'s constructor, not lazily on first use, so a misconfiguration fails
  fast at startup rather than on the first `AcquireAsync`.

## Consequences
- No behavior change for existing callers that don't pass `DataverseClientOptions` (affinity cookie
  fix aside, which is an unconditional correctness improvement for pooled usage).
- Callers with a tight end-to-end timeout budget (e.g. driven by `PoolOptions.AcquireTimeout` or
  their own operation timeout) can now lower `MaxRetryCount`/`RetryPauseTime` so SDK-internal retries
  don't silently eat most of that budget before the pool/circuit breaker ever sees a failure.
- Not solved: proactively reading Dataverse's `x-ms-ratelimit-*`/`x-ms-dop-hint` response headers
  before a call fails - still not possible, per docs/adr/0008, because `ServiceClient` does not
  surface headers for successful calls.
- Testing: `ServiceClient` cannot be constructed without a live Dataverse connection (its
  parameterless constructor is private, `IsReady` is non-virtual, so it can't be subclassed/faked),
  so the actual override *application* to a real client is not unit-testable. What is tested:
  `DataverseClientOptions.Validate()` range checks, and that `DataverseServiceClientPolicy`'s
  constructor runs that validation eagerly (throws `ArgumentOutOfRangeException` before any network
  attempt) - see `DataverseClientOptionsTests`.
