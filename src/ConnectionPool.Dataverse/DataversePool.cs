using ConnectionPool.Core;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Composes one or more <see cref="DataverseUserPool"/> instances (one per Dataverse
/// application/service user) and selects one per acquire via a pluggable
/// <see cref="ISlotSelectionStrategy"/> (round-robin by default). This is the recommended
/// top-level entry point regardless of member count: use it even for a single user if you might
/// ever need to scale out beyond that user's Dataverse service-protection limit later - going from
/// one member to several is then purely a construction/DI-config change (add another
/// <see cref="DataverseUserPool"/> to the collection), not an application code change. See
/// docs/adr/0006 and docs/adr/0019.
///
/// Note this coordination is entirely in-process: throttle/circuit state is not shared across
/// multiple instances of your application (e.g. multiple pods) targeting the same member service
/// principals - see the README's "production constraint" section and docs/adr/0009/0010.
/// </summary>
public sealed class DataversePool : IAsyncDisposable
{
    private readonly IReadOnlyList<DataverseUserPool> _members;
    private readonly ISlotSelectionStrategy _strategy;
    private readonly AllUnavailableBehavior _allUnavailableBehavior;
    private readonly PoolSizingController? _sizing;

    /// <param name="members">The member pools, one per Dataverse application/service user.</param>
    /// <param name="strategy">Member selection; defaults to <see cref="HealthAwareRoundRobinSlotSelectionStrategy"/>.</param>
    /// <param name="allUnavailableBehavior">What <see cref="AcquireAsync"/> does when every member is unavailable.</param>
    /// <param name="metricsOptions">Optional operation-metrics settings (the <c>pool.name</c> tag) for
    /// <see cref="ExecuteWithThrottleRetryAsync{T}(string, Func{ServiceClient, CancellationToken, Task{T}}, int?, TimeSpan?, CancellationToken)"/>
    /// and any <see cref="PooledOrganizationService"/> wrapping this pool. See docs/adr/0024.</param>
    /// <param name="sizingOptions">Optional automatic member sizing (docs/adr/0026). Omitted, or a
    /// <see cref="FixedPoolSizingStrategy"/>, keeps every member's configured size.</param>
    public DataversePool(
        IEnumerable<DataverseUserPool> members,
        ISlotSelectionStrategy? strategy = null,
        AllUnavailableBehavior allUnavailableBehavior = AllUnavailableBehavior.FailOpen,
        DataverseOperationMetricsOptions? metricsOptions = null,
        PoolSizingOptions? sizingOptions = null)
    {
        metricsOptions?.Validate();
        sizingOptions?.Validate();
        MetricsPoolName = metricsOptions?.PoolName ?? DataverseOperationMetricsOptions.DefaultPoolName;
        _members = members?.ToArray() ?? throw new ArgumentNullException(nameof(members));
        if (_members.Count == 0)
        {
            throw new ArgumentException("At least one member pool is required.", nameof(members));
        }

        _strategy = strategy ?? new HealthAwareRoundRobinSlotSelectionStrategy();
        _allUnavailableBehavior = allUnavailableBehavior;
        if (sizingOptions is not null && (sizingOptions.Strategy is not FixedPoolSizingStrategy || sizingOptions.ObserveResponses))
        {
            _sizing = new PoolSizingController(_members, sizingOptions);
        }
    }

    internal IOperationOutcomeSink? SizingSink => _sizing;

    /// <summary>
    /// EXPERIMENTAL. The latest service-protection budget Dataverse reported for <paramref name="member"/>
    /// (<see cref="PoolSizingOptions.ObserveResponses"/>), or <c>null</c> if observation is off or no
    /// response has been seen yet.
    /// </summary>
    public ResponseBudget? GetResponseBudget(DataverseUserPool member) => _sizing?.GetBudget(member);

    /// <summary>Per backend node statistics (responses, lowest and highest burst budget) observed for
    /// <paramref name="member"/>; empty unless response observation is on. Diagnostic only.</summary>
    public IReadOnlyList<ServerNodeStats> GetServerNodeStats(DataverseUserPool member) =>
        _sizing?.GetServerStats(member) ?? [];

    /// <summary>Convenience constructor for the common single-member case - equivalent to
    /// <c>new DataversePool(new[] { member }, strategy, allUnavailableBehavior)</c>. Prefer this
    /// (rather than using <paramref name="member"/> directly) if you might ever add more members
    /// later - see the type's remarks.</summary>
    public DataversePool(
        DataverseUserPool member,
        ISlotSelectionStrategy? strategy = null,
        AllUnavailableBehavior allUnavailableBehavior = AllUnavailableBehavior.FailOpen,
        DataverseOperationMetricsOptions? metricsOptions = null,
        PoolSizingOptions? sizingOptions = null)
        : this(new[] { member ?? throw new ArgumentNullException(nameof(member)) }, strategy, allUnavailableBehavior, metricsOptions, sizingOptions)
    {
    }

    public IReadOnlyList<DataverseUserPool> Members => _members;

    /// <summary>
    /// The member-selection strategy in use - exposed read-only so telemetry
    /// (<see cref="DataverseUserPoolMetrics"/>) can read <see cref="ISlotSelectionStrategy.Breaker"/>
    /// for a <c>breaker_state</c> gauge without this type needing its own breaker-forwarding API.
    /// </summary>
    public ISlotSelectionStrategy Strategy => _strategy;

    /// <summary>The <c>pool.name</c> tag value for operation metrics. See docs/adr/0024.</summary>
    internal string MetricsPoolName { get; }

    /// <summary>Test seam: lets tests observe operation metrics on an isolated meter.</summary>
    internal DataverseOperationRecorder Recorder { get; init; } = DataverseOperationRecorder.Shared;

    /// <exception cref="DataversePoolUnavailableException">
    /// Every member is circuit-open/throttled and this pool is configured with
    /// <see cref="AllUnavailableBehavior.FailFast"/>. See docs/adr/0010.
    /// </exception>
    public async Task<DataverseLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var stats = _members.Select(m => m.GetStats()).ToArray();
        var selection = _strategy.SelectNext(_members, stats);

        if (selection.AllMembersUnavailable && _allUnavailableBehavior == AllUnavailableBehavior.FailFast)
        {
            throw BuildUnavailableException();
        }

        try
        {
            var lease = await selection.Member.AcquireAsync(cancellationToken).ConfigureAwait(false);
            // Report the real outcome so circuit-breaker-aware strategies can close/reopen based on
            // what actually happened, instead of relying solely on the half-open probe-claim timeout
            // expiring. See docs/adr/0011. Pass the claim generation (if any) through so a
            // circuit-breaker-aware strategy can reject a stale report against a since-superseded
            // claim - see docs/adr/0014.
            _strategy.ReportAcquireOutcome(selection.Member, succeeded: true, selection.ProbeClaimGeneration);
            return new DataverseLease(selection.Member, lease);
        }
        catch (PoolAcquireTimeoutException)
        {
            // A pool-wide capacity/load timeout is not evidence about *this member's* health - it
            // just means every slot was busy for longer than PoolOptions.AcquireTimeout. Treating it
            // as a failed health probe would unnecessarily extend a recovering member's circuit
            // cooldown. Release the probe claim (if one was won) without asserting failure. See
            // docs/adr/0013.
            _strategy.ReportAcquireAbandoned(selection.Member, selection.ProbeClaimGeneration);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller-initiated cancellation is not a signal about the member's health - don't let it
            // flip/extend the circuit's open state.
            throw;
        }
        catch
        {
            _strategy.ReportAcquireOutcome(selection.Member, succeeded: false, selection.ProbeClaimGeneration);
            throw;
        }
    }

    private DataversePoolUnavailableException BuildUnavailableException()
    {
        var names = _members.Select(m => m.Name).ToArray();
        var now = DateTimeOffset.UtcNow;
        var earliestRetryAt = _members
            .Select(m => m.ThrottledUntil)
            .Where(t => t is not null && t.Value > now) // ignore stale ticks that already expired - see docs/adr/0011
            .Select(t => t!.Value)
            .DefaultIfEmpty()
            .Min();
        var earliest = earliestRetryAt == default ? (DateTimeOffset?)null : earliestRetryAt;

        var message = earliest is { } when
            ? $"All {names.Length} member(s) are currently circuit-open, throttled or quarantined. Earliest known retry: {when:O}."
            : $"All {names.Length} member(s) are currently circuit-open, throttled or quarantined.";

        return new DataversePoolUnavailableException(message, names, earliest);
    }

    /// <summary>
    /// Warms up each member pool in turn. Members are warmed sequentially (not just each member's
    /// own connections) to keep the "never clone in parallel" guarantee (docs/adr/0002) global
    /// across the whole group, not just within a single member pool.
    /// </summary>
    public async Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        foreach (var member in _members)
        {
            await member.WarmupAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that no two members authenticate as the same Dataverse user in the same organization.
    /// Service-protection budgets are per user, so two members on one user share a budget without
    /// seeing each other's 429s and both overshoot. Calls <c>WhoAmI</c> once per member (sequentially,
    /// one lease each) - opt-in, intended once at startup after <see cref="WarmupAsync"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Two or more members resolve to the same user.</exception>
    public async Task ValidateDistinctIdentitiesAsync(CancellationToken cancellationToken = default)
    {
        var identities = new List<(string Member, Guid UserId, Guid OrganizationId)>(_members.Count);
        foreach (var member in _members)
        {
            await using var lease = await member.AcquireAsync(cancellationToken).ConfigureAwait(false);
            var who = (Microsoft.Crm.Sdk.Messages.WhoAmIResponse)await lease.Resource
                .ExecuteAsync(new Microsoft.Crm.Sdk.Messages.WhoAmIRequest(), cancellationToken)
                .ConfigureAwait(false);
            identities.Add((member.Name, who.UserId, who.OrganizationId));
        }

        var duplicates = FindDuplicateIdentities(identities);
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "Members share a Dataverse user (and therefore one service-protection budget): " +
                string.Join("; ", duplicates.Select(g => string.Join(", ", g))) +
                ". Use exactly one member per application user.");
        }
    }

    internal static IReadOnlyList<IReadOnlyList<string>> FindDuplicateIdentities(
        IEnumerable<(string Member, Guid UserId, Guid OrganizationId)> identities) =>
        identities
            .GroupBy(i => (i.UserId, i.OrganizationId))
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<string>)g.Select(i => i.Member).ToArray())
            .ToArray();

    /// <summary>
    /// Runs <paramref name="operation"/> against a leased <see cref="ServiceClient"/>, and if it
    /// throws a Dataverse HTTP 429/service-protection signal (detected via
    /// <see cref="DataverseThrottleDetector"/>, same as <see cref="DataverseLease.ReportIfThrottled(Exception, TimeSpan?)"/>),
    /// reports the throttle on the member that served it and retries. Works correctly regardless of
    /// how many members this pool has - including exactly one:
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fresh <see cref="AcquireAsync"/> after a throttle report will, thanks to the pool's
    /// throttle-aware selection strategy, steer away from the just-throttled member as long as a
    /// different one is available - in which case the retry happens immediately, no wait needed. But
    /// if the freshly-acquired lease comes from the <b>same</b> member that was just throttled - the
    /// only possible outcome for a single-member pool, or a multi-member pool where every member is
    /// currently over budget and <see cref="AllUnavailableBehavior.FailOpen"/> hands one back anyway -
    /// retrying instantly would just re-hit the same still-over-budget connection for no benefit. This
    /// method detects that specific case and waits out the capped <c>Retry-After</c> before trying
    /// again, so a single-member pool (and an all-throttled multi-member pool) both get a real,
    /// bounded wait instead of a useless instant re-throttle. See docs/adr/0019.
    /// </para>
    /// <para>
    /// Only a recognized throttling signal is retried - any other exception from
    /// <paramref name="operation"/> propagates immediately, unretried. General-purpose retry/circuit
    /// -breaking for arbitrary failures is deliberately out of scope here (that's what the optional
    /// Polly adapter package is for - see docs/adr/0005); this method exists specifically to close
    /// the "retry when throttled" gap, not to become a general resilience pipeline.
    /// </para>
    /// <para>
    /// Note <see cref="DataversePool.AcquireAsync"/> itself is not retried here - if acquiring a
    /// lease fails (e.g. <see cref="DataversePoolUnavailableException"/> when every member is
    /// circuit-open/throttled and <see cref="AllUnavailableBehavior.FailFast"/> is configured),
    /// that exception propagates immediately; only failures from <paramref name="operation"/> itself,
    /// once a lease was successfully acquired, are eligible for this retry loop.
    /// </para>
    /// </remarks>
    /// <param name="operation">The operation to run against the leased <see cref="ServiceClient"/>.</param>
    /// <param name="maxAttempts">
    /// Maximum number of attempts (not additional retries - a value of 1 never retries). Defaults to
    /// <c>Math.Max(member count, 3)</c> - a plain member-count default (as used before this pool
    /// supported the single-member case well) would give a single-member pool exactly one attempt,
    /// i.e. no retry at all by default. Must be positive.
    /// </param>
    /// <param name="maxRetryAfter">
    /// Cap applied to Dataverse's reported <c>Retry-After</c> before it's recorded as the throttled
    /// member's cooldown window (and, for the same-member case above, before it's actually waited
    /// out). Defaults to <see cref="DataverseThrottleDetector.DefaultMaxRetryAfter"/> - see that
    /// constant's docs for why Dataverse's raw value (observed up to ~17 minutes) is not always
    /// honored verbatim.
    /// </param>
    /// <param name="cancellationToken">
    /// Propagated to <see cref="AcquireAsync"/>, <paramref name="operation"/>, and the same-member
    /// wait itself.
    /// </param>
    /// <remarks>Operation metrics (docs/adr/0024) record this call with
    /// <c>dataverse.operation.name</c> = <see cref="DataverseOperationMetrics.CustomOperationName"/>;
    /// use the overload taking an <c>operationName</c> to distinguish workloads.</remarks>
    public Task<T> ExecuteWithThrottleRetryAsync<T>(
        Func<ServiceClient, CancellationToken, Task<T>> operation,
        int? maxAttempts = null,
        TimeSpan? maxRetryAfter = null,
        CancellationToken cancellationToken = default) =>
        ExecuteWithThrottleRetryAsync(DataverseOperationMetrics.CustomOperationName, operation, maxAttempts, maxRetryAfter, cancellationToken);

    /// <summary>
    /// Same as <see cref="ExecuteWithThrottleRetryAsync{T}(Func{ServiceClient, CancellationToken, Task{T}}, int?, TimeSpan?, CancellationToken)"/>,
    /// but tags its operation metrics with <paramref name="operationName"/>. See docs/adr/0024.
    /// </summary>
    /// <param name="operationName">
    /// Low-cardinality workload label recorded as <c>dataverse.operation.name</c>, e.g.
    /// <c>"import_accounts"</c>. Must be a fixed string chosen at the call site - never derived from
    /// record ids, entity values, user input, or exception text, since every distinct value creates
    /// a new time series in the metrics backend.
    /// </param>
    /// <param name="operation">See the other overload.</param>
    /// <param name="maxAttempts">See the other overload.</param>
    /// <param name="maxRetryAfter">See the other overload.</param>
    /// <param name="cancellationToken">See the other overload.</param>
    public async Task<T> ExecuteWithThrottleRetryAsync<T>(
        string operationName,
        Func<ServiceClient, CancellationToken, Task<T>> operation,
        int? maxAttempts = null,
        TimeSpan? maxRetryAfter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);

        var attempts = maxAttempts ?? Math.Max(_members.Count, 3);
        if (attempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Must be positive.");
        }

        // The loop itself (lease release before any same-member Retry-After wait, throttle
        // reporting on every failed attempt, exception identity, metrics) lives in the shared
        // executor so it's unit-testable with fake leases. See docs/adr/0019 and docs/adr/0024.
        return await DataverseOperationExecutor.ExecuteAsync(
            new OperationMetricsScope(Recorder, MetricsPoolName, operationName),
            DataverseLeaseAccessors.Instance,
            AcquireAsync,
            (lease, ct) => operation(lease.Resource, ct),
            attempts,
            maxRetryAfter,
            cancellationToken,
            _sizing).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _sizing?.Dispose();
        foreach (var member in _members)
        {
            await member.DisposeAsync().ConfigureAwait(false);
        }
    }
}
