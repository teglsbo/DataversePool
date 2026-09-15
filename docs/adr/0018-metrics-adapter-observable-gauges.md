# ADR-0018: Optional metrics adapter using `System.Diagnostics.Metrics` observable gauges

## Status
Accepted

## Context
The library already exposes pool health three ways: a pull-based snapshot (`ResourcePool<T>.GetStats()`
/ `PoolStats`, also surfaced by `DataverseUserPool.GetStats()`), a push event
(`ResourcePool<T>.HealthChanges`), and a push callback (`PoolOptions.OnLeakDetected`), plus routine
`ILogger` logging throughout. None of these plug into a metrics backend (Prometheus, OTLP, Azure
Monitor, etc.) without the caller writing glue code themselves. The user asked whether the library
should ship that glue.

`System.Diagnostics.Metrics` (the `Meter`/`Instrument` API introduced in .NET 6) is the standard,
vendor-neutral way to publish metrics from a .NET library: any OpenTelemetry-compatible exporter can
subscribe to a named `Meter` without the library taking a dependency on OpenTelemetry (or any other
specific backend) itself. It ships in the shared framework - no extra NuGet package reference was
needed to build against it on net8.0 (confirmed by building `ConnectionPool.Metrics` with only a
`ProjectReference` to `ConnectionPool.Core`).

## Decision
- New optional package: **`ConnectionPool.Metrics`** (NuGet `DataversePool.Metrics`), referencing
  only `ConnectionPool.Core` - no Dataverse dependency, mirroring ADR-0005's "optional adapter, not a
  core dependency" shape for the Polly package. Consumers who don't want metrics never pay for this
  package.
- **`PoolMetrics`** wraps a `Meter` (default name `"DataversePool"`, overridable) and publishes every
  `PoolStats` field as an **observable gauge** (`Meter.CreateObservableGauge`), not a counter:
  `PoolStats` is a snapshot obtained via a supplied `Func<PoolStats> statsProvider` (typically
  `pool.GetStats` or `DataverseUserPool.GetStats`), not a stream of individual increment/decrement
  events this code observes as they happen. An observable gauge's callback only runs when a listener
  actually collects (e.g. an OTel exporter on its export interval), so this adds no background
  polling thread of its own - consistent with how `PoolStats` already behaves for its other consumer
  (`PoolAcquireTimeoutException`'s diagnostic snapshot).
- `PoolStats.DetectedLeakCount` is conceptually cumulative (see ADR docs on leak detection), but is
  still published as a gauge, not a `Counter<T>`: the running total already lives inside `PoolStats`
  as maintained by the pool itself; a true `Counter<T>` would require this code to call `.Add()` for
  each individual increment as it happens, which would need a push subscription that doesn't exist
  for GC-detected leaks. A gauge reporting "current cumulative total" is the only instrument shape
  consistent with what's actually observable here.
- Every measurement is tagged with `pool.name` (caller-supplied string) so multiple pools - e.g. each
  member of a `DataverseGroupPool` - can share one `Meter` and still be distinguished by an exporter.
- **`ResourcePoolMetricsExtensions.AddMetrics(poolName, meterName?)`**: a convenience extension method
  on `ResourcePool<T>` for the common case, matching the Polly package's extension-method style
  (`AddRetryWithPoolHealthSignal`). Callers using `DataverseUserPool`/`DataverseGroupPool` directly
  (which don't expose their inner `ResourcePool<T>`) construct `PoolMetrics` directly instead, passing
  `pool.GetStats` as the `statsProvider` - the same `PoolStats` type is returned either way, so no
  Dataverse-specific extension overload was needed.
- Scope is deliberately limited to the generic `PoolStats` fields already in `ConnectionPool.Core`.
  Dataverse-specific signals (e.g. per-member circuit breaker state, throttle counts) are not covered
  here; that's flagged as a possible future enhancement rather than folded into this addition, to
  avoid scope creep.

## Consequences
- No behavior change for existing callers; this is a purely additive, opt-in package.
- Fully unit-testable, unlike most of the Dataverse-layer ADRs: `ConnectionPool.Metrics.Tests` uses
  the same `FakePolicy`/`FakeResource` pattern as `ConnectionPool.Core.Tests` /
  `ConnectionPool.Dataverse.Polly.Tests` to construct a real `ResourcePool<T>` with no network
  dependency, and uses the real `System.Diagnostics.Metrics.MeterListener` API (built into the BCL,
  no OpenTelemetry package needed) to assert actual emitted gauge values and tags - including that
  `Dispose()` correctly unregisters the instruments. Each test uses a unique `Meter` name so
  parallel/concurrent test runs never observe each other's instruments.
- Consumers wire this into an actual exporter themselves (e.g.
  `builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("DataversePool"))`) - this package
  only publishes the `Meter`, it does not take a dependency on any exporter or hosting model.
