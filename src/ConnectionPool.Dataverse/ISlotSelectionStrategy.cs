using ConnectionPool.Core;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Pluggable strategy for choosing which member <see cref="DataverseUserPool"/> of a
/// <see cref="DataverseGroupPool"/> should service the next acquire. v1 ships round-robin,
/// health-aware round-robin, and least-connections; this interface exists so further strategies
/// can be added without changing DataverseGroupPool's public API.
/// See docs/adr/0006-dual-pooling-model-single-user-and-round-robin-group.md.
/// </summary>
public interface ISlotSelectionStrategy
{
    /// <summary>
    /// Picks the next member to serve an acquire. <see cref="SlotSelection.AllMembersUnavailable"/>
    /// tells the caller (<see cref="DataverseGroupPool"/>) whether <see cref="SlotSelection.Member"/>
    /// was chosen from a genuinely eligible set, or is a "fail open" pick because every member
    /// currently looks circuit-open/throttled - see docs/adr/0010-configurable-fail-fast-and-single-probe-half-open.md.
    /// A strategy with no health/throttle awareness at all (e.g. plain round-robin) should always
    /// report <c>false</c>.
    /// </summary>
    SlotSelection SelectNext(IReadOnlyList<DataverseUserPool> members, IReadOnlyList<PoolStats> memberStats);

    /// <summary>
    /// Reports the real outcome of the acquire attempt made against a previously selected member,
    /// called by <see cref="DataverseGroupPool"/> right after every attempt (success or failure) -
    /// not only when the selection was a half-open probe. Default no-op: strategies without
    /// circuit-breaking (e.g. plain round-robin) never need to override this. Circuit-breaker-aware
    /// strategies use this to close/reopen based on the real result instead of relying purely on a
    /// probe-claim timeout - see docs/adr/0011-outcome-reporting-and-finalizer-thread-safety.md.
    /// </summary>
    void ReportAcquireOutcome(DataverseUserPool member, bool succeeded)
    {
    }

    /// <summary>
    /// Overload also carrying the probe claim generation from <see cref="SlotSelection.ProbeClaimGeneration"/>
    /// (null when the selection wasn't a half-open probe claim), so a circuit-breaker-aware strategy
    /// can pass it through to <see cref="MemberCircuitBreaker.CompleteProbe(DataverseUserPool, bool, long?)"/>
    /// and reject a stale report against a since-superseded claim. Default implementation forwards to
    /// the 2-arg overload for strategies that don't need claim correlation. See docs/adr/0014.
    /// </summary>
    void ReportAcquireOutcome(DataverseUserPool member, bool succeeded, long? claimGeneration) =>
        ReportAcquireOutcome(member, succeeded);

    /// <summary>
    /// Reports that an acquire attempt against a previously selected member was abandoned for a
    /// reason unrelated to the member's own health - currently, only a pool-wide
    /// <see cref="ConnectionPool.Core.PoolAcquireTimeoutException"/> (capacity/load saturation, not
    /// evidence the member is unhealthy). Called by <see cref="DataverseGroupPool"/> instead of
    /// <see cref="ReportAcquireOutcome(DataverseUserPool, bool)"/> in that case, so a half-open probe
    /// claim is released without treating a mere capacity timeout as a failed health probe (which
    /// would otherwise unnecessarily extend a recovering member's cooldown). Default no-op: strategies
    /// without circuit-breaking never need to override this. See docs/adr/0013.
    /// </summary>
    void ReportAcquireAbandoned(DataverseUserPool member)
    {
    }

    /// <summary>
    /// Overload also carrying the probe claim generation - see the
    /// <see cref="ReportAcquireOutcome(DataverseUserPool, bool, long?)"/> rationale. Default
    /// implementation forwards to the 1-arg overload. See docs/adr/0014.
    /// </summary>
    void ReportAcquireAbandoned(DataverseUserPool member, long? claimGeneration) =>
        ReportAcquireAbandoned(member);
}

/// <summary>
/// Result of a member selection round. See <see cref="ISlotSelectionStrategy.SelectNext"/>.
/// <see cref="ProbeClaimGeneration"/> is non-null only when this selection actually won a
/// <see cref="MemberCircuitBreaker"/> half-open probe claim; strategies must pass it back unchanged
/// via <see cref="ISlotSelectionStrategy.ReportAcquireOutcome(DataverseUserPool, bool, long?)"/>/
/// <see cref="ISlotSelectionStrategy.ReportAcquireAbandoned(DataverseUserPool, long?)"/> so a
/// stale/late report cannot corrupt a newer claim on the same member - see docs/adr/0014.
/// </summary>
public readonly record struct SlotSelection(DataverseUserPool Member, bool AllMembersUnavailable, long? ProbeClaimGeneration = null);
