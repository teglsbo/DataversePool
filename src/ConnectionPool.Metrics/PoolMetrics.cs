using System.Diagnostics.Metrics;
using ConnectionPool.Core;

namespace ConnectionPool.Metrics;

/// <summary>
/// Publishes a pool's <see cref="PoolStats"/> as <see cref="System.Diagnostics.Metrics"/>
/// observable gauges - standard, OpenTelemetry-compatible instruments any exporter (Prometheus,
/// OTLP, Azure Monitor, etc.) can pick up without this library depending on any specific metrics
/// backend. See docs/adr/0018-metrics-adapter-observable-gauges.md.
///
/// <para>
/// All instruments are <b>observable gauges</b>, not counters, because <see cref="PoolStats"/> is a
/// point-in-time snapshot obtained via a pull (<c>statsProvider()</c>, typically
/// <see cref="ResourcePool{T}.GetStats"/> or DataversePool.Dataverse's <c>DataverseUserPool.GetStats</c>)
/// - there is no push-based event stream of individual state transitions to count. An observable
/// gauge's callback is invoked on demand whenever a listener (e.g. an OpenTelemetry
/// <c>MeterListener</c>/exporter) collects, so this adds no background timer or polling thread of
/// its own.
/// </para>
///
/// <para>
/// <see cref="PoolStats.DetectedLeakCount"/> is cumulative/monotonically increasing by definition
/// (see its own docs), but is still published as a gauge here rather than a genuine
/// <see cref="Meter.CreateCounter{T}(string, string?, string?)"/> - the value already lives in
/// <see cref="PoolStats"/> as a running total the pool itself maintains, not something this type
/// increments itself, so a gauge that reports "the current cumulative total" is the correct/only
/// consistent instrument shape here (a real `Counter&lt;T&gt;` requires *this* code to call
/// <c>.Add()</c> for each increment as it happens, which would require subscribing to a push
/// event - not available for leak detection, which is inherently only observable via GC, see
/// docs/adr/0012).
/// </para>
/// </summary>
public sealed class PoolMetrics : IDisposable
{
    private readonly Meter _meter;

    /// <summary>Meter name used for every instrument this type creates, unless overridden.</summary>
    public const string DefaultMeterName = "DataversePool";

    /// <param name="poolName">
    /// Value for the <c>pool.name</c> tag attached to every measurement - lets you distinguish
    /// multiple pools (e.g. each member of a <c>DataverseGroupPool</c>)
    /// under the same meter.
    /// </param>
    /// <param name="statsProvider">
    /// Invoked on demand whenever a listener collects - typically <c>pool.GetStats</c> for a
    /// <see cref="ResourcePool{T}"/> or a <c>DataverseUserPool</c>.
    /// Must be cheap and side-effect-free; it may be called frequently and concurrently.
    /// </param>
    /// <param name="meterName">
    /// Overrides <see cref="DefaultMeterName"/> if set - use a distinct name per meter version if
    /// you need side-by-side instrumentation, otherwise leave default so all pools share one meter.
    /// </param>
    public PoolMetrics(string poolName, Func<PoolStats> statsProvider, string? meterName = null)
    {
        if (string.IsNullOrWhiteSpace(poolName))
        {
            throw new ArgumentException("Pool name must not be empty.", nameof(poolName));
        }

        ArgumentNullException.ThrowIfNull(statsProvider);

        _meter = new Meter(meterName ?? DefaultMeterName);
        var tags = new KeyValuePair<string, object?>[] { new("pool.name", poolName) };

        _meter.CreateObservableGauge(
            "dataversepool.pool.max_size",
            () => new Measurement<int>(statsProvider().MaxSize, tags),
            description: "Configured maximum number of concurrently created/leased resources.");

        _meter.CreateObservableGauge(
            "dataversepool.pool.created",
            () => new Measurement<int>(statsProvider().CreatedCount, tags),
            description: "Total resources currently created (idle + leased + unhealthy/recycling).");

        _meter.CreateObservableGauge(
            "dataversepool.pool.idle",
            () => new Measurement<int>(statsProvider().IdleCount, tags),
            description: "Resources currently idle and available for lease.");

        _meter.CreateObservableGauge(
            "dataversepool.pool.leased",
            () => new Measurement<int>(statsProvider().LeasedCount, tags),
            description: "Resources currently leased out to a caller.");

        _meter.CreateObservableGauge(
            "dataversepool.pool.unhealthy_or_recycling",
            () => new Measurement<int>(statsProvider().UnhealthyOrRecyclingCount, tags),
            description: "Resources marked unhealthy or being recycled, not available for lease.");

        _meter.CreateObservableGauge(
            "dataversepool.pool.waiting",
            () => new Measurement<int>(statsProvider().WaitingCount, tags),
            description: "Callers currently blocked in AcquireAsync waiting for a resource.");

        _meter.CreateObservableGauge(
            "dataversepool.pool.consecutive_create_failures",
            () => new Measurement<int>(statsProvider().ConsecutiveCreateFailures, tags),
            description: "Consecutive CreateAsync failures - drives circuit-breaker-aware selection strategies.");

        _meter.CreateObservableGauge(
            "dataversepool.pool.consecutive_operational_failures",
            () => new Measurement<int>(statsProvider().ConsecutiveOperationalFailures, tags),
            description: "Consecutive operational (post-creation) failures reported via PooledLease.MarkUnhealthy.");

        _meter.CreateObservableGauge(
            "dataversepool.pool.detected_leak_count",
            () => new Measurement<long>(statsProvider().DetectedLeakCount, tags),
            description: "Cumulative count of GC-detected leaked leases over this pool's lifetime.");
    }

    /// <summary>Disposes the underlying <see cref="Meter"/>, unregistering all instruments it created.</summary>
    public void Dispose() => _meter.Dispose();
}
