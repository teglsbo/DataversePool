# ADR-0025: Runtime-adjustable pool size, as the first step toward automatic DOP

## Status
Accepted — runtime resizing is implemented. Automatic DOP probing is **not** decided here and needs
its own spec (see "Open questions").

## Context

Each `DataverseUserPool` has a fixed concurrency limit, `PoolOptions.MaxSize`, set at construction.
Today the only way to change it is to restart the process. That makes it impractical to:

- follow `ServiceClient.RecommendedDegreesOfParallelism`, which Dataverse feeds from the
  `x-ms-dop-hint` response header on both transports (see the ADR-0006/0016 addenda). It can differ
  per environment and change over time;
- let an operator tune a live system while watching the ADR-0024 metrics (`acquire.duration` and
  `waiting` show when the pool is too small; `throttled` and `retry_after` show when it is too big);
- experiment with an automatic controller later.

The goal is an automatic DOP controller. That needs research first: what signal to trust, how fast
to move, and how to avoid oscillation and amplifying throttling. A manual runtime knob is the
prerequisite for any of that, and it is useful on its own.

## Decision

1. `ResourcePool<T>.SetMaxSize(int)` and `ResourcePool<T>.MaxSize` (the current value) exist, with
   pass-throughs on `DataverseUserPool`. `PoolOptions.MaxSize` is now the *initial* value.
   `PoolStats.MaxSize` reports the current one, so the existing `DataversePool.Metrics` gauge
   follows changes with no extra work.
2. **Growing** releases the extra capacity permits immediately, so callers already waiting proceed
   at once.
3. **Shrinking** never cancels or waits for in-flight work:
   - Free permits are taken back at once.
   - Permits held by in-flight leases become *permit debt*. A single `ReleasePermit` path pays the
     debt down as those leases come back, instead of returning the permit to the semaphore.
   - Idle resources above the new limit are disposed in the background. A lease returned while
     `CreatedCount` exceeds the limit is disposed instead of re-idled (the same applies to a
     background recycle that completes then).
   - So for a short time after a shrink, more leases than the new limit can be outstanding.
     `LeasedCount` shows the true number.
4. Growing after a shrink first forgives outstanding debt, and only then releases new permits, so
   a shrink followed by a grow never overshoots.
5. Resizes are serialized under a lock (all operations inside it are synchronous). Concurrent calls
   apply in some order, and the last one wins.
6. The capacity semaphore's own `maxCount` is now unbounded, since the limit changes. This loses
   `SemaphoreFullException` as an accidental double-release guard. The single `ReleasePermit` path,
   and the tests around it, take over that role.

### Bug fixed along the way

A successful recycle used to add the replacement to `CreatedCount` without removing the disposed
original, so `CreatedCount` crept up by one per recycle. That was invisible until shrinking started
comparing `CreatedCount` with the limit. The original is now un-counted when it is disposed. A
regression test covers this.

## Consequences

- Callers can change concurrency per member without a restart. For example, call
  `member.SetMaxSize(client.RecommendedDegreesOfParallelism)` from a background loop, or from an
  admin endpoint.
- Lowering the size is graceful but not instant. A controller must not read the size as an
  immediate hard cap.
- Nothing in the library changes the size on its own. Behavior is identical unless `SetMaxSize` is
  called.
- Not done here: a `DataversePool`-level convenience (resize every member), DI/options-monitor
  binding (e.g. `IOptionsMonitor<PoolOptions>` reload), and a DOP-hint gauge. Each is small, and
  belongs with the automatic-DOP spec rather than being guessed now.

## Open questions for the automatic-DOP spec

To research before designing a controller:

1. **What does `RecommendedDegreesOfParallelism` actually track?** Is it per user, per org, or per
   frontend server? How often does it change, and does it react to load or stay a static
   per-environment number? Answering this needs live sampling across tenants and times of day.
2. **Which signal drives the controller?** The DOP hint, throttle rate (which needs the Phase 2
   SOAP-detector fix first, or it reads zero), operation-latency inflection (AIMD/TCP-Vegas-style,
   as in Netflix `concurrency-limits`), or a combination?
3. **Scope.** Service-protection limits are per user, per web server, and over a 5-minute sliding
   window. An in-process controller sees only one replica's share, so the plan must handle
   multi-replica deployments without global coordination, or state that it can't.
4. **Safety.** Floors and ceilings, step size, cooldown and hysteresis, and behavior when
   throttling is already hidden inside SDK-internal retries (`MaxRetryCount` > 0).
5. **Interaction with the selection strategy.** If a throttled member is shrunk *and* avoided,
   does it double-penalise that member, or starve it of the samples needed to recover?
6. **Testing.** A deterministic simulator for the limit model (5-minute window, request count,
   execution time, concurrency), so the controller can be evaluated without a live tenant.
