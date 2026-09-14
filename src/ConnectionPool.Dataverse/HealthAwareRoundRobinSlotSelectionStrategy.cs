using ConnectionPool.Core;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Round-robin selection that skips group members whose underlying pool is persistently failing to
/// create connections (a "dead" service user/app registration - e.g. revoked credentials), instead
/// of blindly continuing to send 1/N of traffic to a member that will just hang or fail every time.
/// Also skips members currently marked as Dataverse-throttled (<see cref="DataverseUserPool.IsThrottled"/>,
/// set via <see cref="DataverseUserPool.ReportThrottled"/> / <see cref="DataverseGroupLease.ReportIfThrottled"/>
/// - see docs/adr/0008).
///
/// Circuit-breaking (open/half-open/closed, with a real single-probe half-open - see
/// docs/adr/0010) is delegated to a shared <see cref="MemberCircuitBreaker"/>. If *all* members are
/// currently circuit-open or throttled (e.g. a shared transient outage, or every member hit its
/// budget at once), <see cref="SlotSelection.AllMembersUnavailable"/> is reported so
/// <see cref="DataverseGroupPool"/> can decide - per its configured
/// <see cref="GroupAllUnavailableBehavior"/> - whether to fail open (pick one anyway, the default,
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
        var eligible = new List<int>(members.Count);

        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];

            // Check throttle first (cheap, no side effects) so a throttled member never consumes
            // the circuit breaker's single half-open probe slot for no reason.
            if (member.IsThrottled)
            {
                continue;
            }

            if (_breaker.IsEligible(member, memberStats[i], now))
            {
                eligible.Add(i);
            }
        }

        var allUnavailable = eligible.Count == 0;

        // Fail open: if every member currently looks unhealthy/throttled, still pick one rather
        // than making the whole group unavailable (default behavior, see docs/adr/0007 #6). The
        // caller (DataverseGroupPool) decides whether to honor this pick or fail fast instead -
        // see docs/adr/0010.
        var candidates = allUnavailable ? Enumerable.Range(0, members.Count).ToList() : eligible;

        var next = Interlocked.Increment(ref _cursor);
        var index = candidates[(int)((uint)next % (uint)candidates.Count)];
        return new SlotSelection(members[index], allUnavailable);
    }

    /// <inheritdoc />
    public void ReportAcquireOutcome(DataverseUserPool member, bool succeeded) => _breaker.CompleteProbe(member, succeeded);
}
