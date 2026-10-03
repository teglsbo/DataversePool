using System.Diagnostics;

namespace ConnectionPool.Dataverse;

/// <summary>Reports whether <paramref name="exception"/> is a recognized Dataverse throttle, and if
/// so records it against the lease's member and returns the capped window via
/// <paramref name="retryAfter"/>.</summary>
internal delegate bool ThrottleReporter<in TLease>(TLease lease, Exception exception, TimeSpan? maxRetryAfter, out TimeSpan retryAfter);

/// <summary>How <see cref="DataverseOperationExecutor"/> reads the member identity/name and
/// reports throttling and connection faults for a given lease type. One instance per lease type, created once - not
/// per call.</summary>
internal sealed class LeaseAccessors<TLease>(
    Func<TLease, object> member,
    Func<TLease, string> memberName,
    ThrottleReporter<TLease> reportIfThrottled,
    Func<TLease, Exception, bool>? reportIfConnectionFault = null)
{
    public Func<TLease, object> Member { get; } = member;
    public Func<TLease, string> MemberName { get; } = memberName;
    public ThrottleReporter<TLease> ReportIfThrottled { get; } = reportIfThrottled;

    /// <summary>Reports a non-throttle failure that looks like a connection fault; null = never.</summary>
    public Func<TLease, Exception, bool>? ReportIfConnectionFault { get; } = reportIfConnectionFault;
}

/// <summary>Receives each completed attempt (with the serving member) for automatic pool sizing.</summary>
internal interface IOperationOutcomeSink
{
    void Record(object member, PoolSizingOperationOutcome outcome);

    /// <summary>Called after a lease is held and before the attempt runs; may delay it (pacing).</summary>
    ValueTask BeforeAttemptAsync(object member, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>True if the sink wants <see cref="OnResponse"/> calls for attempts it is attached to.</summary>
    bool ObservesResponses => false;

    /// <summary>The budget headers of an HTTP response made during an attempt on <paramref name="member"/>.</summary>
    void OnResponse(object member, ResponseBudget budget)
    {
    }
}

internal static class DataverseLeaseAccessors
{
    public static readonly LeaseAccessors<DataverseLease> Instance = new(
        static lease => lease.Member,
        static lease => lease.Member.Name,
        static (DataverseLease lease, Exception exception, TimeSpan? maxRetryAfter, out TimeSpan retryAfter) =>
            lease.ReportIfThrottled(exception, out retryAfter, maxRetryAfter),
        static (lease, exception) => lease.ReportIfConnectionFault(exception));
}

/// <summary>
/// The single acquire → run → report-throttle → release → (maybe) retry loop behind both
/// <see cref="PooledOrganizationService"/> (<c>maxAttempts: 1</c>) and
/// <see cref="DataversePool.ExecuteWithThrottleRetryAsync{T}(string, Func{Microsoft.PowerPlatform.Dataverse.Client.ServiceClient, CancellationToken, Task{T}}, int?, TimeSpan?, CancellationToken)"/>,
/// instrumented per docs/adr/0024. Generic over the lease type and free of any
/// <c>ServiceClient</c> dependency, so its lease-release, exception-identity, retry, and metric
/// semantics are unit-testable with fake leases (a real <c>ServiceClient</c> cannot be constructed
/// without a live connection). Replaces the former <c>LeaseScope</c> helper (docs/adr/0020), whose
/// guarantees it keeps:
/// <list type="bullet">
/// <item>Every acquired lease is released exactly once, whether the attempt succeeds, throws, or is
/// canceled.</item>
/// <item>The exception that propagates is always the original instance, never wrapped or replaced -
/// including when throttle reporting or a metrics listener itself throws.</item>
/// <item>Throttle reporting runs for every failed attempt, including the last one, before that
/// attempt's lease is released.</item>
/// </list>
/// </summary>
internal static class DataverseOperationExecutor
{
    public static async Task<TResult> ExecuteAsync<TLease, TResult>(
        OperationMetricsScope scope,
        LeaseAccessors<TLease> accessors,
        Func<CancellationToken, Task<TLease>> acquire,
        Func<TLease, CancellationToken, Task<TResult>> operation,
        int maxAttempts,
        TimeSpan? maxRetryAfter,
        CancellationToken cancellationToken,
        IOperationOutcomeSink? sink = null)
        where TLease : IAsyncDisposable
    {
        var recorder = scope.Recorder;

        // Captured once: every paired write in this call (active/waiting +1/-1) must agree on it.
        var enabled = recorder.IsEnabled;
        var callStart = enabled ? Stopwatch.GetTimestamp() : 0;
        string? lastMemberName = null;
        Exception? lastThrottle = null;
        Exception? failure = null;

        object? previouslyThrottledMember = null;
        var previousRetryAfter = TimeSpan.Zero;

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var lease = await AcquireAsync(scope, enabled, accessors, acquire, cancellationToken).ConfigureAwait(false);

                // No better option was available than the very member we just reported as
                // throttled: release the lease *before* waiting out its capped Retry-After, then
                // re-acquire. Holding it across the wait would pin a capacity permit for the whole
                // window and stall unrelated callers behind it (docs/adr/0019, docs/adr/0022). No
                // lease is held during the delay, so canceling it cannot leak one.
                if (previouslyThrottledMember is not null && ReferenceEquals(accessors.Member(lease), previouslyThrottledMember))
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                    await Task.Delay(previousRetryAfter, cancellationToken).ConfigureAwait(false);
                    lease = await AcquireAsync(scope, enabled, accessors, acquire, cancellationToken).ConfigureAwait(false);
                }

                var memberName = accessors.MemberName(lease);
                lastMemberName = memberName;
                var attemptStart = 0L;
                if (enabled)
                {
                    recorder.ActiveChanged(scope, memberName, +1);
                }

                if (enabled || sink is not null)
                {
                    attemptStart = Stopwatch.GetTimestamp();
                }

                try
                {
                    if (sink is not null)
                    {
                        await sink.BeforeAttemptAsync(accessors.Member(lease), cancellationToken).ConfigureAwait(false);
                        if (enabled || sink is not null)
                        {
                            attemptStart = Stopwatch.GetTimestamp(); // pacing wait is not call latency
                        }
                    }

                    if (sink is { ObservesResponses: true })
                    {
                        ResponseObservation.Current.Value = new ResponseObservation.Target(sink, accessors.Member(lease));
                    }

                    var result = await operation(lease, cancellationToken).ConfigureAwait(false);
                    if (enabled)
                    {
                        recorder.AttemptCompleted(scope, memberName, attemptStart, DataverseOperationMetrics.OutcomeSuccess, exception: null);
                    }

                    TryRecordOutcome(sink, accessors, lease, scope, attemptStart, PoolSizingOutcomeKind.Success, null, null);

                    return result;
                }
                catch (Exception ex)
                {
                    var throttled = TryReportThrottle(accessors, lease, ex, maxRetryAfter, out var retryAfter);
                    if (!throttled && !cancellationToken.IsCancellationRequested)
                    {
                        TryReportConnectionFault(accessors, lease, ex);
                    }

                    if (sink is not null)
                    {
                        var kind = throttled
                            ? PoolSizingOutcomeKind.Throttled
                            : DataverseOperationRecorder.ClassifyFailure(ex, cancellationToken) == DataverseOperationMetrics.OutcomeCanceled
                                ? PoolSizingOutcomeKind.Canceled
                                : PoolSizingOutcomeKind.Error;
                        TryRecordOutcome(sink, accessors, lease, scope, attemptStart, kind, throttled ? ex : null, throttled ? retryAfter : null);
                    }

                    if (enabled)
                    {
                        var outcome = throttled
                            ? DataverseOperationMetrics.OutcomeThrottled
                            : DataverseOperationRecorder.ClassifyFailure(ex, cancellationToken);
                        recorder.AttemptCompleted(scope, memberName, attemptStart, outcome, ex);
                        if (throttled)
                        {
                            recorder.Throttled(scope, memberName, retryAfter);
                        }
                    }

                    if (!throttled || attempt >= maxAttempts)
                    {
                        if (throttled)
                        {
                            lastThrottle = ex;
                        }

                        throw;
                    }

                    if (enabled)
                    {
                        recorder.RetryScheduled(scope, memberName);
                    }

                    previouslyThrottledMember = accessors.Member(lease);
                    previousRetryAfter = retryAfter;
                }
                finally
                {
                    ResponseObservation.Current.Value = null;
                    if (enabled)
                    {
                        recorder.ActiveChanged(scope, memberName, -1);
                    }

                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            // In finally (not the catch/return paths) so total duration includes lease release.
            if (enabled)
            {
                var outcome = failure is null
                    ? DataverseOperationMetrics.OutcomeSuccess
                    : ReferenceEquals(failure, lastThrottle)
                        ? DataverseOperationMetrics.OutcomeThrottled
                        : DataverseOperationRecorder.ClassifyFailure(failure, cancellationToken);
                recorder.CallCompleted(scope, lastMemberName, callStart, outcome, failure);
            }
        }
    }

    /// <summary>Overload for operations with no result (most <see cref="PooledOrganizationService"/>
    /// mutations).</summary>
    public static Task ExecuteAsync<TLease>(
        OperationMetricsScope scope,
        LeaseAccessors<TLease> accessors,
        Func<CancellationToken, Task<TLease>> acquire,
        Func<TLease, CancellationToken, Task> operation,
        int maxAttempts,
        TimeSpan? maxRetryAfter,
        CancellationToken cancellationToken,
        IOperationOutcomeSink? sink = null)
        where TLease : IAsyncDisposable =>
        ExecuteAsync<TLease, bool>(
            scope,
            accessors,
            acquire,
            async (lease, ct) =>
            {
                await operation(lease, ct).ConfigureAwait(false);
                return true;
            },
            maxAttempts,
            maxRetryAfter,
            cancellationToken,
            sink);

    /// <summary>Sizing feedback must never replace the operation's own result or exception.</summary>
    private static void TryRecordOutcome<TLease>(
        IOperationOutcomeSink? sink,
        LeaseAccessors<TLease> accessors,
        TLease lease,
        OperationMetricsScope scope,
        long attemptStart,
        PoolSizingOutcomeKind kind,
        Exception? throttle,
        TimeSpan? retryAfter)
    {
        if (sink is null)
        {
            return;
        }

        try
        {
            ThrottleReason? reason = null;
            if (throttle is not null)
            {
                reason = DataverseThrottleDetector.TryGetThrottleReason(throttle, out var r) ? r : ThrottleReason.Unknown;
            }

            sink.Record(
                accessors.Member(lease),
                new PoolSizingOperationOutcome(scope.OperationName, Stopwatch.GetElapsedTime(attemptStart), kind, reason, retryAfter, InFlightCount: 0));
        }
        catch
        {
            // intentionally ignored
        }
    }

    private static Task<TLease> AcquireAsync<TLease>(
        OperationMetricsScope scope,
        bool enabled,
        LeaseAccessors<TLease> accessors,
        Func<CancellationToken, Task<TLease>> acquire,
        CancellationToken cancellationToken) =>
        enabled
            ? AcquireInstrumentedAsync(scope, accessors, acquire, cancellationToken)
            : acquire(cancellationToken); // no extra async layer at all when nobody is listening

    private static async Task<TLease> AcquireInstrumentedAsync<TLease>(
        OperationMetricsScope scope,
        LeaseAccessors<TLease> accessors,
        Func<CancellationToken, Task<TLease>> acquire,
        CancellationToken cancellationToken)
    {
        var recorder = scope.Recorder;
        recorder.WaitingChanged(scope, +1);
        var start = Stopwatch.GetTimestamp();
        try
        {
            var lease = await acquire(cancellationToken).ConfigureAwait(false);
            recorder.AcquireCompleted(scope, accessors.MemberName(lease), start, DataverseOperationMetrics.OutcomeSuccess, exception: null);
            return lease;
        }
        catch (Exception ex)
        {
            // Member is unknown: the pool chooses it inside acquire, and it failed.
            recorder.AcquireCompleted(scope, member: null, start, DataverseOperationRecorder.ClassifyFailure(ex, cancellationToken), ex);
            throw;
        }
        finally
        {
            recorder.WaitingChanged(scope, -1);
        }
    }

    /// <summary>Health reporting must never replace the operation's own exception, so a throwing
    /// reporter is swallowed.</summary>
    private static void TryReportConnectionFault<TLease>(LeaseAccessors<TLease> accessors, TLease lease, Exception exception)
    {
        try
        {
            accessors.ReportIfConnectionFault?.Invoke(lease, exception);
        }
        catch
        {
            // intentionally ignored
        }
    }

    /// <summary>A reporter that throws (e.g. an invalid <paramref name="maxRetryAfter"/> reaching
    /// <see cref="DataverseThrottleDetector"/>) is treated as "not throttled" so it cannot replace the
    /// operation's own exception - exactly how the former exception-filter implementation behaved,
    /// since an exception thrown inside a <c>when</c> filter is swallowed and evaluates to false.</summary>
    private static bool TryReportThrottle<TLease>(
        LeaseAccessors<TLease> accessors,
        TLease lease,
        Exception exception,
        TimeSpan? maxRetryAfter,
        out TimeSpan retryAfter)
    {
        try
        {
            return accessors.ReportIfThrottled(lease, exception, maxRetryAfter, out retryAfter);
        }
        catch
        {
            retryAfter = TimeSpan.Zero;
            return false;
        }
    }
}
