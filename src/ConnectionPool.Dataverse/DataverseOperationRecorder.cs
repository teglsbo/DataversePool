using System.Diagnostics;
using System.Diagnostics.Metrics;
using static ConnectionPool.Dataverse.DataverseOperationMetrics;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Owns the operation-level instruments from docs/adr/0024 and is the only code that writes to
/// them, so instrument names, tag sets, and outcome classification cannot drift between the
/// <see cref="PooledOrganizationService"/> facade and <c>ExecuteWithThrottleRetryAsync</c>.
///
/// <para>
/// Tags are built in a stack-allocated <see cref="TagList"/> (at most 5 tags, well under its
/// 8-tag inline capacity), and callers check <see cref="IsEnabled"/> once per logical call before
/// touching any of this - so with no listener attached the instrumented paths allocate nothing
/// extra and start no timers.
/// </para>
///
/// <para>
/// Every write is individually guarded (inline try/catch, not a lambda wrapper - that would
/// allocate a closure per write): a <see cref="MeterListener"/> callback runs synchronously inside
/// <c>Counter.Add</c>/<c>Histogram.Record</c>, and a faulty one must never replace or mask the
/// caller's real exception, or fail an operation that actually succeeded (same stance as
/// <c>ResourcePool.PublishHealthChanged</c>, docs/adr/0011).
/// </para>
/// </summary>
internal sealed class DataverseOperationRecorder : IDisposable
{
    /// <summary>Process-wide recorder publishing under <see cref="MeterName"/>.</summary>
    internal static DataverseOperationRecorder Shared { get; } = new(MeterName);

    private readonly Meter _meter;
    private readonly Histogram<double> _acquireDuration;
    private readonly Histogram<double> _operationDuration;
    private readonly Histogram<double> _totalDuration;
    private readonly UpDownCounter<long> _active;
    private readonly UpDownCounter<long> _waiting;
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _calls;
    private readonly Counter<long> _retries;
    private readonly Histogram<double> _retryAfter;

    /// <param name="meterName">Only overridden by tests, to get an isolated meter that no other
    /// (parallel) test's listener can observe.</param>
    internal DataverseOperationRecorder(string meterName)
    {
        _meter = new Meter(meterName);
        _acquireDuration = _meter.CreateHistogram<double>(AcquireDuration, "s",
            "Time from call entry until a pool lease is acquired (capacity wait + any creation).");
        _operationDuration = _meter.CreateHistogram<double>(OperationDuration, "s",
            "Time inside the leased ServiceClient for one attempt, including any SDK-internal retries.");
        _totalDuration = _meter.CreateHistogram<double>(TotalDuration, "s",
            "End-to-end latency of one caller-visible call, across all attempts and throttle waits.");
        _active = _meter.CreateUpDownCounter<long>(Active, "{operation}",
            "Dataverse operations currently executing against a leased client.");
        _waiting = _meter.CreateUpDownCounter<long>(Waiting, "{operation}",
            "Calls currently waiting to acquire a pool lease.");
        _attempts = _meter.CreateCounter<long>(Attempts, "{attempt}",
            "Operation attempts visible to DataversePool (not necessarily raw HTTP requests).");
        _calls = _meter.CreateCounter<long>(Calls, "{call}",
            "Completed caller-visible calls, regardless of retries.");
        _retries = _meter.CreateCounter<long>(Retries, "{retry}",
            "Retries scheduled after a recognized Dataverse throttle.");
        _retryAfter = _meter.CreateHistogram<double>(RetryAfter, "s",
            "Capped Retry-After applied for each recognized Dataverse throttle.");
    }

    /// <summary>True if any listener has enabled any of this recorder's instruments. Read once per
    /// logical call and reused for every paired write, so an UpDownCounter increment is never left
    /// without its decrement when a listener attaches mid-call.</summary>
    public bool IsEnabled =>
        _acquireDuration.Enabled || _operationDuration.Enabled || _totalDuration.Enabled ||
        _active.Enabled || _waiting.Enabled || _attempts.Enabled || _calls.Enabled ||
        _retries.Enabled || _retryAfter.Enabled;

    public void WaitingChanged(in OperationMetricsScope scope, long delta)
    {
        var tags = BaseTags(scope, member: null);
        try { _waiting.Add(delta, tags); } catch { /* see remarks */ }
    }

    public void ActiveChanged(in OperationMetricsScope scope, string member, long delta)
    {
        var tags = BaseTags(scope, member);
        try { _active.Add(delta, tags); } catch { /* see remarks */ }
    }

    public void AcquireCompleted(in OperationMetricsScope scope, string? member, long startTimestamp, string outcome, Exception? exception)
    {
        var tags = OutcomeTags(scope, member, outcome, exception);
        var seconds = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
        try { _acquireDuration.Record(seconds, tags); } catch { /* see remarks */ }
    }

    public void AttemptCompleted(in OperationMetricsScope scope, string member, long startTimestamp, string outcome, Exception? exception)
    {
        var tags = OutcomeTags(scope, member, outcome, exception);
        var seconds = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
        try { _attempts.Add(1, tags); } catch { /* see remarks */ }
        try { _operationDuration.Record(seconds, tags); } catch { /* see remarks */ }
    }

    public void CallCompleted(in OperationMetricsScope scope, string? member, long startTimestamp, string outcome, Exception? exception)
    {
        var tags = OutcomeTags(scope, member, outcome, exception);
        var seconds = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
        try { _calls.Add(1, tags); } catch { /* see remarks */ }
        try { _totalDuration.Record(seconds, tags); } catch { /* see remarks */ }
    }

    public void Throttled(in OperationMetricsScope scope, string member, TimeSpan retryAfter)
    {
        var tags = BaseTags(scope, member);
        try { _retryAfter.Record(retryAfter.TotalSeconds, tags); } catch { /* see remarks */ }
    }

    public void RetryScheduled(in OperationMetricsScope scope, string member)
    {
        var tags = BaseTags(scope, member);
        try { _retries.Add(1, tags); } catch { /* see remarks */ }
    }

    /// <summary>Outcome for an exception that was not a recognized throttle. Only a cancellation of
    /// the caller's own token counts as <see cref="OutcomeCanceled"/> - e.g. an SDK-internal HTTP
    /// timeout surfacing as <see cref="TaskCanceledException"/> is a real failure, not a cancel.</summary>
    public static string ClassifyFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            ? OutcomeCanceled
            : OutcomeError;

    public void Dispose() => _meter.Dispose();

    private static TagList BaseTags(in OperationMetricsScope scope, string? member)
    {
        var tags = new TagList
        {
            { PoolNameTag, scope.PoolName },
            { OperationNameTag, scope.OperationName },
        };
        if (member is not null)
        {
            tags.Add(MemberNameTag, member);
        }

        return tags;
    }

    private static TagList OutcomeTags(in OperationMetricsScope scope, string? member, string outcome, Exception? exception)
    {
        var tags = BaseTags(scope, member);
        tags.Add(OutcomeTag, outcome);
        if (exception is not null)
        {
            // Type name only: messages can carry record IDs, query text, or URLs (docs/adr/0024 §5).
            var type = exception.GetType();
            tags.Add(ErrorTypeTag, type.FullName ?? type.Name);
        }

        return tags;
    }
}

/// <summary>Stable metric identity of one logical call: which recorder, pool, and operation.</summary>
internal readonly record struct OperationMetricsScope(DataverseOperationRecorder Recorder, string PoolName, string OperationName);
