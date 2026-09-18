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
    private long _throttledUntilTicks; // 0 = not throttled; otherwise DateTimeOffset.UtcTicks (UTC)

    public string Name { get; }

    public DataverseUserPool(string name, string connectionString, PoolOptions? options = null, ILogger? logger = null, DataverseClientOptions? clientOptions = null)
    {
        Name = name;
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
        _policy = new DataverseServiceClientPolicy(baseClientFactory, logger, clientOptions);
        _pool = new ResourcePool<ServiceClient>(_policy, options);
    }

    /// <summary>Sequentially creates the configured prewarm count of connections. See docs/adr/0002.</summary>
    public Task WarmupAsync(CancellationToken cancellationToken = default) => _pool.WarmupAsync(cancellationToken);

    public Task<PooledLease<ServiceClient>> AcquireAsync(CancellationToken cancellationToken = default)
        => _pool.AcquireAsync(cancellationToken);

    public PoolStats GetStats() => _pool.GetStats();

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
