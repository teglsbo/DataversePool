using ConnectionPool.Core;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Per-process circuit-breaker bookkeeping for group member selection strategies. Deliberately has
/// no cross-process/shared state (no external store, no distributed lock) - see
/// docs/adr/0010-configurable-fail-fast-and-single-probe-half-open.md for why that line was drawn
/// here. Shared by <see cref="HealthAwareRoundRobinSlotSelectionStrategy"/> and
/// <see cref="LeastConnectionsSlotSelectionStrategy"/> so both get identical, correct half-open
/// semantics without duplicating the concurrency-sensitive logic.
///
/// A member is "open" once <see cref="PoolStats.ConsecutiveCreateFailures"/> OR
/// <see cref="PoolStats.ConsecutiveOperationalFailures"/> reaches the configured failure threshold -
/// see docs/adr/0012 for why operational (not just creation) failures now count. Once
/// <c>cooldownPeriod</c> has elapsed since it opened, it becomes "half-open":
/// unlike the original implementation (which let every concurrent caller treat a half-open member as
/// eligible - a thundering-herd risk on the very connection that's trying to recover), only a single
/// caller per cooldown window wins the probe slot via <see cref="IsEligible"/>. Everyone else stays
/// routed elsewhere until that probe's outcome is visible (the member's failure counters reset below
/// threshold - success, ideally reported explicitly via <see cref="CompleteProbe"/> - or the probe
/// claim itself times out, allowing a fresh probe).
/// </summary>
public sealed class MemberCircuitBreaker
{
    private sealed class CircuitState
    {
        public DateTimeOffset OpenedAt;
        public DateTimeOffset? ProbeClaimedAt;

        // Incremented every time a new half-open probe claim is granted. Lets CompleteProbe/
        // AbandonProbe callers that captured the generation at claim time (see the IsEligible
        // overload below) detect and ignore a stale report belonging to an already-superseded
        // claim, instead of accidentally mutating a newer caller's in-flight probe - see docs/adr/0014.
        public long ClaimGeneration;
    }

    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldownPeriod;
    private readonly TimeSpan _probeClaimTimeout;
    private readonly Dictionary<DataverseUserPool, CircuitState> _state = new();
    private readonly object _lock = new();

    public MemberCircuitBreaker(int failureThreshold, TimeSpan cooldownPeriod, TimeSpan? probeClaimTimeout = null)
    {
        if (failureThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold), "Must be positive.");
        }

        if (cooldownPeriod <= TimeSpan.Zero)
        {
            // Zero/negative would make every half-open check win the probe simultaneously,
            // defeating the entire single-probe guarantee this type exists for. See docs/adr/0011.
            throw new ArgumentOutOfRangeException(nameof(cooldownPeriod), "Must be a positive duration.");
        }

        if (probeClaimTimeout is { } explicitTimeout && explicitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(probeClaimTimeout), "Must be a positive duration.");
        }

        _failureThreshold = failureThreshold;
        _cooldownPeriod = cooldownPeriod;

        // If a claimed probe never actually resolves (e.g. the caller acquired the lease but the
        // process crashed, or it never triggered CreateAsync at all), don't let that block recovery
        // forever - allow a fresh probe to be claimed after this window. Prefer calling
        // CompleteProbe explicitly (see below) so this timeout is only a fallback, not the primary
        // mechanism - see docs/adr/0011.
        _probeClaimTimeout = probeClaimTimeout ?? cooldownPeriod;
    }

    /// <summary>
    /// Returns true if <paramref name="member"/> is eligible for selection right now: either its
    /// circuit is closed, or it is half-open and this call wins the single probe slot for the
    /// current cooldown window. Call at most once per candidate per selection round - winning the
    /// probe slot is a one-shot claim, so calling this twice for the same half-open member in the
    /// same round would incorrectly make it look ineligible the second time.
    /// </summary>
    public bool IsEligible(DataverseUserPool member, PoolStats stats, DateTimeOffset now) =>
        IsEligible(member, stats, now, out _);

    /// <summary>
    /// Overload that also hands back an opaque claim generation whenever this call actually wins a
    /// half-open probe slot (null in every other case - circuit closed, still fully open, or denied
    /// because someone else currently owns the claim). Callers that plan to later report the outcome
    /// via <see cref="CompleteProbe"/>/<see cref="AbandonProbe"/> should capture and pass this value
    /// back so a stale/late report from a since-superseded attempt cannot corrupt a newer claim - see
    /// docs/adr/0014.
    /// </summary>
    public bool IsEligible(DataverseUserPool member, PoolStats stats, DateTimeOffset now, out long? claimGeneration)
    {
        claimGeneration = null;

        // Open on either signal: a member that can't be created, OR one that gets created fine but
        // keeps failing operationally (PooledLease.MarkUnhealthy), should both trip the circuit -
        // see docs/adr/0012.
        var isOpen = stats.ConsecutiveCreateFailures >= _failureThreshold
            || stats.ConsecutiveOperationalFailures >= _failureThreshold;

        lock (_lock)
        {
            if (!isOpen)
            {
                _state.Remove(member); // healthy again - clear all breaker bookkeeping
                return true;
            }

            if (!_state.TryGetValue(member, out var state))
            {
                _state[member] = new CircuitState { OpenedAt = now };
                return false; // just opened this round - not eligible yet
            }

            if (now - state.OpenedAt < _cooldownPeriod)
            {
                return false; // still fully open
            }

            if (state.ProbeClaimedAt is { } claimedAt && now - claimedAt < _probeClaimTimeout)
            {
                return false; // another caller already owns the probe slot for this window
            }

            state.ProbeClaimedAt = now;
            state.ClaimGeneration++;
            claimGeneration = state.ClaimGeneration;
            return true; // this caller wins the single half-open probe
        }
    }

    /// <summary>
    /// Reports the real outcome of an acquire attempt for <paramref name="member"/>. Callers should
    /// invoke this exactly once per attempt (success or failure), in a try/finally around whatever
    /// operation <see cref="IsEligible"/> gated - not just when a probe was won. This is what lets
    /// the breaker react to the actual result instead of relying solely on <c>probeClaimTimeout</c>
    /// expiring, closing the residual thundering-herd window from a stale claim.
    ///
    /// Note this remains an approximation, not a perfect guarantee: if the acquire is satisfied by an
    /// already-created idle resource (no <c>CreateAsync</c> call at all), a "success" here does not
    /// prove the member can create fresh connections again - and if the in-flight attempt hangs
    /// indefinitely (no <see cref="PoolOptions.CreateTimeout"/> configured), neither outcome is ever
    /// reported and the <c>probeClaimTimeout</c> fallback is still what eventually allows a new
    /// probe. Configure <see cref="PoolOptions.CreateTimeout"/> to bound that residual case. See
    /// docs/adr/0011.
    /// </summary>
    public void CompleteProbe(DataverseUserPool member, bool succeeded) => CompleteProbe(member, succeeded, claimGeneration: null);

    /// <summary>
    /// Overload accepting the claim generation captured from <see cref="IsEligible(DataverseUserPool, PoolStats, DateTimeOffset, out long?)"/>.
    /// If a non-null generation is passed and it no longer matches the member's current claim (a
    /// newer probe has since been won), this report is silently ignored instead of mutating that
    /// newer claim's state - see docs/adr/0014. Pass <c>null</c> to preserve the old,
    /// generation-unaware behavior (e.g. when the caller never captured one).
    /// </summary>
    public void CompleteProbe(DataverseUserPool member, bool succeeded, long? claimGeneration)
    {
        lock (_lock)
        {
            if (!_state.TryGetValue(member, out var state))
            {
                return; // already closed/removed - nothing to reconcile
            }

            if (claimGeneration is { } gen && gen != state.ClaimGeneration)
            {
                return; // stale report for a since-superseded probe claim - ignore, see docs/adr/0014
            }

            if (succeeded)
            {
                _state.Remove(member); // close the circuit immediately rather than waiting for the next stats snapshot
                return;
            }

            // Failed attempt: restart the cooldown window from now and release the claim so the
            // *next* cooldown's probe isn't blocked by this attempt's now-resolved claim.
            state.OpenedAt = DateTimeOffset.UtcNow;
            state.ProbeClaimedAt = null;
        }
    }

    /// <summary>
    /// Releases a claimed half-open probe without asserting anything about the member's health -
    /// use when an attempt was abandoned for a reason unrelated to the member itself, e.g. the
    /// pool-wide capacity wait timed out (<see cref="PoolOptions.AcquireTimeout"/>/
    /// <see cref="PoolAcquireTimeoutException"/>) before the member's <c>AcquireAsync</c> even got a
    /// chance to attempt creation. Unlike <see cref="CompleteProbe"/>'s failure branch, this does
    /// NOT restart the cooldown window - a capacity/load timeout is not evidence the member is
    /// unhealthy, so extending its open-circuit cooldown on that basis would be wrong. It just frees
    /// the claim so a fresh probe can be attempted. See docs/adr/0013.
    /// </summary>
    public void AbandonProbe(DataverseUserPool member) => AbandonProbe(member, claimGeneration: null);

    /// <summary>
    /// Overload accepting the claim generation captured at claim time (see
    /// <see cref="CompleteProbe(DataverseUserPool, bool, long?)"/> for the same rationale) so a
    /// stale/late abandon cannot clear a newer, still-active claim.
    /// </summary>
    public void AbandonProbe(DataverseUserPool member, long? claimGeneration)
    {
        lock (_lock)
        {
            if (!_state.TryGetValue(member, out var state))
            {
                return;
            }

            if (claimGeneration is { } gen && gen != state.ClaimGeneration)
            {
                return; // stale - a newer probe already superseded this one, see docs/adr/0014
            }

            state.ProbeClaimedAt = null;
        }
    }
}
