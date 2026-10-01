namespace ConnectionPool.Dataverse;

/// <summary>
/// Optional <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient"/> settings applied
/// by <see cref="DataverseServiceClientPolicy"/> to both the base client and every clone it produces.
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
/// <see cref="UseWebApi"/> and <see cref="SessionTrackingId"/> are also applied to every client and
/// restored when a lease is returned. Leaving either unset preserves the base client's configured
/// value.
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
///
/// <para>
/// <b><see cref="MaxRetryCount"/> also governs HTTP 429 (service-protection/throttling) retries</b>,
/// not just other transient errors - it is not limited to the latter. Set it to <c>0</c> to make
/// the SDK never retry internally on a 429; the exception surfaces immediately. This library's
/// <see cref="DataverseThrottleDetector"/> can drive backoff only for recognized HTTP 429s
/// (see docs/adr/0008). Note <see cref="RetryPauseTime"/> does *not*
/// govern the wait between 429 retries when the SDK does retry them - Dataverse's own
/// <c>Retry-After</c> response header is used for that instead; <see cref="RetryPauseTime"/> only
/// applies to other transient errors.
/// </para>
///
/// <para>
/// <b>Does <c>MaxRetryCount=0</c> preserve a usable backoff signal?</b> On the Web API path,
/// <see cref="DataverseThrottleDetector"/> can read <c>Retry-After</c> from the HTTP 429 response
/// even when SDK retries are disabled (inferred, not live-verified). On SOAP requests, the
/// exception and its retry detail have not been confirmed in a live throttle; the current detector
/// only matches HTTP 429 exceptions. Until SOAP detection is verified, disabling SDK retries may
/// expose a throttle that the pool cannot classify or back off from.
/// </para>
/// </summary>
public sealed class DataverseClientOptions
{
    /// <summary>
    /// Overrides <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient.UseWebApi"/> if
    /// set. This is a per-pool setting, not a per-lease switch; the configured value is restored
    /// when a client is returned.
    /// </summary>
    public bool? UseWebApi { get; init; }

    /// <summary>
    /// Overrides <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient.SessionTrackingId"/>
    /// if set. The configured value is restored when a client is returned to the pool.
    /// </summary>
    public Guid? SessionTrackingId { get; init; }

    /// <summary>
    /// Overrides <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient.MaxRetryCount"/>
    /// (SDK default: 10) if set. Must be zero or greater. Governs retries for both generic
    /// transient errors and HTTP 429 (service-protection/throttling) - set to <c>0</c> to disable
    /// all internal retrying and fail fast on either.
    /// </summary>
    public int? MaxRetryCount { get; init; }

    /// <summary>
    /// Overrides <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient.RetryPauseTime"/>
    /// (SDK default: 5 seconds) if set. Must be zero or greater. Does not apply to the wait between
    /// HTTP 429 retries - those honor Dataverse's <c>Retry-After</c> response header instead (see
    /// <see cref="UseExponentialRetryDelayForConcurrencyThrottle"/> for that path).
    /// </summary>
    public TimeSpan? RetryPauseTime { get; init; }

    /// <summary>
    /// Overrides <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient.UseExponentialRetryDelayForConcurrencyThrottle"/>
    /// (SDK default: <c>false</c>) if set. When <c>true</c>, the SDK's internal retry delay for
    /// repeated HTTP 429 concurrency-throttle hits grows exponentially instead of using Dataverse's
    /// raw <c>Retry-After</c> value on every attempt - spreads retry load out more when many callers
    /// are being throttled at once, at the cost of a longer worst-case wait per call.
    /// </summary>
    public bool? UseExponentialRetryDelayForConcurrencyThrottle { get; init; }

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
