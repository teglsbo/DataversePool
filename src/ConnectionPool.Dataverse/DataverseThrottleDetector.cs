using Microsoft.PowerPlatform.Dataverse.Client.Exceptions;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Detects Dataverse's HTTP 429 / service-protection throttling signal from an exception thrown
/// while using a leased <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient"/>, and
/// extracts the <c>Retry-After</c> duration Dataverse told the caller to back off for.
///
/// Why the 429/exception path, not the proactive <c>x-ms-ratelimit-*</c> response headers:
/// Dataverse's Web API returns proactive budget headers
/// (<c>x-ms-ratelimit-burst-remaining-xrm-requests</c>,
/// <c>x-ms-ratelimit-time-remaining-xrm-requests</c>, etc.) on *every* response, success or not -
/// in general that is the better signal, because it's a leading indicator (react before you get
/// throttled) instead of a lagging one (react only after Dataverse has already rejected a
/// request). But <c>Microsoft.PowerPlatform.Dataverse.Client.ServiceClient</c>, which this library
/// wraps, does not surface response headers anywhere in its public API for *successful* calls
/// (verified by reflecting over <c>ServiceClient</c> and <c>ConnectionOptions</c> - no such
/// property/event exists). It only surfaces headers on failure, via
/// <see cref="HttpOperationException"/>.<c>Response</c>, once its own internal retry budget
/// (<c>ServiceClient.MaxRetryCount</c>/<c>RetryPauseTime</c>) is exhausted. So for this SDK, the
/// 429/exception path is not a preference, it is the *only* signal actually available without
/// reflecting into ServiceClient's private HTTP pipeline (fragile, unsupported, rejected - see
/// docs/adr/0008). If a future SDK version exposes response headers on success too, a proactive
/// strategy could be added without changing this type's public contract.
/// </summary>
public static class DataverseThrottleDetector
{
    private const string RetryAfterHeaderName = "Retry-After";
    private static readonly TimeSpan DefaultRetryAfterWhenUnspecified = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Walks <paramref name="exception"/> and its <see cref="Exception.InnerException"/> chain
    /// looking for a Dataverse 429 (service-protection limit exceeded). Returns <c>true</c> and
    /// sets <paramref name="retryAfter"/> if found.
    /// </summary>
    public static bool TryGetRetryAfter(Exception? exception, out TimeSpan retryAfter)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is HttpOperationException httpEx && IsThrottlingStatusCode(httpEx.Response?.StatusCode))
            {
                retryAfter = TryReadRetryAfterHeader(httpEx.Response!.Headers, out var parsed)
                    ? parsed
                    : DefaultRetryAfterWhenUnspecified;
                return true;
            }
        }

        retryAfter = default;
        return false;
    }

    private static bool IsThrottlingStatusCode(System.Net.HttpStatusCode? statusCode) =>
        statusCode.HasValue && (int)statusCode.Value == 429;

    private static bool TryReadRetryAfterHeader(
        IDictionary<string, IEnumerable<string>>? headers,
        out TimeSpan retryAfter)
    {
        retryAfter = default;
        if (headers is null || !headers.TryGetValue(RetryAfterHeaderName, out var values))
        {
            return false;
        }

        var raw = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        // Retry-After is either delay-seconds (Dataverse's usual form) or an HTTP-date.
        if (int.TryParse(raw, out var seconds))
        {
            retryAfter = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return true;
        }

        if (DateTimeOffset.TryParse(raw, out var when))
        {
            var delta = when - DateTimeOffset.UtcNow;
            retryAfter = delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            return true;
        }

        return false;
    }
}
