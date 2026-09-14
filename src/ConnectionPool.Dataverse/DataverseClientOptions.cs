namespace ConnectionPool.Dataverse;

/// <summary>
/// Optional overrides for <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient"/>
/// settings that affect perceived latency and failure-detection speed, applied by
/// <see cref="DataverseServiceClientPolicy"/> to both the base client and every clone it produces.
///
/// <para>
/// Unlike <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient.EnableAffinityCookie"/>
/// (always forced to <c>false</c> - see <see cref="DataverseServiceClientPolicy"/>), there is no
/// single correct value for these settings: the right choice depends on how long your callers are
/// willing to block per operation. Left <c>null</c> (default), the SDK's own defaults apply
/// (<c>MaxRetryCount = 10</c>, <c>RetryPauseTime = 5s</c>).
/// </para>
///
/// <para>
/// Why this matters for a pool specifically: the SDK's internal retry loop for transient (non-429)
/// errors runs entirely inside a single leased <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient"/>
/// call, before anything is thrown to caller code. With the defaults, a single call hitting a
/// transient error can block for up to ~50 seconds (10 retries x 5s) before an exception ever
/// reaches this library's throttle detection or a circuit breaker built on top of it - the pool has
/// no way to know a lease is "stuck" versus merely slow. Lowering these values trades some of the
/// SDK's built-in resilience for faster, more pool-visible failure signals; raising them (or leaving
/// the defaults) trades the reverse. Tune based on your own timeout budget, e.g. via
/// <see cref="ConnectionPool.Core.PoolOptions.AcquireTimeout"/> or a caller-side operation timeout.
/// </para>
/// </summary>
public sealed class DataverseClientOptions
{
    /// <summary>
    /// Overrides <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient.MaxRetryCount"/>
    /// (SDK default: 10) if set. Must be zero or greater.
    /// </summary>
    public int? MaxRetryCount { get; init; }

    /// <summary>
    /// Overrides <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient.RetryPauseTime"/>
    /// (SDK default: 5 seconds) if set. Must be zero or greater.
    /// </summary>
    public TimeSpan? RetryPauseTime { get; init; }

    /// <summary>Throws if any set value is out of range.</summary>
    public void Validate()
    {
        if (MaxRetryCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryCount), MaxRetryCount, "Must be zero or greater.");
        }

        if (RetryPauseTime is { } pause && pause < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryPauseTime), pause, "Must be zero or greater.");
        }
    }
}
