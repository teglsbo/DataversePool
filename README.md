# DataversePool

Connection pooling for [`Microsoft.PowerPlatform.Dataverse.Client.ServiceClient`](https://learn.microsoft.com/power-platform/developer/data-platform/xrm-tooling/use-dataverse-service-client) —
because a `ServiceClient` only handles **one request at a time**, and cloning a new one is
expensive enough (hundreds of ms to seconds, see [ADR-0002](docs/adr/0002-serial-creation-gate.md))
that doing it per-request/per-thread is a real bottleneck and, if done in parallel, actively
counter-productive due to internal lock contention.

DataversePool gives you a small, generic async resource pool (`DataversePool.Core`) plus a Dataverse-specific
adapter (`DataversePool.Dataverse`) and an optional Polly v8 integration (`DataversePool.Polly`) — so you check
out a ready-to-use `ServiceClient`, use it, and return/dispose it, instead of managing
construction/cloning/health yourself.

> Status: **pre-1.0 / preview**. Core design is implemented and tested (see [`TODO.md`](TODO.md)
> for exact scope and open items). API may still shift before a 1.0 release.
>
> Sister project: DataverseDuck (`dvduck`) — a separate tool, not a
> dependency of this library.

## Why not just `new ServiceClient(...)` per request?

- **Serialized/expensive construction.** A `ServiceClient` clone can take from ~1ms (warm,
  sequential) up to 1–3.2s (cold, or under construction-time lock contention) — see
  [ADR-0002](docs/adr/0002-serial-creation-gate-no-parallel-cloning.md). DataversePool serializes all
  creation through a single gate so you get the fast path, not the contention path.
- **Per-user Dataverse service-protection limits (~52 concurrent requests/user).** Round-robin
  pooling across multiple application users is the standard way to scale beyond one user's budget
  — see [`DataversePool.Dataverse`'s group pool](#quickstart-group-pool-multiple-application-users).
- **A dead pool member shouldn't take down the group.** The default group-pool strategy is
  health-aware: it circuit-opens a consistently-failing member, retries it after a cooldown, and
  fails open (keeps serving) rather than locking the whole pool out — see
  [ADR-0007](docs/adr/0007-race-conditions-timeouts-and-failure-scenarios.md).

## Prior art / how this compares

A few existing projects address parts of the same problem, but not the full scope of this library:

- **[PooledServiceClientFactory](https://github.com/zhufamily/PooledServiceClientFactory)** — an
  existing open-source `ServiceClient` pool: configurable capacity, auto-scale-down, and avoids the
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
| `DataversePool.Dataverse` | `ServiceClient` policy + single-user pool + round-robin group pool. | `DataversePool.Core`, `Microsoft.PowerPlatform.Dataverse.Client` |
| `DataversePool.Polly` | Wires Polly v8 retry/circuit-breaker outcomes to a lease's health signal. | `DataversePool.Core`, `Polly.Core` (optional — not required by the other two packages) |

## Quickstart: single user

```csharp
using ConnectionPool.Core;
using ConnectionPool.Dataverse;

var pool = new DataverseUserPool(
    name: "primary",
    connectionString: "AuthType=ClientSecret;Url=...;ClientId=...;ClientSecret=...;",
    options: new PoolOptions { MaxSize = 8, PrewarmCount = 2 });

await pool.WarmupAsync(); // sequential, see ADR-0002 — do this once at startup

await using (var lease = await pool.AcquireAsync())
{
    var who = lease.Resource.Execute(new WhoAmIRequest());
    // lease.Resource is a Microsoft.PowerPlatform.Dataverse.Client.ServiceClient
}
// disposing the lease returns the ServiceClient to the pool (or recycles it, if unhealthy)
```

> **`EnableAffinityCookie` is forced to `false` automatically.** Dataverse's server affinity cookie
> (on by default) pins all requests from one `ServiceClient` to a single backend node - good for a
> single interactive session, but counter-productive here: a pool exists specifically to spread
> concurrent requests out, and pinning every pooled resource's traffic to one node just recreates a
> single-node bottleneck server-side. `DataverseServiceClientPolicy` sets this to `false` in code on
> every client it creates (base and clones), regardless of what your connection string says, so you
> don't need to remember to add it yourself. See
> [Microsoft's docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.powerplatform.dataverse.client.serviceclient.enableaffinitycookie)
> for details.

> **`MaxRetryCount`/`RetryPauseTime` are optional overrides, not forced.** The SDK's own defaults
> (10 retries, 5s pause) mean a single call hitting a transient error can silently block a leased
> client for up to ~50 seconds before an exception ever reaches this pool's throttle detection or a
> circuit breaker built on top of it. Unlike the affinity cookie, there's no single correct value
> here - it depends on your own timeout budget - so pass a `DataverseClientOptions` to
> `DataverseUserPool`'s constructor to override either setting; leave it `null` (default) to keep the
> SDK's defaults. See [ADR-0016](docs/adr/0016-affinity-cookie-forced-off-retry-knobs-exposed.md).

```csharp
var pool = new DataverseUserPool(
    "sample-user",
    connectionString,
    clientOptions: new DataverseClientOptions { MaxRetryCount = 2, RetryPauseTime = TimeSpan.FromSeconds(1) });
```

## Quickstart: group pool (multiple application users)

Use this when one application (service principal) user's ~52-concurrent-request budget isn't
enough — register several application users and round-robin across them:

```csharp
using ConnectionPool.Core;
using ConnectionPool.Dataverse;

var options = new PoolOptions { MaxSize = 8, PrewarmCount = 2 };
var group = new DataverseGroupPool(new[]
{
    new DataverseUserPool("app-user-1", connectionStringUser1, options),
    new DataverseUserPool("app-user-2", connectionStringUser2, options),
    new DataverseUserPool("app-user-3", connectionStringUser3, options),
});

await group.WarmupAsync(); // warms up each member sequentially

await using var lease = await group.AcquireAsync();
// selection uses HealthAwareRoundRobinSlotSelectionStrategy by default:
// a member that keeps failing gets circuit-opened, retried after a cooldown,
// and the whole group fails open (rather than deadlocking) if all members are down.
```

**Round-robin vs. load-aware selection.** The default `HealthAwareRoundRobinSlotSelectionStrategy`
distributes evenly and skips dead members, but doesn't look at how busy each member currently is.
If call durations vary a lot (some members can end up stuck on long-running requests), pass
`LeastConnectionsSlotSelectionStrategy` instead — it picks whichever member currently has the
fewest leased connections (`PoolStats.LeasedCount`), with the same dead-member circuit-breaking:

```csharp
var group = new DataverseGroupPool(members, new LeastConnectionsSlotSelectionStrategy());
```

**Throttle-aware routing.** Both strategies also skip a member that's currently marked as
Dataverse-throttled. `DataverseGroupPool.AcquireAsync()` returns a `DataverseGroupLease` (not a
plain lease) specifically so you can report a 429 back to the member that actually served the
request:

```csharp
await using var lease = await group.AcquireAsync();
try
{
    var response = (WhoAmIResponse)lease.Resource.Execute(new WhoAmIRequest());
}
catch (Exception ex) when (lease.ReportIfThrottled(ex))
{
    // Dataverse returned HTTP 429; DataverseThrottleDetector parsed Retry-After from the exception
    // and ReportIfThrottled recorded it on lease.Member. The group's selection strategy will steer
    // new acquires to other members until that window expires. lease.Member is still not "unhealthy"
    // - the connection itself is fine, just decide here whether to retry, rethrow, etc.
    throw;
}
```

Why 429/exception-based rather than proactively reading Dataverse's `x-ms-ratelimit-*` response
headers on every call: headers are the theoretically better (leading, not lagging) signal, but
`ServiceClient` doesn't surface response headers for *successful* calls anywhere in its public API
— only on failure, via `HttpOperationException.Response`. See
[ADR-0008](docs/adr/0008-throttle-detection-429-not-headers.md) for the full reasoning and its
limits (this only reports throttling that a caller both hits *and* explicitly reports back — the
pool cannot infer it on its own).

**Circuit breaking: real single-probe half-open.** Both strategies delegate open/half-open/closed
bookkeeping to a shared `MemberCircuitBreaker`. When a member's cooldown expires, only a *single*
concurrent caller wins the half-open "probe" slot — everyone else stays routed to other members
until that probe's outcome is observable, instead of every waiting caller piling onto the
just-recovering member at once. See [ADR-0010](docs/adr/0010-configurable-fail-fast-and-single-probe-half-open.md).

**What happens when every member is unavailable?** By default, `DataverseGroupPool` still picks a
member anyway (`GroupAllUnavailableBehavior.FailOpen`, unchanged from earlier versions) — useful
when a resilience layer above you (Polly, your own retry) already handles the resulting
failure/throttle. If you'd rather get immediate backpressure instead of adding load to a group you
already know is unavailable, opt into fail-fast:

```csharp
var group = new DataverseGroupPool(members, strategy, GroupAllUnavailableBehavior.FailFast);

try
{
    await using var lease = await group.AcquireAsync();
    // ...
}
catch (DataverseGroupUnavailableException ex)
{
    // ex.MemberNames - every member in the group
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
});

// resolve later:
var pool = provider.GetRequiredKeyedService<DataverseUserPool>("primary");
```

Registered pools warm up sequentially via one `IHostedService` per pool, relying on the generic
host's sequential `StartAsync` — consistent with the "never clone in parallel" rule.

## Optional: Polly integration

`DataversePool.Core` has **no** dependency on Polly (see [ADR-0005](docs/adr/0005-polly-as-optional-adapter-not-core-dependency.md)).
If you want a failing/opening resilience pipeline to also mark the pooled resource unhealthy (so
it gets recycled instead of handed out again), add `DataversePool.Polly`:

```csharp
using ConnectionPool.Dataverse.Polly;
using Polly;

var pipeline = new ResiliencePipelineBuilder<WhoAmIResponse>()
    .AddRetryWithPoolHealthSignal(lease, new RetryStrategyOptions<WhoAmIResponse>
    {
        ShouldHandle = new PredicateBuilder<WhoAmIResponse>().Handle<Exception>(),
        MaxRetryAttempts = 3,
    })
    .Build();

var response = await pipeline.ExecuteAsync(async _ => (WhoAmIResponse)lease.Resource.Execute(new WhoAmIRequest()));
```

Any `OnRetry`/`OnOpened` callback you already had on `RetryStrategyOptions`/`CircuitBreakerStrategyOptions`
keeps firing — `AddRetryWithPoolHealthSignal`/`AddCircuitBreakerWithPoolHealthSignal` only adds the
`lease.MarkUnhealthy(...)` call, it doesn't replace your callback.

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
6. [Dual pooling model: single-user + round-robin group](docs/adr/0006-dual-pooling-model-single-user-and-round-robin-group.md)
7. [Hardening: races, timeouts, dead group members](docs/adr/0007-race-conditions-timeouts-and-failure-scenarios.md)
8. [Throttle detection: 429/exception, not proactive headers](docs/adr/0008-throttle-detection-429-not-headers.md)
9. [Return-scrubbing hook (CallerId leak fix) + documented single-process constraint](docs/adr/0009-return-scrubbing-hook-caller-id-leak.md)
10. [Configurable fail-fast (not just fail-open) + real single-probe half-open circuit breaker](docs/adr/0010-configurable-fail-fast-and-single-probe-half-open.md)
11. [Outcome-based probe completion + finalizer-thread safety](docs/adr/0011-outcome-reporting-and-finalizer-thread-safety.md)
12. [Bounded acquire (timeout), operational-failure-aware circuit breaker, log-only leak-detection](docs/adr/0012-log-only-leak-detection-and-bounded-acquire.md)
13. [End-to-end AcquireTimeout, fair operational-failure counting, idempotent warmup, correct probe-outcome reporting, durable leak visibility](docs/adr/0013-acquire-timeout-end-to-end-and-review-round-four-fixes.md)
14. [Probe-claim generation correlation, and correctly distinguishing AcquireTimeout cancellation from a real CreateTimeout](docs/adr/0014-probe-claim-generation-and-cancellation-vs-createtimeout-misclassification.md)
15. [Complexity review — pause "fix everything" review cycles, split ResourcePool.cs](docs/adr/0015-complexity-review-file-split-no-behavior-change.md)
16. [Affinity cookie forced off in code; MaxRetryCount/RetryPauseTime exposed as optional overrides](docs/adr/0016-affinity-cookie-forced-off-retry-knobs-exposed.md)

## Status / open items

See [`TODO.md`](TODO.md) — in particular, the "Open questions" section lists claims that are
believed true (e.g. socket exhaustion on new-per-request usage, `CallerId` cross-thread races) but
have **not** been directly verified by this project's own tests.

> ⚠️ **Production constraint: single process per service-principal set.** All pool, throttle, and
> circuit-breaker state lives in-process memory only — it is **not** coordinated across multiple
> instances of your application (e.g. multiple Kubernetes pods) sharing the same
> `DataverseGroupPool` service principals. Running more than one instance against the same
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
