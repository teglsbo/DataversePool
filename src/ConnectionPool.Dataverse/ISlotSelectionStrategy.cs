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
}

/// <summary>
/// Result of a member selection round. See <see cref="ISlotSelectionStrategy.SelectNext"/>.
/// </summary>
public readonly record struct SlotSelection(DataverseUserPool Member, bool AllMembersUnavailable);
