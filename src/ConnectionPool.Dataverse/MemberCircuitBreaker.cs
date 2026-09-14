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
/// A member is "open" once <see cref="PoolStats.ConsecutiveCreateFailures"/> reaches the configured
/// failure threshold. Once <c>cooldownPeriod</c> has elapsed since it opened, it becomes "half-open":
/// unlike the original implementation (which let every concurrent caller treat a half-open member as
/// eligible - a thundering-herd risk on the very connection that's trying to recover), only a single
/// caller per cooldown window wins the probe slot via <see cref="IsEligible"/>. Everyone else stays
/// routed elsewhere until that probe's outcome is visible (the member's
/// <see cref="PoolStats.ConsecutiveCreateFailures"/> either resets below threshold - success - or the
/// probe claim itself times out, allowing a fresh probe).
/// </summary>
public sealed class MemberCircuitBreaker
{
    private sealed class CircuitState
    {
        public DateTimeOffset OpenedAt;
        public DateTimeOffset? ProbeClaimedAt;
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

        _failureThreshold = failureThreshold;
        _cooldownPeriod = cooldownPeriod;

        // If a claimed probe never actually resolves (e.g. the caller acquired the lease but the
        // process crashed, or it never triggered CreateAsync at all), don't let that block recovery
        // forever - allow a fresh probe to be claimed after this window.
        _probeClaimTimeout = probeClaimTimeout ?? cooldownPeriod;
    }

    /// <summary>
    /// Returns true if <paramref name="member"/> is eligible for selection right now: either its
    /// circuit is closed, or it is half-open and this call wins the single probe slot for the
    /// current cooldown window. Call at most once per candidate per selection round - winning the
    /// probe slot is a one-shot claim, so calling this twice for the same half-open member in the
    /// same round would incorrectly make it look ineligible the second time.
    /// </summary>
    public bool IsEligible(DataverseUserPool member, PoolStats stats, DateTimeOffset now)
    {
        var isOpen = stats.ConsecutiveCreateFailures >= _failureThreshold;

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
            return true; // this caller wins the single half-open probe
        }
    }
}
