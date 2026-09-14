namespace ConnectionPool.Core;

/// <summary>
/// Describes the most recent reason a pooled resource was flagged unhealthy, either via an explicit
/// <see cref="PooledLease{T}.MarkUnhealthy"/> call from a consumer, or via automatic leak detection.
/// </summary>
public sealed record PoolIncidentInfo(Exception? Exception, DateTimeOffset OccurredAt, bool WasLeakDetected = false);
