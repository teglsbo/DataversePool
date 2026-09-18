using ConnectionPool.Core;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Round-robin selection that skips members whose underlying pool is persistently failing to
/// create connections (a "dead" service user/app registration - e.g. revoked credentials), instead
/// of blindly continuing to send 1/N of traffic to a member that will just hang or fail every time.
/// Also skips members currently marked as Dataverse-throttled (<see cref="DataverseUserPool.IsThrottled"/>,
/// set via <see cref="DataverseUserPool.ReportThrottled"/> / <see cref="DataverseLease.ReportIfThrottled"/>
/// - see docs/adr/0008).
///
/// Circuit-breaking (open/half-open/closed, with a real single-probe half-open - see
/// docs/adr/0010) is delegated to a shared <see cref="MemberCircuitBreaker"/>. If *all* members are
/// currently circuit-open or throttled (e.g. a shared transient outage, or every member hit its
/// budget at once), <see cref="SlotSelection.AllMembersUnavailable"/> is reported so
/// <see cref="DataversePool"/> can decide - per its configured
/// <see cref="AllUnavailableBehavior"/> - whether to fail open (pick one anyway, the default,
/// preserving docs/adr/0007 #6) or fail fast.
/// </summary>
public sealed class HealthAwareRoundRobinSlotSelectionStrategy : ISlotSelectionStrategy
{
    private readonly MemberCircuitBreaker _breaker;
    private int _cursor = -1;

    public HealthAwareRoundRobinSlotSelectionStrategy(int failureThreshold = 3, TimeSpan? cooldownPeriod = null)
        : this(new MemberCircuitBreaker(failureThreshold, cooldownPeriod ?? TimeSpan.FromSeconds(30)))
    {
    }

    /// <summary>Advanced constructor allowing a caller-owned <see cref="MemberCircuitBreaker"/> (e.g. shared with another strategy instance, or configured with a custom probe-claim timeout).</summary>
    public HealthAwareRoundRobinSlotSelectionStrategy(MemberCircuitBreaker breaker)
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
        // Parallel to `eligible`'s indices: the claim generation (if any) that IsEligible handed
        // back for that candidate, so it can be threaded through SlotSelection for whichever
        // candidate ultimately gets chosen below - see docs/adr/0014.
        var eligible = new List<int>(members.Count);
        var claimGenerations = new List<long?>(members.Count);

        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];

            // Check throttle first (cheap, no side effects) so a throttled member never consumes
            // the circuit breaker's single half-open probe slot for no reason.
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

        // Fail open: if every member currently looks unhealthy/throttled, still pick one rather
        // than making the whole group unavailable (default behavior, see docs/adr/0007 #6). The
        // caller (DataversePool) decides whether to honor this pick or fail fast instead -
        // see docs/adr/0010. None of these candidates won a probe claim (IsEligible only returns a
        // generation when it returns true), so there's no generation to carry for this branch.
        if (allUnavailable)
        {
            var fallbackNext = Interlocked.Increment(ref _cursor);
            var fallbackIndex = (int)((uint)fallbackNext % (uint)members.Count);
            return new SlotSelection(members[fallbackIndex], AllMembersUnavailable: true);
        }

        var next = Interlocked.Increment(ref _cursor);
        var pick = (int)((uint)next % (uint)eligible.Count);

        // Any OTHER half-open candidate scanned above (claimGenerations[i] not null) but not the
        // one selected this round won a real probe claim from IsEligible - without releasing it,
        // that member stays blocked from a fresh probe until probeClaimTimeout expires, even though
        // no attempt is actually in flight for it. Release those unused claims immediately instead
        // of leaking them. See docs/adr/0022.
        for (var i = 0; i < eligible.Count; i++)
        {
            if (i != pick && claimGenerations[i] is { } unusedClaim)
            {
                _breaker.AbandonProbe(members[eligible[i]], unusedClaim);
            }
        }

        return new SlotSelection(members[eligible[pick]], AllMembersUnavailable: false, claimGenerations[pick]);
    }

    /// <inheritdoc />
    public void ReportAcquireOutcome(DataverseUserPool member, bool succeeded) => _breaker.CompleteProbe(member, succeeded);

    public void ReportAcquireOutcome(DataverseUserPool member, bool succeeded, long? claimGeneration) =>
        _breaker.CompleteProbe(member, succeeded, claimGeneration);

    public void ReportAcquireAbandoned(DataverseUserPool member) => _breaker.AbandonProbe(member);

    public void ReportAcquireAbandoned(DataverseUserPool member, long? claimGeneration) =>
        _breaker.AbandonProbe(member, claimGeneration);
}
