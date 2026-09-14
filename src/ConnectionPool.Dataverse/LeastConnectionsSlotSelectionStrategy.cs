using ConnectionPool.Core;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Load-aware selection: picks the group member with the fewest currently-leased connections
/// (<see cref="PoolStats.LeasedCount"/>), instead of blindly cycling 1/N like
/// <see cref="HealthAwareRoundRobinSlotSelectionStrategy"/>. Ties are broken round-robin.
///
/// Why this matters: Dataverse's per-user service-protection limit (~52 concurrent requests) is
/// consumed per application user, not per group. Plain round-robin sends an equal share of *new*
/// acquires to every member regardless of how many requests each member is already mid-flight on -
/// if one member happens to be holding several long-running calls, round-robin keeps piling more
/// traffic onto it anyway. Least-connections instead steers new acquires toward whichever member
/// currently has the most spare budget.
///
/// This still only reacts to lease-level load (how many leases are checked out), not to
/// Dataverse-reported throttling signals on individual requests made through a leased ServiceClient
/// beyond the explicit <see cref="DataverseGroupLease.ReportIfThrottled"/> report (see docs/adr/0008).
///
/// Circuit-breaking (open/half-open/closed, with a real single-probe half-open - see
/// docs/adr/0010) is delegated to a shared <see cref="MemberCircuitBreaker"/>, identical to
/// <see cref="HealthAwareRoundRobinSlotSelectionStrategy"/>. This strategy also skips members
/// currently marked Dataverse-throttled (<see cref="DataverseUserPool.IsThrottled"/>, docs/adr/0008).
/// If *all* members are unavailable, <see cref="SlotSelection.AllMembersUnavailable"/> is reported so
/// <see cref="DataverseGroupPool"/> can apply its configured <see cref="GroupAllUnavailableBehavior"/>.
/// </summary>
public sealed class LeastConnectionsSlotSelectionStrategy : ISlotSelectionStrategy
{
    private readonly MemberCircuitBreaker _breaker;
    private int _cursor = -1;

    public LeastConnectionsSlotSelectionStrategy(int failureThreshold = 3, TimeSpan? cooldownPeriod = null)
        : this(new MemberCircuitBreaker(failureThreshold, cooldownPeriod ?? TimeSpan.FromSeconds(30)))
    {
    }

    /// <summary>Advanced constructor allowing a caller-owned <see cref="MemberCircuitBreaker"/>.</summary>
    public LeastConnectionsSlotSelectionStrategy(MemberCircuitBreaker breaker)
    {
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
    }

    public SlotSelection SelectNext(IReadOnlyList<DataverseUserPool> members, IReadOnlyList<PoolStats> memberStats)
    {
        if (members.Count == 0)
        {
            throw new InvalidOperationException("No member pools to select from.");
        }

        var now = DateTimeOffset.UtcNow;
        // Parallel to `eligible`'s indices: the claim generation (if any) IsEligible handed back for
        // that candidate - see docs/adr/0014.
        var eligible = new List<int>(members.Count);
        var claimGenerations = new List<long?>(members.Count);

        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];

            if (member.IsThrottled)
            {
                continue;
            }

            if (_breaker.IsEligible(member, memberStats[i], now, out var claimGeneration))
            {
                eligible.Add(i);
                claimGenerations.Add(claimGeneration);
            }
        }

        var allUnavailable = eligible.Count == 0;

        // Fail open: if every member currently looks unhealthy/throttled, still pick one (default
        // behavior, see docs/adr/0007 #6; caller may override via GroupAllUnavailableBehavior). None
        // of these candidates won a probe claim, so there's no generation to carry here.
        if (allUnavailable)
        {
            var fallbackCandidates = Enumerable.Range(0, members.Count).ToList();
            var minLeasedFallback = fallbackCandidates.Min(i => memberStats[i].LeasedCount);
            var tiedFallback = fallbackCandidates.Where(i => memberStats[i].LeasedCount == minLeasedFallback).ToList();
            var fallbackNext = Interlocked.Increment(ref _cursor);
            var fallbackIndex = tiedFallback[(int)((uint)fallbackNext % (uint)tiedFallback.Count)];
            return new SlotSelection(members[fallbackIndex], AllMembersUnavailable: true);
        }

        var minLeased = eligible.Min(i => memberStats[i].LeasedCount);
        var tied = eligible
            .Select((memberIndex, pos) => (memberIndex, pos))
            .Where(t => memberStats[t.memberIndex].LeasedCount == minLeased)
            .ToList();

        var next = Interlocked.Increment(ref _cursor);
        var (index, tiedPos) = tied[(int)((uint)next % (uint)tied.Count)];
        return new SlotSelection(members[index], AllMembersUnavailable: false, claimGenerations[tiedPos]);
    }

    /// <inheritdoc />
    public void ReportAcquireOutcome(DataverseUserPool member, bool succeeded) => _breaker.CompleteProbe(member, succeeded);

    public void ReportAcquireOutcome(DataverseUserPool member, bool succeeded, long? claimGeneration) =>
        _breaker.CompleteProbe(member, succeeded, claimGeneration);

    public void ReportAcquireAbandoned(DataverseUserPool member) => _breaker.AbandonProbe(member);

    public void ReportAcquireAbandoned(DataverseUserPool member, long? claimGeneration) =>
        _breaker.AbandonProbe(member, claimGeneration);
}
