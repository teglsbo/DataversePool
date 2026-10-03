# ADR-0026: Opt-in automatic pool sizing via `IPoolSizingStrategy`

## Status
Accepted. `Fixed` (default), `DopHint`, `Aimd`, `Gradient` and `Composite` are implemented. A
per-member request-rate pacer is implemented. `Aimd` probing is implemented (opt-in). The execution-time pacer is **not**.

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
  A Web API 429 is decoded best-effort from the message and body text; this is **not verified
  live**, and anything unrecognized is `Unknown`.

### Deviations from the §9.2 sketch

- `GetInitialSize(member, configuredMaxSize)`: a member does not expose its `PoolOptions`.
- `OnOperationCompleted` returns `PoolSizingDecision?` so a reaction can be immediate.
- Request-count pacing is implemented: `PoolSizingDecision.MaxRequestsPerWindow`/`SampleWindow` drive a
  per-member sliding-window limiter (`MemberRequestPacer`). The executor calls the sink's
  `BeforeAttemptAsync` after the lease is held and before the attempt runs; the wait is cancellable
  and excluded from call latency. `null` means unlimited, so a pacing strategy repeats its limit in
  every decision; `Composite` takes the tightest child limit. `MaxExecutionTimePerWindow` is ignored.
- `TrickleMinSize` is not implemented.

### Strategy behaviour

- `Aimd` halves only on `ConcurrentRequests`. A `DecreaseHoldoff` (10 s) ignores the burst of
  in-flight throttles that follow one congestion event, otherwise one event would collapse the
  size. Regrowth is +1 per tick after `max(CooldownAfterThrottle, Retry-After)`. Request-count,
  execution-time and unknown throttles never change the size. A request-count throttle instead caps
  the request rate at 80% of what was sent in the last 5 min (pacer), holds it for the cooldown,
  then relaxes it ~10% per tick and removes it once it passes twice the throttled volume.
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
