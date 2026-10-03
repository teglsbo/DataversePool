using ConnectionPool.Core;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace ConnectionPool.Dataverse;

/// <summary>
/// A connection pool of Dataverse <see cref="ServiceClient"/> instances for a single service
/// user/connection string. Use <see cref="DataversePool"/> instead when you want to spread
/// load (and Dataverse service-protection budget) across multiple service users.
/// See docs/adr/0006-dual-pooling-model-single-user-and-round-robin-group.md.
/// </summary>
public sealed class DataverseUserPool : IAsyncDisposable
{
    private readonly DataverseServiceClientPolicy _policy;
    private readonly ResourcePool<ServiceClient> _pool;
    private readonly ILogger? _logger;
    private long _throttledUntilTicks; // 0 = not throttled; otherwise DateTimeOffset.UtcTicks (UTC)
    private QuarantineInfo? _quarantine;
    private TimeSpan _quarantineDuration = TimeSpan.FromMinutes(10);

    private sealed record QuarantineInfo(DateTimeOffset Until, string Reason);

    public string Name { get; }

    public DataverseUserPool(string name, string connectionString, PoolOptions? options = null, ILogger? logger = null, DataverseClientOptions? clientOptions = null)
    {
        Name = name;
        _logger = logger;
        _policy = new DataverseServiceClientPolicy(connectionString, logger, clientOptions);
        _pool = new ResourcePool<ServiceClient>(_policy, options);
    }

    /// <summary>
    /// Constructs this member from a caller-supplied base-client factory instead of a connection
    /// string - see <see cref="DataverseServiceClientPolicy(Func{CancellationToken, Task{ServiceClient}}, ILogger?, DataverseClientOptions?)"/>
    /// for when to use this (e.g. MSAL/custom token-provider authentication that doesn't fit the
    /// connection-string constructor).
    /// </summary>
    public DataverseUserPool(string name, Func<CancellationToken, Task<ServiceClient>> baseClientFactory, PoolOptions? options = null, ILogger? logger = null, DataverseClientOptions? clientOptions = null)
    {
        Name = name;
        _logger = logger;
        _policy = new DataverseServiceClientPolicy(baseClientFactory, logger, clientOptions);
        _pool = new ResourcePool<ServiceClient>(_policy, options);
    }

    /// <summary>Sequentially creates the configured prewarm count of connections. See docs/adr/0002.</summary>
    public Task WarmupAsync(CancellationToken cancellationToken = default) => _pool.WarmupAsync(cancellationToken);

    public async Task<PooledLease<ServiceClient>> AcquireAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not PoolAcquireTimeoutException
            && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            if (DataverseFailureClassifier.IsPermanent(ex, out var reason))
            {
                Quarantine(reason);
            }

            throw;
        }
    }

    /// <summary>
    /// How long a member stays quarantined after a permanent failure (see <see cref="Quarantine"/>)
    /// before selection tries it again. Default 10 minutes - deliberately much longer than the
    /// circuit breaker's cooldown, because a permanent failure needs a human fix, not a retry.
    /// </summary>
    public TimeSpan QuarantineDuration
    {
        get => _quarantineDuration;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _quarantineDuration = value;
        }
    }

    /// <summary>True while this member is quarantined for a permanent (non-self-healing) failure.</summary>
    public bool IsQuarantined => Volatile.Read(ref _quarantine) is { } q && q.Until > DateTimeOffset.UtcNow;

    /// <summary>UTC instant the quarantine ends, or <c>null</c> if not quarantined.</summary>
    public DateTimeOffset? QuarantinedUntil => IsQuarantined ? Volatile.Read(ref _quarantine)!.Until : null;

    /// <summary>Why this member is quarantined, or <c>null</c> if it is not.</summary>
    public string? QuarantineReason => IsQuarantined ? Volatile.Read(ref _quarantine)!.Reason : null;

    /// <summary>
    /// Takes this member out of selection for <see cref="QuarantineDuration"/>. Called automatically
    /// when an acquire fails with a failure <see cref="DataverseFailureClassifier"/> considers
    /// permanent (revoked secret, disabled app user, ...); callable directly too. Logs at error
    /// level once per quarantine, not once per failed call. A quarantined member is skipped like a
    /// throttled one, and the pool still fails open if every member is unavailable.
    /// </summary>
    public void Quarantine(string reason)
    {
        var next = new QuarantineInfo(DateTimeOffset.UtcNow + _quarantineDuration, reason);
        var previous = Interlocked.Exchange(ref _quarantine, next);
        if (previous is null || previous.Until <= DateTimeOffset.UtcNow)
        {
            _logger?.LogError(
                "Dataverse member '{Member}' quarantined for {Duration} - permanent failure: {Reason}. " +
                "Fix the credentials/configuration, or call ClearQuarantine() after fixing.",
                Name, _quarantineDuration, reason);
        }
    }

    /// <summary>Ends any quarantine immediately (e.g. an operator rotated the secret).</summary>
    public void ClearQuarantine() => Interlocked.Exchange(ref _quarantine, null);

    public PoolStats GetStats() => _pool.GetStats();

    /// <summary>This member's current concurrency limit (its maximum concurrently leased
    /// <c>ServiceClient</c>s). See <see cref="SetMaxSize"/>.</summary>
    public int MaxSize => _pool.MaxSize;

    /// <summary>
    /// Changes this member's concurrency limit at runtime - for example to follow
    /// <c>ServiceClient.RecommendedDegreesOfParallelism</c>, or an operator's decision, without a
    /// restart. Growing takes effect immediately. Shrinking never cancels in-flight calls; the excess
    /// leases are retired as they are returned. See <see cref="ResourcePool{T}.SetMaxSize"/> and
    /// docs/adr/0025.
    /// </summary>
    public void SetMaxSize(int maxSize) => _pool.SetMaxSize(maxSize);

    /// <summary>
    /// UTC instant this member is throttled until, or <c>null</c> if not currently throttled.
    /// Set via <see cref="ReportThrottled"/> in response to a Dataverse 429/service-protection
    /// signal (see <see cref="DataverseThrottleDetector"/>, docs/adr/0008). This does NOT recycle
    /// or mark unhealthy any pooled <see cref="ServiceClient"/> - the connection itself is fine,
    /// this member (application user) is just temporarily over its own request budget.
    /// </summary>
    public DateTimeOffset? ThrottledUntil
    {
        get
        {
            var ticks = Interlocked.Read(ref _throttledUntilTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>True if <see cref="ThrottledUntil"/> is set and still in the future.</summary>
    public bool IsThrottled => ThrottledUntil is { } until && until > DateTimeOffset.UtcNow;

    /// <summary>
    /// Most recently observed <c>ServiceClient.RecommendedDegreesOfParallelism</c> (the
    /// <c>x-ms-dop-hint</c> response header, see ADR-0025) across this member's connections, or
    /// <c>null</c> if none has been observed yet. Not acted on automatically - see ADR-0025's open
    /// questions for why an automatic DOP controller needs more research first.
    /// </summary>
    public int? RecommendedDegreesOfParallelism => _policy.LastRecommendedDegreesOfParallelism;

    /// <summary>
    /// Records that Dataverse throttled this member for <paramref name="retryAfter"/>, so that a
    /// group pool's <see cref="ISlotSelectionStrategy"/> can steer new acquires toward other
    /// members until the window expires. Safe to call concurrently; overlapping reports only ever
    /// extend the throttle window, never shorten it.
    /// </summary>
    public void ReportThrottled(TimeSpan retryAfter)
    {
        if (retryAfter < TimeSpan.Zero)
        {
            retryAfter = TimeSpan.Zero;
        }

        var candidateTicks = DateTimeOffset.UtcNow.Add(retryAfter).UtcTicks;
        long existing;
        do
        {
            existing = Interlocked.Read(ref _throttledUntilTicks);
            if (existing >= candidateTicks)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref _throttledUntilTicks, candidateTicks, existing) != existing);
    }

    /// <summary>Health signal stream, see docs/adr/0004 and docs/adr/0005 (Polly adapter consumes this).</summary>
    public IObservable<SlotHealthChanged> HealthChanges => _pool.HealthChanges;

    public async ValueTask DisposeAsync()
    {
        await _pool.DisposeAsync().ConfigureAwait(false);
        await _policy.DisposeAsync().ConfigureAwait(false);
    }
}
