using ConnectionPool.Core;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Headroom-aware selection (docs/research/autoscaling.md §9.6, items 1-2). Like
/// <see cref="LeastConnectionsSlotSelectionStrategy"/> it skips throttled and circuit-open members,
/// but instead of ranking survivors by raw <see cref="PoolStats.LeasedCount"/> it ranks them by
/// <b>headroom</b>: <c>(cap - leased) / cap</c>, where
/// <c>cap = min(MaxSize, dop_hint, rampCap)</c>.
///
/// <list type="bullet">
/// <item><description><c>dop_hint</c>: <see cref="DataverseUserPool.RecommendedDegreesOfParallelism"/>
/// when observed and <see cref="HonorDopHint"/> is set.</description></item>
/// <item><description><c>rampCap</c> (slow-start after a 429): a throttle is not a failure and is not
/// probed (the server's <c>Retry-After</c> already says when to retry), but releasing every queued
/// caller onto the member the instant the window ends can re-trip the limit. So after
/// <see cref="DataverseUserPool.ThrottledUntil"/> passes, the member's cap starts at
/// <see cref="RampStart"/> and grows by one every <see cref="RampStepInterval"/> until it reaches the
/// full cap. A new throttle moves <c>ThrottledUntil</c> forward and so restarts the ramp. Stateless:
/// derived from <c>ThrottledUntil</c> alone.</description></item>
/// </list>
///
/// The ramp is a <b>ranking preference, not a hard limit</b>: a member over its cap just scores
/// negative and is chosen last. If every member is saturated, the least-saturated still serves the
/// call (the pool's own <c>MaxSize</c> remains the only hard cap). Circuit-breaking, probe-claim
/// handling and fail-open behavior match <see cref="LeastConnectionsSlotSelectionStrategy"/>.
/// </summary>
public sealed class HealthWeightedLeastConnectionsSlotSelectionStrategy : ISlotSelectionStrategy
{
    private const double TieEpsilon = 1e-9;

    private readonly MemberCircuitBreaker _breaker;
    private int _cursor = -1;

    /// <summary>Concurrency cap a member starts with right after its throttle window ends.</summary>
    public int RampStart { get; }

    /// <summary>How long each +1 of the post-throttle ramp takes.</summary>
    public TimeSpan RampStepInterval { get; }

    /// <summary>Whether <c>x-ms-dop-hint</c> lowers a member's cap.</summary>
    public bool HonorDopHint { get; }

    public HealthWeightedLeastConnectionsSlotSelectionStrategy(
        int failureThreshold = 3,
        TimeSpan? cooldownPeriod = null,
        int rampStart = 2,
        TimeSpan? rampStepInterval = null,
        bool honorDopHint = true)
        : this(new MemberCircuitBreaker(failureThreshold, cooldownPeriod ?? TimeSpan.FromSeconds(30)),
            rampStart, rampStepInterval, honorDopHint)
    {
    }

    /// <summary>Advanced constructor allowing a caller-owned <see cref="MemberCircuitBreaker"/>.</summary>
    public HealthWeightedLeastConnectionsSlotSelectionStrategy(
        MemberCircuitBreaker breaker,
        int rampStart = 2,
        TimeSpan? rampStepInterval = null,
        bool honorDopHint = true)
    {
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        if (rampStart < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rampStart), rampStart, "Must be at least 1.");
        }

        var step = rampStepInterval ?? TimeSpan.FromSeconds(5);
        if (step <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(rampStepInterval), step, "Must be a positive duration.");
        }

        RampStart = rampStart;
        RampStepInterval = step;
        HonorDopHint = honorDopHint;
    }

    /// <summary>
    /// Post-throttle slow-start cap: <paramref name="fullCap"/> if the member was never throttled or
    /// is still inside its window (the throttle skip handles that case), otherwise
    /// <c>rampStart + floor(sinceRecovery / step)</c>, bounded to <paramref name="fullCap"/>.
    /// </summary>
    public static int ComputeRampCap(DateTimeOffset? throttledUntil, DateTimeOffset now, int rampStart, TimeSpan step, int fullCap)
    {
        if (throttledUntil is not { } until || until > now)
        {
            return fullCap;
        }

        var steps = (long)((now - until).Ticks / step.Ticks);
        return (int)Math.Min(fullCap, rampStart + steps);
    }

    internal int EffectiveCap(DataverseUserPool member, PoolStats stats, DateTimeOffset now)
    {
        var cap = Math.Max(1, stats.MaxSize);
        if (HonorDopHint && member.RecommendedDegreesOfParallelism is { } hint && hint > 0)
        {
            cap = Math.Min(cap, hint);
        }

        return Math.Max(1, ComputeRampCap(member.ThrottledUntil, now, RampStart, RampStepInterval, cap));
    }

    public SlotSelection SelectNext(IReadOnlyList<DataverseUserPool> members, IReadOnlyList<PoolStats> memberStats)
    {
        if (members.Count == 0)
        {
            throw new InvalidOperationException("No member pools to select from.");
        }

        var now = DateTimeOffset.UtcNow;
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

        if (eligible.Count == 0)
        {
            // Fail open (docs/adr/0007 #6): nothing won a probe claim, so no generation to carry.
            var all = Enumerable.Range(0, members.Count).ToList();
            var best = all.Max(i => Headroom(members[i], memberStats[i], now));
            var tiedAll = all.Where(i => Headroom(members[i], memberStats[i], now) >= best - TieEpsilon).ToList();
            var fallbackNext = Interlocked.Increment(ref _cursor);
            return new SlotSelection(
                members[tiedAll[(int)((uint)fallbackNext % (uint)tiedAll.Count)]],
                AllMembersUnavailable: true);
        }

        var scores = eligible.Select(i => Headroom(members[i], memberStats[i], now)).ToList();
        var top = scores.Max();
        var tied = Enumerable.Range(0, eligible.Count).Where(p => scores[p] >= top - TieEpsilon).ToList();

        var next = Interlocked.Increment(ref _cursor);
        var tiedPos = tied[(int)((uint)next % (uint)tied.Count)];

        // Release probe claims won by candidates that were scanned but not selected (docs/adr/0022).
        for (var p = 0; p < eligible.Count; p++)
        {
            if (p != tiedPos && claimGenerations[p] is { } unusedClaim)
            {
                _breaker.AbandonProbe(members[eligible[p]], unusedClaim);
            }
        }

        return new SlotSelection(members[eligible[tiedPos]], AllMembersUnavailable: false, claimGenerations[tiedPos]);
    }

    private double Headroom(DataverseUserPool member, PoolStats stats, DateTimeOffset now)
    {
        var cap = EffectiveCap(member, stats, now);
        return (double)(cap - stats.LeasedCount) / cap;
    }

    /// <inheritdoc />
    public void ReportAcquireOutcome(DataverseUserPool member, bool succeeded) => _breaker.CompleteProbe(member, succeeded);

    public void ReportAcquireOutcome(DataverseUserPool member, bool succeeded, long? claimGeneration) =>
        _breaker.CompleteProbe(member, succeeded, claimGeneration);

    public void ReportAcquireAbandoned(DataverseUserPool member) => _breaker.AbandonProbe(member);

    public void ReportAcquireAbandoned(DataverseUserPool member, long? claimGeneration) =>
        _breaker.AbandonProbe(member, claimGeneration);

    /// <inheritdoc />
    public MemberCircuitBreaker Breaker => _breaker;
}
