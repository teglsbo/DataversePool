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

    public DataversePool(
        IEnumerable<DataverseUserPool> members,
        ISlotSelectionStrategy? strategy = null,
        AllUnavailableBehavior allUnavailableBehavior = AllUnavailableBehavior.FailOpen)
    {
        _members = members?.ToArray() ?? throw new ArgumentNullException(nameof(members));
        if (_members.Count == 0)
        {
            throw new ArgumentException("At least one member pool is required.", nameof(members));
        }

        _strategy = strategy ?? new HealthAwareRoundRobinSlotSelectionStrategy();
        _allUnavailableBehavior = allUnavailableBehavior;
    }

    /// <summary>Convenience constructor for the common single-member case - equivalent to
    /// <c>new DataversePool(new[] { member }, strategy, allUnavailableBehavior)</c>. Prefer this
    /// (rather than using <paramref name="member"/> directly) if you might ever add more members
    /// later - see the type's remarks.</summary>
    public DataversePool(
        DataverseUserPool member,
        ISlotSelectionStrategy? strategy = null,
        AllUnavailableBehavior allUnavailableBehavior = AllUnavailableBehavior.FailOpen)
        : this(new[] { member ?? throw new ArgumentNullException(nameof(member)) }, strategy, allUnavailableBehavior)
    {
    }

    public IReadOnlyList<DataverseUserPool> Members => _members;

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
            ? $"All {names.Length} member(s) are currently circuit-open or throttled. Earliest known retry: {when:O}."
            : $"All {names.Length} member(s) are currently circuit-open or throttled.";

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
    public async Task<T> ExecuteWithThrottleRetryAsync<T>(
        Func<ServiceClient, CancellationToken, Task<T>> operation,
        int? maxAttempts = null,
        TimeSpan? maxRetryAfter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var attempts = maxAttempts ?? Math.Max(_members.Count, 3);
        if (attempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Must be positive.");
        }

        DataverseUserPool? previouslyThrottledMember = null;
        var previousRetryAfter = TimeSpan.Zero;

        for (var attempt = 1; ; attempt++)
        {
            var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);

            // No better option was available than the very member we just reported as throttled -
            // wait out the capped window before trying again instead of instantly re-hitting the
            // same still-over-budget connection. See the method's <remarks> above.
            if (previouslyThrottledMember is not null && ReferenceEquals(lease.Member, previouslyThrottledMember))
            {
                await Task.Delay(previousRetryAfter, cancellationToken).ConfigureAwait(false);
            }

            TimeSpan retryAfter;
            try
            {
                return await operation(lease.Resource, cancellationToken).ConfigureAwait(false);
            }
            // ReportIfThrottled always runs (left side of && is unconditionally evaluated first), so
            // the throttle window is recorded even on the final attempt - only whether we swallow the
            // exception and loop again depends on attempts remaining.
            catch (Exception ex) when (lease.ReportIfThrottled(ex, out retryAfter, maxRetryAfter) && attempt < attempts)
            {
                previouslyThrottledMember = lease.Member;
                previousRetryAfter = retryAfter;
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
