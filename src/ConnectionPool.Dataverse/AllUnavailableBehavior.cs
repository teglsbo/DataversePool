namespace ConnectionPool.Dataverse;

/// <summary>
/// How <see cref="DataversePool.AcquireAsync"/> behaves when the configured
/// <see cref="ISlotSelectionStrategy"/> reports that every member is currently circuit-open or
/// Dataverse-throttled (<see cref="SlotSelection.AllMembersUnavailable"/>). See
/// docs/adr/0010-configurable-fail-fast-and-single-probe-half-open.md.
/// </summary>
public enum AllUnavailableBehavior
{
    /// <summary>
    /// Default, backward-compatible behavior (docs/adr/0007 #6): still acquire from whichever
    /// member the strategy picked, rather than making the whole group unavailable. Appropriate when
    /// a caller-side resilience layer (e.g. Polly, or the caller's own retry) already handles the
    /// resulting failure/throttle and you'd rather attempt the call than reject it outright.
    /// </summary>
    FailOpen = 0,

    /// <summary>
    /// Throw <see cref="DataversePoolUnavailableException"/> immediately instead of acquiring from
    /// a known-bad/throttled member. Appropriate when sending traffic to a member you already know
    /// is unavailable would only add load to (or extend an outage against) a system that is already
    /// struggling - i.e. you want backpressure instead of amplification. See docs/adr/0010 for why
    /// unconditional fail-open was judged unsafe as the *only* option.
    /// </summary>
    FailFast = 1,
}
