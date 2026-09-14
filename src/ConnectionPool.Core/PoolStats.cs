namespace ConnectionPool.Core;

/// <summary>
/// Point-in-time snapshot of a <see cref="ResourcePool{T}"/>'s internal state, exposed for telemetry
/// and for pluggable slot-selection strategies (e.g. a future throttle-aware group pool strategy).
/// </summary>
/// <param name="DetectedLeakCount">
/// Cumulative, monotonically-increasing count of GC-detected leaked leases (see
/// <see cref="PoolOptions.OnLeakDetected"/>) over this pool's lifetime. Since log-only leak
/// detection (docs/adr/0012) never reclaims a leaked slot's capacity, this is the durable signal
/// operators need to tell "the pool is saturated under legitimate load" apart from "the pool has
/// permanently lost N slots of capacity to leaks" - both otherwise look identical as a stream of
/// <see cref="PoolAcquireTimeoutException"/>s. Unlike the transient <see cref="OnLeakDetected"/>
/// callback/<see cref="ResourcePool{T}.HealthChanges"/> event, this survives being read late or
/// having no subscriber attached at the time a leak was detected. See docs/adr/0013.
/// </param>
public sealed record PoolStats(
    int MaxSize,
    int CreatedCount,
    int IdleCount,
    int LeasedCount,
    int UnhealthyOrRecyclingCount,
    int WaitingCount,
    int ConsecutiveCreateFailures = 0,
    int ConsecutiveOperationalFailures = 0,
    int DetectedLeakCount = 0);
