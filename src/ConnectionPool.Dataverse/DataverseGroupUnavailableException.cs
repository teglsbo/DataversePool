namespace ConnectionPool.Dataverse;

/// <summary>
/// Thrown by <see cref="DataverseGroupPool.AcquireAsync"/> when
/// <see cref="GroupAllUnavailableBehavior.FailFast"/> is configured and every member is currently
/// circuit-open or Dataverse-throttled. See docs/adr/0010.
/// </summary>
public sealed class DataverseGroupUnavailableException : Exception
{
    /// <summary>Names of every member pool, for diagnostics.</summary>
    public IReadOnlyList<string> MemberNames { get; }

    /// <summary>
    /// The earliest UTC instant at which any member is known to become available again (the
    /// minimum <see cref="DataverseUserPool.ThrottledUntil"/> across members that are throttled),
    /// or <c>null</c> if no member currently reports a throttle window (e.g. all are circuit-open
    /// instead, which has no fixed reopen time until the next probe succeeds).
    /// </summary>
    public DateTimeOffset? EarliestKnownRetryAt { get; }

    public DataverseGroupUnavailableException(string message, IReadOnlyList<string> memberNames, DateTimeOffset? earliestKnownRetryAt)
        : base(message)
    {
        MemberNames = memberNames;
        EarliestKnownRetryAt = earliestKnownRetryAt;
    }
}
