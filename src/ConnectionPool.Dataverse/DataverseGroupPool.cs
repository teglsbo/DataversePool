using ConnectionPool.Core;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Composes multiple <see cref="DataverseUserPool"/> instances (typically one per Dataverse
/// application/service user) and selects one per acquire via a pluggable
/// <see cref="ISlotSelectionStrategy"/> (round-robin by default). This is how you scale out beyond
/// a single user's Dataverse service-protection limit - see docs/adr/0006.
///
/// Note this coordination is entirely in-process: throttle/circuit state is not shared across
/// multiple instances of your application (e.g. multiple pods) targeting the same member service
/// principals - see the README's "production constraint" section and docs/adr/0009/0010.
/// </summary>
public sealed class DataverseGroupPool : IAsyncDisposable
{
    private readonly IReadOnlyList<DataverseUserPool> _members;
    private readonly ISlotSelectionStrategy _strategy;
    private readonly GroupAllUnavailableBehavior _allUnavailableBehavior;

    public DataverseGroupPool(
        IEnumerable<DataverseUserPool> members,
        ISlotSelectionStrategy? strategy = null,
        GroupAllUnavailableBehavior allUnavailableBehavior = GroupAllUnavailableBehavior.FailOpen)
    {
        _members = members?.ToArray() ?? throw new ArgumentNullException(nameof(members));
        if (_members.Count == 0)
        {
            throw new ArgumentException("At least one member pool is required.", nameof(members));
        }

        _strategy = strategy ?? new HealthAwareRoundRobinSlotSelectionStrategy();
        _allUnavailableBehavior = allUnavailableBehavior;
    }

    public IReadOnlyList<DataverseUserPool> Members => _members;

    /// <exception cref="DataverseGroupUnavailableException">
    /// Every member is circuit-open/throttled and this pool is configured with
    /// <see cref="GroupAllUnavailableBehavior.FailFast"/>. See docs/adr/0010.
    /// </exception>
    public async Task<DataverseGroupLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var stats = _members.Select(m => m.GetStats()).ToArray();
        var selection = _strategy.SelectNext(_members, stats);

        if (selection.AllMembersUnavailable && _allUnavailableBehavior == GroupAllUnavailableBehavior.FailFast)
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
            return new DataverseGroupLease(selection.Member, lease);
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

    private DataverseGroupUnavailableException BuildUnavailableException()
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
            ? $"All {names.Length} group member(s) are currently circuit-open or throttled. Earliest known retry: {when:O}."
            : $"All {names.Length} group member(s) are currently circuit-open or throttled.";

        return new DataverseGroupUnavailableException(message, names, earliest);
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
    /// Runs <paramref name="operation"/> against a leased <see cref="ServiceClient"/>, and if it
    /// throws a Dataverse HTTP 429/service-protection signal (detected via
    /// <see cref="DataverseThrottleDetector"/>, same as <see cref="DataverseGroupLease.ReportIfThrottled"/>),
    /// reports the throttle on the member that served it and retries against a freshly-acquired
    /// lease - which, thanks to the group's throttle-aware selection strategy, will steer away from
    /// the just-throttled member as long as another one is available. See docs/adr/0017.
    ///
    /// <para>
    /// This only retries on a recognized throttling signal - any other exception from
    /// <paramref name="operation"/> propagates immediately, unretried. General-purpose retry/circuit
    /// -breaking for arbitrary failures is deliberately out of scope here (that's what the optional
    /// Polly adapter package is for - see docs/adr/0005); this method exists specifically to close
    /// the "retry on a different group member when throttled" gap, not to become a general resilience
    /// pipeline.
    /// </para>
    ///
    /// <para>
    /// Note <see cref="DataverseGroupPool.AcquireAsync"/> itself is not retried here - if acquiring a
    /// lease fails (e.g. <see cref="DataverseGroupUnavailableException"/> when every member is
    /// circuit-open/throttled and <see cref="GroupAllUnavailableBehavior.FailFast"/> is configured),
    /// that exception propagates immediately; only failures from <paramref name="operation"/> itself,
    /// once a lease was successfully acquired, are eligible for this retry loop.
    /// </para>
    /// </summary>
    /// <param name="operation">The operation to run against the leased <see cref="ServiceClient"/>.</param>
    /// <param name="maxAttempts">
    /// Maximum number of attempts (not additional retries - a value of 1 never retries). Defaults to
    /// the number of group members, so by default every member gets at most one attempt before
    /// giving up. Must be positive.
    /// </param>
    /// <param name="maxRetryAfter">
    /// Cap applied to Dataverse's reported <c>Retry-After</c> before it's recorded as the throttled
    /// member's cooldown window. Defaults to <see cref="DataverseThrottleDetector.DefaultMaxRetryAfter"/>
    /// - see that constant's docs for why Dataverse's raw value (observed up to ~17 minutes) is not
    /// always honored verbatim.
    /// </param>
    /// <param name="cancellationToken">
    /// Propagated to both <see cref="AcquireAsync"/> and <paramref name="operation"/>.
    /// </param>
    public async Task<T> ExecuteWithThrottleRetryAsync<T>(
        Func<ServiceClient, CancellationToken, Task<T>> operation,
        int? maxAttempts = null,
        TimeSpan? maxRetryAfter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var attempts = maxAttempts ?? _members.Count;
        if (attempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Must be positive.");
        }

        for (var attempt = 1; ; attempt++)
        {
            var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await operation(lease.Resource, cancellationToken).ConfigureAwait(false);
            }
            // ReportIfThrottled always runs (left side of && is unconditionally evaluated first), so
            // the throttle window is recorded even on the final attempt - only whether we swallow the
            // exception and loop again depends on attempts remaining.
            catch (Exception ex) when (lease.ReportIfThrottled(ex, maxRetryAfter) && attempt < attempts)
            {
                // Fall through to the next loop iteration - a fresh AcquireAsync will steer away from
                // the member just marked throttled, per the group's selection strategy.
            }
            finally
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var member in _members)
        {
            await member.DisposeAsync().ConfigureAwait(false);
        }
    }
}
