# ADR-0024: Operation-level metrics on instrumented execution paths

## Status
Accepted — implemented (2026-10-01). See "Implementation notes" at the end.

## Context
ADR-0018 added `DataversePool.Metrics`, which publishes the generic pool's point-in-time
`PoolStats` as `System.Diagnostics.Metrics` observable gauges. Those gauges answer pool-state
questions:

- How many resources are created, idle, leased, unhealthy/recycling, or waiting?
- Is the pool at its configured maximum?
- Are connection creation or post-creation operational failures accumulating?
- Have leaked leases been detected?

They do **not** answer operation-level questions:

- How many Dataverse operations are being attempted and completed?
- What are p50/p95/p99 operation latencies?
- How much time is spent waiting for a lease versus calling Dataverse?
- Which application-user/member is serving the traffic?
- Which member is slower, failing, or being throttled?
- How many retries and how much `Retry-After` delay are callers experiencing?
- Did adding another application user actually increase throughput?
- Does round-robin or least-connections perform better for this workload?

These questions are especially important because Dataverse service-protection limits are not
reliable constants that a client should hardcode. Microsoft's documentation gives published
reference values, but also says to let the server indicate what it can handle and explicitly says
the successful-response `x-ms-ratelimit-*` headers are for debugging rather than request-rate
control. This project's live tests also reached 150 directly-observed concurrent reads and 100
directly-observed concurrent writes on one application user without a rejection. Operation metrics
therefore give more reliable production evidence than a locally modeled "52 concurrent" or
"6,000 requests per five minutes" budget.

### Where operations can and cannot be observed

`DataversePool` deliberately exposes the underlying `ServiceClient` through
`DataverseLease.Resource`. Once a caller has acquired a lease, it can call any `ServiceClient`
method directly. The pool cannot transparently observe when those calls start, finish, retry
inside the SDK, or fail. `ServiceClient` exposes no general interception hook and does not expose
successful response headers through its public API (ADR-0008).

DataversePool does control two execution paths:

1. `PooledOrganizationService`, which implements `IOrganizationServiceAsync2` and wraps every
   operation in acquire/use/release logic (ADR-0020).
2. `DataversePool.ExecuteWithThrottleRetryAsync`, which owns the retry loop and therefore sees each
   attempt, recognized throttle, `Retry-After`, selected member, and final logical outcome
   (ADR-0017/0019).

Accurate automatic operation metrics are possible on those paths. They are not possible for direct
`lease.Resource` calls without either changing the public contract to return a proxy instead of a
`ServiceClient`, reflecting into the SDK's private HTTP pipeline, or asking the caller to add its
own wrapper. All three would be materially more invasive than the metrics feature warrants.

### Why metrics, not proactive rate limiting

A local rolling request counter can avoid one rejected HTTP call and reduce retry storms, but it
cannot know the real shared budget:

- Limits may be adaptive and differ from the published reference values.
- Multiple processes or applications may share the same application user.
- Requests have very different execution costs.
- DataversePool disables the affinity cookie, so successful-response diagnostic headers can refer
  to different backend servers and reset as routing changes.
- `ServiceClient` does not expose those successful-response headers anyway.

Operation metrics are observational: they provide evidence without delaying traffic based on an
unverified local model. A future adaptive-concurrency controller may consume this evidence, but
proactive rate limiting is not part of this ADR.

## Decision

### 1. Use standard .NET metrics, with no exporter dependency

Operation metrics will use `System.Diagnostics.Metrics`, under the existing default meter name
`DataversePool`. DataversePool will not depend on OpenTelemetry, Prometheus, Azure Monitor, or any
other exporter. Consumers opt into collection by subscribing to the meter, for example:

```csharp
services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("DataversePool"));
```

Unlike ADR-0018's pull-based `PoolStats` gauges, operation metrics are push-based instruments:
DataversePool directly observes each controlled execution path as it happens.

The implementation belongs in `ConnectionPool.Dataverse`, not `ConnectionPool.Metrics`:

- The events and tags are Dataverse-specific (`operation`, selected member, throttle outcome).
- Putting them in `ConnectionPool.Metrics` would make that currently Core-only package depend on
  `ConnectionPool.Dataverse` and the Dataverse SDK.
- `System.Diagnostics.Metrics` is part of the target framework and adds no metrics-backend
  dependency to `ConnectionPool.Dataverse`.
- Recording is effectively dormant when no listener subscribes. No background thread, timer, or
  exporter is created by the library.

ADR-0018 remains unchanged: `DataversePool.Metrics` continues to publish generic pool-state gauges.
This ADR adds Dataverse-operation instruments alongside them under the same meter.

### 2. Instrument only paths DataversePool genuinely controls

The first implementation will instrument:

- Every `PooledOrganizationService` method, including synchronous methods through their existing
  async delegation. A synchronous call must produce one metric series, not a second duplicate
  series around the sync wrapper.
- Every attempt made by `DataversePool.ExecuteWithThrottleRetryAsync`, plus the final logical-call
  outcome and retry information.

Direct calls made through `DataverseLease.Resource` or a `PooledLease<ServiceClient>.Resource` are
explicitly outside automatic instrumentation. The README must say this next to the metrics example
so users do not assume the counters cover all traffic merely because the pool created the client.

No private SDK reflection, HTTP-message-handler replacement, or proxy pretending to be a
`ServiceClient` will be introduced to close this gap.

### 3. Measure separate phases instead of publishing one ambiguous latency

For `PooledOrganizationService`, three durations are useful and have distinct meanings:

| Instrument | Meaning |
|---|---|
| `dataversepool.operation.acquire.duration` | Time from facade entry until a lease is acquired. Includes capacity waiting and resource creation if required. |
| `dataversepool.operation.duration` | Time spent invoking the leased `ServiceClient` for one attempt. Excludes lease acquisition. |
| `dataversepool.operation.total.duration` | End-to-end facade latency: acquire + SDK operation + release/reporting overhead. |

All durations are `Histogram<double>` values expressed in seconds, matching OpenTelemetry metric
conventions and allowing exporters to calculate percentiles.

`dataversepool.operation.active` is an `UpDownCounter<long>` incremented immediately before the SDK
call and decremented in `finally`. It represents actual Dataverse operations in flight, not callers
waiting for a pool lease.

`dataversepool.operation.waiting` is an `UpDownCounter<long>` incremented while a facade call is
waiting for/acquiring a lease. This separates Dataverse latency from pool saturation.

### 4. Count attempts and logical calls separately

Retries make "request count" ambiguous. `ExecuteWithThrottleRetryAsync` can perform several SDK
attempts for one caller-visible invocation. The metrics model therefore distinguishes:

| Instrument | Type | Meaning |
|---|---|---|
| `dataversepool.operation.attempts` | `Counter<long>` | One increment per actual SDK operation attempt. This is the closest available approximation to requests Dataverse received. |
| `dataversepool.operation.calls` | `Counter<long>` | One increment per completed caller-visible facade/helper invocation, regardless of retries. |
| `dataversepool.operation.retries` | `Counter<long>` | One increment when the helper schedules another attempt after a recognized throttle. |
| `dataversepool.operation.retry_after` | `Histogram<double>` (seconds) | The capped `Retry-After` duration used by DataversePool for a recognized throttle. |

`PooledOrganizationService` does not retry, so one facade call normally produces one attempt and
one logical call. `ServiceClient` may still retry internally when `MaxRetryCount > 0`; those hidden
SDK attempts cannot be measured individually. Documentation and metric descriptions must say that
`attempts` means attempts visible to DataversePool, not necessarily raw HTTP requests. Setting
`MaxRetryCount = 0` makes the relationship exact for service-protection retries owned by
DataversePool.

The existing overload of `ExecuteWithThrottleRetryAsync` remains source-compatible and reports
`operation.name = "custom"`. A new additive overload accepts a low-cardinality `operationName`, so
callers that use the helper can distinguish stable workload categories without parsing delegates
or request payloads.

### 5. Use a small, stable, low-cardinality tag set

Every operation measurement uses only tags needed for aggregation:

| Tag | Values / rule |
|---|---|
| `pool.name` | Caller-configured stable pool name; defaults to `default`. |
| `pool.member.name` | `DataverseUserPool.Name` selected for the attempt. |
| `dataverse.operation.name` | Fixed facade names (`create`, `retrieve`, `update`, `delete`, `execute`, `associate`, `disassociate`, `retrieve_multiple`, `create_and_return`) or caller-supplied stable name for the helper. |
| `outcome` | `success`, `error`, `throttled`, or `canceled`. |
| `error.type` | Exception type name, only on `error`/`throttled`/`canceled` outcomes. |

The following must **not** be metric tags:

- Entity/record IDs.
- Tenant, environment URL, client ID, or secret-related values.
- Query text, FetchXML, request payloads, filter expressions, or exception messages.
- Caller/impersonated user IDs.
- Retry attempt number.
- Arbitrary entity logical names by default.

Those values are either sensitive or unbounded/high-cardinality and can make a metrics backend
expensive or unusable. Applications needing per-entity analysis should create a small, explicit
set of stable operation names at their own execution boundary.

`pool.name`, member names, and custom operation names are caller-controlled. Their XML
documentation must require stable configuration values rather than request-derived strings.

### 6. Define outcomes consistently

- `success`: the SDK operation returned normally.
- `throttled`: the thrown exception was recognized by `DataverseThrottleDetector`, whether or not
  `ExecuteWithThrottleRetryAsync` later retries it.
- `canceled`: an `OperationCanceledException` escaped because the supplied cancellation token was
  canceled.
- `error`: any other exception.

Each attempt records exactly one outcome. Each logical call also records exactly one final outcome.
Exceptions continue to propagate unchanged; metrics are observational side effects and must never
swallow, wrap, replace, or delay an exception.

The implementation uses only BCL `Meter` instruments and fixed tag construction; DataversePool
does not call an exporter directly. A custom `MeterListener` can execute its measurement callback
synchronously as part of `Counter.Add`/`Histogram.Record`; that is standard
`System.Diagnostics.Metrics` behavior and outside DataversePool's control. The implementation must
avoid its own fallible user callbacks and should check `Instrument.Enabled` before allocating tag
arrays or starting detailed timers, so the no-listener path stays close to zero-allocation and does
not change normal operation behavior.

### 7. Keep metrics distinct from traces and logs

This ADR does not add `ActivitySource` tracing, request-body logging, or correlation IDs. Metrics
answer aggregate rate/latency/error questions. Distributed traces may be added separately if a
concrete need emerges, using the same operation-name and outcome vocabulary.

`ILogger` remains appropriate for exceptional diagnostic detail. Metrics must not copy exception
messages into tags.

## Proposed public configuration

The exact constructor shape can be adjusted during implementation, but the required behavior is:

```csharp
var service = new PooledOrganizationService(
    pool,
    new DataverseOperationMetricsOptions
    {
        PoolName = "orders-import"
    });
```

The options object supplies stable metric identity only; it does not select an exporter or contain
callbacks. Omitting it uses `pool.name = "default"` while still publishing to the standard meter
when a listener is present.

For `ExecuteWithThrottleRetryAsync`, the additive named overload is conceptually:

```csharp
await pool.ExecuteWithThrottleRetryAsync(
    operationName: "retrieve_account_batch",
    operation: async (client, ct) => { /* ... */ },
    cancellationToken: cancellationToken);
```

The implementation should share one internal recorder between the facade and retry helper so
instrument names, tags, outcome classification, and timer behavior cannot drift.

## Testing requirements

The implementation is not accepted until tests prove:

1. A successful facade operation records one attempt, one logical call, acquire/operation/total
   durations, and balanced active/waiting counters with the selected member tag.
2. A non-throttle exception records `outcome=error`, preserves the exact original exception
   instance, and leaves active/waiting counters balanced.
3. Cancellation records `outcome=canceled` and does not become a generic error.
4. A recognized 429 records `outcome=throttled` and its capped `retry_after`.
5. A retry-helper invocation with two throttled attempts followed by success records three
   attempts, two retries, and one successful logical call.
6. Synchronous `PooledOrganizationService` methods do not double-count their delegated async call.
7. Direct `lease.Resource` use emits no automatic operation metrics (documenting the boundary).
8. Tags contain no entity IDs, payloads, URLs, client IDs, query text, or exception messages.
9. Metrics work with the real BCL `MeterListener`, without an OpenTelemetry package in tests.
10. With no listener, behavior and exception propagation remain unchanged, and no per-call tag
    arrays are allocated.

Most tests can exercise the internal recorder and the generic `LeaseScope` timing hooks with fake
leases, as ADR-0020 already does. A small live integration smoke test may validate real member
attribution, but live Dataverse access is not required for the core metric semantics.

## Consequences

- Operators can compare throughput, latency, errors, throttles, and member distribution without
  adopting a DataversePool-specific dashboard or metrics backend.
- Pool saturation becomes distinguishable from slow Dataverse operations because acquisition and
  operation durations are separate.
- Multi-app-user scaling claims become measurable in the consuming application's own workload.
- Retry behavior becomes visible without inferring it from logs.
- Direct `ServiceClient` calls through a raw lease remain invisible unless the caller instruments
  them. This is an explicit API-boundary limitation, not an implementation defect.
- The metrics are process-local. Multiple application replicas export separate measurements; the
  backend can aggregate them, but DataversePool does not coordinate rate or throttle state across
  processes.
- This ADR does not implement proactive rate limiting or adaptive concurrency. The resulting
  metrics may inform such a future design, but no control loop should be added until production
  evidence demonstrates a need and a safe algorithm.

## Implementation notes

- **Shared executor instead of `LeaseScope` hooks.** `PooledOrganizationService` and
  `ExecuteWithThrottleRetryAsync` now both run through one internal, generic
  `DataverseOperationExecutor` loop. The facade calls it with `maxAttempts: 1`. `LeaseScope`
  (ADR-0020) was removed: the executor keeps all of its guarantees and tests (release exactly once,
  exact exception identity, throttle reported before release). It also makes the retry loop
  testable with fake leases for the first time.
- **Recorder.** `DataverseOperationRecorder` owns all nine instruments on a meter named
  `"DataversePool"`, the same name as the `DataversePool.Metrics` gauges. Each write is guarded, so
  a throwing listener cannot alter an outcome. The enabled flag is read once per call, so a listener
  attaching mid-call cannot unbalance the up-down counters. Tags use a stack `TagList`. A test
  asserts that with no listener, the executor allocates no more per call than a plain
  acquire/try/finally.
- **Public surface:** `DataverseOperationMetrics` (name constants), `DataverseOperationMetricsOptions`
  (`PoolName`), optional `metricsOptions` parameters on the `DataversePool` and
  `PooledOrganizationService` constructors, and the named `ExecuteWithThrottleRetryAsync` overload.
  The unnamed overload records `dataverse.operation.name = "custom"`. `AddDataversePool` uses its
  DI key as `pool.name`. A facade over a `DataversePool` inherits the pool's name unless overridden.
- **Classification:** `canceled` requires the caller's own token to be cancelled. An SDK-internal
  `TaskCanceledException` (e.g. HTTP timeout) counts as `error`. A throttle reporter that throws is
  treated as "not throttled", which keeps the old exception-filter semantics.
- **Testing requirements 1–10** are covered in `DataverseOperationExecutorTests` and
  `PooledOrganizationServiceTests`, using a real `MeterListener`. Beyond them, tests also show that
  the lease is released before a same-member `Retry-After` wait, and that a throwing listener
  changes nothing.
- **SOAP-path throttling (resolved).** Until 2026-10 the throttle reporter inside the executor only
  recognized the Web API's HTTP 429, so on the SOAP transport a throttled call was classified
  `error`, never `throttled`, and never fed `dataverse.operation.throttle.*`. `DataverseThrottleDetector`
  now also recognizes the three service-protection `OrganizationServiceFault` codes (confirmed live
  for `ConcurrentRequests` and `NumberOfRequests`; see ADR-0008's addendum), so the `throttled`
  outcome and the throttle metrics are populated on both transports. The metrics do not yet say
  *which* limit fired (request count, execution time, concurrency).
- **Connection faults.** The executor also reports non-throttle transport failures to the lease
  (`ReportIfConnectionFault`), recycling the connection and feeding the breaker. This changes pool
  health, not the recorded outcome: such a call is still classified `error`.
