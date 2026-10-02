# Adaptive concurrency / automatic DOP control for DataversePool

> Status: research only. Nothing in this document is implemented. It is the research artifact
> requested as the prerequisite for the "automatic-DOP spec" named in
> [`docs/adr/0025-runtime-adjustable-pool-size.md`](../adr/0025-runtime-adjustable-pool-size.md#open-questions-for-the-automatic-dop-spec).
> Every claim below is tagged **[MS]** (documented by Microsoft), **[Measured]** (observed in this
> repo's live tests against a real tenant), **[Lit]** (external literature/prior art), or
> **[Inferred]** (reasoning/derivation by the author of this document, not independently confirmed
> against a live tenant). Anything tagged **[Inferred]** that materially affects safety is also
> listed again in §12 ("Live experiments needed") and §13 ("Open questions / risks").

## 1. Summary

- Dataverse's service-protection system enforces three *independent* limits per
  (application user × web server): **request count** (default 6,000 / 300 s), **execution time**
  (default 1,200,000 ms / 300 s), and **concurrent requests** (default 52, "or higher")
  **[MS]** ([Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits)).
  They have different dynamics, different error codes, and — this is the most important
  conclusion of this document — **different correct control reactions**. A controller that only
  reduces concurrency in response to any 429 conflates three different problems (§2).
- `x-ms-dop-hint` / `ServiceClient.RecommendedDegreesOfParallelism` is a genuine, already-reachable
  **[MS]**/**[Measured-reachable]** server-supplied signal, but it is explicitly documented by
  Microsoft as varying with environment resources, not as a hard ceiling, and this repo's own live
  tests show it can be far more conservative than the concurrency the server actually accepts: one
  pinned-server experiment admitted **100** concurrent requests from one user before the first 429
  (`0x80072326`, "limit of 100"), and an insert workload hit a **40**-concurrent ceiling on a
  different (write) code path — both far from, and on opposite sides of, the commonly cited 52
  **[Measured]** (`TODO.md:183-196`, `TODO.md:230-231`). DOP hint should be used as an **initial
  value / soft floor**, not an authoritative ceiling (§3, §9).
- With `EnableAffinityCookie` forced off (`docs/adr/0016`), requests from one application user are
  spread across however many web servers the environment has, and Microsoft documents the limits
  as enforced **per web server**, independently
  **[MS]**. The *effective* aggregate concurrency budget for one user is therefore
  `S × per-server-limit`, but only approximately and only in expectation — the load-balancer
  assignment of individual requests to servers is, from the client's point of view, a random
  process, so a single server can be hit with more than its fair share purely by chance even when
  total in-flight requests are comfortably under `S × limit` (classic balls-into-bins variance,
  §3.3) **[Inferred]**. `S` (the server count) is not documented as directly observable by
  Microsoft **[MS absence]**, so it can only be estimated indirectly (§12).
- Latency/duration is a useful *secondary* signal but needs normalization per operation type
  (read vs. create vs. bulk vs. metadata) because Dataverse operation costs are wildly
  heterogeneous, and because the **execution-time** limit creates a direct, derivable relationship
  between sustained concurrency and duration: if workers keep the pipe saturated with
  back-to-back calls, the execution-time budget caps *sustained* average concurrency at
  `1200 s / 300 s = 4` "concurrent-seconds per wall-clock-second" **[Inferred from MS numbers]** —
  far below the concurrent-request limit's 52+, which is the derivation behind the maintainer's
  hypothesis #2. This bound is a worst case that assumes server execution time ≈ observed request
  duration; if a large share of observed latency is network/queueing rather than server execution,
  the true bound is looser (§2.2, §12).
- A layered controller — a fast, loss-based (429-driven) concurrency limiter per member, informed
  by latency as a secondary/anticipatory signal, with DOP hint as the initial value and a
  separate, slow, budget-aware layer for the two sliding-window limits — is recommended over a
  single generic AIMD loop reacting to "any 429" (§8, §9). The controller itself should be a
  **pluggable strategy** (`IPoolSizingStrategy`), mirroring the library's existing
  `ISlotSelectionStrategy`/`IPooledResourcePolicy<T>` pattern, with `Fixed` (today's behavior) as
  the default and `DopHint`, `Aimd`, `Gradient`, and `Composite` as additional, independently
  selectable/combinable implementations (§9, §10).
- DataversePool already has the mechanical primitives this needs: runtime-adjustable `MaxSize`
  with graceful (non-instant) shrink (`docs/adr/0025`), per-member throttle state
  (`docs/adr/0008`), circuit breakers, and OpenTelemetry-shaped operation metrics
  (`docs/adr/0024`). What is missing is (a) the SOAP-path 429-detection gap flagged in
  `REVIEW-2026-09-30.md:201-223`, (b) a `RecommendedDegreesOfParallelism` gauge, and (c) the
  controller itself — all open items, not yet built.
- Multi-replica deployments cannot coordinate through Dataverse's limits directly (no shared
  state is exposed), but AIMD-family controllers have proven distributed fairness/convergence
  properties without coordination (Chiu & Jain 1989, §8, §11) that make a coordination-free design
  plausible as a first step, with shared external state (cache/DB) as a later enhancement.
- Dataverse's own backend is itself elastic — Microsoft documents that the number and
  capabilities of servers allocated to an environment "might vary over time" **[MS]**, though
  without a documented trigger, timescale, or observability mechanism **[MS absence]** — which
  makes rising latency structurally ambiguous between "back off, we're overloading fixed
  capacity" and "push through, sustained demand is what triggers more capacity." A controller
  that only reacts to latency risks either fighting the platform's own scaling or making its own
  under-provisioning self-fulfilling; 429s, not latency, must remain the authoritative signal for
  any *decrease*, with latency only gating further *increase*, and a bounded, BBR-`ProbeBW`-style
  periodic probe above the last-known ceiling is recommended so the controller keeps rediscovering
  capacity changes in either direction rather than assuming any learned ceiling is permanent (§4).
- **Comprehensive signal collection must precede any controller, not accompany it.** The hardest
  and most valuable missing signal is per-frontend/web-server identity — needed to test the §3.3
  hypothesis directly — and the only candidate for it, the `ARRAffinity` routing cookie, is
  reported **[Inferred — `REVIEW-2026-09-30.md`; needs a live check, §12]** to be sent on every
  response even with `EnableAffinityCookie` forced off, but is reachable only via an optional, SDK-agnostic `DiagnosticListener`-based
  response-capture mechanism, not through any public `ServiceClient` member (§6). The recommended
  rollout therefore starts with an explicit "observe only" phase — telemetry and (optionally) this
  capture mechanism, no control logic at all — before any `IPoolSizingStrategy` other than `Fixed`
  is considered for production use (§9.5).
- **A "hanging" call is often SDK-internal retry time, not server time, and the two are
  conflated by default.** With SDK defaults (`MaxRetryCount=10`, `RetryPauseTime=5s`), one
  `Execute` call can silently block up to ~50 s inside the SDK before anything is visible to the
  pool **[Measured, `docs/adr/0016`]**, and `MaxRetryCount` governs both transient-error *and*
  429 retries, so a default-configured call's `Duration` is an opaque mix of real server time,
  retry sleeps, and (occasionally) MSAL token-refresh cost. Setting `MaxRetryCount=0` and moving
  retries into the pool, combined with per-attempt response-capture timing (§6.4), is a
  precondition for trusting latency as a controller signal at all (§7) — and an explicit
  per-attempt and total-operation timeout (§10.8) should be set regardless of whether any
  controller is ever adopted.

## 2. Dataverse limits model: per user × web server, three independent facets

**[MS]** Microsoft documents service protection limits as evaluated **per authenticated user**,
with **"each web server that your environment makes available enforces these limits
independently"** — i.e., the same user can be concurrently active on multiple web servers, and
each server tracks its own window for that user
([api-limits §"How the system enforces..."](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits)).
Two of the three limits (request count, execution time) use a 5-minute (300 s) **sliding** window;
the third (concurrency) is evaluated instantaneously. The default values, per web server, are:

| Facet | Default limit | Window | SDK error code | Hex / Web API |
|---|---|---|---|---|
| Number of requests | 6,000 | 300 s sliding | `-2147015902` | `0x80072322` |
| Execution time | 1,200,000 ms (20 min) | 300 s sliding | `-2147015903` | `0x80072321` |
| Concurrent requests | 52 ("or higher") | instantaneous | `-2147015898` | `0x80072326` |

All three rows above are **[MS]**, verified against the exact error-message text on
[Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits):
`"Number of requests exceeded the limit of 6000 over time window of 300 seconds."`;
`"Combined execution time of incoming requests exceeded limit of 1,200,000 milliseconds over time
window of 300 seconds. Decrease number of concurrent requests or reduce the duration of requests
and try again later."`; `"Number of concurrent requests exceeded the limit of 52."` Microsoft
explicitly flags these as **defaults that can be configured higher per environment** ("This
important: these limits can change and might vary between different environments"), consistent
with this repo's measured 100/40 values on its own tenant (§1, §12).

Three Web API/SDK response headers exist alongside the 429 body: `Retry-After` (seconds, or an
HTTP date) on every 429
**[MS]**, and, only for the raw Web API (not surfaced through `ServiceClient` on success —
`docs/adr/0008`), `x-ms-ratelimit-burst-remaining-xrm-requests` and
`x-ms-ratelimit-time-remaining-xrm-requests`, which Microsoft explicitly says are
**"intended for debugging purposes"**, not for controlling send rate
**[MS]** ([api-limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits)).

Two further boundary facts, both **[MS]**, matter for a controller:

- **Plug-ins/custom workflow activities do not themselves count against the limits** (they run in
  the isolated sandbox, off the public API endpoints) — but the **execution time they consume is
  charged to the triggering request**. A client-visible create/update can therefore carry an
  execution-time cost much larger than its own apparent work, invisible to the client except as
  elevated latency.
- **Entitlement limits (API request limits per 24 hours, by licensing)** are a *completely
  separate* system from service protection limits, evaluated independently and not bypassed by
  batching
  ([API limits overview](https://learn.microsoft.com/en-us/power-apps/maker/data-platform/api-limits-overview#entitlement-limits)).
  A 429/throttle-shaped failure that doesn't match one of the three error codes above, or that
  persists across a much longer horizon than 5 minutes, is a signal to check entitlement
  consumption, not to tune the concurrency controller. Other non-Dataverse 429/503 sources —
  Azure Front Door/infra throttling, SQL timeouts surfaced as other fault codes, or
  provider-level connection-reset instability — should also be excluded by checking the fault
  code/exception type before feeding a signal into the controller, which is the rationale behind
  treating the three reasons separately before generalizing (per the task brief).

### 2.1 Number of requests (`-2147015902` / `0x80072322`)

- **What it measures:** cumulative count of requests over the trailing 300 s, regardless of
  duration or concurrency **[MS]**.
- **Dynamics:** sliding window; Microsoft notes you can transiently exceed the window's instantaneous
  rate before the system "catches up" and starts rejecting — the enforcement is not a hard
  per-request token check at submission time but a trailing-sum evaluation **[MS]**.
- **Retry-After:** present; Microsoft explicitly says the duration grows the longer the offending
  pattern continues, "to minimize the impact on shared resources" **[MS]**.
- **Correct reaction:** this is a **rate** problem, not (only) a concurrency problem. A workload
  that sends many short requests sequentially at high rate but low concurrency can hit this limit
  while never approaching the concurrency limit. **Reducing concurrency alone does not fix it** —
  if each worker just resubmits immediately, aggregate rate stays the same; the correct actuator
  is a **token-bucket / rate limiter** (e.g., `System.Threading.RateLimiting`'s
  `TokenBucketRateLimiter` or Polly's rate-limiter strategy, both **[Lit]** — see §8) gating
  request *submission* rate, independent of how many are in flight. Batching into
  `ExecuteMultipleRequest` reduces request count but Microsoft explicitly warns this shifts
  pressure to the execution-time limit **[MS]**.
- **Right actuator:** rate limiter (requests/second budget), not `MaxSize`/concurrency permits.

### 2.2 Execution time (`-2147015903` / `0x80072321`)

- **What it measures:** the server-side *processing* time summed across all requests from the
  user in the trailing 300 s — not wall-clock latency observed by the client, though the two are
  correlated **[MS]**.
- **Dynamics:** sliding window, same 300 s. Explicitly coupled to concurrency by Microsoft's own
  error text: `"Decrease number of concurrent requests or reduce the duration of requests"`
  **[MS]** — this is the clearest first-party statement that concurrency × duration, not either
  alone, is what this limit tracks.
- **Derived steady-state bound [Inferred]:** if `c` workers are continuously busy (back-to-back
  calls, no idle gaps) for the full window, each worker contributes ~1 second of execution time
  per wall-clock second it is busy, so the rate of execution-time consumption is `c` seconds per
  second. Over the 300 s window the budget is 1,200 s, giving
  `c_max = 1200 / 300 = 4` sustained concurrent workers before this limit alone would start
  tripping — *if* server execution time tracks wall-clock request duration 1:1. This is
  dramatically lower than the 52-default (or 100/40 measured) concurrency limit, which is exactly
  why the maintainer's hypothesis (duration matters, and matters differently from raw concurrency)
  is directionally correct. **Caveat [Inferred, needs live verification]:** Dataverse's internal
  "execution time" metric is unlikely to equal full client-observed latency 1:1 — network
  transit, client-side queueing, and any server-side queueing-before-execution plausibly are not
  charged as "execution time." If a large fraction of observed duration is such overhead, the
  *true* sustainable `c_max` is higher than 4. This bound should therefore be read as "the right
  order of magnitude to worry about, not a verified hard ceiling" until measured (§12).
- **Correct reaction:** reduce the *cost* of work (smaller batches, cheaper queries, fewer
  plugins triggered) and/or reduce sustained concurrency on expensive operation types
  specifically; a generic per-member concurrency cut helps but may be insufficient if individual
  operations are simply slow/heavy (e.g. `RetrieveAllEntities`-class metadata calls, which this
  repo measured at a p95 near 20-55 s even without any rejection — `TODO.md:183-186`).
- **Right actuator:** a duration/cost-aware budget — e.g., weight each in-flight operation by an
  estimated or measured execution-time cost class, and cap the sum of in-flight weighted cost
  (a "leaky/token bucket on cost," not on raw request count) rather than capping the count of
  in-flight requests uniformly. This is a different actuator from both of the other two limits
  and is the least-supported by DataversePool's current primitives (`MaxSize` is a pure count).

### 2.3 Concurrent requests (`-2147015898` / `0x80072326`)

- **What it measures:** the number of requests from the same user simultaneously executing on one
  web server, checked continuously (not windowed) **[MS]**.
- **Dynamics:** instantaneous — Microsoft states this explicitly: *"exceeding the number of
  concurrent requests returns an error immediately"*, unlike the other two, which need the
  trailing-300 s sum to climb first **[MS]**.
- **Retry-After:** present, and in this repo's measurements scaled with how overloaded the pattern
  was — observed `Retry-After` of 6m03s–7m07s after a 128-worker burst against a 100-limit server
  **[Measured]** (`TODO.md:196-198`), i.e., materially longer than the 300 s window itself once
  the server decides the client is being persistently demanding, matching Microsoft's documented
  behavior that *"the duration is extended to minimize the impact... if the application continues
  to send such demanding requests"* **[MS]**.
- **Correct reaction:** this is the one limit where **immediately reducing in-flight concurrency**
  is unambiguously the right and sufficient reaction — shed load now, retry later, honor
  `Retry-After`.
- **Right actuator:** the pool's concurrency permits (`MaxSize`/`SetMaxSize`), which is exactly
  the primitive `docs/adr/0025` already built.

### 2.4 Only after separating the three: what's common

Given the above, a controller that unconditionally treats every 429 the same way is wrong in two
directions: it may cut concurrency hard in response to a request-count or execution-time 429 where
concurrency wasn't the actual problem (over-reacting on the wrong axis, potentially leaving
throughput on the table for no safety benefit), and it may under-react to an execution-time 429 by
only trimming a couple of permits when the real fix is reducing operation cost. What *is* common
and shared safely across all three: (a) **always honor `Retry-After` as a hard floor** on the next
attempt for that member regardless of which facet tripped; (b) **always record the specific error
code/outcome**, not a generic "throttled" boolean, so the three can be handled, metered, and
alerted on separately; (c) a **concurrency floor/ceiling + cooldown** mechanism (§9) is a
reasonable chassis for facet (2.3) and a reasonable *secondary* lever (reduce load, buying margin)
for (2.1)/(2.2), but must not be the *only* lever for those two.

## 3. What `x-ms-dop-hint` is, and why it is likely conservative

**[MS]** Per
[Send Parallel Requests to Dataverse](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests):
*"Dataverse manages resource allocation for environments... The number and capabilities of the
servers allocated might vary over time, so there's no fixed number for the optimum degree of
parallelism. Instead, use the integer value returned from the `x-ms-dop-hint` response header."*
It is explicitly a **recommendation for the environment** (capacity-tier-shaped), not a
per-request or per-operation-type adaptive signal, and Microsoft's own guidance for maximizing
throughput says to **ignore pre-computed numbers and let the server tell you via gradual ramp +
`Retry-After`** instead — i.e., even Microsoft doesn't present the DOP hint as sufficient on its
own for a throughput-maximizing client (`"Let the server tell you how much it can handle"`,
same page, "How to maximize throughput" section). `ServiceClient.RecommendedDegreesOfParallelism`
mirrors this header on both SOAP and Web API transports
**[MS]**/**[Measured]** — confirmed in this repo by both documentation
([property reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.powerplatform.dataverse.client.serviceclient.recommendeddegreesofparallelism))
and SDK-assembly string-table inspection (`REVIEW-2026-09-30.md:305-310`, `:458-459`), and is
**already reachable today** without any reflection into SDK internals — it is simply not yet
wired into a pool gauge (`docs/adr/0016` addendum; `REVIEW-2026-09-30.md:396-403`).

Why the hint is likely conservative relative to what a given tenant will actually tolerate:

1. It is pitched at **environment-level** resourcing, which must be safe for *any* client pattern
   against that environment, including ones affinity-pinned to one server and ones that are not.
   A conservative, one-size-fits-all number is the expected shape of such a hint.
2. This repo's own measurements put the *actual*, directly observed concurrent-request ceiling at
   **100** on one pinned server for a read-path query (vs. the commonly assumed 52) and at **40**
   for an insert/write path on the same tenant **[Measured]** (`TODO.md:192-198`, `:230-231`) —
   both environment- and operation-path-dependent, exactly as Microsoft's "limits... can change and
   might vary between different environments" disclaimer predicts **[MS]**, and neither number is
   obviously derivable from a single static DOP hint value.
3. With affinity off (always true in this library, `docs/adr/0016`), a single user's in-flight
   requests are spread across all of the environment's web servers, each of which separately
   enforces its own concurrency ceiling. The *effective* achievable aggregate concurrency for one
   user, summed across servers, is then **[Inferred]** approximately `S × per-server-limit` — which
   this repo's two-identity and two-server-class experiments are consistent with in spirit (two
   application users independently exceeding what one alone could sustain,
   `TODO.md:198-214`), though that specific experiment varied *user* not *server* count, so it is
   evidence for the "limits are independent per partition key" model in general, not direct proof
   of the per-server multiplier specifically (§12 lists the missing direct experiment).

### 3.3 Affinity-off, multi-server concurrency as a stochastic process

**[Inferred]** — this subsection is original modeling for this document, not a Microsoft-documented
formula, and should be treated as a hypothesis to validate (§12), not an engineering constant.

Assume: the environment has `S` web servers; some load-balancing layer assigns each individual
HTTP(S)/SOAP request from the (affinity-disabled) client to one of the `S` servers in a way that,
absent better information, we model as i.i.d. uniform across servers for a given user (a
simplifying assumption — real load balancers may be round-robin, least-connections, or
geo/health-aware, which would reduce variance relative to pure uniform-random, not increase it;
this is intentionally a conservative/worst-case model). If the user keeps `N` requests in flight
at some instant, the number landing on any particular server `X_i` is `Binomial(N, 1/S)`, with
`E[X_i] = N/S` and `Var(X_i) = N·(1/S)·(1−1/S)`.

For small `1/S`, `X_i` is well approximated by `Poisson(N/S)`. The probability that *at least one*
of the `S` servers exceeds its own per-server limit `L` is, by a union bound,
`P(any server > L) ≤ S · P(X_i > L)`, where `P(X_i > L)` for a Poisson/Binomial tail shrinks
rapidly once `L` is several standard deviations above the mean `N/S`
(`σ ≈ √(N/S)` for the Poisson approximation).

Concretely, with `S` servers and a target "safe" aggregate `N = k · S · L` for some fraction
`k < 1`, each server's mean load is `k·L`. For this to stay comfortably under `L` with low
tail-violation probability, `k·L` must be far enough below `L` that the `√(k·L)`-scale Poisson
fluctuation rarely pushes `X_i` over `L` — i.e., the *aggregate* safe concurrency a controller
should target is **strictly less than `S × L`**, by a margin that shrinks as `S` and `L` grow (more
servers / higher per-server limits → relatively tighter concentration around the mean, so `k` can
be pushed closer to 1) and grows as `S` or `L` shrink (few servers, low per-server limit → fatter
relative tail → need more margin). This is the standard balls-into-bins/occupancy intuition
applied to this setting; it predicts that **the "safe aggregate concurrency per user" is not a
fixed multiple of a single-server number and should itself be measured empirically per
environment**, rather than computed from a formula with an assumed, unverified `S`.

Two consequences for design: (1) a controller should not naively multiply a measured single-server
limit by a guessed server count to get an aggregate target; (2) because the assignment is random
per request rather than sticky, *successive* requests from the same logical caller can land on
different servers even within one burst, so a controller's "current concurrency" state is better
tracked **per member, in aggregate**, with the per-server limit acting only as an unseen,
probabilistic background constraint it must leave headroom against — not as a per-server counter
DataversePool can directly observe or enforce (it has no visibility into which physical server a
given SDK call landed on).

## 4. Interaction with Dataverse platform autoscaling

This section addresses the maintainer's observation that Dataverse's own backend capacity is not
static: if it autoscales in response to load, then "rising latency" is structurally ambiguous
between two opposite correct reactions — *back off* (we are overloading a fixed-capacity backend)
or *hold/push through* (we are under-provisioned and sustained pressure is what triggers the
platform to add capacity, after which the per-user×server aggregate grows and our own ceiling
should grow with it). Getting this wrong in either direction is costly: backing off prematurely
can make a controller's own "problem" self-fulfilling (§4.4), while pushing through blindly risks
exactly the costly concurrency-facet 429s §2.3 warns about.

### 4.1 What Microsoft documents about backend elasticity — and what it does not

**[MS]** The clearest, most direct primary-source statement is on
[Send Parallel Requests to Dataverse](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests):
*"Dataverse manages resource allocation for environments. Production environments that many
licensed users heavily use have more allocated resources. **The number and capabilities of the
servers allocated might vary over time**, so there's no fixed number for the optimum degree of
parallelism."* Two things follow directly from this sentence, both **[MS]**: (1) Dataverse
environments are backed by more than one web server ("servers", plural) and the same page
separately confirms this by recommending affinity-cookie disabling specifically "to reduce the
impact of service protection limits because each limit applies per server"; (2) the *number* of
servers, not just instantaneous load distribution across a fixed number, is described as variable
over time — i.e., Microsoft is documenting something that functions as backend autoscaling (or at
minimum, backend re-provisioning) for the environment's web tier, even though Microsoft does not
use the word "autoscale" or "autoscaling" anywhere on this page or on the
[Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits)
page.

What is **not** documented, anywhere in first-party Dataverse developer documentation found
during this research **[MS absence]**:

- **No documented trigger condition** (e.g., a CPU/latency/queue-depth threshold, analogous to
  Azure App Service's or Azure Monitor autoscale rules) for when additional web-server capacity is
  allocated to an environment, or when it is reclaimed.
- **No documented timescale.** Nothing states whether reallocation happens in seconds, minutes, or
  hours, nor whether there is a minimum "stickiness" period once capacity is added (the sort of
  thing that *is* documented for some other Microsoft capacity products — see the explicit
  contrast below).
- **No documented way to observe server count directly.** No response header, SDK property, or
  admin API found exposes "how many web servers currently back this environment" as a number. The
  `x-ms-dop-hint` value is the closest proxy Microsoft publishes, but it is presented as a
  parallelism recommendation, not as a server-count telemetry signal, and Microsoft does not state
  that a hint change corresponds 1:1 to a server-count change (it could equally reflect a change
  in per-server allocated capability/resources on the *same* server count — the quoted sentence
  explicitly separates "number" from "capabilities" as two independently-variable things).
- **No per-response server/node identifier usable for inference.** Dataverse responses carry a
  per-*request* correlation identifier (`x-ms-service-request-id` and, on error, `REQ_ID` in the
  fault detail) **[MS]**, but these are unique per request, intended for Microsoft support
  correlation, not stable per-server identifiers — there is no documented header analogous to a
  "server instance ID" that would let a client empirically count distinct backing servers by
  collecting distinct values over many requests **[MS absence]**. (This also means the "count
  distinct values of some header" experiment idea, a natural first thing to try, is not known to
  work and would need to be verified empirically, cautiously, against a non-production tenant —
  §12.2.)
- **No documented relationship between `x-ms-dop-hint` and a scaling *event*.** Nothing confirms
  or denies whether the hint changes abruptly (step function, at the moment of a scale-out) or
  smoothly/periodically (re-evaluated on a fixed schedule regardless of whether a scaling action
  actually occurred).

**[Inferred, architectural analogy, not confirmed]** Dataverse is a multi-tenant, Azure-hosted
PaaS; the general Azure pattern for this class of service (web/app tier in front of a
database tier) is some form of autoscaled compute pool (e.g., Azure App Service /
Service Fabric-style elastic backends, scaling primarily on request-rate/CPU/queue-depth metrics
with a provisioning lag of low minutes before new instances are warm and absorbing traffic — this
is standard public guidance for Azure App Service autoscale, e.g.
[Azure Monitor autoscale overview](https://learn.microsoft.com/en-us/azure/azure-monitor/autoscale/autoscale-overview),
cited here only as an architectural pattern reference, **not** as a statement about Dataverse's
specific implementation). It is reasonable to *expect* a provisioning lag on the order of minutes,
not milliseconds and not hours, for genuinely new capacity to come online, by analogy with this
general pattern — but this is an inference by analogy, explicitly not Microsoft-documented for
Dataverse, and must be flagged as such everywhere it informs a design choice below.

**[MS, explicit contrast — different product, cited to show Microsoft *does* publish such details
when it chooses to]** Microsoft Fabric/Power BI Premium capacity autoscale is documented with
concrete operational parameters: autoscale cores activate in response to sustained overload and,
once activated, **remain active for a minimum of 24 hours** before being reconsidered
([Fabric capacity autoscale documentation](https://learn.microsoft.com/en-us/fabric/enterprise/autoscale)).
This is a genuinely different product (Fabric/Power BI capacity, not the Dataverse web/app tier)
and must not be conflated with Dataverse's own backend scaling — there is no evidence the two
systems share an implementation or a timescale. It is cited here only to establish that (a)
Microsoft's cloud platform generally *does* publish autoscale trigger/timescale details for
capacity-based products when such details exist and are considered safe to disclose, and (b) the
*absence* of equivalent published detail for Dataverse's web tier is therefore more likely to
reflect "not productized/not committed as a documented contract" than "accidentally undocumented,"
which argues for treating any assumed timescale as a planning hypothesis to validate (§12.2), not
as a constant to hardcode.

### 4.2 Why this makes latency structurally ambiguous as a signal

Rising per-operation latency (§5, duration row) is consistent with at least three different
underlying causes, each demanding a different controller reaction:

1. **Genuine overload of current capacity** (queueing delay rising because in-flight work exceeds
   what the currently-allocated servers can execute promptly) — correct reaction: **back off**,
   consistent with §8/§9's Gradient-style delay-based design.
2. **Sustained demand that is itself the trigger for the platform to add capacity** — if (1) is
   happening but is also exactly the signal Dataverse's own infrastructure uses to decide to scale
   out **[Inferred — mechanism not confirmed, §4.1]**, then backing off *removes the very pressure
   that would have caused more capacity to be provisioned*, and the controller never discovers the
   higher ceiling that would otherwise have become available — a **self-fulfilling
   under-utilization** trap (§4.4).
3. **Rising server-side execution cost with no capacity question at all** — e.g., a genuinely
   more expensive query pattern, unrelated to concurrency or platform capacity (§2.2's execution-
   time facet is about cost, not availability). Latency rising here says nothing about scaling,
   one way or the other.

Because cause (1)/(2) vs. cause (3) cannot be distinguished from duration alone, and cause (1) vs.
(2) cannot be distinguished from the client side at all without knowing the platform's trigger
condition (§4.1, undocumented), **latency alone is an unreliable basis for a concurrency-reduction
decision when it is being used as the *primary* signal.** This is a stronger and more specific
version of §5/§8's existing "delay-based reasoning is weaker evidence here" caveat — it is not
only that Dataverse latency is noisy and heterogeneous (already covered), but that *even a clean,
well-normalized, per-operation-type latency increase* does not tell you which of three structurally
different situations you are in.

Two further confounds compound this, both already noted elsewhere in this document and restated
here because they interact directly with the autoscaling question:

- **Queueing delay vs. server-side execution time are not the same thing**, and only the latter
  feeds the execution-time service-protection facet (§2.2) or plausibly feeds a platform
  autoscaling trigger (cause 2). Client-observed `Duration` (§9.2's `PoolSizingOperationOutcome`)
  conflates both. Without a way to separate them, a `Gradient`-style strategy reacting to raw
  duration cannot tell cause (1)/(2) (queueing, which *is* capacity-relevant) from a case where
  the extra time is pure network/transit variance (which is not).
- **SDK-internal retries inflate observed duration** (§8, §11) in a way that can mimic rising
  latency without any change in true server load at all, and must be excluded from whatever
  latency signal a strategy consumes (reinforces the existing `MaxRetryCount=0`/surface-429s-to-
  the-pool recommendation, `docs/adr/0016`).

### 4.3 Two coupled controllers with different time constants

Treat Dataverse's own (hypothesized, §4.1) capacity-management loop and DataversePool's proposed
`IPoolSizingStrategy` as two feedback controllers acting on the same plant (available
concurrency), each observing a signal partly caused by the other's actions — a textbook setup for
oscillation or fighting if their time constants and reaction thresholds are not deliberately kept
apart **[Inferred — general control-theory reasoning, not Dataverse-specific]**:

- If DataversePool's controller reacts **faster** than the platform's scaling decision horizon
  (plausibly true: an event-driven `Aimd` reacting to a single 429, §9.4, can react in
  milliseconds, while any plausible platform-capacity change is unlikely to be faster than low
  minutes, §4.1), then by the time additional server capacity would have come online, our own
  controller has already shrunk `MaxSize` well below what the (now-larger) aggregate ceiling could
  support, and has no mechanism to discover the increase except its own slow additive-increase
  re-probing (§9.3's `Aimd.IncreaseStep`/`CooldownAfterThrottle`) — which, if tuned conservatively
  (as §9's design recommends, given the cost of a concurrency 429), could re-probe *slower* than
  the platform scales, leaving real throughput on the table for an extended period. This is the
  "self-fulfilling under-utilization" risk named by the maintainer.
- Conversely, if the platform's scale-out is itself influenced by aggregate demonstrated demand
  (cause 2, §4.2) and our controller oscillates — backs off, re-probes, gets throttled, backs off
  again — the *pattern* of demand the platform observes is noisier and less sustained than a
  steady, deliberately-sustained pressure would be, which could, under some plausible but
  unconfirmed autoscaling designs, delay or prevent a scale-out decision that a smoother demand
  profile would have triggered. (This is speculative — **[Inferred]**, flagged because it is a
  plausible mechanism, not because it is known to be true.)
- **Fighting** is also possible in the other direction: if the platform scales *down* during a
  genuine lull (consistent with "resources... might vary over time" working in both directions,
  §4.1) at the same moment our controller's `Aimd` is in its additive-increase phase from a
  previous cooldown, the next burst could hit the now-smaller actual ceiling, produce a 429, and
  trigger a decrease — a controller oscillating against a *target that itself moved*, which no
  purely reactive, single-loop design can fully avoid without either (a) much wider hysteresis
  margins than a static-ceiling design would need, or (b) treating "ceiling" as inherently
  time-varying and re-discoverable, never assumed fixed once learned (§9.3's `Aimd.Ceiling` should
  be read as "last known safe ceiling," not "true ceiling," for exactly this reason).

### 4.4 Disambiguation strategy: what a strategy implementation should actually do

Given §4.1's documented absence of a direct platform-scaling signal, a strategy cannot *detect*
platform autoscaling directly — it can only act in a way that is robust to not knowing whether it
is happening at any given moment. Concrete, falsifiable-by-measurement (§12.2) design rules:

1. **429s remain the authoritative overload signal; latency is demoted to secondary, exploratory
   evidence only.** This document's own §2/§8 design already treats a concurrency-facet 429 as the
   fast, unambiguous, immediately-actionable signal and latency as a softer secondary pressure —
   §4.2 is the reason *why* that hierarchy must be preserved specifically (not just "because
   latency is noisy" in general, but because latency is the one signal that is ambiguous about
   *direction* — whether more concurrency helps or hurts — while a 429 never is: it is always,
   unambiguously, "stop growing here, for now"). A `Gradient`/latency-based strategy should
   therefore never be the sole driver of a *decrease* past a level that has not also seen 429s; at
   most, latency should slow or pause *further increase* (gate growth, don't trigger shrink).
2. **An explicit, bounded exploration/probing phase, à la BBR's `ProbeBW`.** BBR
   ([Cardwell et al., 2016](https://queue.acm.org/detail.cfm?id=3022184)) periodically and
   deliberately probes above its current operating point specifically to discover whether more
   capacity has become available, rather than assuming the last-measured ceiling is permanent —
   exactly the posture needed here. A concrete analogue for `AimdPoolSizingStrategy`/`Composite`:
   on a schedule independent of (and slower than) the reactive decrease path — e.g., every
   `ProbeInterval` with no intervening 429, step `TargetMaxSize` briefly above the current
   steady-state value (a bounded, time-limited probe, not a sustained increase) and observe
   whether it is accepted. This directly counters the "self-fulfilling under-utilization" trap in
   §4.3: a controller that never explores above its last-learned ceiling will never discover that
   the ceiling has moved, in *either* direction, regardless of what caused the move.
3. **Keep the controller's own reaction time constant deliberately slower than the fastest
   plausible platform reaction, and much slower than the server's own 300 s sliding window for the
   other two facets** — not because a specific platform timescale is known (§4.1 — it is not), but
   because *not knowing* it means a controller that is too fast relative to *any* plausible
   platform timescale (seconds-to-low-minutes, by analogy, §4.1) maximizes the risk described in
   §4.3, while a controller that is slow relative to all plausible timescales degrades gracefully
   to "mostly 429-reactive, rarely fighting," which is the safer failure mode. This reinforces
   (does not replace) §8's existing cooldown/hysteresis design: `CooldownAfterThrottle` and
   `IncreaseStep` (§9.4) should be tuned with this in mind, not purely against the 300 s
   service-protection windows.
4. **Normalize latency per operation type before using it even as a secondary signal** — restates
   and strengthens §5/§8's existing requirement, because an un-normalized aggregate latency signal
   cannot even support the weaker, gated role assigned to it in rule 1 above: a mix-shift toward
   heavier operations would look identical to genuine platform-side degradation without
   normalization.
5. **Treat queueing delay and reported execution cost as separate quantities where possible** —
   DataversePool's own `dataversepool.operation.acquire.duration` (ADR-0024) is *pool-local*
   queueing, not server-side queueing, but is at least a clean, zero-ambiguity proxy for "our own
   permits are the bottleneck right now," which is a useful control to rule out before attributing
   a latency rise to the server/platform at all — if the pool-local acquire queue is empty but
   operation duration is still rising, the cause is more likely server/platform-side; if the
   acquire queue itself is growing, DataversePool's own `MaxSize` is too small for currently
   *offered* load, independent of any Dataverse-side question.

These rules are expressed as strategy-design guidance, not a new interface member — no new field
on `PoolSizingOperationOutcome`/`PoolSizingDecision` (§9.2) is required to implement them; rule 2
is a probing *schedule* a strategy's own `OnTick` can implement internally (a new
`AimdPoolSizingStrategyOptions.ProbeInterval`/`ProbeStepSize`, §10.4), and rules 1/3/4/5 are
implementation discipline within `Aimd`/`Gradient`/`Composite`, re-using existing inputs.

## 5. Candidate signals

| Signal | Leading / lagging | Available today? | Noise | Informs which limit |
|---|---|---|---|---|
| `x-ms-dop-hint` / `RecommendedDegreesOfParallelism` | Leading (server tells you before you're throttled) | **Yes** — public property on `ServiceClient`, fed on both transports **[Measured]**, not yet wired to a pool gauge (`REVIEW-2026-09-30.md:396`) | Low-frequency changes, environment-level granularity, may lag real tenant capacity (§3) | Concurrency (§2.3), loosely |
| 429 / concurrency error (`0x80072326`) | Lagging | Partially — `HttpOperationException` path works; SOAP-path string-match detection has a gap being fixed (`REVIEW-2026-09-30.md:201-223`, `docs/adr/0008`) | Low noise, unambiguous when it fires, but only fires *after* the SDK's own internal retry budget is exhausted unless `MaxRetryCount=0` (`docs/adr/0016`) | Concurrency (§2.3) directly |
| 429 / request-count error (`0x80072322`) | Lagging | Same path as above once distinguished by error code | Low noise; distinguishable from concurrency 429 by SDK error code, currently **not** distinguished by `DataverseThrottleDetector`, which only detects "a throttle happened," not which facet **[Measured: gap]** | Request-rate limiter (§2.1) |
| 429 / execution-time error (`0x80072321`) | Lagging | Same path, same gap | Same as above | Cost/duration budget (§2.2) |
| Operation duration (`dataversepool.operation.duration`, per `dataverse.operation.name`) | Leading (queueing precedes hard rejection) | **Yes**, already instrumented per ADR-0024, tagged by low-cardinality `operation.name` and `pool.member.name` | High — Dataverse is a shared, multi-tenant, noisy backend; includes SDK-internal retry/backoff time when `MaxRetryCount>0` (`docs/adr/0024`); must bucket per operation type, not pool-wide, because read/create/bulk/metadata costs differ by >10x in this repo's own measurements (12s vs 55s p50/p95 on one path alone, `TODO.md:183-186`) | Execution-time budget (§2.2) primarily; secondarily an early-warning proxy for concurrency saturation (queueing) |
| Acquire wait / waiting count (`dataversepool.operation.acquire.duration`, `.waiting`) | Leading for **pool-local** saturation, not server-side saturation | **Yes**, ADR-0024 | Low — this is DataversePool's own permit queue, deterministic | None of the three Dataverse limits directly; informs whether `MaxSize` is the bottleneck vs. Dataverse itself |
| `x-ms-ratelimit-burst-remaining-xrm-requests` / `-time-remaining-xrm-requests` | Leading | **No** — not surfaced by `ServiceClient` on success; only reachable via direct Web API calls bypassing `ServiceClient`, and Microsoft documents them as debugging-only, explicitly not for request-rate control **[MS]** (`docs/adr/0008`) | N/A (not available) — if ever exposed, resets whenever affinity is off and routing changes server, per Microsoft's own doc | Would inform request-count (§2.1) and execution-time (§2.2) proactively if ever available |

## 6. Signal collection & telemetry

### 6.1 Collection must precede control

Every strategy in §9 is only as good as the data it is tuned and validated against. Before any
controller logic is built, DataversePool should be instrumentable enough to answer, retrospectively
and empirically: how many web servers is one application user actually spread across right now
(§3.3), which of the three service-protection facets (§2) actually bites in production, how stable
is the DOP hint (§3), and does sustained load ever provoke an observable capacity change (§4)? None
of these questions can be answered by reasoning alone — they require observation. This section is
therefore **not** part of the algorithm design; it is the prerequisite step, and the roadmap in
§9.5 makes "observe only, no control action" its explicit first phase, mirroring this repository's
own actual engineering plan (`PLAN-2026-09-30.md`'s Phase 3 "cheap telemetry" before its Phase 5
"optional response observer" — "Phase 5 is decided after the gauges show what operators still
can't see," `PLAN-2026-09-30.md:91-96`).

### 6.2 What is reachable today, without any new capture mechanism **[Measured]**

A prior review of this repository (`REVIEW-2026-09-30.md`), grounded directly in disassembly /
string-table inspection of the Dataverse SDK assemblies (`Microsoft.PowerPlatform.Dataverse.Client.dll`,
`Microsoft.Xrm.Sdk.dll`, `Microsoft.Crm.Sdk.Proxy.dll`, both v1.2.2 and v1.2.27), establishes exactly
which signals are already public, with no reflection or response-capture mechanism required:

- **`ServiceClient.RecommendedDegreesOfParallelism`** — fed by `x-ms-dop-hint` on *both* transports
  (`DataverseTelemetryBehaviors.AfterReceiveReply` for SOAP, `Command_WebExecuteAsync` for Web API),
  confirmed present in the SDK's string table, public, read-only, per-clone, updated after every
  call **[Measured]** (`REVIEW-2026-09-30.md:305-310`). The library does not currently sample it.
- **Outbound correlation headers are fully supported**: `OrganizationRequest.RequestId` →
  `x-ms-client-request-id`, `ServiceClient.SessionTrackingId` → `x-ms-client-session-id`, and
  `CreateRequestBuilder().WithHeader/WithRequestId/WithCorrelationId` or
  `ConnectionOptions.RequestAdditionalHeadersAsync` for arbitrary additional headers — all
  confirmed present in the string table **[Measured]** (`REVIEW-2026-09-30.md:315-321`). These are
  **client-generated** correlation identifiers, re-sent in the request and echoed back for
  support-ticket/server-log correlation **[MS]** — they identify *this specific call*, not *which
  server answered it*, and should not be confused with a server-identity signal (§6.3).
- **On throttle failure**, the Web API path's `HttpOperationException.Response.Headers` carries
  the *full* header set from that response, including any `x-ms-ratelimit-*` headers if present;
  the SOAP path's fault carries only `ErrorDetails["Retry-After"]` **[Measured]**
  (`REVIEW-2026-09-30.md:322-324`). This means the Web API throttle path can already observe
  rate-limit remaining values *on the specific requests that fail* — the gap is only for
  **successful** calls, and only for SOAP failures.
- **What the SDK never reads, on any transport, for any call**: `x-ms-ratelimit-burst-remaining-xrm-requests`,
  `x-ms-ratelimit-time-remaining-xrm-requests`, `x-ms-service-request-id`, and `ARRAffinity` do not
  appear anywhere in the SDK assemblies' string tables, in either SDK version checked **[Measured]**
  (`REVIEW-2026-09-30.md:326-330`). This is direct evidence, not inference, that these four signals
  are structurally unreachable through any public `ServiceClient` member — on a *successful* call,
  they can only be obtained by capturing the raw HTTP response before the SDK discards it.

### 6.3 Per-frontend/web-server identity: the hardest and most valuable signal

The maintainer's central hypothesis (§3.3, §4) — that the effective concurrency budget for one
user with affinity off is `S × per-server-limit`, and that platform autoscaling changes `S` or
`per-server-limit` over time — cannot be confirmed or refuted without *some* way to tell which
backend server answered a given request. This is the single most valuable, and hardest, signal to
collect.

- **No header is documented or designed to expose a stable server/node identifier.**
  `x-ms-service-request-id` and `x-ms-client-request-id`/`req_id` are per-*request* correlation
  IDs (§6.2) — confirmed by both this repository's SDK inspection and general Dataverse
  documentation on request tracing, which describes them purely as client/server log-correlation
  identifiers, not routing or topology information **[MS]**
  ([Service request tracing](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/service-request-tracing);
  [Azure SDK correlation ID guidance](https://microsoft.github.io/code-with-engineering-playbook/observability/correlation-id/)).
  There is no documented, Dataverse-specific "which web server/scale unit handled this" header.
- **The one candidate is the `ARRAffinity`/`ARRAffinitySameSite` `Set-Cookie` value.** This is not
  a Dataverse-specific mechanism — it is Azure's standard Application Request Routing affinity
  cookie, used across Azure App Service-family products to pin a client's subsequent requests to
  the same backend instance for session-affinity purposes. Microsoft documents its value as an
  **opaque, encrypted token that identifies the backend instance for routing purposes only**, not
  decodable or otherwise meaningful to client code **[MS, general Azure App Service behavior, not
  Dataverse-specific]** ([Azure App Service session affinity / ARRAffinity discussion](https://learn.microsoft.com/en-us/answers/questions/91664/app-service-multi-instances-application-inproc-ses)).
  This repository's own review independently confirms (by direct observation against a live tenant
  via the pinned-affinity test suite) that Dataverse's web tier sends this cookie on every response,
  and — critically — **still sends it even when `EnableAffinityCookie=false`** (DataversePool's
  forced setting, ADR-0016): the SDK simply never replays it on the next request, but the server's
  response still carries it **[Measured]** (`REVIEW-2026-09-30.md:362-366`,
  `tests/ConnectionPool.Dataverse.Tests/LivePinnedWebApiConcurrencyTests.cs:86-97,452-457`). This
  means that *even with routing-affinity correctly disabled* (ADR-0016's design is unaffected),
  the cookie value can still be **observed, per response, as a passive identity signal** — a
  distinct use from its normal sticky-routing purpose, and one that requires no change to
  `EnableAffinityCookie` or routing behavior at all.
- **Caveats on using it this way, stated plainly:**
  - Counting distinct `ARRAffinity` values seen over a sliding window is a workable
    **approximation** of "how many distinct backend instances has this user's traffic touched,"
    not a guaranteed exact count — it rests on an undocumented implementation detail (that the
    opaque token is stable per backend instance and distinct across instances) that Microsoft is
    free to change without notice **[Inferred, repo's own characterization]**
    (`REVIEW-2026-09-30.md:369-371`).
  - It says nothing about the *size* of a server's capacity or its current load, only its
    identity — it cannot alone tell you a server's per-server limit `L` (§2.3), only help estimate
    `S` and attribute individual request outcomes to a particular server.
  - Its stability over time (does the token rotate on a schedule, on reconnect, or only on an
    actual backend-instance change such as a scale-out or instance recycle?) is not documented by
    Microsoft for Dataverse specifically and was **not** independently confirmed in this review —
    flagged here as an open question requiring the live experiment in §12.2 item 7, not assumed.
  - A general Azure App Service caveat (not confirmed against Dataverse specifically) is that ARR
    affinity cookies are typically tied to the lifetime of the underlying App Service
    instance/worker process, so a token value changing is circumstantial evidence of a
    backend-instance change (e.g., a scale event or instance recycle) but not proof of which kind
    **[Inferred]**.
- **No other viable per-server identity signal was found.** TCP remote IP is not a usable signal
  here: Dataverse's public endpoint sits behind Azure's front-door/load-balancing layer, so the
  client-visible remote IP (if even inspectable through `ServiceClient`'s abstractions, which it
  is not) would be the load balancer's address, not the backend server's — a dead end, flagged as
  such rather than silently omitted. No other `x-ms-*` response header beyond the ones already
  named was identified, by either the repo's assembly inspection or general-purpose research, as
  carrying server/node/scale-unit identity.

### 6.4 Capture mechanisms (none implemented; ranked by the repo's own review)

Because none of the headers in §6.2/§6.3 beyond the DOP hint and correlation IDs are reachable
through any public `ServiceClient` member, observing them requires capturing the raw HTTP
response. Three concrete mechanisms were evaluated (`REVIEW-2026-09-30.md:380-430`), consistent
with this document's maintainer-referenced "Phase 5 optional response observer":

| Mechanism | Covers | Cost / risk | Supportability |
|---|---|---|---|
| **`DiagnosticListener("HttpHandlerDiagnosticListener")`** subscriber, filtered to the environment host | Both transports — WCF's `HttpChannelFactory` (SOAP) and the SDK's internal `"DataverseHttpClientFactory"` (Web API) both ultimately ride `System.Net.Http.HttpClient`, so one listener sees every Dataverse call; the `System.Net.Http.HttpRequestOut.Stop` payload exposes the full `HttpResponseMessage`, all headers (including `Set-Cookie`/`ARRAffinity`) and, as a bonus, the built-in `System.Net.Http` meter gives free per-host duration/count | Medium cost, process-global (must filter by environment host), zero reflection | **Supported by the .NET runtime, not by the SDK** — mark experimental; `HttpRequestOut` is a legacy diagnostic-source contract Microsoft could change, and header semantics on undocumented headers remain undocumented regardless of how they're captured. This is the repo's own planned, recommended-first approach (`PLAN-2026-09-30.md:91-96`, `REVIEW-2026-09-30.md:420-432`). |
| **`IEndpointBehavior`/`IClientMessageInspector.AfterReceiveReply`** on `ServiceClient.OrganizationWebProxyClient` | SOAP path only — mirrors exactly how the SDK itself reads `x-ms-dop-hint` internally | Medium-high cost: needs one internal property via reflection, then a public WCF extensibility point; must be redone per clone | Most precise for the SOAP path, but unsupported (breaks silently on SDK internal changes); feature-flag and version-guard if pursued, with a live test that fails loudly if the internal member disappears (`REVIEW-2026-09-30.md:434-439`). |
| **Handler injection into the internal `"DataverseHttpClientFactory"`** via `ClientServiceProviders.Instance` reflection | Web API path only | Medium-high cost; `Clone()` does not copy `WebApiHttpClient`, so this must be redone per clone | Unsupported, same risk profile as above; redundant with the `DiagnosticListener` approach for the Web API transport, so only useful if that approach proves insufficient. |

The repository's own recommendation — and this document's — is to attempt the `DiagnosticListener`
route first, as a separate **opt-in** package (consistent with ADR-0024's existing low-cardinality
metrics staying in the core library, while this higher-cardinality/higher-risk capture stays
optional), and treat the WCF/reflection routes as a fallback only if that proves insufficient
(`PLAN-2026-09-30.md:91-96`).

### 6.5 Proposed telemetry schema

One record per completed Dataverse operation attempt, regardless of whether a response-capture
mechanism (§6.4) is enabled. Field names below reuse ADR-0024's and §9.2's existing vocabulary
(`PoolSizingOperationOutcome`) wherever a field already exists there, rather than inventing a
parallel one; fields marked "capture-dependent" are only populated when a §6.4 mechanism is active
and have no value otherwise (not zero/empty-string — genuinely absent).

| Field | Type | Source | Notes |
|---|---|---|---|
| `Timestamp` | instant | Always available | Operation completion time. |
| `MemberName` | string (low-cardinality) | Always available (`pool.member.name`, ADR-0024) | Which application user handled this call. |
| `ReplicaId` | string (low-cardinality, configured) | Always available, new | Identifies which process/instance of a multi-replica deployment (§11) recorded this sample — required to separate per-replica partial views from a global picture when aggregating offline. |
| `OperationName` | string (low-cardinality) | Always available (`dataverse.operation.name`, ADR-0024 / §9.2) | e.g. `"create"`, `"retrieve_multiple"` — latency must be read per-bucket, never pool-wide (§5). |
| `DurationTotal` | duration | Always available (ADR-0024) | Client-observed wall-clock duration of the whole attempt, including any SDK-internal retry/backoff. |
| `DurationSdkRetry` | duration, nullable | Capture-dependent / SDK-internal, likely unavailable without SDK instrumentation hooks | Portion of `DurationTotal` spent inside the SDK's own retry loop, when `DataverseClientOptions.MaxRetryCount>0` — separable only if the SDK exposes per-attempt timing, which it does not publicly; recorded as a schema placeholder flagged **[needs live/SDK-source confirmation]** rather than assumed available — with `MaxRetryCount=0` (this document's own recommendation, `docs/adr/0016`) this field is moot since there is no SDK-internal retry to subtract. |
| `Outcome` | enum | Always available (ADR-0024 / §9.2 `PoolSizingOutcomeKind`) | `success` / `error` / `throttled` / `canceled`. |
| `ThrottleReasonCode` | enum, nullable | Always available once the SOAP-path detection gap (§2, `REVIEW-2026-09-30.md:201-223`) is fixed | Decoded facet (§2.1-§2.3), not a generic flag — §9.2's `ThrottleReason`. |
| `RetryAfter` | duration, nullable | Always available when `Outcome=throttled` (ADR-0008) | As reported by the server. |
| `DopHintAtCall` | int, nullable | Always available, new gauge (`REVIEW-2026-09-30.md:396-403`) | `RecommendedDegreesOfParallelism` sampled on this clone immediately before/after the call — "at call time," not a stale pool-wide average. |
| `InFlightMember` | int | Always available (ADR-0024 `dataversepool.operation.active` / §9.2 `InFlightCount`) | This member's in-flight count at completion. |
| `InFlightPool` | int | Always available (sum across members, new aggregate) | Pool-wide in-flight count at completion — needed to reason about whether a single member's pressure is incidental to broader pool-wide load. |
| `FrontendId` | string, nullable, **high-cardinality** | Capture-dependent (§6.3, §6.4) | The observed `ARRAffinity`/`ARRAffinitySameSite` cookie value on this specific response, if a capture mechanism is enabled — the only candidate per-server identity signal found (§6.3); absent (not empty) when no capture mechanism is active. |
| `RatelimitBurstRemaining` | int, nullable, capture-dependent | Capture-dependent (§6.4), and only ever observed on **throttled Web API failures** without a capture mechanism (`REVIEW-2026-09-30.md:322-324`) | `x-ms-ratelimit-burst-remaining-xrm-requests`, when observed; explicitly diagnostic per Microsoft, not a control signal (§5), and with affinity off reflects only the one server that answered this specific request, not a pool-wide value. |
| `RatelimitTimeRemaining` | duration, nullable, capture-dependent | Same as above | `x-ms-ratelimit-time-remaining-xrm-requests`. |
| `ServerRequestId` | string, nullable, capture-dependent | Capture-dependent (§6.4) | `x-ms-service-request-id`, kept for log/support-ticket correlation only — **not** a server-identity signal (§6.3). |
| `ClientRequestId` | string | Always available if set (`OrganizationRequest.RequestId` / `SessionTrackingId`, §6.2) | Client-generated correlation ID, recommended to always set (`REVIEW-2026-09-30.md:434-439` recommendation #3) for support-ticket correlation, independent of any capture mechanism. |

### 6.6 What this data enables

- **Estimating the server count `S` and per-node concurrency (§3.3, §12.2 item 2).** Counting
  distinct `FrontendId` values seen for one member over a sliding window, cross-tabulated against
  `InFlightMember` at the same moments, gives both a lower-bound estimate of `S` and an empirical
  per-node concurrency distribution — the direct input the balls-into-bins model in §3.3 needs to
  move from a theoretical worst case to a calibrated one.
- **Detecting platform scale-out (§4).** A new `FrontendId` value appearing in the window that was
  never seen before, especially correlated with a prior period of sustained near-ceiling pressure,
  is circumstantial but concrete evidence of the hypothesized capacity response (§4.1, §12.2 item
  6) — something no amount of latency or DOP-hint observation alone can show, since both of those
  are aggregate/scalar signals blind to *which* server changed.
- **Attributing 429s to node × user (§2, §12.2 item 3).** Joining `ThrottleReasonCode` and
  `FrontendId` on the same record directly tests whether a given server is throttling one user
  specifically (consistent with the per-user × per-server enforcement model, §2) versus every
  user hitting it at once (which would instead suggest a server-wide, not per-user, pressure
  point, or a different limit entirely).
- **Offline simulator calibration (§12.1).** Every distribution the simulator currently
  parameterizes from `TODO.md`'s existing live-test numbers — per-operation duration percentiles,
  server-count-to-concurrency-ceiling ratios, `Retry-After` growth on repeated offenses — can be
  re-calibrated directly from this schema's recorded data once collection is live, replacing
  assumed/estimated inputs with measured ones, and the `FrontendId`-based `S` estimate (above)
  feeds directly into the simulator's load-balancer-routing model (§12.1) as a concrete parameter
  rather than a swept unknown.

### 6.7 Privacy and cardinality: metrics vs. logs/traces

This schema deliberately spans two different telemetry destinations with different cardinality and
retention properties, consistent with ADR-0024's existing split:

- **Metrics** (OpenTelemetry counters/histograms, ADR-0024's existing pattern) must stay
  low-cardinality and aggregatable: `MemberName`, `OperationName`, `Outcome`,
  `ThrottleReasonCode`, and numeric aggregates (counts, duration histograms, a `dop_hint` gauge,
  a `distinct_frontends` gauge computed *after* the high-cardinality raw value has already been
  reduced to a count) belong here. **`FrontendId`, `ServerRequestId`, and `ClientRequestId` must
  never be used as metric tag values** — an opaque per-instance/per-request token as a tag would
  produce unbounded tag cardinality, degrading or breaking most metrics backends, exactly the
  failure mode ADR-0024's existing low-cardinality tag discipline is designed to avoid.
- **Logs/traces** are the correct destination for the high-cardinality, per-request fields:
  `FrontendId`, `ServerRequestId`, `ClientRequestId`, and the full per-record detail needed for the
  attribution/calibration uses in §6.6 — sampled or retained per the operator's own log-volume and
  retention policy, not emitted as always-on metrics.
- **Privacy/sensitivity caution.** `FrontendId` is an opaque Azure-generated token with no
  documented content beyond routing identity (§6.3), and `ServerRequestId`/`ClientRequestId` are
  correlation identifiers, not end-user data — none of these fields are expected to carry PII.
  However, because capture (§6.4) necessarily intercepts the *entire* raw HTTP response/request,
  an operator enabling it must ensure the capture layer only extracts the specific named fields in
  §6.5 and does not log full request/response bodies by default, since Dataverse payloads
  frequently do carry business/customer data; this is an implementation requirement for any
  `DiagnosticListener`-based package (§6.4), not an optional nicety.
- **Retention.** Because `FrontendId` values are only useful for the sliding-window
  distinct-count/attribution uses in §6.6, they do not need long retention — a rolling window
  (minutes to low hours) is sufficient for the live uses, with longer retention reserved for an
  explicit calibration/export workflow feeding the simulator (§12.1), not indefinite default log
  retention.

## 7. Diagnosing slow/hanging calls: retries vs. server time

A call that appears to "hang" for tens of seconds to several minutes is, from the caller's side,
indistinguishable between two very different situations: Dataverse itself is genuinely slow to
respond, or the SDK is silently absorbing retries/backoff *inside* one `Execute` call before ever
returning. This ambiguity directly undermines every signal in §5/§6 — a `Duration` sample that is
actually 46 seconds of SDK-internal retry sleep, not server time, would badly mislead a
latency-based strategy (§8's `Gradient`) into reacting to the wrong thing. This section documents
the internal retry loop precisely enough to separate the two, and is itself part of the §6.1
"observe only" first phase — none of it requires a controller, only better timing/attribution.

### 7.1 `ServiceClient`'s internal retry loop **[Measured, from SDK source/behavior]**

The SDK's retry behavior for both transports is governed by three public properties already
exposed by DataversePool's `DataverseClientOptions` (`docs/adr/0016`):

- **`MaxRetryCount`** (SDK default **10**) — maximum retry attempts *inside one logical `Execute`
  call*, before the SDK gives up and throws to caller code.
- **`RetryPauseTime`** (SDK default **5 s**) — base pause between attempts.
- **`UseExponentialRetryDelayForConcurrencyThrottle`** (SDK default **`false`**) — if `true`,
  repeated 429/concurrency-throttle retries back off exponentially instead of repeatedly honoring
  the server's raw `Retry-After` value as-is each time.

With all defaults left in place, **one single `Execute` call hitting a persistently transient
condition can block the caller for up to `MaxRetryCount × RetryPauseTime` ≈ 50 seconds** before an
exception (or a slow success) is ever observed outside the SDK — this arithmetic and its
consequence for the pool's own throttle detection are already documented in this repository's own
`docs/adr/0016` (*"one call hitting a transient error can block for up to ~50 seconds before an
exception ever reaches this library's throttle detection... the pool has no way to distinguish
'stuck' from 'merely slow' until the SDK gives up"*). Two further facts sharpen this, both
**[Measured — from SDK source and this repository's own review, GitHub:
[microsoft/PowerPlatform-DataverseServiceClient](https://github.com/microsoft/PowerPlatform-DataverseServiceClient)]**:

- **`MaxRetryCount` governs *both* transient (non-429) errors *and* HTTP 429/service-protection
  retries** — it is not two separate budgets. A caller who sets a low `MaxRetryCount` to bound
  transient-error latency also, as a side effect, bounds how many times the SDK will silently
  re-honor a 429 `Retry-After` before giving up and throwing — which is exactly why
  `docs/adr/0016` recommends `MaxRetryCount=0` specifically to make 429s pool-visible immediately
  rather than retried away inside one opaque call.
- **The retry loop's wait is `Task.Delay`/`Thread.Sleep`-based, not cancellable mid-wait by a
  caller-supplied timeout shorter than the full retry budget** in older SDK revisions — a known
  defect (fixed after v1.2.6,
  [microsoft/PowerPlatform-DataverseServiceClient#511](https://github.com/microsoft/PowerPlatform-DataverseServiceClient/issues/511))
  allowed the internal loop to keep retrying even when the *caller's own* cancellation token had
  already fired, because the stop condition checked retry count and cancellation independently
  rather than stopping on *either*. Confirm the SDK version in use is new enough that an external
  `CancellationToken`/timeout reliably interrupts the loop rather than silently finishing all
  `MaxRetryCount` attempts regardless (`ConnectionPool.Dataverse.csproj` pins 1.2.27, after the fix,
  but this should be explicitly re-verified whenever the pinned version changes).
- **Visibility into retries from outside the SDK is limited.** `ServiceClient` accepts an
  `ILogger` at construction and uses it for its own internal diagnostic messages (including, per
  Microsoft's own samples, retry/backoff and MSAL token-acquisition tracing when a sufficiently
  verbose log level is configured) — this is the primary **[MS]**-documented way to observe
  "a retry happened" without reflection, but it surfaces as unstructured log lines, not a
  structured retry-count/wait-time field the pool can read programmatically per call. Two
  properties sometimes cited for post-hoc diagnosis, `ServiceClient.LastException` and
  `ServiceClient.LastError`, hold only the *final* error state after `Execute` returns/throws —
  they say nothing about how many retries happened or how long the SDK slept internally before
  that final result, so they cannot separate "one slow call" from "ten retried calls" after the
  fact. No counter/event exposing a per-call retry count or cumulative internal wait time was
  found on the public `ServiceClient` surface **[MS absence]** — this is the core justification for
  §7.3's recommendation to make every attempt externally visible rather than rely on anything the
  SDK reports about its own retries.

### 7.2 Other hidden time sinks besides the retry loop

A "hang" is not necessarily a retry loop at all. Several other causes produce the same external
symptom (a single `Execute`/pool-acquire call taking far longer than expected) and must be ruled
out or attributed separately:

- **Token acquisition/refresh (MSAL).** `ServiceClient` acquires and silently refreshes Azure AD
  tokens via MSAL.NET internally; a cold start, a network-impaired token endpoint, or a clock/cache
  issue can add seconds to tens of seconds before the actual Dataverse request is even sent
  **[MS, general MSAL behavior]**. This cost is per-token-lifetime (an hour-scale cache), not
  per-call, so it shows up as an occasional outlier on an otherwise-fast member, not as sustained
  elevated latency — a useful discriminator from genuine server-side slowness, which tends to be
  more broadly/persistently elevated.
- **Connection setup/TLS and `MaxConnectionTimeout`.** `ServiceClient.MaxConnectionTimeout` is a
  **static, class-level** property with an SDK default of **4 minutes**
  **[MS]** ([property reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.powerplatform.dataverse.client.serviceclient.maxconnectiontimeout)),
  settable only before the *first* `ServiceClient` is constructed in the process (later changes
  have no effect on already-constructed instances, including clones) — a 4-minute ceiling is far
  longer than any reasonable per-operation budget this library would want, and is a plausible
  silent contributor to an apparent "hang" during initial connection/handshake if left at its
  default and not explicitly lowered at process startup, before `DataverseUserPool` constructs its
  first member.
- **WCF channel timeouts (SOAP path).** `OrganizationServiceProxy`/`OrganizationWebProxyClient`'s
  underlying WCF bindings carry their own `OpenTimeout`/`SendTimeout`/`ReceiveTimeout`, independent
  of `MaxConnectionTimeout` and of `MaxRetryCount` — a channel-level timeout firing looks, from the
  caller's side, like a generic communication exception, and (per §7.1) is itself subject to the
  SDK's own retry loop before it ever reaches calling code **[MS, general WCF behavior]**.
- **`ServicePointManager`/`HttpClient` connection limits causing client-side queueing.** On older
  .NET Framework-targeting configurations, `ServicePointManager.DefaultConnectionLimit` caps
  concurrent outbound connections *per endpoint*, independent of anything Dataverse itself is
  doing — if this limit is lower than DataversePool's configured `MaxSize` × member count, requests
  queue **client-side**, inside the OS/BCL networking stack, invisibly to both the pool's own
  acquire-wait metric (ADR-0024's `dataversepool.operation.acquire.duration` measures pool-permit
  wait, not this layer) and to Dataverse (which never sees the request until it's dequeued)
  **[MS, general .NET Framework behavior]**. The SDK's internal `"DataverseHttpClientFactory"`
  (`REVIEW-2026-09-30.md`) uses `HttpClient`/`SocketsHttpHandler` on modern .NET, which has its own,
  separate connection-pooling limits (`SocketsHttpHandler.MaxConnectionsPerServer`) with a
  materially different default behavior than the legacy `ServicePointManager` path — which concrete
  path/limit applies depends on the target framework and SDK version in use and should be confirmed
  for the pinned SDK version rather than assumed **[needs live/SDK-source confirmation]**.
- **Thread-pool starvation / sync-over-async.** If any code path in the call chain blocks a
  thread-pool thread on an async operation (`.Result`/`.Wait()`/`GetAwaiter().GetResult()`), under
  load this can exhaust the thread pool and delay *unrelated* work process-wide, including the
  timer callback this document's own `PoolSizingController` tick (§9.4) would depend on — a
  starvation-induced delay look like "everything is slow," including the pool's own control loop,
  not just Dataverse calls **[Lit — general .NET thread-pool behavior, not Dataverse-specific]**.
  DataversePool's own async paths should be audited for this independently of anything described
  here; it is mentioned so an operator diagnosing a hang does not prematurely attribute it to
  Dataverse/the SDK when the actual cause is process-local thread-pool exhaustion.

### 7.3 How DataversePool can separate them

None of the above can be disambiguated from a single, already-available `Duration` measurement — a
46-second call could be one slow server-side execution, ten retried attempts, or a mix. Separating
them requires:

1. **Set `MaxRetryCount=0` and move retries to the pool** (already `docs/adr/0016`'s recommended
   pattern, restated here as a *diagnostic* requirement, not just a throttle-visibility one): once
   the SDK never retries internally, **every** `Execute` call the pool sees corresponds to exactly
   one real network attempt, so `DurationTotal` (§6.5) stops being an opaque mix of retries and
   becomes directly attributable — and the pool's own retry loop (wherever it is implemented,
   `docs/adr/0017`/`0019`) can then emit one §6.5-shaped telemetry record **per attempt**, with an
   explicit `AttemptNumber`, rather than one record hiding an unknown number of internal attempts.
2. **Per-attempt timing via the same response-capture mechanisms as §6.4.** A
   `DiagnosticListener`/`IClientMessageInspector` hook sees the start and stop of each individual
   HTTP request, independent of how many logical `Execute`-level attempts the pool's own retry
   layer makes — this gives a true per-network-round-trip duration, cleanly separable from any
   pool-level retry wait inserted *between* attempts (which the pool itself controls and already
   knows, once step 1 is in place).
3. **Hook SDK logging to count any *remaining* SDK-internal retries as a canary.** Even with
   `MaxRetryCount=0` eliminating internal retries going forward, passing an `ILogger` into
   `ServiceClient` and watching for retry/backoff-shaped log messages is a cheap way to verify, in
   production, that no internal retries are slipping through (e.g. via a code path that
   doesn't honor the option, or a future SDK update changing the behavior) — a structured counter
   derived from log-message pattern matching is weak evidence (log text is not a stable contract)
   but better than nothing, and should be treated as a smoke-test signal, not a precise metric.
4. **Measure server-reported time if any header exists.** No header carrying a
   server-computed/reported execution-time value for a *successful* call was found during this
   research (§6.2's `x-ms-ratelimit-*` headers are diagnostic counters, not a per-call duration;
   no other candidate header was identified) — so "server time" for a successful call can only be
   *approximated* as (per-attempt network round-trip duration from step 2) minus (any known
   client-side overhead such as serialization), not read directly. This should be stated as a
   limitation, not worked around with an invented proxy.
5. **Per-attempt OpenTelemetry/`Activity` spans.** Modern `System.Net.Http`/`SocketsHttpHandler`
   emits built-in `Activity`/`DiagnosticListener` events for outbound HTTP requests
   (`System.Net.Http.HttpRequestOut.Start`/`.Stop`, already named in §6.4's capture-mechanism
   table) **[MS, documented .NET networking instrumentation]**, giving a free per-attempt span with
   accurate start/stop timestamps without any SDK cooperation — whether the Dataverse SDK itself
   emits any *additional*, SDK-specific `Activity`/`DiagnosticSource` events (beyond what the
   underlying `HttpClient` already provides) was **not confirmed** during this research and should
   be checked directly against the pinned SDK version's source/assembly
   **[needs live/SDK-source confirmation]**; absent that, the built-in `System.Net.Http` activity
   is the reliable, already-present source for per-attempt timing spans, consistent with §6.4's
   recommendation to build on the `DiagnosticListener` route first.
6. **Set both a per-attempt and a total-operation timeout, explicitly, rather than relying on SDK
   defaults.** Given `MaxConnectionTimeout`'s 4-minute default (§7.2) and the ~50 s internal-retry
   worst case (§7.1) if `MaxRetryCount` is left at its default, an operator who sets neither a
   per-attempt timeout (bounding one network round-trip/WCF channel operation) nor a
   total-operation timeout (bounding the pool's own retry loop across however many attempts it
   makes, step 1) has no enforced upper bound on how long one logical caller-visible operation can
   take — both are recommended as **explicit, required-to-set** DataversePool-level settings (§10),
   not left to whatever the SDK/WCF/connection-pooling layer defaults to, precisely because those
   defaults (4 minutes, ~50 s, plus whatever client-side queueing §7.2 describes) stack rather than
   substitute for each other.

Collecting the telemetry in step 1-2 (per-attempt records) and acting on the timeouts in step 6 are
both part of the §6.1 "observe only" first phase (§9.5) — distinguishing retries from server time
is a measurement problem to solve *before* any latency-based strategy (§8's `Gradient`) can be
trusted to react to `Duration` at all, since an un-separated `Duration` signal conflates exactly
the two things (SDK-internal wait vs. genuine server-side slowness) that a Gradient-style
controller most needs to tell apart.

### 7.4 Telemetry fields to add

These fields extend §6.5's schema; they require `MaxRetryCount=0` plus the pool owning its own
retry loop (step 1 above) to be meaningful — without that precondition, `AttemptNumber` is always
`1` and `SdkInternalRetryWait`/`ServerTimeEstimate` cannot be computed at all.

| Field | Type | Source | Notes |
|---|---|---|---|
| `AttemptNumber` | int | Pool-owned retry loop (precondition: `MaxRetryCount=0`) | 1-based index of this attempt within the logical operation; without this precondition, always `1` and uninformative. |
| `AttemptDuration` | duration | Per-attempt capture (§6.4/§7.3 step 2) | One network round-trip's duration, cleanly separable from inter-attempt pool-owned retry waits. |
| `TotalDuration` | duration | Sum of all attempts + all pool-owned inter-attempt waits for one logical operation | Replaces/extends §6.5's `DurationTotal` once multi-attempt tracking exists; the field a caller actually experienced end-to-end. |
| `SdkInternalRetryWait` | duration, nullable | Canary only (§7.3 step 3), log-pattern-derived | Expected `TimeSpan.Zero`/absent when `MaxRetryCount=0` is correctly honored; any non-zero value is evidence of an unexpected internal retry path and should alert, not silently feed a strategy. |
| `TokenAcquisitionTime` | duration, nullable | `ILogger`-derived (§7.2) if MSAL logging is enabled, else unavailable | Separates cold-token-refresh outliers from genuine server slowness; expected to be rare/sparse, not present on every record. |
| `ServerTimeEstimate` | duration, nullable | Derived: `AttemptDuration` − known client-side overhead (§7.3 step 4) | An approximation, not a direct measurement — no header was found carrying a server-reported per-call duration for successful calls; should be documented as such wherever surfaced. |
| `PerAttemptTimeoutHit` / `TotalOperationTimeoutHit` | bool | New, explicit timeouts (§7.3 step 6, §10) | Distinguishes "we gave up on our own budget" from "the server/SDK actually returned" — both are themselves informative outcomes, not failures to discard. |

## 8. Algorithm survey and fit

| Algorithm | Signal type | Core idea | Fit for Dataverse |
|---|---|---|---|
| **AIMD** (TCP Reno-style) **[Lit]** | Loss-based | Additive increase each "round" without loss; multiplicative decrease on loss. Proven (Chiu & Jain 1989, [paper](https://www.cse.wustl.edu/~jain/papers/cong_av.htm)) to converge to fair, efficient allocation among independent, uncoordinated agents sharing one bottleneck. | Good match for the concurrency facet (§2.3): a 429 there is an unambiguous "loss" signal, and the multi-replica/no-coordination property (§11) is exactly what distributed AIMD was designed for. Slow to re-probe if step sizes are tuned too conservatively relative to Dataverse's long `Retry-After` penalties. |
| **TCP Vegas** **[Lit]** ([Brakmo & Peterson 1995](https://web.stanford.edu/class/cs244/papers/tcp_vegas.pdf)) | Delay-based | Compares actual throughput to expected throughput at minimum observed RTT; increases/decreases window based on how far current RTT diverges from a baseline "no queueing" RTT. | Appealing in principle (anticipates congestion before loss), but Dataverse latency is dominated by operation-type heterogeneity and multi-tenant noise, not a stable "pipe," so a clean minRTT baseline is harder to establish than on a dedicated TCP path; needs per-operation-type baselines (§5) to be usable at all. |
| **Gradient / Gradient2** (Netflix `concurrency-limits`) **[Lit]** ([repo](https://github.com/Netflix/concurrency-limits)) | Delay-based, drift-corrected | `gradient = (minRTT + B) / sampleRTT`; `limit_new = gradient × limit_old + headroom`. Gradient2 adds long/short exponential-average divergence tracking to resist drift/bias from a creeping minRTT baseline. | Closest prior art to "latency as a leading signal" requested in the brief. Would need per-operation-type instances (one Gradient limiter per operation class) given Dataverse's heterogeneous cost profile, otherwise a batch of slow `RetrieveAllEntities` calls would crush the limit for fast `Retrieve` calls sharing the same member. |
| **Envoy adaptive concurrency filter** **[Lit]** ([docs](https://www.envoyproxy.io/docs/envoy/latest/configuration/http/http_filters/adaptive_concurrency_filter)) | Delay-based (same Gradient formula), productionized | Periodically re-measures minRTT by deliberately clamping concurrency to a minimum ("minRTT calculation window"), with jitter so correlated clients don't all clamp simultaneously. | The periodic-reprobe-at-floor pattern is directly transferable: DataversePool could periodically force one member down to a small `MaxSize` to refresh a "best observed latency" baseline per operation class, with jitter across members/replicas to avoid synchronized throughput dips. |
| **BBR** **[Lit]** ([Cardwell et al., ACM Queue 2016](https://queue.acm.org/detail.cfm?id=3022184)) | Bandwidth + min-RTT model-based | Explicitly models bottleneck bandwidth and propagation RTT instead of inferring congestion from loss; cycles through probe phases. | Elegant, but requires a stable notion of "bottleneck bandwidth," which doesn't map cleanly onto a multi-tenant HTTP API with per-operation-type costs and server-side budgets measured in execution-*time* rather than bytes/sec. Treat as inspiration (periodic active probing of true capacity) rather than a directly portable algorithm. |
| **PID control** **[Lit]** | Generic (error-based) | Proportional-Integral-Derivative feedback on an error signal (e.g., target latency − observed latency, or target headroom below the concurrency limit). | Flexible chassis that can combine the 429-loss signal and the latency signal in one loop; harder to tune safely (gain selection) than AIMD/Gradient, and PID's continuous-error assumption fits a stable plant better than Dataverse's bursty 429/Retry-After step response. Worth prototyping in the simulator (§12) before trusting in production. |
| **CoDel** **[Lit]** ([RFC 8289](https://www.rfc-editor.org/rfc/rfc8289.html)) | Delay-based, queue-management | Tracks per-packet sojourn time in a queue; drops/signals once sojourn exceeds a target for a sustained interval, self-tuning without manual parameters. | The "self-tuning off of sojourn time, not queue length" idea maps onto DataversePool's own **acquire wait** (queueing for a pool permit) as an input to decide when the pool itself — not Dataverse — is the bottleneck, complementing (not replacing) server-side signals. |
| **System.Threading.RateLimiting** / Polly rate limiter **[Lit]** ([BCL docs](https://learn.microsoft.com/en-us/dotnet/api/system.threading.ratelimiting.concurrencylimiter), [Polly docs](https://www.pollydocs.org/strategies/rate-limiter.html)) | Mechanism, not algorithm | `ConcurrencyLimiter` (permit-count gate) and `TokenBucketRateLimiter`/`SlidingWindowRateLimiter` (rate gate) are off-the-shelf .NET primitives; Polly wraps them as a resilience strategy. | Good off-the-shelf actuators: `ConcurrencyLimiter`-shaped semantics for §2.3 (DataversePool's own `MaxSize`/permit semaphore already plays this role), and a token-bucket/sliding-window limiter is the natural off-the-shelf actuator for §2.1's request-count facet — likely cheaper to adopt than hand-rolling one. |

**Loss-based vs. delay-based, applied to Dataverse's specific signal set [Inferred synthesis]:**
a 429 is unambiguously a loss signal and should drive a loss-based (AIMD-family) reaction for the
concurrency facet, because that facet's own documented dynamics are "instantaneous, immediate
error" (§2.3) — there's no graceful early-warning queueing state visible from *that* signal before
the hard rejection. Duration/latency plays the delay-based role, but because Dataverse's queueing
(if any) happens server-side and isn't separately exposed, duration is at best a *proxy* for
queueing, muddied by genuine per-operation cost variance — so delay-based reasoning here is
weaker evidence than in a classical TCP/Envoy setting and should be a secondary, slower-moving
signal rather than the primary actuator.

## 9. Recommended controller design sketch (proposal — not implemented)

### 9.1 Why a strategy pattern

DataversePool already solves two structurally similar problems this way:
`ISlotSelectionStrategy` (`src/ConnectionPool.Dataverse/ISlotSelectionStrategy.cs`) pulls "which
member serves this acquire" out of `DataversePool` so round-robin, health-aware round-robin, and
least-connections can be swapped or added without touching pool internals (`docs/adr/0006`); and
`IPooledResourcePolicy<T>` (`src/ConnectionPool.Core/IPooledResourcePolicy.cs`) pulls all
domain-specific create/health/dispose decisions out of the generic `ResourcePool<T>` engine
(`docs/adr/0001`). "How big should this member's `MaxSize` be right now" is the same shape of
question — a pluggable decision, made against a generic engine (`ResourcePool<T>`/
`DataverseUserPool`) that should not need to know *how* the decision is made — so it should be
designed the same way, not as a single hardcoded algorithm wired into `DataversePool`. This also
directly serves §1's conclusion that no single algorithm is obviously correct yet (AIMD vs.
Gradient vs. DOP-hint-only are all defensible starting points) and §12's simulation plan (candidate
algorithms need to be swappable to compare in the simulator and, later, against a live tenant,
without a code fork).

### 9.2 Proposed interface: `IPoolSizingStrategy`

Named and shaped to sit next to `ISlotSelectionStrategy`, with the same conventions: an
XML-doc-driven interface in `ConnectionPool.Dataverse`, default-interface-method no-ops for
callbacks most implementations won't need, and ADR cross-references once an ADR is written for it.

```csharp
namespace ConnectionPool.Dataverse;

/// <summary>
/// Pluggable strategy for deciding each member's <see cref="DataverseUserPool.MaxSize"/> at
/// runtime. The pool engine itself has no opinion on *how* a target size is computed (ADR-0001's
/// domain-agnostic-policy rationale, applied to sizing instead of lifecycle) - this interface
/// exists so Fixed (today's static behavior), DopHint, Aimd, Gradient, and Composite strategies
/// can all be swapped or combined without changing DataversePool's public API. v1 ships `Fixed`
/// as the default; every other strategy here is a proposal (see docs/research/autoscaling.md).
/// </summary>
public interface IPoolSizingStrategy
{
    /// <summary>
    /// Called once when a member joins the pool (construction or a later <c>AddMember</c>), to
    /// seed its initial <see cref="DataverseUserPool.MaxSize"/>. Receives the member's
    /// configured <see cref="PoolOptions.MaxSize"/> as the strategy's starting point/fallback.
    /// Most strategies return it unchanged; <c>DopHint</c> may override it once a hint is
    /// available (which it isn't yet at construction - see <see cref="OnTick"/>).
    /// </summary>
    int GetInitialSize(DataverseUserPool member, PoolOptions configuredOptions);

    /// <summary>
    /// Reports the outcome of one completed Dataverse operation attempt against
    /// <paramref name="member"/>. Called by the same execution paths ADR-0024 already
    /// instruments (<c>PooledOrganizationService</c>, <c>ExecuteWithThrottleRetryAsync</c>), so
    /// this strategy interface reuses that outcome vocabulary rather than inventing a new one.
    /// Event-driven: a loss-based strategy (<c>Aimd</c>) reacts here, immediately, rather than
    /// waiting for the next <see cref="OnTick"/> - the brief for a concurrency-facet 429 is
    /// "immediate", per §2.3, and an event callback is what makes that possible. Default no-op:
    /// strategies that only care about periodic signals (e.g. a pure DOP-hint follower) don't
    /// need to override this.
    /// </summary>
    void OnOperationCompleted(DataverseUserPool member, PoolSizingOperationOutcome outcome)
    {
    }

    /// <summary>
    /// Called on a fixed external cadence (owned by the caller - see §9.4 - not by the strategy)
    /// for every member, to let slower/periodic signals (DOP-hint re-seed, Gradient's minRTT
    /// re-probe window, additive-increase-after-cooldown) drive a size change without needing a
    /// dedicated timer per strategy. Receives the latest <see cref="DataverseUserPool.Stats"/>
    /// and the strategy's own previously returned <see cref="PoolSizingDecision"/> so it can see
    /// whether a prior decision has fully taken effect yet (ADR-0025's permit-debt shrink is not
    /// instant - a strategy should read <c>PoolStats.LeasedCount</c>, not just the configured
    /// <c>MaxSize</c>, before deciding to shrink further). Default no-op: a purely event-driven
    /// strategy (reacting only in <see cref="OnOperationCompleted"/>) never needs to override
    /// this.
    /// </summary>
    PoolSizingDecision? OnTick(DataverseUserPool member, PoolStats currentStats, PoolSizingDecision? lastDecision) => null;
}

/// <summary>
/// One completed operation attempt, as seen by a <see cref="IPoolSizingStrategy"/>. Mirrors the
/// outcome vocabulary ADR-0024 already defines for metrics, plus the throttle-reason
/// decomposition this document's §2 argues is required (a generic "throttled" boolean is not
/// enough to pick the correct reaction).
/// </summary>
/// <param name="OperationName">Low-cardinality operation class, e.g. <c>"create"</c>,
/// <c>"retrieve_multiple"</c> - the same tag ADR-0024 uses, so latency baselines can be kept
/// per operation type (§5) instead of pool-wide.</param>
/// <param name="Duration">Wall-clock duration of this attempt, excluding lease-acquire wait.</param>
/// <param name="Outcome">Same vocabulary as ADR-0024: <c>success</c>, <c>error</c>,
/// <c>throttled</c>, <c>canceled</c>.</param>
/// <param name="ThrottleReason">Populated only when <see cref="Outcome"/> is <c>throttled</c>:
/// which of the three service-protection facets fired (§2), decoded from the SDK/Web-API error
/// code, not a generic flag.</param>
/// <param name="RetryAfter">The server-reported <c>Retry-After</c>, when present.</param>
/// <param name="InFlightCount">Snapshot of this member's in-flight operation count at the moment
/// this attempt completed (pairs with <c>dataversepool.operation.active</c>, ADR-0024).</param>
public readonly record struct PoolSizingOperationOutcome(
    string OperationName,
    TimeSpan Duration,
    PoolSizingOutcomeKind Outcome,
    ThrottleReason? ThrottleReason,
    TimeSpan? RetryAfter,
    int InFlightCount);

/// <summary>Mirrors ADR-0024's existing <c>outcome</c> tag values.</summary>
public enum PoolSizingOutcomeKind { Success, Error, Throttled, Canceled }

/// <summary>Decoded service-protection facet (§2.1-§2.3), not a generic throttle flag.</summary>
public enum ThrottleReason { RequestCount, ExecutionTime, ConcurrentRequests, Unknown }

/// <summary>
/// A strategy's output: a target <see cref="DataverseUserPool.MaxSize"/> for one member, plus an
/// optional pacing decision for the two sliding-window facets a concurrency permit count cannot
/// fix alone (§2.1, §2.2, §9.3). <c>null</c> fields mean "this strategy has no opinion on this
/// axis" - e.g. a pure <c>Aimd</c> concurrency strategy leaves <see cref="MaxRequestsPerWindow"/>
/// and <see cref="MaxExecutionTimePerWindow"/> null, deferring that axis to a <c>Composite</c>
/// partner strategy or to no pacing at all.
/// </summary>
/// <param name="TargetMaxSize">New value to apply via <see cref="DataverseUserPool.SetMaxSize"/>.
/// Growing is immediate; shrinking is graceful/permit-debt (ADR-0025) - the strategy does not need
/// to know or care which, it just states the target.</param>
/// <param name="MaxRequestsPerWindow">Optional target for a Layer-2 request-rate pacer (§2.1),
/// over <see cref="SampleWindow"/>. Not an actuator on its own; a pacing component (§9.3)
/// consumes this.</param>
/// <param name="MaxExecutionTimePerWindow">Optional target for a Layer-2 cost-budget pacer
/// (§2.2), over <see cref="SampleWindow"/>.</param>
/// <param name="SampleWindow">The window the two budgets above apply to; strategies that set
/// either budget must set this too.</param>
public readonly record struct PoolSizingDecision(
    int TargetMaxSize,
    int? MaxRequestsPerWindow,
    TimeSpan? MaxExecutionTimePerWindow,
    TimeSpan? SampleWindow);
```

Each strategy's own tunables (decrease factor, cooldown, Gradient's buffer percentage, etc.) live
in that strategy's **own options type** (`AimdPoolSizingStrategyOptions`,
`GradientPoolSizingStrategyOptions`, ...), exactly as `PoolOptions`/`DataverseClientOptions` are
already separate, focused option types rather than one flat bag — not a single shared
`AutoScalingOptions` god-object. §10 reflects this.

### 9.3 Concrete strategies to ship

| Strategy | Implements | Inputs used | Output | Notes |
|---|---|---|---|---|
| **`Fixed`** (default) | `IPoolSizingStrategy` | None | `TargetMaxSize` = the configured `PoolOptions.MaxSize`, unchanged, forever | Exactly today's behavior (`docs/adr/0025` pre-automation). Ships as the default so adopting this feature is strictly opt-in - `AutoScaling.Enabled=false`-equivalent, expressed as "the active strategy is `Fixed`" rather than a separate on/off flag layered on top. |
| **`DopHint`** | `IPoolSizingStrategy` | `RecommendedDegreesOfParallelism`, read periodically in `OnTick` | `TargetMaxSize = clamp(hint × Multiplier, Floor, Ceiling)` | Directly answers §3/§9 "DOP hint as floor not ceiling": `Multiplier` defaults to `1.0`, `Floor`/`Ceiling` are this strategy's own options. Does not react to 429s or latency at all - a thin, cheap, already-reachable-signal-only strategy, useful alone on a tenant where the hint has been observed to track reality, or as one input into a `Composite`. |
| **`Aimd`** | `IPoolSizingStrategy` | `ThrottleReason`-decoded 429s (§2), `RetryAfter` | On a `ConcurrentRequests` 429: `TargetMaxSize = max(MinSize, round(current × DecreaseFactor))`, immediately, via `OnOperationCompleted`. On a `RequestCount`/`ExecutionTime` 429: does **not** touch `TargetMaxSize` (§2.4) - instead sets `MaxRequestsPerWindow`/`MaxExecutionTimePerWindow` down, so a Layer-2 pacer (not built yet, §13) can react on the correct axis. On sustained no-throttle operation past `CooldownAfterThrottle` (checked in `OnTick`): `TargetMaxSize += IncreaseStep`, capped at `Ceiling`. On an even longer, slower schedule (`ProbeInterval`, §10.4) with no intervening throttle: briefly probes above the current ceiling (BBR-`ProbeBW`-style, §4.4) to discover whether Dataverse's own backend capacity has grown (§4), reverting the probe immediately unless it goes unthrottled. | This is the reason-aware loss-based controller from §8/§9's original layered sketch, now expressed as one strategy implementation rather than an implicit "Layer 1"; the probing addition is what keeps it from permanently under-utilizing a ceiling that has moved (§4). |
| **`Gradient`** | `IPoolSizingStrategy` | `Duration` per `OperationName`, `InFlightCount` | `TargetMaxSize = clamp(gradient × current + headroom, Floor, Ceiling)` per the Envoy/Netflix formula (§8), computed per `OperationName` bucket and reduced to one `TargetMaxSize` (e.g. the minimum across buckets, so a slow bucket can't be masked by a fast one) | Needs `OnTick` for periodic minRTT re-probing (Envoy-style, §8) in addition to `OnOperationCompleted` for sample collection. Latency-only; does not read 429s at all, so it is usually paired with `Aimd` in a `Composite` rather than used alone, per §8's "delay-based reasoning is weaker evidence here" caveat. |
| **`Composite`** | `IPoolSizingStrategy` | Delegates every callback to an ordered list of child strategies | `TargetMaxSize = min(child decisions)` by default (most conservative wins - a safety-first combination rule); a `CompositePoolSizingStrategy` constructor option can select `max` or a named child's value instead for cases where that is deliberately wanted. `MaxRequestsPerWindow`/`MaxExecutionTimePerWindow` combine the same way, independently of `TargetMaxSize`. | The concrete way to assemble the §9 (old) "layered" design as data, not code: e.g. `new CompositePoolSizingStrategy(new DopHintPoolSizingStrategy(...), new AimdPoolSizingStrategy(...))` gets DOP-hint-seeded floor behavior plus reason-aware 429 reaction without a bespoke combined class. Each child keeps its own options type (§9.2); `Composite` itself has none beyond the combination rule and child list. |

### 9.4 Who calls `SetMaxSize`, and when

- **Not the strategy itself.** `IPoolSizingStrategy` is a pure decision interface — it returns a
  `PoolSizingDecision`, it never calls `SetMaxSize` directly, exactly as `ISlotSelectionStrategy`
  never acquires a lease itself. This keeps strategies trivially unit-testable (feed in outcomes/
  stats, assert the returned decision) without a live `DataverseUserPool`, the same testability
  argument ADR-0024 made for extracting `DataverseOperationExecutor`.
- **A new, small orchestrator component** (name TBD, e.g. `PoolSizingController`, owned by
  `DataversePool`, one instance per `DataversePool` — not per member) applies decisions:
  - **Event-driven path:** after every operation `DataversePool`/`PooledOrganizationService`
    already observes (reusing ADR-0024's existing instrumentation points so no new interception
    surface is needed), the controller calls the active strategy's `OnOperationCompleted`, and if
    a strategy needs to react immediately (the `Aimd` concurrency-429 case, §2.3), it returns
    (or the controller requests) an immediate decision applied via `member.SetMaxSize(...)` right
    then — this is what makes the "immediate" reaction in §2.3/§2.4 actually immediate rather than
    waiting for the next tick.
  - **Tick-driven path:** the controller also drives a periodic timer (one per `DataversePool`,
    default interval a new `PoolSizingOptions.TickInterval`, e.g. 5–15 s) that calls every
    member's `OnTick` with current `PoolStats`, for the slower signals (DOP-hint re-seed, AIMD's
    cooldown-gated additive increase, Gradient's minRTT re-probe scheduling) that don't need
    per-operation granularity and would be wasteful or noisy to evaluate on every single
    completion.
  - Either path, when a decision changes `TargetMaxSize` from the member's current `MaxSize`, the
    controller calls `member.SetMaxSize(decision.TargetMaxSize)` (the existing ADR-0025
    primitive) and, if present, forwards the pacing targets to a Layer-2 budget/rate component
    (§13 — not built yet, so today this is a no-op until that component exists).
- **Per-member vs. pool-wide strategy instances.** The *type* of strategy is a pool-wide choice
  (one `IPoolSizingStrategy` — or one `Composite` tree — configured per `DataversePool`, mirroring
  how `ISlotSelectionStrategy` is already a pool-wide choice, not per-member), but **state is
  per-member**: `Aimd`'s current step size and cooldown timer, `Gradient`'s per-operation-type
  minRTT baseline, and `DopHint`'s last-seen hint value must each be tracked independently for
  member A vs. member B — two application users can have genuinely different real ceilings
  (§1: 100 vs. 40 were different *paths*, but different *members* on the same path are equally
  plausible, e.g. different licensing/role). The clean way to express this without the interface
  itself becoming stateful-and-shared-incorrectly: either (a) the controller keeps one strategy
  *instance* per member (cheap, stateless-to-construct strategies like these), constructed from a
  shared `IPoolSizingStrategy` *factory*/options, or (b) a single shared strategy instance keyed
  internally by `DataverseUserPool` identity. (a) is simpler and is the recommended default shape;
  it also matches `IPooledResourcePolicy<T>`'s existing pattern of "one policy instance, used
  consistently for one pool" scaled down to "one strategy instance per member within one
  `DataversePool`".
- **Interaction with selection strategy and graceful shrink** — unchanged from the earlier design
  reasoning, now simply reframed as the controller's/strategy's responsibility rather than an
  implicit "Layer 3": a member whose `Aimd`/`Composite`-driven `TargetMaxSize` has shrunk should
  still receive a trickle of traffic once past `CooldownAfterThrottle` so the strategy keeps
  getting fresh `OnOperationCompleted` samples to decide when it's safe to grow again (§9, old
  "Layer 3" / "Interaction with selection strategy" notes below, unchanged in substance).
- **Retry amplification, jitter, and honoring `Retry-After` as a hard floor** are unaffected by
  the strategy-pattern reframing: these remain the responsibility of
  `ExecuteWithThrottleRetryAsync`'s existing retry loop (`docs/adr/0017`/`0019`/`0024`), which
  feeds `PoolSizingOperationOutcome` to the strategy but does not itself consult it for retry
  timing.

Remaining design notes from the original layered sketch, which still hold verbatim under the
strategy-pattern framing (they constrain *what a correct strategy implementation does*, not *how
strategies are plugged in*):

- **Why two actuators, not one (§2.4).** `TargetMaxSize`/permits is the right actuator for the
  concurrency facet only; the two sliding-window facets need the independent pacing fields
  (`MaxRequestsPerWindow`/`MaxExecutionTimePerWindow`) on `PoolSizingDecision`, consumed by a
  separate Layer-2 component this document does not fully design (§13) — cutting concurrency to 1
  does not stop one worker from exceeding a 300 s request-count or execution-time budget.
- **Step size, cooldown, hysteresis.** Given the measured 6m+ `Retry-After` penalties after a
  128-worker concurrency-limit burst (`TODO.md:196-198`), `Aimd`'s decrease should be a large
  single step (e.g. halve), and its additive increase should be gated by a cooldown informed by
  the just-observed `Retry-After`, not a fixed constant.
- **Sampling window vs. the server's 5-minute window.** Any component consuming
  `MaxRequestsPerWindow`/`MaxExecutionTimePerWindow` should track its own local approximation of
  the same ~300 s trailing window so it can preemptively pace before the server-side window
  actually trips, not only react after the first 429 — necessarily imperfect in a multi-replica
  deployment (§11), but strictly better than nothing.
- **Graceful shrink interaction.** Because `SetMaxSize` shrink is permit debt, not instant
  (`docs/adr/0025`), `OnTick`'s `currentStats`/`lastDecision` parameters exist specifically so a
  strategy can read `PoolStats.LeasedCount` rather than assume its last `TargetMaxSize` has fully
  taken effect before deciding whether to shrink further.

### 9.5 Rollout roadmap: observe first, then control

Given §6's conclusion that collection must precede control, the recommended rollout is explicitly
phased, not a single release:

1. **Phase 0 — observe only.** Ship the `Fixed` strategy as the sole default (true today, zero
   behavior change), plus the always-reachable telemetry from §6.2/§6.5 (the `dop_hint` gauge, the
   `ThrottleReasonCode` decomposition once the SOAP-path gap is fixed, `InFlightMember`/
   `InFlightPool`). **No `IPoolSizingStrategy` other than `Fixed` is recommended for production use
   at this phase.** This phase's only goal is collecting enough real data to answer §12.2's
   questions — it ships no control logic at all, consistent with `PLAN-2026-09-30.md`'s own
   ordering of "cheap telemetry" before anything experimental.
2. **Phase 1 — opt-in capture + simulator-validated strategies.** Once Phase 0's telemetry has run
   long enough to answer §12.2 items 1-3 and 5-6, the optional `DiagnosticListener`-based capture
   package (§6.4) can be introduced as a separate, explicitly experimental, opt-in component for
   operators who want `FrontendId`-based attribution — and `DopHint`/`Aimd`/`Gradient`/`Composite`
   strategies can move from "available in the simulator" to "available, but not default, in
   production," gated by the simulator work in §12.1 having scored them against realistic,
   calibrated scenarios (§6.6's calibration use).
3. **Phase 2 — a recommended (not just available) non-`Fixed` default.** Only after live
   experiments (§12.2) and a production burn-in period (operators opting in to a non-`Fixed`
   strategy and the results being monitored) is it appropriate to consider recommending, rather
   than merely offering, a specific strategy as the new default for new adopters — this document
   deliberately does not pick one (§13).

This phasing applies regardless of which strategies are ultimately implemented: it is a statement
about evidence sequencing, not about algorithm preference.

## 10. Configurable settings proposal

Strategy selection and shared/global settings are listed first; strategy-specific tunables live on
each strategy's own options type (§9.2), not a shared bag, mirroring how `PoolOptions` and
`DataverseClientOptions` are already separate today. All names are proposals; none exist in the
codebase today.

### 10.1 Shared / orchestrator settings

| Name | Meaning | Suggested default | Rationale |
|---|---|---|---|
| `PoolSizingOptions.Strategy` | The active `IPoolSizingStrategy` (or `Composite` tree) for a `DataversePool` | `new FixedPoolSizingStrategy()` | Opt-in by construction: adopting automatic sizing means choosing a different strategy, not flipping a separate boolean that could drift out of sync with strategy choice. |
| `PoolSizingOptions.TickInterval` | Period between `OnTick` calls (§9.4) | `10s` | Frequent enough for DOP-hint re-seeds and cooldown-gated increases to feel responsive; infrequent enough to be cheap pool-wide across many members. |
| `PoolSizingOptions.MinSizeFloor` | Absolute floor no strategy's `TargetMaxSize` may go below, enforced by the controller regardless of what the strategy returns | `1` | A last-resort safety net independent of any individual strategy's own floor option, so a misconfigured/buggy strategy can't starve a member to zero. |
| `PoolSizingOptions.MaxSizeCeiling` | Absolute ceiling no strategy's `TargetMaxSize` may exceed, enforced by the controller | Unset (falls back to the member's configured `PoolOptions.MaxSize` × a small constant, e.g. 4×) | Protects against a runaway strategy overshooting into a *different*, invisible-to-the-controller bottleneck (shared infra, downstream DB) — see §13. |

### 10.2 `FixedPoolSizingStrategyOptions`

No settings beyond the `PoolOptions.MaxSize` it already reads — this strategy's entire point is to
reproduce today's behavior with zero new configuration surface.

### 10.3 `DopHintPoolSizingStrategyOptions`

| Name | Meaning | Suggested default | Rationale |
|---|---|---|---|
| `Multiplier` | Scales the observed `RecommendedDegreesOfParallelism` before clamping | `1.0` | Identity by default; exists because §1/§3 show the hint can be conservative relative to the true ceiling in either direction, and an operator with tenant-specific evidence (§12) may want to scale it. |
| `Floor` | Minimum `TargetMaxSize` regardless of hint value | `2` | Hint values of 1 have been anecdotally reported in other contexts for constrained environments; avoid collapsing a member to uselessly small capacity from a single low reading. |
| `Ceiling` | Maximum `TargetMaxSize` regardless of hint value | `PoolOptions.MaxSize` configured initial value, or an explicit cap | Keeps this strategy from being the sole source of truth if the hint is ever anomalously high; still bounded by `PoolSizingOptions.MaxSizeCeiling` as a second line of defense. |
| `ReseedInterval` | How often (via `OnTick`) to re-read the hint and consider updating the floor | Same as `PoolSizingOptions.TickInterval` | §12.2 item 5 flags that whether the hint changes over time at all is still unverified; re-seeding on every tick is cheap and safe either way. |

### 10.4 `AimdPoolSizingStrategyOptions`

| Name | Meaning | Suggested default | Rationale |
|---|---|---|---|
| `MinSize` | This strategy's own floor, independent of `PoolSizingOptions.MinSizeFloor` | `2` | Lets an `Aimd` instance used alone (not in a `Composite`) express its own safety floor without depending on orchestrator-level config. |
| `Ceiling` | This strategy's own ceiling | `PoolOptions.MaxSize` configured initial value, or an explicit cap | Same rationale as `DopHint.Ceiling`. |
| `DecreaseFactor` | Multiplicative decrease ratio applied on a `ConcurrentRequests` 429 | `0.5` | Matches AIMD convention; large single-step reaction given multi-minute `Retry-After` penalties observed (§2.3, §9.3). |
| `IncreaseStep` | Additive increase per cooldown-elapsed `OnTick` with no recent throttle | `1` | Standard AIMD additive increase; conservative re-growth after a costly backoff. |
| `CooldownAfterThrottle` | Minimum time after a `ConcurrentRequests` 429 before any increase is attempted | `max(observed Retry-After, 30s)` | Couples cooldown to the server's own stated recovery time, floored by the existing circuit-breaker cooldown default (30 s) for consistency. |
| `RequestCountBudgetDecreaseFactor` | How much to shrink `MaxRequestsPerWindow` on a `RequestCount` 429 | `0.5` | Independent lever from `DecreaseFactor` (§2.4) — a request-count 429 must not multiplicatively shrink `TargetMaxSize`. |
| `ExecutionTimeBudgetDecreaseFactor` | How much to shrink `MaxExecutionTimePerWindow` on an `ExecutionTime` 429 | `0.5` | Same rationale, independent axis (§2.2). |
| `ProbeEnabled` | Whether to periodically probe above the current steady-state `TargetMaxSize`, BBR-`ProbeBW`-style (§4.4) | `true` | Without probing, a controller that has shrunk once has no way to discover that Dataverse's own backend capacity (§4) has since grown; disableable for operators who would rather stay strictly reactive. |
| `ProbeInterval` | Minimum time with no throttle before attempting a probe | `≥ 10 × CooldownAfterThrottle`, e.g. `5–15 min` | Must be much slower than the reactive decrease path and than any plausible platform scaling timescale (§4.3/§4.4 — timescale itself unconfirmed, so this errs conservative) to avoid the probe itself becoming a source of oscillation. |
| `ProbeStepSize` | How far above current `TargetMaxSize` one probe attempt goes, and for how long | Small, bounded (e.g. `+2`, held for one `OnTick` cycle, reverted immediately unless no throttle observed) | Mistimed probes cost a concurrency-429's multi-minute `Retry-After` (§2.3), far more than a TCP probe's extra queueing delay (§13) — bounded, reversible-by-default step is essential, not a sustained increase. |

### 10.5 `GradientPoolSizingStrategyOptions`

| Name | Meaning | Suggested default | Rationale |
|---|---|---|---|
| `MinConcurrency` | Floor used during a minRTT re-probe window (Envoy's `min_concurrency`, §8) | `2` | Mirrors Envoy's own default shape; needs to be small enough to measure a genuine floor latency, large enough not to starve the member during the probe. |
| `BufferPercent` | The `B = minRTT × BufferPercent` headroom term (§8) | `0.25` (Envoy-typical range) | Allows normal latency variance before the gradient starts pulling the limit down; needs tenant-specific tuning via the simulator (§12.1). |
| `MinRttReprobeInterval` | How often (via `OnTick`) to force a re-probe at `MinConcurrency` | `60s`, with jitter | Prevents a stale, artificially-low baseline from permanently suppressing growth; jitter avoids correlated dips across members/replicas (§11). |
| `OperationNameGrouping` | Whether/how to bucket latency baselines by `OperationName` | Always on; no pool-wide bucket option | §5/§9.3 — a single pool-wide baseline would be dominated by the slowest operation class observed (e.g. `RetrieveAllEntities`-style metadata calls at 12–55 s vs. simple `Retrieve` calls), so this is not meaningfully optional. |

### 10.6 `CompositePoolSizingStrategyOptions`

| Name | Meaning | Suggested default | Rationale |
|---|---|---|---|
| `Children` | Ordered list of child `IPoolSizingStrategy` instances | (required, no default) | The actual composition — e.g. `[DopHint, Aimd]` or `[DopHint, Aimd, Gradient]`. |
| `CombineMaxSize` | How to combine children's `TargetMaxSize`: `Min` (default), `Max`, or `Named(childIndex)` | `Min` | Safety-first default: the most conservative child wins, so adding an optional `Gradient` child to an `[DopHint, Aimd]` composite can only ever tighten, never loosen, the result unless explicitly configured otherwise. |
| `CombineWindowBudgets` | Same combination rule, applied independently to `MaxRequestsPerWindow`/`MaxExecutionTimePerWindow` | `Min` | Consistency with `CombineMaxSize`'s safety-first rationale; these are genuinely independent axes (§2.4) and may be combined differently if a future need arises. |

### 10.7 Trickle/selection-interaction setting (orchestrator-level, not strategy-specific)

| Name | Meaning | Suggested default | Rationale |
|---|---|---|---|
| `PoolSizingOptions.TrickleMinSize` | Minimum `TargetMaxSize` the controller will apply for a member that is throttled but past its cooldown, overriding a strategy that would otherwise return a smaller value | `1`–`2` | Addresses the "double-penalization" interaction (§9.4, ADR-0025's open question #5): a member needs at least a trickle of traffic to keep producing `OnOperationCompleted` samples a strategy needs to decide it's safe to grow again. Lives on the orchestrator, not inside each strategy, because it is a cross-cutting selection-strategy interaction, not a sizing decision any individual `IPoolSizingStrategy` should need to know about. |

### 10.8 Hang-diagnosis / timeout settings (§7)

| Name | Meaning | Suggested default | Rationale |
|---|---|---|---|
| `DataverseClientOptions.MaxRetryCount` | SDK-internal retry budget (already exists, `docs/adr/0016`) | `0` (recommended, not the SDK's `10`) | §7.1/§7.3 step 1 — the precondition for every per-attempt telemetry field in §7.4; without it, `AttemptNumber`/`SdkInternalRetryWait` are meaningless and `DurationTotal` stays an opaque mix of retries and real time. |
| `OperationOptions.PerAttemptTimeout` | New — bounds one network round-trip/attempt | Explicit, operator-set (e.g. a few seconds to low tens of seconds depending on operation class, §6.5's `OperationName` bucketing) | §7.3 step 6 — with `MaxConnectionTimeout`'s 4-minute SDK default (§7.2) otherwise the effective ceiling, an unset per-attempt timeout lets one bad network path or a cold connection silently consume minutes before anything else in the pool notices. |
| `OperationOptions.TotalOperationTimeout` | New — bounds the pool's own retry loop across all attempts for one logical operation | Explicit, operator-set, informed by `PoolOptions.AcquireTimeout` and the caller's own end-to-end budget | §7.3 step 6 — the pool-owned counterpart to `PerAttemptTimeout`; without it, a persistently-retrying operation (once the pool itself owns retries, per `MaxRetryCount=0`) has no enforced upper bound distinct from the per-attempt one. |
| `TelemetryOptions.EnableMsalLogging` | Whether to pass an `ILogger` into `ServiceClient` for MSAL/retry-canary log capture (§7.1, §7.3 step 3) | `false` in production (verbose), `true` during the §9.5 Phase 0 observe-only rollout | MSAL/internal SDK logging is unstructured and can be noisy/verbose at the level needed to see retry/token messages; recommended as a temporary diagnostic toggle during initial rollout, not a permanent always-on setting. |

## 11. Multi-replica considerations

Dataverse's limits are per (application user × web server), evaluated by Dataverse centrally —
**not** per DataversePool process. When several application instances (replicas) share the same
pool of application users, each replica's in-process controller sees only its own share of traffic
against a given member and has no visibility into the other replicas' concurrent load against that
same member **[Inferred from architecture]**. Two broad strategies:

1. **Coordination-free (recommended as the first increment).** Each replica runs its own
   `Aimd` strategy instance (§9.3) independently, per member. Chiu & Jain's 1989 result
   ([paper](https://www.cse.wustl.edu/~jain/papers/cong_av.htm)) is directly relevant here: AIMD is
   proven to converge multiple *independent, uncoordinated* agents sharing one bottleneck toward a
   fair and efficient allocation, provided the shared feedback signal (here: Dataverse's 429s) is
   visible to each agent individually, which it is (each replica gets its own 429s for its own
   share of traffic against the shared per-user-per-server budget). This gives a theoretically
   grounded reason to expect convergent, fair behavior **without** building shared state — at the
   cost of slower convergence than a coordinated design, and no guarantee that any single replica's
   *local* view of "safe concurrency" matches the true remaining shared budget at any instant,
   which argues for conservative step sizes (§9.3, §10.4) specifically in multi-replica deployments.
2. **Shared-state (future enhancement, not in scope now).** A distributed counter/cache (e.g.
   Redis) tracking aggregate in-flight count and a shared rolling request/cost budget per member
    across replicas would let the pacing budgets a strategy outputs (`MaxRequestsPerWindow`/
    `MaxExecutionTimePerWindow`, §9.2, which are fundamentally about a *shared* 5-minute window) be
    tracked exactly rather than approximated per-replica. This is a strictly more complex,
    additional dependency DataversePool does not have today (it is explicitly process-local per
    ADR-0024: *"DataversePool does not coordinate rate or throttle state across processes"*), and
    should only be pursued if the coordination-free approach is measured to be insufficient (§12).

A practical middle ground worth prototyping: each replica reduces its own strategy's *local*
budget outputs (`MaxRequestsPerWindow`, `MaxExecutionTimePerWindow`) by a configured or
auto-detected `replicaCount` divisor, so that even without shared state, the aggregate *intended*
usage across replicas stays under the single-tenant server-side budget in expectation — this is a
static, conservative approximation, not true coordination, and degrades gracefully (just leaves
headroom unused) rather than failing unsafely if the replica count estimate is wrong.

## 12. Simulation & validation plan

### 12.1 Deterministic discrete-event simulator (build before any live controller)

A simulator should model, independently of any real Dataverse connection:

- `S` web servers, each with its own independent three-facet limit state (concurrent count,
  300 s-sliding request count, 300 s-sliding execution-time sum), matching §2's model exactly,
  including each server's own `Retry-After` growth-on-repeat-offense behavior **[MS]**-documented
  qualitatively, calibrated to this repo's measured durations (6–7 min after a 128-worker/100-limit
  burst, `TODO.md:196-198`) where a quantitative curve is needed.
- A load-balancer routing model for the affinity-off case: start with uniform-random (§3.3's
  conservative assumption), but make it pluggable so round-robin and least-connections-at-the-LB
  variants can be compared — since the real Microsoft load-balancing algorithm is not documented,
  the simulator's job is to show how sensitive the "safe aggregate concurrency" conclusion is to
  that choice, not to assert one is correct.
- Per-operation-type cost distributions (duration, server-side execution-time charge) drawn from
  this repo's own measured percentiles where available (e.g., the metadata-query p50/p95 numbers
  in `TODO.md:183-186`) and parameterized/configurable otherwise.
- **A time-varying per-server limit/capacity model (§4), to specifically test the autoscaling
  interaction** — since the real trigger/timescale is undocumented (§4.1), the simulator should
  treat this as a parameterized hypothesis, not a fixed fact: configurable scenarios where `S` or
  the per-server concurrency limit `L` step up after sustained near-ceiling load persists for a
  configurable duration (and, symmetrically, step down after a sustained lull), so candidate
  strategies — especially an `Aimd`/`Composite` with the BBR-style probing phase from §4.4 — can
  be scored specifically on: how quickly they discover a capacity increase (lost-throughput area
  under the curve between the capacity change and the controller catching up), whether they
  oscillate against a moving ceiling (§4.3), and whether a purely reactive (no-probing) baseline
  measurably underperforms a probing variant under this scenario — this is the direct, pre-live
  way to test the "self-fulfilling under-utilization" and "fighting controllers" risks before
  spending any live-tenant experiment budget on them.
- Candidate controllers (AIMD, Gradient/Gradient2, PID, the layered design in §9, and an
  explicit BBR-`ProbeBW`-style variant per §4.4) run against the
  simulated environment across many random seeds, scored on: time-to-converge after a step change
  in true capacity, steady-state throughput achieved vs. true achievable throughput, 429 rate,
  oscillation amplitude, and fairness across members/replicas (Chiu-Jain-style fairness index).
- Multi-replica scenarios (§11): N independent controller instances against the shared simulated
  limit state, to directly measure whether coordination-free AIMD in fact converges fairly at
  Dataverse's specific feedback latencies (429 + multi-minute `Retry-After`), rather than relying
  only on the general theoretical result.

This is pure software (no live tenant dependency) and should be the first deliverable of any
follow-up implementation work, consistent with ADR-0025's own listed open item #6.

### 12.2 Live experiments needed (ranked by how directly they test an open hypothesis)

1. **Measure per-user aggregate concurrency with affinity off vs. the DOP hint**, on the same
   tenant/operation path already characterized pinned-affinity (the 100-limit read path,
   `TODO.md:192-198`): repeat the same burst ladder *without* forcing a shared `ARRAffinity`
   cookie, and compare the aggregate accepted concurrency against (a) the pinned single-server
   number times an estimated server count, and (b) the live `RecommendedDegreesOfParallelism`
   value observed during the same run. This is the single most direct test of the §3 hypothesis
   and is not yet done — the existing multi-identity tests varied *user* count with affinity
   pinned or via normal pool routing, not *server distribution for one user* with affinity
   deliberately off.
2. **Identify the server count `S` for the test tenant.** Not documented as directly observable
   by Microsoft **[MS absence — confirmed by absence in all fetched pages]**. Indirect strategies
   to test: (a) repeatedly connect with affinity *on* (pinned) and inspect whether the
   `ARRAffinity`/routing cookie value changes across many fresh connections, giving a lower bound
   on distinct servers seen; (b) drive sustained concurrent load with affinity off and look for a
   concurrency-429 ceiling that is a clean small-integer multiple of the known single-server
   ceiling (100), which would be strong circumstantial evidence for a specific `S`; (c) ask
   Microsoft support directly, since the docs state server count is a managed-service factor
   partly driven by licensed-user count, which support can presumably state outright for a given
   tenant.
3. **Record the 429 error-code distribution under realistic mixed load**, not just synthetic
   bursts — run the existing burst-ladder-style tests but classify every rejection by its specific
   error code (`0x80072322`/`0x80072321`/`0x80072326`) rather than a generic "throttled" boolean,
   to confirm which facet actually bites first for realistic (not adversarial) workloads, and
   whether the derived execution-time bound (§2.2, `c_max ≈ 4`) is anywhere close to being the
   binding constraint in practice, or whether the concurrency facet always dominates in this
   tenant's observed regime.
4. **Verify the SOAP-path 429 detection gap is fixed** (`REVIEW-2026-09-30.md:201-223`) before
   trusting *any* throttle-driven signal from that transport — currently it reads zero and would
   silently blind a controller running on SOAP traffic.
5. **Confirm `RecommendedDegreesOfParallelism` actually varies** across environments/times of day
   as Microsoft's "resources... might vary over time" wording implies, by sampling it over an
   extended period against the test tenant, rather than assuming it is static — this affects
   whether `DopHintPoolSizingStrategyOptions.ReseedInterval` (§10.3) needs frequent re-seeding or
   only a one-time initial read.
6. **Correlate sustained pressure with observable capacity changes (§4).** Run a long (multi-hour),
   deliberately sustained, moderately-demanding load (well below the known concurrency ceiling, to
   avoid 429s, but high enough to keep queueing latency elevated) and watch, over the course of
   the run, whether (a) `RecommendedDegreesOfParallelism` changes, (b) the concurrency-429 ceiling
   (measured by periodic brief bursts above the current `MaxSize`) changes, and (c) per-operation
   latency, after any initial ramp, trends back down rather than staying elevated — any of these
   would be circumstantial evidence of a platform-side capacity response to sustained demand
   (§4.1's hypothesized mechanism) and would additionally give an empirical estimate of the
   provisioning-lag timescale §4.1 could not find documented.
7. **Test whether a per-request header's distinct-value count is a usable proxy for server
   count.** Collect `x-ms-service-request-id` (and any other per-response headers) across a large
   number of requests with affinity off and check whether any of them reveal a bounded, repeating
   set of distinct values (which would suggest a stable node identifier hiding in a
   support-correlation-shaped header) versus fully unique values per request (which would confirm
   §4.1's finding that no such proxy is currently known) — cheap to run alongside experiment 1,
   and only ever against a non-production tenant given the uncertainty about what these headers
   are intended to reveal.
8. **Deliberately induce a controller/platform fight in a controlled test**, if experiment 6
   finds evidence of platform-side scaling: run a workload shaped specifically to test §4.3's
   oscillation concern — sustained pressure just below the concurrency ceiling, held for long
   enough to plausibly trigger a platform scale-out (per experiment 6's measured timescale), then
   observe whether an `Aimd` strategy's conservative cooldown/re-probe schedule (§9.3, §10.4)
   actually prevents it from fighting the platform (shrinking right as more capacity arrives) or
   needs retuning; use the simulator (§12.1) first to rehearse scenarios cheaply before running
   this against a live tenant.

## 13. Open questions / risks

- **Execution-time bound caveat (§2.2).** The `c_max ≈ 4` sustained-concurrency derivation assumes
  server-side execution time ≈ client-observed duration. This is unverified and plausibly
  pessimistic; needs a live experiment that compares cumulative execution-time-limit 429 onset
  against cumulative client-observed busy-time to calibrate the real ratio.
- **Server count `S` is unobservable through documented means.** Every formula in §3.3 and §9/§11
  that references `S` is therefore a planning aid, not a value a strategy can read at runtime;
  every strategy in §9.3 deliberately avoids needing `S` directly (each reacts to observed
  429s/latency per member, not to a computed `S × L` target), but any future enhancement that
  *does* want an explicit aggregate target needs §12.2 item 2 resolved first.
- **Load-balancer assignment model is assumed, not confirmed.** §3.3's uniform-random assumption
  is a deliberately conservative default for risk analysis; if Microsoft's actual load balancer is
  closer to round-robin or least-connections, real variance is lower and more aggregate
  concurrency is safely achievable than the pure-random model predicts. The simulator (§12.1)
  should be used to bound how much this matters before hardcoding any specific "safety margin"
  constant.
- **Whether Dataverse's backend actually autoscales in a way relevant to this controller is
  itself unconfirmed (§4).** Microsoft documents that allocated server "number and capabilities...
  might vary over time" **[MS]**, but not a trigger, timescale, or mechanism **[MS absence]** —
  everything in §4.3/§4.4 (coupled-controller risk, the recommended probing/exploration
  mitigation) is built to be robust to this uncertainty rather than to assume a specific
  autoscaling model, but if §12.2 experiment 6 finds *no* observable capacity response to
  sustained load within a practical testing window, the BBR-style probing design in §4.4/§9.3
  still does no harm (it degrades to a slightly more exploratory AIMD) but its primary
  justification would be weaker than currently argued, and simpler designs become relatively more
  attractive.
- **The probing/exploration step (§4.4, BBR-style) introduces its own tuning risk.** A probe that
  is too large or too frequent reintroduces exactly the concurrency-429 cost §2.3/§8 warns is
  expensive (multi-minute `Retry-After`); a probe that is too small or too infrequent fails to
  discover a raised ceiling within a useful time. Unlike BBR's networking context, where probe
  cost is a small amount of extra queueing delay, a mistimed probe here can cost minutes of
  `Retry-After` lockout — this asymmetry (Dataverse's penalty for "loss" is far harsher than TCP's)
  means the probe step size/interval (`AimdPoolSizingStrategyOptions.ProbeInterval`/
  `ProbeStepSize`, §10.4) needs conservative defaults and simulator-driven tuning (§12.1) before
  any live use, not values carried over from networking contexts.
- **Entitlement limits and non-Dataverse 429/503 sources are out of scope for the controller
  described here**, but a production rollout must ensure the error-classification layer correctly
  excludes them (§2) — an entitlement-exhaustion 429 or an upstream infra 503 reacted to as if it
  were a concurrency-facet signal would cause the controller to shrink a perfectly healthy member
  for a problem `SetMaxSize` cannot fix.
- **Measured numbers (100, 40, 52-default) are tenant- and operation-path-specific** and must not
  be hardcoded as cross-environment defaults (§1, §10); the whole point of this design is to let
  per-member, per-operation-class *observation* replace any single assumed constant.
- **Layer 2 (rate/cost budget) has no existing DataversePool primitive to build on** — unlike
  the concurrency permits `IPoolSizingStrategy` actuates through (ADR-0025) and the throttle
  detection chassis it consumes events from (ADR-0008), there is currently no token-bucket or
  cost-weighted budget component in the codebase to apply a strategy's
  `MaxRequestsPerWindow`/`MaxExecutionTimePerWindow` output to; this is the largest net-new
  implementation surface implied by this document, not an extension of existing code — until it
  exists, those two `PoolSizingDecision` fields have no consumer.
- **Fairness vs. starvation trade-off (§9.4, §11)** between "skip a throttled member entirely" and
  "trickle traffic to it for recovery/sampling" needs empirical tuning per the simulator, not just
  reasoning; too generous a trickle during a real overload re-triggers the same 429 the cooldown
  was meant to avoid.
- **Which strategy should ship as the actual default is still open.** `Fixed` is the only safe
  *initial-release* default (§9.3 — zero behavior change, fully opt-in), but once validated,
  whether `DopHint`, `Aimd`, or a `Composite` of both becomes the recommended (not just available)
  default for new adopters needs the live experiments in §12.2, not armchair reasoning.
- **`Composite` ordering/precedence rules are unresolved beyond the basic min/max/named options
  (§9.3, §10.6).** It is not yet clear whether `min`-of-children is always correct once more than
  two strategies are combined (e.g. a `Gradient` child's transient latency-driven dip combined
  with an `Aimd` child's independent 429-driven dip via plain `min` could compound more
  conservatively than intended) — this needs simulator coverage (§12.1) with multi-strategy
  `Composite` configurations before being recommended as a default.
- **`ARRAffinity` token stability is unconfirmed for Dataverse specifically (§6.3).** Whether the
  value rotates on a schedule, on reconnect, or only on an actual backend-instance change is not
  documented by Microsoft for Dataverse's web tier; every frontend-count/scale-out-detection use
  in §6.6 is only as reliable as this unconfirmed assumption, and §12.2 item 7 is the direct way
  to test it before relying on it for anything beyond a rough approximation.
- **Whether the pinned SDK version emits any SDK-specific `Activity`/`DiagnosticSource` events
  beyond the built-in `System.Net.Http` ones, and which connection-limit mechanism
  (`ServicePointManager` vs. `SocketsHttpHandler`) applies to the SDK's internal HTTP client on the
  target framework in use, are both unconfirmed (§7.2, §7.3 step 5)** — neither changes this
  document's recommendation to build on the `System.Net.Http` activity/`DiagnosticListener` route
  first (§6.4, §7.3), but both should be checked directly against the pinned SDK assembly before
  assuming either behavior.
- **`ServiceClient.LastException`/`LastError` cannot retroactively reveal retry history (§7.1)** —
  confirming whether any *other* public member exposes a structured per-call retry count would
  remove the need for the log-pattern-matching canary in §7.3 step 3, but none was found during
  this research; worth re-checking against future SDK versions before assuming it remains absent.

## 14. References

**Microsoft documentation (primary sources):**
- [Service protection API limits (Microsoft Dataverse)](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits) — three facets, default values, error codes, Retry-After behavior, per-web-server enforcement, debugging-only rate-limit headers.
- [Send Parallel Requests to Dataverse](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests) — `x-ms-dop-hint` semantics, server affinity and its effect on service-protection limits, throughput-maximization guidance, connection tuning.
- [API limits overview (Microsoft Dataverse) — entitlement limits](https://learn.microsoft.com/en-us/power-apps/maker/data-platform/api-limits-overview#entitlement-limits) — the separate, licensing-based 24-hour limit system.
- [`ServiceClient.RecommendedDegreesOfParallelism` property reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.powerplatform.dataverse.client.serviceclient.recommendeddegreesofparallelism?view=dataverse-sdk-latest)
- [`ServiceClient.EnableAffinityCookie` property reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.powerplatform.dataverse.client.serviceclient.enableaffinitycookie)
- [`System.Threading.RateLimiting.ConcurrencyLimiter` class reference](https://learn.microsoft.com/en-us/dotnet/api/system.threading.ratelimiting.concurrencylimiter)
- [Polly rate limiter resilience strategy](https://www.pollydocs.org/strategies/rate-limiter.html) / [Polly/docs/strategies/rate-limiter.md](https://github.com/App-vNext/Polly/blob/main/docs/strategies/rate-limiter.md)
- [Service request tracing (Dynamics 365 Finance & Operations)](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/service-request-tracing) — general Microsoft documentation of `x-ms-client-request-id`-style correlation headers as log-correlation, not routing/topology, identifiers; cited in §6.3 as corroboration, not Dataverse-specific.
- [`ServiceClient.MaxConnectionTimeout` property reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.powerplatform.dataverse.client.serviceclient.maxconnectiontimeout?view=dataverse-sdk-latest) — static, class-level, 4-minute SDK default, cited in §7.2.
- [`ServiceClient.RetryPauseTime` property reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.powerplatform.dataverse.client.serviceclient.retrypausetime?view=dataverse-sdk-latest) — cited in §7.1 alongside `MaxRetryCount`/`UseExponentialRetryDelayForConcurrencyThrottle` (already cited via `docs/adr/0016` below).

**Diagnosing slow/hanging calls (§7):**
- `microsoft/PowerPlatform-DataverseServiceClient`, [`ServiceClient.cs` source](https://github.com/microsoft/PowerPlatform-DataverseServiceClient/blob/master/src/GeneralTools/DataverseClient/Client/ServiceClient.cs) — the SDK's own retry-loop implementation underlying §7.1's claims about `MaxRetryCount`/`RetryPauseTime` governing both transient and 429 retries.
- `microsoft/PowerPlatform-DataverseServiceClient`, [issue #511, "ServiceClient does not respect max retries and may lead to infinite retries"](https://github.com/microsoft/PowerPlatform-DataverseServiceClient/issues/511) — the pre-v1.2.7 defect where the internal retry loop's stop condition checked retry count and cancellation independently rather than on either, cited in §7.1 as the reason to re-verify cancellation/timeout behavior whenever the pinned SDK version changes.
- `microsoft/PowerPlatform-DataverseServiceClient`, [issue #442, "Retrying requests"](https://github.com/microsoft/PowerPlatform-DataverseServiceClient/issues/442) — community discussion of the SDK's retry behavior, cited as corroborating context for §7.1.

**Signal collection & telemetry (§6):**
- [Azure App Service — session affinity / `ARRAffinity` cookie discussion (Microsoft Q&A)](https://learn.microsoft.com/en-us/answers/questions/91664/app-service-multi-instances-application-inproc-ses) — general Azure App Service-family documentation describing the `ARRAffinity` cookie as an opaque, encrypted backend-instance routing token; **not Dataverse-specific**, cited in §6.3 only for the general mechanism, with Dataverse-specific behavior (sent even when unused for routing) separately confirmed by this repository's own review.
- Engineering Fundamentals Playbook, [Correlation IDs](https://microsoft.github.io/code-with-engineering-playbook/observability/correlation-id/) — general guidance on client-generated correlation identifiers, cited in §6.3 to corroborate that `x-ms-client-request-id`-style headers are request-correlation, not server-identity, signals.

**Platform autoscaling / elasticity (§4):**
- [Send Parallel Requests to Dataverse](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests) — "the number and capabilities of the servers allocated might vary over time" (same primary source as above, re-cited here for the elasticity claim specifically).
- [Azure Monitor autoscale overview](https://learn.microsoft.com/en-us/azure/azure-monitor/autoscale/autoscale-overview) — cited only as a general Azure PaaS autoscaling pattern reference (trigger metrics, scale-out lag), explicitly **not** a statement about Dataverse's own implementation (§4.1).
- [Microsoft Fabric / Power BI Premium capacity autoscale](https://learn.microsoft.com/en-us/fabric/enterprise/autoscale) — documented trigger behavior and the 24-hour minimum active period for a *different* Microsoft capacity product, cited in §4.1 to contrast with the absence of equivalent published detail for Dataverse's web tier.

**Prior art on adaptive concurrency:**
- Netflix, [`concurrency-limits`](https://github.com/Netflix/concurrency-limits) — Vegas/Gradient/Gradient2 delay-based limiters, AIMD loss-based limiter, Little's Law framing.
- Envoy proxy, [adaptive concurrency filter documentation](https://www.envoyproxy.io/docs/envoy/latest/configuration/http/http_filters/adaptive_concurrency_filter) — Gradient controller, minRTT re-probing, jitter rationale, headroom term.
- Cardwell, Cheng, Gunn, Hassas Yeganeh, Jacobson, ["BBR: Congestion-Based Congestion Control"](https://queue.acm.org/detail.cfm?id=3022184), ACM Queue 14(5), 2016.
- Brakmo & Peterson, ["TCP Vegas: End to End Congestion Avoidance on a Global Internet"](https://web.stanford.edu/class/cs244/papers/tcp_vegas.pdf), IEEE JSAC 13(8), 1995.
- Nichols & Jacobson, CoDel — [RFC 8289](https://www.rfc-editor.org/rfc/rfc8289.html); [Wikipedia summary](https://en.wikipedia.org/wiki/CoDel).
- Chiu & Jain, ["Analysis of the Increase and Decrease Algorithms for Congestion Avoidance in Computer Networks"](https://www.cse.wustl.edu/~jain/papers/cong_av.htm), Computer Networks and ISDN Systems 17(1), 1989 — AIMD fairness/convergence proof.
- Gunther, Universal Scalability Law — [perfdynamics.com overview](http://www.perfdynamics.com/Manifesto/USLscalability.html); formal treatment in Gunther, "Guerrilla Capacity Planning" (Springer, 2007); general form [arXiv:0808.1431](https://arxiv.org/pdf/0808.1431v1).
- Little's Law (`L = λW`) as framed in the Netflix `concurrency-limits` README (above) for concurrency-limit estimation.

**This repository (internal context, not re-derived, cited for traceability):**
- `docs/adr/0025-runtime-adjustable-pool-size.md` — runtime `SetMaxSize`, graceful/permit-debt shrink, open questions this document answers.
- `docs/adr/0008-throttle-detection-429-not-headers.md` — why 429/exception is the only reachable throttle signal through `ServiceClient`, `Retry-After` parsing, per-member throttle state.
- `docs/adr/0016-affinity-cookie-forced-off-retry-knobs-exposed.md` — forced `EnableAffinityCookie=false`, `DataverseClientOptions`, the `x-ms-dop-hint` reachability addendum.
- `docs/adr/0024-operation-level-metrics-on-instrumented-execution-paths.md` — existing OpenTelemetry-shaped operation metrics (acquire/operation/total duration, attempts/retries/retry_after, outcome tagging) this design reuses.
- `REVIEW-2026-09-30.md:201-223, :305-310, :396-403` — the SOAP-path 429-detection gap, confirmation that `RecommendedDegreesOfParallelism` is already reachable and unwired, and the proposed `dop_hint`/throttle gauges.
- `REVIEW-2026-09-30.md:295-330, :362-371, :380-439` — the full header-reachability survey underpinning §6: confirmed-present vs. confirmed-absent strings in the SDK assemblies, the `ARRAffinity`/`EnableAffinityCookie=false` interaction, and the three ranked response-capture mechanisms.
- `PLAN-2026-09-30.md:68-76, :91-96` — this repository's own actual phased plan ("cheap telemetry" before the "optional response observer"), the source for §6.1/§9.5's "observe only first" phasing and the concrete gauges (`dataversepool.member.dop_hint`, `throttled`, `throttled_until_seconds`, `breaker_state`) §6.5's schema aligns with.
- `tests/ConnectionPool.Dataverse.Tests/LivePinnedWebApiConcurrencyTests.cs:86-97, 452-457` — existing live test that already captures and relies on the `ARRAffinity` cookie value via raw `HttpClient`, cited in §6.3 as working evidence that the cookie is observable in practice against a live tenant.
- `TODO.md:160-236` — live-tenant measurements: 100-concurrent-request ceiling on a pinned server (read path), two-identity aggregate concurrency exceeding the single-identity ceiling, 40-concurrent ceiling on an insert path, 1.45x two-user insert-throughput result.
