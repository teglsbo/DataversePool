# DataversePool

Connection pooling for [`Microsoft.PowerPlatform.Dataverse.Client.ServiceClient`](https://learn.microsoft.com/power-platform/developer/data-platform/xrm-tooling/use-dataverse-service-client),
built around one core fact: **Dataverse's concurrent-request budget is enforced per application
user, not per environment/tenant** (the exact limit is environment-dependent — see the
[live measurements](#live-proof-multiple-application-users-raise-throughput) below). A single application
user can only ever push so much traffic through Dataverse at once, no matter how it's called. The
only way to raise that ceiling is to spread load across **multiple** application users (service
principals) — and once you're doing that, you need something to round-robin/load-balance across
them, keep enough clients warm and ready to avoid paying construction cost per request, and stop
routing to a member that's gone bad or is currently throttled. That's what DataversePool is: a
small, generic async resource pool (`DataversePool.Core`) plus a Dataverse-specific adapter
(`DataversePool.Dataverse`, with health-aware/least-connections/round-robin load balancing across
members and per-member circuit breaking + throttle-awareness) and an optional Polly v8 integration
(`DataversePool.Polly`) — so you check out a ready-to-use `ServiceClient` from whichever member the
pool decides is best, use it, and return/dispose it, instead of managing
construction/cloning/round-robin/health yourself.

It's also useful with just a **single** application user: constructing a new `ServiceClient` per
request pays the cold authentication/discovery cost (hundreds of ms to seconds), while cloning
sequentially from a warmed base costs about 1ms ([ADR-0002](docs/adr/0002-serial-creation-gate-no-parallel-cloning.md)).
A `ServiceClient` also carries per-instance mutable state (e.g. `CallerId`) that isn't safe to share
across concurrent callers using different identities. But the multi-application-user case — raising
your effective throughput ceiling — is the main reason this library exists.

> **Direct pinned raw-`HttpClient` experiment against a real Dataverse environment:** one application user completed 110
> slow requests and rejected 18 with an explicit concurrency-limit HTTP 429 in 32.7 seconds. Two
> distinct application users on the same server completed all 160 requests in 34.0 seconds. That is
> about **40% more successful operations per second** in this saturated raw-HTTP burst. This is a
> controlled experiment, not a `DataversePool` throughput benchmark. Separately, the same 160-operation
> workload completed through real `DataversePool` leases with an exact 80/80 member split and no rejection.
>
> A separate practical write benchmark inserted 1,000 independent rows into the purpose-built
> `new_loadthin` standard table. One user with 32 leases averaged **333.8 rows/s**; two users with
> 32 leases each averaged **484.8 rows/s**. That is **1.45x throughput** and about **32% shorter
> completion time**, with every one of the 4,000 measured rows succeeding and then being deleted.

> Status: **pre-1.0 / preview**. Core design is implemented and tested (see [`TODO.md`](TODO.md)
> for exact scope and open items). API may still shift before a 1.0 release.
>
> Sister project: DataverseDuck (`dvduck`) — a separate tool, not a
> dependency of this library.

## Live proof: multiple application users raise throughput

If you came here assuming a single `ServiceClient` instance *itself* serializes concurrent async
calls (e.g. because of some internal lock), and that this library exists to work around *that* —
it doesn't, and it isn't. Measured directly against a real Dataverse instance, a single
`ServiceClient` genuinely executes concurrent calls concurrently; see
[ADR-0023](docs/adr/0023-serviceclient-async-concurrency-corrected-premise.md) for that
measurement. This library's live tests have also reached 150 locally outstanding metadata reads
and 100 locally outstanding real `Create` calls against one application user with zero rejection.
A sustained test reached 320 locally outstanding heavy metadata calls without a 429, but latency
collapsed (p50 38s, p95 56s) and one call eventually failed at the SSL transport layer. "Locally
outstanding" is deliberately not described as 320 requests executing simultaneously inside
Dataverse: client, transport, or server queues may sit between those measurements.

A direct Web API test then removed most of that ambiguity: one `HttpClient`, one shared
`ARRAffinity` cookie (pinning every request to the same Dataverse web server), no retries, and the
slow `solutioncomponents?$top=5000` query. All 64 requests in the first burst succeeded. With 128
workers released within 26ms, **110 succeeded and 18 received HTTP 429** with error
`0x80072326`: `Number of concurrent requests exceeded the limit of 100`. The rejected responses
reported `Retry-After` values from 6m03s to 7m07s. This tenant therefore has a measured
per-application-user concurrent-request limit of **100**, not the commonly cited 52.

The same test was then run with **two distinct application users sharing the same affinity
cookie/server**: 160 requests started within 39ms, split 80/80. All **160 returned HTTP 200**,
with no 429 or transport failure. The burst completed in 34.0s (p50 21.0s, p95 33.4s), compared
with the one-user 128-request burst's 110 successes and 18 concurrency rejections in 32.7s. This
is approximately 4.70 successful operations/s versus 3.36/s: about **40% higher successful
throughput** under saturation. It directly demonstrates that the per-user ceiling is independent
per application user and that two users can keep more than one user's 100-request budget accepted
on the same server. See
`LivePinnedWebApiConcurrencyTests` and `TODO.md` for the full instrumented results.

An increasing two-user ladder sharpened the boundary. Bursts of 180 (90/90), 200 (100/100), and
220 (110/110 offered) all completed without rejection. At 240 (120/120 offered), each user
completed 114 requests and received 6 explicit concurrency-limit 429s; at 256, each completed 115
and received 13. The *offered-burst* threshold therefore fell between 220 and 240 for this query,
while the server continued to state the actual active limit as 100 per user. Offered requests can
exceed 200 because some finish or wait in queues before all requests are simultaneously active.

The 160-request case was also executed through the real `DataversePool` API rather than the direct
HTTP harness: every worker acquired a group lease and invoked the same slow query through
`lease.Resource.ExecuteWebRequestAsync`. Round-robin selected each member exactly 80 times, all 160
operations returned HTTP 200, and the corrected burst duration was 33.7s (p50 19.9s, p95 32.1s).
This pool test uses DataversePool's normal production policy with affinity disabled; the pinned
test above remains the controlled proof of the per-user, per-server limit.

### Practical insert throughput

The concurrency result also translates into faster useful work rather than only more accepted
requests. `LiveInsertThroughputBenchmarkTests` uses the existing `new_loadthin` custom standard
table: one primary key, one name column, no application business logic, and one individual
`CreateAsync` per `DataversePool` lease. It deliberately does not use `CreateMultiple` or
`ExecuteMultiple` for the measured inserts.

Four balanced runs inserted 1,000 independent rows each in the order single A, two users, two
users, single B. Each identity therefore handled the same total number of rows. Concurrency was
kept below the observed write limit: 32 leases for a single user and 32 per user (64 total) for two.
This is intentionally a scale-out comparison rather than a same-aggregate-concurrency control:
adding the second identity adds another safe 32-operation lane, which is the capacity DataversePool
exists to expose. The earlier pinned and 160-worker tests isolate identity budgets at equal offered
load.

| Mode | Run 1 | Run 2 | Average |
|---|---:|---:|---:|
| One application user | 286.5 rows/s | 381.1 rows/s | **333.8 rows/s** |
| Two application users | 452.8 rows/s | 516.8 rows/s | **484.8 rows/s** |

All four runs completed 1,000/1,000 inserts with zero throttle or other error. Two users were
**1.45x faster by throughput**, reducing average completion time from 3.06s to 2.07s. Cleanup then
deleted all 4,000 rows exactly, with no batch fault.

An earlier overload probe used 160 workers for both modes. The single-user runs received explicit
`Number of concurrent requests exceeded the limit of 40` faults (27 and 55 respectively), while
both two-user runs completed 1,000/1,000. This differs from the pinned read query's measured limit
of 100 and is another reason not to treat any one observed number as a universal `MaxSize`:
operation path, backend server, environment, and routing all matter.

That doesn't make the per-user concurrent-request ceiling irrelevant, though — it's a real,
documented, server-enforced limit, just not one that a single `ServiceClient` instance's own
threading model has anything to do with hitting or avoiding. Round-robin pooling across multiple
application users (this library's actual purpose, see above) raises that ceiling by spreading load
across the users the limit applies to; it isn't "working around" `ServiceClient` itself. A
longer repeated one-user-versus-two-user benchmark is still required to quantify steady-state
throughput and determine where a shared environment or endpoint bottleneck eventually takes over.

## Why not just `new ServiceClient(...)` per request?

- **Serialized/expensive construction.** A `ServiceClient` clone can take from ~1ms (warm,
  sequential) up to 1–3.2s (cold, or under construction-time lock contention) — see
  [ADR-0002](docs/adr/0002-serial-creation-gate-no-parallel-cloning.md). DataversePool serializes all
  creation through a single gate so you get the fast path, not the contention path.
- **Per-user Dataverse service-protection limits (~52 concurrent requests/user, commonly cited —
  but measured as 100 per user on one pinned server in this sandbox).** This is the
  real, server-side constraint — round-robin pooling across multiple application users is the
  standard way to scale beyond one user's budget, independent of how a single `ServiceClient`
  instance behaves under concurrent load — see
  [`DataversePool.Dataverse`'s group pool](#quickstart-group-pool-multiple-application-users).
- **`CallerId` (and similar per-instance state) isn't safe to share across concurrent identities.**
  It's a plain property read at call time, not per-call/thread-local state, so two callers using the
  same instance with different `CallerId` values concurrently can race. Pooling gives each caller/lease
  an instance it isn't sharing concurrently with a *different identity* — see
  [ADR-0023](docs/adr/0023-serviceclient-async-concurrency-corrected-premise.md).
- **A dead pool member shouldn't take down the group.** The default group-pool strategy is
  health-aware: It circuit-opens a consistently-failing member, retries it after a cooldown, and
  fails open (keeps serving) rather than locking the whole pool out — see
  [ADR-0007](docs/adr/0007-race-conditions-timeouts-and-failure-scenarios.md). Its operational
  failure count is pool-wide, not per clone; successful returns from other slots can mask one
  persistently failing clone under mixed traffic.

> **`UseWebApi` is not a substitute for pooling on read-heavy workloads.** In the current SDK,
> `RetrieveMultiple` (and reads generally) never route through the Web API/HTTP translation path —
> only `Create`/`Update`/`Delete`/`ImportSolution`/`ExportSolution`/`StageSolution` are eligible for
> that translation, regardless of the `UseWebApi` connection setting. So a read-heavy caller (e.g.
> an existence-check/lookup workload doing many `RetrieveMultiple` calls) always goes through the
> legacy proxy and always contends for the same per-application-user server-side request ceiling —
> `UseWebApi: true` does nothing for that contention. Round-robin pooling across application users
> is the only lever for raising that ceiling, independent of `UseWebApi`.

## Prior art / how this compares

A few existing projects address parts of the same problem, but not the full scope of this library:

- **[PooledServiceClientFactory](https://github.com/zhufamily/PooledServiceClientFactory)** — an
  existing open-source `ServiceClient` pool: Configurable capacity, auto-scale-down, and avoids the
  socket-exhaustion/thread-safety issues of constructing a `ServiceClient` per request. It pools a
  single connection string (comparable to this library's `DataverseUserPool`) but does not do
  cross-service-principal round-robin/load-balancing, and has no circuit breaker or
  throttle-awareness.
- **[Microsoft's own `PowerPlatform-DataverseServiceClient` GitHub discussion #399](https://github.com/microsoft/PowerPlatform-DataverseServiceClient/discussions/399)**
  — a community discussion suggesting manually cycling across multiple MSAL
  `ConfidentialClientApplication` instances (i.e. multiple application users) to spread load. No
  concrete, reusable implementation — just the idea.

As far as could be determined, no existing library combines health-aware round-robin/least-connections
load balancing **across multiple Dataverse service principals**, with per-member circuit breaking and
throttle-awareness, the way `DataversePool.Dataverse`'s
[group pool](#quickstart-group-pool-multiple-application-users) does — that combination is this library's main
differentiator over rolling your own `ServiceClient` pool or using the single-connection-string
alternatives above.

## Packages

| Package | Purpose | Depends on |
|---|---|---|
| `DataversePool.Core` | Generic async resource pool engine. No Dataverse/network dependency. | — |
| `DataversePool.Dataverse` | `ServiceClient` policy + `DataversePool` (1..N members, round-robin/health-aware). | `DataversePool.Core`, `Microsoft.PowerPlatform.Dataverse.Client` |
| `DataversePool.Polly` | Wires Polly v8 retry/circuit-breaker outcomes to a lease's health signal. | `DataversePool.Core`, `Polly.Core` (optional — not required by the other two packages) |
| `DataversePool.Metrics` | Publishes pool health as `System.Diagnostics.Metrics` observable gauges (OpenTelemetry-compatible). | `DataversePool.Core` (optional — no metrics backend dependency) |

## Quickstart: One user (start here)

Even with a single Dataverse application user today, construct a `DataversePool` (not
`DataverseUserPool` directly) if there's any chance you'll add more users later — going from one
member to several then only means changing how you construct/configure it, not your call sites.
See [ADR-0019](docs/adr/0019-unify-single-and-multi-user-pools-as-dataversepool.md).

```csharp
using ConnectionPool.Core;
using ConnectionPool.Dataverse;

var member = new DataverseUserPool(
    name: "primary",
    connectionString: "AuthType=ClientSecret;Url=...;ClientId=...;ClientSecret=...;",
    options: new PoolOptions { MaxSize = 8, PrewarmCount = 2 });

var pool = new DataversePool(member); // single-member convenience constructor

await pool.WarmupAsync(); // sequential, see ADR-0002 — do this once at startup

await using (var lease = await pool.AcquireAsync())
{
    var who = lease.Resource.Execute(new WhoAmIRequest());
    // lease.Resource is a Microsoft.PowerPlatform.Dataverse.Client.ServiceClient
}
// disposing the lease returns the ServiceClient to the pool (or recycles it, if unhealthy)
```

`MaxSize` is a **local lease/resource limit per application user**, not Dataverse's server limit.
The values in these examples are illustrative, not recommended production defaults. In particular,
do not hardcode 52, 80, or the sandbox's measured 100 as a universal ceiling: DataversePool disables
server affinity, so production traffic may be spread across multiple backend servers, and capacity
also varies by environment, workload, other processes, and other consumers of the same identity.
Choose `MaxSize` from the application's resource and latency budget, then tune it from observed
throughput, latency, waiting leases, and real 429 signals. Multiple processes/pods do not share the
pool's lease count.

You can change it at runtime without a restart. Growing takes effect immediately. Shrinking never
cancels in-flight calls: the excess leases are retired as they are returned. See
[ADR-0025](docs/adr/0025-runtime-adjustable-pool-size.md).

```csharp
member.SetMaxSize(16);              // DataverseUserPool (or ResourcePool<T>)
var current = member.MaxSize;       // also reported as PoolStats.MaxSize
// e.g. follow Dataverse's hint, read from any leased client:
member.SetMaxSize(lease.Resource.RecommendedDegreesOfParallelism);
```

> **`AcquireTimeout` and `CreateTimeout` default to 30 seconds, not "wait forever".** An unbounded
> default turns a saturated pool or a hung connection attempt into callers blocked indefinitely with
> no exception and no signal - strictly harder to diagnose than a bounded failure, and the usual
> root cause behind "the app just got slow". `AcquireTimeout` bounds the *entire* acquire (queue wait
> plus any inline recycle/creation), and a finite `CreateTimeout` is what makes that bound hold even
> when the underlying `CreateAsync` ignores its cancellation token - because creation is serialized,
> one hung create otherwise stalls every other caller. A blown `AcquireTimeout` throws
> `PoolAcquireTimeoutException`, whose message carries a full `PoolStats` snapshot. Both are
> deliberately generous (they exist to catch hangs, not to act as a per-operation latency budget -
> pass your own `CancellationToken` for that); set either explicitly to `null` to restore the
> previous unbounded behavior.

> **`EnableAffinityCookie` is forced to `false` automatically.** Dataverse's server affinity cookie
> (on by default) pins all requests from one `ServiceClient` to a single backend node - good for a
> single interactive session, but counter-productive here: A pool exists specifically to spread
> concurrent requests out, and pinning every pooled resource's traffic to one node just recreates a
> single-node bottleneck server-side. `DataverseServiceClientPolicy` sets this to `false` in code on
> every client it creates (base and clones), regardless of what your connection string says, so you
> don't need to remember to add it yourself. See
> [Microsoft's docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.powerplatform.dataverse.client.serviceclient.enableaffinitycookie)
> for details.

> **`UseWebApi` is a pool-wide option.** Set it in the connection string or via
> `DataverseClientOptions.UseWebApi`; the configured baseline is restored when each lease returns,
> so callers must not toggle it per lease. The SDK routes only eligible operations through Web API
> (including `Create`, `Update`, and `Delete`); reads such as `RetrieveMultiple` still use the legacy
> proxy path. See the Web API note above for the effect on read-heavy workloads.

> **`MaxRetryCount`/`RetryPauseTime`/`UseExponentialRetryDelayForConcurrencyThrottle` are optional
> overrides, not forced.** The SDK's own defaults (10 retries, 5s pause) mean a single call hitting a
> transient error can silently block a leased client for up to ~50 seconds before an exception ever
> reaches this pool's throttle detection or a circuit breaker built on top of it. `MaxRetryCount`
> also governs HTTP 429 (service-protection/throttling) retries, not just other transient errors -
> set it to `0` to make the SDK fail fast. The pool currently detects HTTP 429s, but SOAP-fault
> throttles have not been confirmed or classified; do not assume that the pool can honor their
> `Retry-After` when SDK retries are disabled. Unlike the affinity cookie, there's no single correct
> value here - it depends on
> your own timeout budget - so pass a `DataverseClientOptions` to `DataverseUserPool`'s constructor
> (or to `AddDataverseUserPool`) to override any of these; leave them `null` (default) to keep the
> SDK's defaults. See
> [ADR-0016](docs/adr/0016-affinity-cookie-forced-off-retry-knobs-exposed.md).

```csharp
var member = new DataverseUserPool(
    "sample-user",
    connectionString,
    clientOptions: new DataverseClientOptions { MaxRetryCount = 0, UseWebApi = true }); // fail fast; enable Web API where supported
```

**Never going to scale beyond one user, and want to skip the selection-strategy layer entirely?**
Use `DataverseUserPool` directly instead of wrapping it in a `DataversePool` - see ADR-0006 for
the zero-overhead rationale. You lose `ExecuteWithThrottleRetryAsync` and `DataverseLease`, but
`DataverseUserPool` still exposes `ReportThrottled`/`ThrottledUntil`/`IsThrottled` directly if you
want to hand-roll retry logic yourself.

## Scaling to multiple application users

Use this when one application (service principal) user's concurrent-request budget isn't enough —
register several application users and let the *same* `DataversePool` type round-robin
across them. This is the one behavior change from the single-user quickstart above: More members
passed to the same constructor, nothing else in your code changes.

```csharp
using ConnectionPool.Core;
using ConnectionPool.Dataverse;

var options = new PoolOptions { MaxSize = 8, PrewarmCount = 2 };
var pool = new DataversePool(new[]
{
    new DataverseUserPool("app-user-1", connectionStringUser1, options),
    new DataverseUserPool("app-user-2", connectionStringUser2, options),
    new DataverseUserPool("app-user-3", connectionStringUser3, options),
});

await pool.WarmupAsync(); // warms up each member sequentially

await using var lease = await pool.AcquireAsync();
// selection uses HealthAwareRoundRobinSlotSelectionStrategy by default:
// a member that keeps failing gets circuit-opened, retried after a cooldown,
// and the whole pool fails open (rather than deadlocking) if all members are down.
```

**Round-robin vs. load-aware selection.** The default `HealthAwareRoundRobinSlotSelectionStrategy`
distributes evenly and skips dead members, but doesn't look at how busy each member currently is.
If call durations vary a lot (some members can end up stuck on long-running requests), pass
`LeastConnectionsSlotSelectionStrategy` instead — it picks whichever member currently has the
fewest leased connections (`PoolStats.LeasedCount`), with the same dead-member circuit-breaking:

```csharp
var pool = new DataversePool(members, new LeastConnectionsSlotSelectionStrategy());
```

**Throttle-aware routing.** Both strategies also skip a member that's currently marked as
Dataverse-throttled. `DataversePool.AcquireAsync()` returns a `DataverseLease` (not a
plain lease) specifically so you can report a 429 back to the member that actually served the
request:

```csharp
await using var lease = await pool.AcquireAsync();
try
{
    var response = (WhoAmIResponse)lease.Resource.Execute(new WhoAmIRequest());
}
catch (Exception ex) when (lease.ReportIfThrottled(ex))
{
    // Dataverse returned HTTP 429; DataverseThrottleDetector parsed Retry-After from the exception
    // and ReportIfThrottled recorded it on lease.Member. The pool's selection strategy will steer
    // new acquires to other members until that window expires. lease.Member is still not "unhealthy"
    // - the connection itself is fine, just decide here whether to retry, rethrow, etc.
    throw;
}
```

**Want that retry to happen automatically?** Use `DataversePool.ExecuteWithThrottleRetryAsync`
instead of hand-rolling the loop above - it acquires a lease, runs your operation, and on a 429
reports the throttle and retries. If a different, non-throttled member is available, the retry
happens immediately (routed there by the selection strategy); if not - including the single-member
case above - it actually waits out the capped `Retry-After` first, instead of instantly re-hitting
the same still-throttled connection for no benefit. See
[ADR-0019](docs/adr/0019-unify-single-and-multi-user-pools-as-dataversepool.md).

```csharp
var response = await pool.ExecuteWithThrottleRetryAsync(
    (client, ct) => Task.FromResult((WhoAmIResponse)client.Execute(new WhoAmIRequest())));
```

Only a recognized throttling signal is retried - any other exception from your operation propagates
immediately. This is deliberately narrow (closing the "retry when throttled" gap), not a general
resilience pipeline - use the optional Polly adapter package for arbitrary retry/circuit-breaking
needs.

> **Dataverse's `Retry-After` is capped by default, not honored verbatim.** Real-world 429 responses
> have been observed reporting `Retry-After` values as high as ~17 minutes. Honoring that literally
> would exclude a member from the pool's rotation for a very long time from one signal (or, for a
> single-member pool, mean an actual ~17-minute wait before the next retry).
> `DataverseThrottleDetector.DefaultMaxRetryAfter` (80 seconds) is applied everywhere a `Retry-After`
> is translated into a duration - `ReportIfThrottled` and `ExecuteWithThrottleRetryAsync` both accept
> an explicit `maxRetryAfter` override if you want a different cap, including `TimeSpan.MaxValue` to
> opt back into Dataverse's raw value. See
> [ADR-0017](docs/adr/0017-group-throttle-retry-helper-and-capped-retry-after.md).

> **Scaling and telemetry notes.** Pool size does not adjust itself (no autoscaling or idle eviction), but you can
> change it at runtime with `SetMaxSize` (ADR-0025). Throughput scales by adding application users to a `DataversePool`
> (round-robin group). `ServiceClient.RecommendedDegreesOfParallelism` exposes Dataverse's `x-ms-dop-hint`, but the pool
> does not read it automatically yet; automatic DOP needs its own spec (ADR-0025, "Open questions"). Capturing `x-ms-*` headers on successful calls
> needs an HTTP-level observer outside `ServiceClient`; see `REVIEW-2026-09-30.md` Part 2.

Why 429/exception-based rather than proactively reading Dataverse's `x-ms-ratelimit-*` response
headers on every call: Headers are the theoretically better (leading, not lagging) signal, but
`ServiceClient` doesn't surface response headers for *successful* calls anywhere in its public API
— only on failure, via `HttpOperationException.Response`. See
[ADR-0008](docs/adr/0008-throttle-detection-429-not-headers.md) for the full reasoning and its
limits (this only reports throttling that a caller both hits *and* explicitly reports back — the
pool cannot infer it on its own).

**Circuit breaking: Real single-probe half-open.** Both strategies delegate open/half-open/closed
bookkeeping to a shared `MemberCircuitBreaker`. When a member's cooldown expires, only a *single*
concurrent caller wins the half-open "probe" slot — everyone else stays routed to other members
until that probe's outcome is observable, instead of every waiting caller piling onto the
just-recovering member at once. See [ADR-0010](docs/adr/0010-configurable-fail-fast-and-single-probe-half-open.md).

**What happens when every member is unavailable?** By default, `DataversePool` still picks a
member anyway (`AllUnavailableBehavior.FailOpen`, unchanged from earlier versions) — useful
when a resilience layer above you (Polly, your own retry) already handles the resulting
failure/throttle. If you'd rather get immediate backpressure instead of adding load to a pool you
already know is unavailable, opt into fail-fast:

```csharp
var pool = new DataversePool(members, strategy, AllUnavailableBehavior.FailFast);

try
{
    await using var lease = await pool.AcquireAsync();
    // ...
}
catch (DataversePoolUnavailableException ex)
{
    // ex.MemberNames - every member in the pool
    // ex.EarliestKnownRetryAt - earliest known throttle-window expiry across members, if any
}
```

See ADR-0010 for the full rationale, and the README's "production constraint" note above the
design-decisions list for what this does *not* solve (no budget coordination across multiple
processes/instances sharing the same service principals).

## Dependency injection (ASP.NET Core / generic host)

```csharp
services.AddDataverseUserPool("primary", connectionString, options =>
{
    options.MaxSize = 8;
    options.PrewarmCount = 2;
},
// Same retry knobs as DataverseUserPool's constructor - see the MaxRetryCount note above for why
// you may want the SDK to fail fast and let this pool own backoff instead.
clientOptions: new DataverseClientOptions { MaxRetryCount = 0 });
services.AddDataversePool("primary-pool", new[] { "primary" }); // single member today, add more names later

// resolve later:
var pool = provider.GetRequiredKeyedService<DataversePool>("primary-pool");
```

Registered pools warm up sequentially via one `IHostedService` per user pool, relying on the generic
host's sequential `StartAsync` — consistent with the "never clone in parallel" rule.


## Optional: Polly integration

`DataversePool.Core` has **no** dependency on Polly (see [ADR-0005](docs/adr/0005-polly-as-optional-adapter-not-core-dependency.md)).
If you want a failing/opening resilience pipeline to also mark the pooled resource unhealthy (so
it gets recycled instead of handed out again), add `DataversePool.Polly`:

```csharp
using ConnectionPool.Dataverse;
using ConnectionPool.Dataverse.Polly;
using Polly;

var pipeline = new ResiliencePipelineBuilder<WhoAmIResponse>()
    .AddRetryWithPoolHealthSignal(
        lease,
        new RetryStrategyOptions<WhoAmIResponse>
        {
            ShouldHandle = new PredicateBuilder<WhoAmIResponse>().Handle<Exception>(),
            MaxRetryAttempts = 3,
        },
        shouldMarkUnhealthy: exception =>
            !DataverseThrottleDetector.TryGetRetryAfter(exception, out _))
    .Build();

var response = await pipeline.ExecuteAsync(async _ => (WhoAmIResponse)lease.Resource.Execute(new WhoAmIRequest()));
```

Any `OnRetry`/`OnOpened` callback you already had on `RetryStrategyOptions`/`CircuitBreakerStrategyOptions`
keeps firing — `AddRetryWithPoolHealthSignal`/`AddCircuitBreakerWithPoolHealthSignal` only adds the
`lease.MarkUnhealthy(...)` call, it doesn't replace your callback. By default every exception marks
the resource unhealthy; the optional `shouldMarkUnhealthy` predicate lets Dataverse callers exclude
recognized throttles, which affect the application user's request budget rather than the connection's
health. The detector currently recognizes HTTP 429s; SOAP-fault detection remains pending confirmation.

## Optional: Metrics (OpenTelemetry-compatible)

`DataversePool.Core` has no dependency on any metrics library. If you want pool health published as
standard `System.Diagnostics.Metrics` instruments — consumable by any OpenTelemetry exporter
(Prometheus, OTLP, Azure Monitor, etc.) — add `DataversePool.Metrics`:

```csharp
using ConnectionPool.Metrics;

using var metrics = pool.AddMetrics("my-pool"); // Pool: a ResourcePool<T>
// or, for DataverseUserPool/DataversePool (no direct ResourcePool<T> access):
using var metrics = new PoolMetrics("my-pool", pool.GetStats);
```

This publishes every `PoolStats` field (`CreatedCount`, `IdleCount`, `LeasedCount`,
`UnhealthyOrRecyclingCount`, `WaitingCount`, `MaxSize`, `ConsecutiveCreateFailures`,
`ConsecutiveOperationalFailures`, `DetectedLeakCount`) as an **observable gauge** on a `Meter` named
`"DataversePool"` (overridable), tagged with `pool.name`. Gauges, not counters, because `PoolStats` is
a pull-based snapshot — the gauge callback only runs when a listener/exporter actually collects, so
this adds no background polling thread. Wire an exporter to see it, e.g.:

```csharp
services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("DataversePool").AddPrometheusExporter());
```

Scope is deliberately generic (the `ConnectionPool.Core` `PoolStats` fields only) — Dataverse-specific
signals like per-member circuit breaker state aren't covered yet. See
[ADR-0018](docs/adr/0018-metrics-adapter-observable-gauges.md).

### Operation latency, retries and throttles (built in)

Gauges show *how full* the pool is, not *how long calls take*. `ConnectionPool.Dataverse` itself
(no extra package) also publishes push-based histograms and counters on the same `"DataversePool"`
meter for every call through `PooledOrganizationService` and `ExecuteWithThrottleRetryAsync`. The
same `AddMeter("DataversePool")` line above picks them up. With no listener attached, they cost
one flag check per call.

| Instrument | Type | Tells you |
|---|---|---|
| `dataversepool.operation.acquire.duration` (s) | histogram | Time waiting for a lease — pool saturation or slow client creation |
| `dataversepool.operation.duration` (s) | histogram | Time inside `ServiceClient` per attempt, including the SDK's own internal retries |
| `dataversepool.operation.total.duration` (s) | histogram | End-to-end call latency across all attempts and `Retry-After` waits |
| `dataversepool.operation.active` / `.waiting` | up-down counter | Calls executing now / queued for a lease now |
| `dataversepool.operation.attempts` / `.calls` | counter | Attempts vs caller-visible calls |
| `dataversepool.operation.retries` | counter | Retries scheduled after a recognized throttle |
| `dataversepool.operation.retry_after` (s) | histogram | Capped `Retry-After` per recognized throttle |

Tags: `pool.name`, `pool.member.name`, `dataverse.operation.name`, `outcome`
(`success`/`error`/`throttled`/`canceled`) and `error.type` (exception type name only — never
messages, IDs or URLs). The facade uses fixed operation names (`create`, `retrieve`,
`retrieve_multiple`, `execute`, …). For the retry helper, pass a fixed low-cardinality name:

```csharp
await pool.ExecuteWithThrottleRetryAsync("import_accounts", (client, ct) => client.CreateAsync(entity, ct));
```

`pool.name` defaults to `"default"`. Set it with `new DataverseOperationMetricsOptions { PoolName = "orders" }`
on the `DataversePool`/`PooledOrganizationService` constructor. `AddDataversePool` uses its pool name
automatically.

Two limits to know:
- Calls made directly on `lease.Resource` are **not** measured. The library can't see inside your
  own `ServiceClient` usage, so instrument it yourself if you need it.
- A long `operation.duration` with few `retries` usually means the SDK is absorbing 429s internally
  before this library ever sees them. Lower `DataverseClientOptions.MaxRetryCount` if you want those
  stalls bounded and visible here.

See [ADR-0024](docs/adr/0024-operation-level-metrics-on-instrumented-execution-paths.md).

## Optional: Drop-in `IOrganizationServiceAsync` facade

If your codebase already has code built around a constructor-injected `IOrganizationServiceAsync`/
`IOrganizationServiceAsync2` — the standard way to consume this SDK — you don't have to rewrite every
call site to an explicit acquire-lease/use/dispose pattern to adopt pooling. `PooledOrganizationService`
implements that interface directly on top of a pool: Each call acquires a lease, runs the SDK call,
and releases the lease before returning.

```csharp
// Before: Constructor-injected IOrganizationServiceAsync2, unchanged.
public class ExistenceChecker
{
    private readonly IOrganizationServiceAsync2 _service;
    public ExistenceChecker(IOrganizationServiceAsync2 service) => _service = service;

    public Task<EntityCollection> FindAsync(QueryBase query, CancellationToken ct) =>
        _service.RetrieveMultipleAsync(query, ct);
}

// After: Only the DI registration changes.
services.AddDataverseUserPool("primary", connectionString);
services.AddDataversePool("primary-pool", new[] { "primary" });
services.AddSingleton<IOrganizationServiceAsync2>(sp =>
    new PooledOrganizationService(sp.GetRequiredKeyedService<DataversePool>("primary-pool")));
services.AddSingleton<ExistenceChecker>();
```

Exceptions from the underlying `ServiceClient` call propagate unchanged through the facade. It does
report a recognized Dataverse throttling signal (HTTP 429) back to whichever member served the
failing call, so a multi-member `DataversePool` used only through this facade still steers future
acquires away from a member Dataverse just throttled — but it does not retry the current call. Use
[`DataversePool.ExecuteWithThrottleRetryAsync`](#scaling-to-multiple-application-users)
directly if you need the current call retried too, working against `DataverseLease` instead of the
plain interface. See [ADR-0020](docs/adr/0020-pooled-organizationservice-facade.md).

> **Cancellation caveat:** a `CancellationToken` passed to `PooledOrganizationService.RetrieveMultipleAsync`
> (or any read call) only prevents a *new* call from starting — it cannot abort a `RetrieveMultiple`
> already in flight. This SDK never routes `retrievemultiple` through the WebAPI/HTTP path (see the
> `UseWebApi` note above), and the legacy WCF/SOAP path it always uses instead does not accept a
> `CancellationToken` mid-call. If you rely on cancellation-based timeouts around read-heavy
> workloads, budget for the in-flight call to still complete (or fail on its own) after your token
> fires.

### Constructing the base client without a connection string

`DataverseServiceClientPolicy`/`DataverseUserPool` also accept a
`Func<CancellationToken, Task<ServiceClient>>` base-client factory instead of a connection string,
for authentication that doesn't fit `AuthType=ClientSecret;Url=...;ClientId=...;ClientSecret=...;` —
for example, an MSAL confidential-client flow or any other custom token-provider callback passed to
`new ServiceClient(instanceUri, tokenProviderFunction, ...)`. The factory is invoked at most once
(serialized the same way as the connection-string path); every pooled slot is still produced by
cloning the resulting base client, never by invoking the factory again.

```csharp
var pool = new DataverseUserPool("primary", async ct =>
{
    var token = await myTokenProvider.GetTokenAsync(ct);
    return new ServiceClient(instanceUri, _ => Task.FromResult(token), useUniqueInstance: true);
});
```

## Sample project

See [`samples/DataversePool.Sample`](samples/DataversePool.Sample) for a runnable console app demonstrating
single-user pooling, group pooling, and (optionally, if you provide real credentials) an actual
live connection smoke test against a Dataverse environment. Run with:

```bash
export DATAVERSEPOOL_SAMPLE_CONNECTION_STRING="AuthType=ClientSecret;Url=https://yourorg.crm.dynamics.com;ClientId=...;ClientSecret=...;"
dotnet run --project samples/DataversePool.Sample
```

Without that environment variable set, the sample runs its pool-mechanics demo against an in-memory
fake resource only (no network) and explains what it would additionally do with real credentials.

## Design decisions

Every non-obvious choice is written up as an ADR in [`docs/adr/`](docs/adr/):

1. [Policy interface decouples Core from Dataverse](docs/adr/0001-pool-core-domain-agnostic-via-policy.md)
2. [Serial creation gate — never clone in parallel](docs/adr/0002-serial-creation-gate-no-parallel-cloning.md)
3. [Lease isolation is a dispose contract, not runtime-enforced](docs/adr/0003-lease-isolation-contract-not-enforced-runtime.md)
4. [No synchronous checkout validation — signal-based health instead](docs/adr/0004-no-checkout-validation-lazy-health-signal-instead.md)
5. [Polly as an optional adapter](docs/adr/0005-polly-as-optional-adapter-not-core-dependency.md)
6. [Dual pooling model: Single-user + round-robin group](docs/adr/0006-dual-pooling-model-single-user-and-round-robin-group.md)
7. [Hardening: Races, timeouts, dead group members](docs/adr/0007-race-conditions-timeouts-and-failure-scenarios.md)
8. [Throttle detection: 429/exception, not proactive headers](docs/adr/0008-throttle-detection-429-not-headers.md)
9. [Return-scrubbing hook (CallerId leak fix) + documented single-process constraint](docs/adr/0009-return-scrubbing-hook-caller-id-leak.md)
10. [Configurable fail-fast (not just fail-open) + real single-probe half-open circuit breaker](docs/adr/0010-configurable-fail-fast-and-single-probe-half-open.md)
11. [Outcome-based probe completion + finalizer-thread safety](docs/adr/0011-outcome-reporting-and-finalizer-thread-safety.md)
12. [Bounded acquire (timeout), operational-failure-aware circuit breaker, log-only leak-detection](docs/adr/0012-log-only-leak-detection-and-bounded-acquire.md)
13. [End-to-end AcquireTimeout, fair operational-failure counting, idempotent warmup, correct probe-outcome reporting, durable leak visibility](docs/adr/0013-acquire-timeout-end-to-end-and-review-round-four-fixes.md)
14. [Probe-claim generation correlation, and correctly distinguishing AcquireTimeout cancellation from a real CreateTimeout](docs/adr/0014-probe-claim-generation-and-cancellation-vs-createtimeout-misclassification.md)
15. [Complexity review — pause "fix everything" review cycles, split ResourcePool.cs](docs/adr/0015-complexity-review-file-split-no-behavior-change.md)
16. [Affinity cookie forced off in code; retry/throttle knobs (MaxRetryCount, RetryPauseTime, UseExponentialRetryDelayForConcurrencyThrottle) exposed as optional overrides](docs/adr/0016-affinity-cookie-forced-off-retry-knobs-exposed.md)
17. [Group-level throttle retry helper (ExecuteWithThrottleRetryAsync) + capped Retry-After](docs/adr/0017-group-throttle-retry-helper-and-capped-retry-after.md)
18. [Optional metrics adapter using System.Diagnostics.Metrics observable gauges](docs/adr/0018-metrics-adapter-observable-gauges.md)
19. [Unify single-user and multi-user pools as DataversePool; wait-when-no-alternative throttle retry](docs/adr/0019-unify-single-and-multi-user-pools-as-dataversepool.md)
20. [`PooledOrganizationService` - an `IOrganizationServiceAsync2` facade over the pool](docs/adr/0020-pooled-organizationservice-facade.md)
21. [Base-client factory constructor for `DataverseServiceClientPolicy`/`DataverseUserPool`](docs/adr/0021-base-client-factory-constructor.md)
22. [Shutdown disposal race, throttle-retry lease leak, probe-claim leak fixes](docs/adr/0022-shutdown-and-probe-claim-leak-fixes.md)
23. [Corrected premise: A `ServiceClient` does not serialize concurrent async requests](docs/adr/0023-serviceclient-async-concurrency-corrected-premise.md)
24. [Operation-level metrics on instrumented execution paths](docs/adr/0024-operation-level-metrics-on-instrumented-execution-paths.md)
25. [Runtime-adjustable pool size, as the first step toward automatic DOP](docs/adr/0025-runtime-adjustable-pool-size.md)

## Status / open items

See [`TODO.md`](TODO.md) — in particular, the "Open questions" section lists claims that are
believed true (e.g. socket exhaustion on new-per-request usage, `CallerId` cross-thread races) but
have **not** been directly verified by this project's own tests.

> ⚠️ **Production constraint: Single process per service-principal set.** All pool, throttle, and
> circuit-breaker state lives in-process memory only — it is **not** coordinated across multiple
> instances of your application (e.g. multiple Kubernetes pods) sharing the same
> `DataversePool` service principals. Running more than one instance against the same
> principal set means each instance independently thinks it has the full Dataverse
> service-protection budget available, a 429 seen by one instance won't stop another from
> continuing to spend the same shared budget, and the "fail open when everything is
> throttled/circuit-open" behavior (deliberate, see ADR-0007/0008, to avoid deadlocking a single
> process) can amplify a tenant-wide outage across instances instead of applying backpressure. See
> [ADR-0009](docs/adr/0009-return-scrubbing-hook-caller-id-leak.md) for the full analysis and the
> prioritized backlog for a future coordinated/distributed mode. Until that exists, either run one
> instance per service-principal set, or accept and plan around this limitation explicitly.

## Author

Niels Teglsbo ([niels@teglsbo.dk](mailto:niels@teglsbo.dk))

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md). Please read the relevant ADR before proposing changes to
pool creation/isolation/health-check timing — several behaviors here look like they could be
"simplified" but are deliberate tradeoffs based on measured `ServiceClient` clone behavior.

## License

[MIT](LICENSE)
