# ADR-0026: Opt-in automatic pool sizing via `IPoolSizingStrategy`

## Status
Accepted. `Fixed` (default), `DopHint`, `Aimd`, `Gradient` and `Composite` are implemented. A
per-member request-rate pacer is implemented. `Aimd` probing is implemented (opt-in). The execution-time pacer is implemented too, but never exercised against a real
ExecutionTime throttle (none could be reproduced).

## Context

ADR-0025 made `DataverseUserPool.MaxSize` adjustable at runtime. `docs/research/autoscaling.md` §9
designed a pluggable decision interface on top of it. Dataverse has three independent service
protection limits and only one of them is a concurrency problem, so a controller must know *which*
limit fired before it shrinks anything.

## Decision

- `IPoolSizingStrategy` is a pure decision interface, like `ISlotSelectionStrategy`. It returns a
  `PoolSizingDecision`; it never calls `SetMaxSize`.
- `DataversePool` takes an optional `PoolSizingOptions`. A `PoolSizingController` is created only
  when the strategy is not `FixedPoolSizingStrategy`, so the default path has no timer, no sink and
  no per-call cost.
- The controller feeds the strategy every completed attempt and a periodic tick, serializes calls
  per member, clamps each decision to `MinSizeFloor` / `MaxSizeCeiling` (default 4x configured), and
  calls `SetMaxSize` only on change. Shrinking stays graceful (ADR-0025). A throwing strategy on
  tick is swallowed.
- Attempts reach the controller through an internal `IOperationOutcomeSink` on the shared
  `DataverseOperationExecutor`, from both `ExecuteWithThrottleRetryAsync` and
  `PooledOrganizationService` over a `DataversePool`. The sink cannot alter the caller's result or
  exception. `DataverseUserPool` facades and a single `DataverseUserPool` are not sized.
- The throttle reason is decoded by `DataverseThrottleDetector.TryGetThrottleReason`. SOAP faults
  are exact (0x80072322 request count, 0x80072321 execution time, 0x80072326 concurrent requests).
  A Web API 429 is decoded from the message and response body text. **Verified live (2026-10-03):**
  through the SDK the `HttpOperationException` message is generic (`TooManyRequests`) and the body
  is `{"error":{"code":"0x80072326","message":"Number of concurrent requests exceeded the limit of 100."}}`,
  decoded as `ConcurrentRequests` (`LiveWebApiThrottleReasonTests`). Only the concurrency facet has
  been seen on the Web API; the other two bodies are assumed to follow the same shape. Anything
  unrecognized is `Unknown`.

### Deviations from the §9.2 sketch

- `GetInitialSize(member, configuredMaxSize)`: a member does not expose its `PoolOptions`.
- `OnOperationCompleted` returns `PoolSizingDecision?` so a reaction can be immediate.
- Request-count pacing is implemented: `PoolSizingDecision.MaxRequestsPerWindow`/`SampleWindow` drive a
  per-member sliding-window limiter (`MemberRequestPacer`). The executor calls the sink's
  `BeforeAttemptAsync` after the lease is held and before the attempt runs; the wait is cancellable
  and excluded from call latency. `null` means unlimited, so a pacing strategy repeats its limit in
  every decision; `Composite` takes the tightest child limit. `MaxExecutionTimePerWindow` works the same way: the pacer sums the durations of finished attempts
  in the window and delays new ones while the sum is at the limit.
- `TrickleMinSize` is not implemented as a separate option: `PoolSizingOptions.MinSizeFloor` and
  `Aimd.MinSize` already keep a throttled member at a trickle so it still produces samples.

### Strategy behaviour

- `Aimd` halves only on `ConcurrentRequests`. A `DecreaseHoldoff` (10 s) ignores the burst of
  in-flight throttles that follow one congestion event, otherwise one event would collapse the
  size. Regrowth is +1 per tick after `max(CooldownAfterThrottle, Retry-After)`. Request-count,
  execution-time and unknown throttles never change the size. A request-count throttle instead caps
  the request rate at 80% of what was sent in the last 5 min (pacer), holds it for the cooldown,
  then relaxes it ~10% per tick and removes it once it passes twice the throttled volume. An
  execution-time throttle caps busy time per window the same way (80% of the last 5 min).
- `Aimd` probing (`ProbeInterval`, default off): after the interval with no throttle, while the member
  reached its size since the last tick, it tries `ProbeStep` more for `ProbeDuration`. If the probe
  size was actually reached without a concurrency throttle the ceiling moves up (never past
  `MaxProbedSize`, default 2x configured); if not reached it reverts; a concurrency throttle reverts
  to the proven size at once, without halving.
- `Gradient` samples successful latency per operation name; each tick it computes
  `minRtt / meanRtt` (clamped to [0.5, 1]), takes the minimum across operations, and moves the size
  toward `gradient * size + sqrt(size)` with smoothing. It never grows an under-used member, and
  re-learns `minRtt` every 5 min. Latency is weak evidence on Dataverse, so pair it with `Aimd`.
- `DopHint` follows `RecommendedDegreesOfParallelism` on tick and has no opinion until a hint exists.
- `Composite` keeps each child's latest vote per member and combines with `Min` (default) or `Max`,
  so a silent child does not lose its earlier, lower vote.

## Consequences

- Strictly opt-in; existing behaviour is unchanged without options.
- Per-user throttling means each member is sized from its own throttles only.
- Unit-tested with a fake clock; no live test, since ExecutionTime throttling has never been
  reproduced and concurrency throttling needs sustained load.
- Recommended for production only after observing it with the ADR-0024 metrics on a non-critical
  tenant.

## Addendum: experimental response observer

`PoolSizingOptions.ObserveResponses` (default off) reads the budget headers Dataverse sends on every
response (`x-ms-ratelimit-burst-remaining-xrm-requests`, `x-ms-ratelimit-time-remaining-xrm-requests`,
`x-ms-dop-hint`, `x-ms-service-request-id`), for both SOAP and Web API.

- A `DiagnosticListener` on `HttpHandlerDiagnosticListener` sees each response. It is attributed to a
  member through an `AsyncLocal` the executor sets only around the attempt. It lives in the core
  assembly because correlation needs that executor hook.
- The listener filter must admit `System.Net.Http.HttpRequestOut` as well as `.Stop`; the runtime
  checks the activity name before emitting `Stop`.
- One process-wide, ref-counted listener serves all pools, so a response is delivered once even with
  several observing pools. `ObservedAt` is stamped by the receiving controller's clock.
- `DataversePool.GetResponseBudget(member)` returns the latest `ResponseBudget`.
- If burst or time remaining falls to `LowBudgetFraction` (default 5 %) of the largest value seen,
  the member's pacer holds attempts for `LowBudgetBackoff` (default 5 s). It fails open afterwards.
- Verified live (`LiveResponseBudgetTests`). Attribution under 2,200 concurrent calls across 50 members
  is verified locally (`ResponseObservationLoadTests`).
- Live, two app users, 300 concurrent SOAP and Web API calls (`TwoMembers_UnderLoad_EachGetsItsOwnBudget`):
  each member's burst budget fell from 8000 to 7845 and 7850, which matches about 150 calls each, so
  budgets are per user and attributed correctly.
- Listener cost, measured on loopback (median of 5 rounds): about 139 us/call without the listener,
  160 us with it unattributed and 154 us attributed, so roughly 15-20 us per call. Real Dataverse calls
  take tens of milliseconds, so this is negligible. Numbers are parsed independently of the
  current culture: `1,199.97` (as sent) and `1.199,97` are accepted, ambiguous values such as `1,5` are
  ignored rather than guessed.

## Addendum: live results for request-count throttling and the low-budget hold

`LiveBudgetDrainTests` (opt-in, `DVPOOL_IT_DRAIN=1`) spends the second identity's request budget.

- A real Web API request-count 429 was reproduced: `0x80072322`, "Number of requests exceeded the limit
  of 8000 over time window of 300 seconds." The decoder returns `RequestCount`. About 8000 cheap
  `WhoAmI()` calls at 16 concurrent took under a minute. The limit is 8000 per 300 s per user, which is
  also the burst budget the headers report.
- The low-budget hold fired live: with the observer on, after burst remaining fell to 5 % of the maximum
  seen (about 400 of 8000), all 55 following calls were held back by at least 2.5 s (backoff 3 s). The
  run stopped at 330 remaining and never reached a throttle.
- Still unseen live: an `ExecutionTime` throttle, so that pacer remains unit-tested only.

### Addendum: backend node and budget
- `X-Source` is sent as two header values; the first is constant, the last identifies the node. `ResponseBudget.ServerId`
  is the last value (then the last `|` part). `GetServerNodeStats` gives per-node response counts and burst range;
  the soak report has a "Backend nodes" section and `a_nodes`/`b_nodes` CSV columns.
- A 4-minute soak saw about 15 nodes per identity, all with the same burst range (about 6165-7337).
- Pinned drain test (`LiveNodeBudgetScopeTests`, opt-in `DVPOOL_IT_NODESCOPE=1`): the affinity cookie did not hold under
  16 concurrent workers (about 87% of calls reached other nodes), yet the `RequestCount` 429 came after 7677 calls
  although the pinned node answered only about 1000. Calls across nodes exhaust one budget: per user in this sandbox.
- Microsoft documents the limits as enforced per user per web server ("each web server ... enforces these limits
  independently"), with defaults of 6000 requests / 20 min execution time / 52+ concurrent. The burst header is
  documented as "remaining requests for this connection", for debugging only, and resetting when the server changes.
  `X-Source` is undocumented, so it may not be a web server. Do not assume more nodes means more budget.
- `ARRAffinity` identifies the node, but only when the client sends it back; the pool disables affinity cookies, so
  without one each response got a fresh value. With a pinned cookie, a series stays on one `X-Source` node.
- Three pinned series run one after another gave burst remaining 7959..7940, 7939..7920, 7919..7900: one
  counter, not one per node. Jumps of 500-700 under concurrent load are likely delayed synchronisation of a shared
  counter. Whether more front-end nodes give more budget in production is open; the logged node id lets a soak
  run answer it.
