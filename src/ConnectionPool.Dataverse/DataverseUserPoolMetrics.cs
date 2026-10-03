using System.Diagnostics.Metrics;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Publishes a single <see cref="DataverseUserPool"/> member's Dataverse-specific, per-member
/// signals as <see cref="System.Diagnostics.Metrics"/> observable gauges: the <c>x-ms-dop-hint</c>
/// recommendation (ADR-0025), this member's own throttle state (ADR-0008), and - when the member's
/// <see cref="ISlotSelectionStrategy"/> uses one - its <see cref="MemberCircuitBreaker"/> state.
///
/// <para>
/// Lives in <c>ConnectionPool.Dataverse</c> rather than the generic <c>ConnectionPool.Metrics</c>
/// package (which only references <c>ConnectionPool.Core</c>, not this assembly) because every
/// signal here is Dataverse-specific; see PLAN-2026-09-30.md Phase 3 for the rationale. Like
/// <c>ConnectionPool.Metrics.PoolMetrics</c>, every instrument is an observable gauge: no
/// background timer or polling thread, callbacks only run when a listener collects.
/// </para>
/// </summary>
public sealed class DataverseUserPoolMetrics : IDisposable
{
    private readonly Meter _meter;

    /// <summary>Meter name used unless overridden - intentionally the same value as
    /// <see cref="DataverseOperationMetrics.MeterName"/> and
    /// <c>ConnectionPool.Metrics.PoolMetrics.DefaultMeterName</c> so one <c>AddMeter("DataversePool")</c>
    /// call collects everything. Duplicated here (rather than referenced) because there is no
    /// project reference between this assembly and <c>ConnectionPool.Metrics</c> - keep in sync if
    /// either changes.</summary>
    public const string DefaultMeterName = "DataversePool";

    /// <param name="member">The member whose per-member signals to publish.</param>
    /// <param name="breaker">
    /// The <see cref="MemberCircuitBreaker"/> that governs <paramref name="member"/>'s selection
    /// eligibility, if any (see <see cref="ISlotSelectionStrategy.Breaker"/>). When <c>null</c>, no
    /// <c>breaker_state</c> instrument is created at all - there is nothing meaningful to report for
    /// a strategy with no circuit-breaking (e.g. plain round-robin).
    /// </param>
    /// <param name="meterName">Overrides <see cref="DefaultMeterName"/> if set.</param>
    public DataverseUserPoolMetrics(DataverseUserPool member, MemberCircuitBreaker? breaker = null, string? meterName = null)
    {
        ArgumentNullException.ThrowIfNull(member);

        _meter = new Meter(meterName ?? DefaultMeterName);
        var tags = new KeyValuePair<string, object?>[] { new(DataverseOperationMetrics.MemberNameTag, member.Name) };

        _meter.CreateObservableGauge(
            "dataversepool.member.dop_hint",
            () => member.RecommendedDegreesOfParallelism is { } hint
                ? new[] { new Measurement<int>(hint, tags) }
                : Array.Empty<Measurement<int>>(),
            description: "Most recently observed x-ms-dop-hint (ServiceClient.RecommendedDegreesOfParallelism). Absent until at least one response has reported one.");

        _meter.CreateObservableGauge(
            "dataversepool.member.throttled",
            () => new Measurement<int>(member.IsThrottled ? 1 : 0, tags),
            description: "1 if this member is currently throttled (service-protection 429), 0 otherwise.");

        _meter.CreateObservableGauge(
            "dataversepool.member.throttled_until_seconds",
            () =>
            {
                var remaining = member.ThrottledUntil is { } until
                    ? (until - DateTimeOffset.UtcNow).TotalSeconds
                    : 0;
                return new Measurement<double>(Math.Max(0, remaining), tags);
            },
            description: "Seconds remaining until this member's throttle window expires, or 0 if not throttled.");

        _meter.CreateObservableGauge(
            "dataversepool.member.quarantined",
            () => member.QuarantineReason is { } reason
                ? new Measurement<int>(1, new KeyValuePair<string, object?>[] { tags[0], new("quarantine_reason", reason) })
                : new Measurement<int>(0, tags),
            description: "1 while this member is quarantined for a permanent failure (revoked secret, disabled app user, ...), with a quarantine_reason tag; 0 otherwise.");

        if (breaker is not null)
        {
            _meter.CreateObservableGauge(
                "dataversepool.member.breaker_state",
                () => new Measurement<int>((int)breaker.GetState(member, member.GetStats(), DateTimeOffset.UtcNow), tags),
                description: "This member's MemberCircuitBreaker state: 0=Closed, 1=HalfOpen, 2=Open.");
        }
    }

    /// <summary>Disposes the underlying <see cref="Meter"/>, unregistering all instruments it created.</summary>
    public void Dispose() => _meter.Dispose();
}

/// <summary>Convenience extensions for publishing <see cref="DataverseUserPoolMetrics"/>.</summary>
public static class DataverseUserPoolMetricsExtensions
{
    /// <summary>
    /// Creates a <see cref="DataverseUserPoolMetrics"/> for a single, standalone
    /// <see cref="DataverseUserPool"/> (no circuit breaker - a bare member has no
    /// <see cref="ISlotSelectionStrategy"/> of its own). Dispose the result when the member itself
    /// is disposed.
    /// </summary>
    public static DataverseUserPoolMetrics AddMetrics(this DataverseUserPool member, string? meterName = null)
    {
        ArgumentNullException.ThrowIfNull(member);
        return new DataverseUserPoolMetrics(member, breaker: null, meterName);
    }

    /// <summary>
    /// Creates one <see cref="DataverseUserPoolMetrics"/> per member of <paramref name="pool"/>,
    /// each wired to <paramref name="pool"/>'s <see cref="ISlotSelectionStrategy.Breaker"/> so
    /// <c>breaker_state</c> is published whenever the strategy has one. Dispose the returned
    /// handle (which disposes every member's metrics) when the pool itself is disposed.
    /// </summary>
    public static IDisposable AddMemberMetrics(this DataversePool pool, string? meterName = null)
    {
        ArgumentNullException.ThrowIfNull(pool);

        var breaker = pool.Strategy.Breaker;
        var perMember = new DataverseUserPoolMetrics[pool.Members.Count];
        for (var i = 0; i < pool.Members.Count; i++)
        {
            perMember[i] = new DataverseUserPoolMetrics(pool.Members[i], breaker, meterName);
        }

        return new CompositeMetricsDisposable(perMember);
    }

    private sealed class CompositeMetricsDisposable(IReadOnlyList<IDisposable> inner) : IDisposable
    {
        public void Dispose()
        {
            foreach (var d in inner)
            {
                d.Dispose();
            }
        }
    }
}
