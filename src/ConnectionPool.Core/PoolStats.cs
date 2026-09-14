namespace ConnectionPool.Core;

/// <summary>
/// Point-in-time snapshot of a <see cref="ResourcePool{T}"/>'s internal state, exposed for telemetry
/// and for pluggable slot-selection strategies (e.g. a future throttle-aware group pool strategy).
/// </summary>
public sealed record PoolStats(
    int MaxSize,
    int CreatedCount,
    int IdleCount,
    int LeasedCount,
    int UnhealthyOrRecyclingCount,
    int WaitingCount,
    int ConsecutiveCreateFailures = 0,
    int ConsecutiveOperationalFailures = 0);
