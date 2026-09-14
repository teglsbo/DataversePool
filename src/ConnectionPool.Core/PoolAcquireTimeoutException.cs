namespace ConnectionPool.Core;

/// <summary>
/// Thrown by <see cref="ResourcePool{T}.AcquireAsync"/> when <see cref="PoolOptions.AcquireTimeout"/>
/// elapses before capacity becomes available. See docs/adr/0012.
/// </summary>
public sealed class PoolAcquireTimeoutException : TimeoutException
{
    public PoolAcquireTimeoutException(TimeSpan timeout, PoolStats stats)
        : base(
            $"Waiting for a pooled resource did not complete within {timeout}. " +
            $"Waiting={stats.WaitingCount}, Idle={stats.IdleCount}, Created={stats.CreatedCount}/{stats.MaxSize}, " +
            $"UnhealthyOrRecycling={stats.UnhealthyOrRecyclingCount}.")
    {
        Timeout = timeout;
        Stats = stats;
    }

    /// <summary>The configured <see cref="PoolOptions.AcquireTimeout"/> that elapsed.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>A snapshot of pool state at the moment the timeout was observed, for diagnostics.</summary>
    public PoolStats Stats { get; }
}
