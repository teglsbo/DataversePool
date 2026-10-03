namespace ConnectionPool.Dataverse;

/// <summary>
/// Names of the push-based, Dataverse-operation-level <see cref="System.Diagnostics.Metrics"/>
/// instruments DataversePool publishes, and of the tags attached to them - see
/// docs/adr/0024-operation-level-metrics-on-instrumented-execution-paths.md. Exposed so dashboards,
/// alerts, and tests can reference them without hardcoding strings.
///
/// <para>
/// Only two execution paths are instrumented automatically, because they are the only ones
/// DataversePool genuinely controls: every <see cref="PooledOrganizationService"/> method, and
/// <see cref="DataversePool.ExecuteWithThrottleRetryAsync{T}(string, Func{Microsoft.PowerPlatform.Dataverse.Client.ServiceClient, CancellationToken, Task{T}}, int?, TimeSpan?, CancellationToken)"/>.
/// Calls made directly against <see cref="DataverseLease.Resource"/> are <b>not</b> observed - the
/// pool hands out the real <c>ServiceClient</c> and cannot see what you do with it.
/// </para>
///
/// <para>
/// Recording is dormant until a listener subscribes to <see cref="MeterName"/> (e.g.
/// <c>metrics.AddMeter("DataversePool")</c> in OpenTelemetry). No exporter, timer, or background
/// thread is created by this library.
/// </para>
/// </summary>
public static class DataverseOperationMetrics
{
    /// <summary>Meter name for every instrument listed here. Shared with the generic pool-state
    /// gauges published by the optional <c>ConnectionPool.Metrics</c> package (ADR-0018), so one
    /// <c>AddMeter</c> call collects both.</summary>
    public const string MeterName = "DataversePool";

    /// <summary>Histogram (seconds): time from call entry until a lease is acquired - capacity
    /// waiting plus any resource creation. Large values mean pool saturation, not slow Dataverse.</summary>
    public const string AcquireDuration = "dataversepool.operation.acquire.duration";

    /// <summary>Histogram (seconds): time spent inside the leased <c>ServiceClient</c> for one
    /// attempt. Includes any retries the SDK performs internally when <c>MaxRetryCount &gt; 0</c> -
    /// those are invisible to DataversePool and show up here as a slow attempt.</summary>
    public const string OperationDuration = "dataversepool.operation.duration";

    /// <summary>Histogram (seconds): end-to-end latency of one caller-visible call - every acquire,
    /// attempt, throttle wait, and lease release.</summary>
    public const string TotalDuration = "dataversepool.operation.total.duration";

    /// <summary>UpDownCounter: Dataverse operations currently executing against a leased client.</summary>
    public const string Active = "dataversepool.operation.active";

    /// <summary>UpDownCounter: calls currently waiting to acquire a lease.</summary>
    public const string Waiting = "dataversepool.operation.waiting";

    /// <summary>Counter: one per SDK operation attempt visible to DataversePool. Not necessarily
    /// one per HTTP request - see <see cref="OperationDuration"/>. Set
    /// <see cref="DataverseClientOptions.MaxRetryCount"/> to <c>0</c> to make them match.</summary>
    public const string Attempts = "dataversepool.operation.attempts";

    /// <summary>Counter: one per completed caller-visible call, regardless of retries.</summary>
    public const string Calls = "dataversepool.operation.calls";

    /// <summary>Counter: one per retry scheduled after a recognized throttle.</summary>
    public const string Retries = "dataversepool.operation.retries";

    /// <summary>Histogram (seconds): the capped <c>Retry-After</c> DataversePool applied for each
    /// recognized throttle.</summary>
    public const string RetryAfter = "dataversepool.operation.retry_after";

    /// <summary>Tag: the configured, stable pool name (<see cref="DataverseOperationMetricsOptions.PoolName"/>).</summary>
    public const string PoolNameTag = "pool.name";

    /// <summary>Tag: <see cref="DataverseUserPool.Name"/> of the member that served the attempt.</summary>
    public const string MemberNameTag = "pool.member.name";

    /// <summary>Tag: a fixed facade operation name (e.g. <c>retrieve_multiple</c>), or the stable name
    /// passed to <c>ExecuteWithThrottleRetryAsync</c> (<see cref="CustomOperationName"/> if none).</summary>
    public const string OperationNameTag = "dataverse.operation.name";

    /// <summary>Tag: one of <see cref="OutcomeSuccess"/>, <see cref="OutcomeError"/>,
    /// <see cref="OutcomeThrottled"/>, <see cref="OutcomeCanceled"/>.</summary>
    public const string OutcomeTag = "outcome";

    /// <summary>Tag: full type name of the exception, present only on non-success outcomes. Never
    /// the exception message.</summary>
    public const string ErrorTypeTag = "error.type";

    public const string OutcomeSuccess = "success";
    public const string OutcomeError = "error";
    public const string OutcomeThrottled = "throttled";
    public const string OutcomeCanceled = "canceled";

    /// <summary>Operation name used by the <c>ExecuteWithThrottleRetryAsync</c> overload that does
    /// not take one.</summary>
    public const string CustomOperationName = "custom";
}
