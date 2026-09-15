using ConnectionPool.Core;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Wraps a <see cref="PooledLease{ServiceClient}"/> acquired through a <see cref="DataversePool"/>
/// together with a reference to the specific <see cref="DataverseUserPool"/> member that served it.
/// The member reference exists so a caller can report a Dataverse throttling signal (HTTP 429) back
/// to the *right* member - see <see cref="ReportIfThrottled"/> and docs/adr/0008.
/// </summary>
public sealed class DataverseLease : IAsyncDisposable
{
    private readonly PooledLease<ServiceClient> _inner;

    internal DataverseLease(DataverseUserPool member, PooledLease<ServiceClient> inner)
    {
        Member = member;
        _inner = inner;
    }

    /// <summary>The member (application user pool) that served this lease.</summary>
    public DataverseUserPool Member { get; }

    public ServiceClient Resource => _inner.Resource;

    /// <summary>See <see cref="PooledLease{T}.MarkUnhealthy"/> - recycles the underlying connection.</summary>
    public void MarkUnhealthy(Exception exception) => _inner.MarkUnhealthy(exception);

    /// <summary>
    /// Records that Dataverse throttled <see cref="Member"/> for <paramref name="retryAfter"/>.
    /// Does NOT recycle the underlying <see cref="ServiceClient"/> (unlike <see cref="MarkUnhealthy"/>)
    /// - the connection itself is fine, this member is just temporarily over its own request budget,
    /// and the pool's <see cref="ISlotSelectionStrategy"/> will steer new acquires to other members
    /// until the window expires.
    /// </summary>
    public void ReportThrottled(TimeSpan retryAfter) => Member.ReportThrottled(retryAfter);

    /// <summary>
    /// Convenience: runs <paramref name="exception"/> through <see cref="DataverseThrottleDetector"/>
    /// and, if it recognizes a Dataverse 429/throttling signal, calls <see cref="ReportThrottled"/>
    /// automatically. Returns <c>true</c> if throttling was detected and reported. The reported
    /// duration is capped at <paramref name="maxRetryAfter"/> (defaults to
    /// <see cref="DataverseThrottleDetector.DefaultMaxRetryAfter"/>) - see that constant's docs for
    /// why Dataverse's own reported value is not always honored verbatim.
    /// </summary>
    public bool ReportIfThrottled(Exception exception, TimeSpan? maxRetryAfter = null) =>
        ReportIfThrottled(exception, out _, maxRetryAfter);

    /// <summary>
    /// Same as <see cref="ReportIfThrottled(Exception, TimeSpan?)"/>, but also hands back the
    /// (already-capped) reported duration via <paramref name="retryAfter"/> - useful for a caller
    /// that needs to actually wait out the window itself (e.g. a single-member pool, where there is
    /// no other member to route a retry to instead). <paramref name="retryAfter"/> is
    /// <see cref="TimeSpan.Zero"/> if throttling was not recognized.
    /// </summary>
    public bool ReportIfThrottled(Exception exception, out TimeSpan retryAfter, TimeSpan? maxRetryAfter = null)
    {
        var cap = maxRetryAfter ?? DataverseThrottleDetector.DefaultMaxRetryAfter;
        if (DataverseThrottleDetector.TryGetRetryAfter(exception, cap, out retryAfter))
        {
            ReportThrottled(retryAfter);
            return true;
        }

        retryAfter = TimeSpan.Zero;
        return false;
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
