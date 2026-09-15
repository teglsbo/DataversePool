using ConnectionPool.Core;

namespace ConnectionPool.Metrics;

/// <summary>Convenience extension for the common case of metering a <see cref="ResourcePool{T}"/> directly.</summary>
public static class ResourcePoolMetricsExtensions
{
    /// <summary>
    /// Creates a <see cref="PoolMetrics"/> instance publishing <paramref name="pool"/>'s
    /// <see cref="PoolStats"/> as observable gauges. Dispose the returned <see cref="PoolMetrics"/>
    /// when the pool itself is disposed to unregister its instruments.
    /// </summary>
    public static PoolMetrics AddMetrics<T>(this ResourcePool<T> pool, string poolName, string? meterName = null)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(pool);
        return new PoolMetrics(poolName, pool.GetStats, meterName);
    }
}
