using System.ServiceModel;
using Microsoft.PowerPlatform.Dataverse.Client.Exceptions;
using Microsoft.Xrm.Sdk;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Detects Dataverse's service-protection throttling signal from an exception thrown while using a
/// leased <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient"/>, and extracts the
/// <c>Retry-After</c> duration Dataverse told the caller to back off for. Covers both transports the
/// SDK can use under the hood: the Web API's HTTP 429 (<see cref="HttpOperationException"/>) and the
/// SOAP path's fault (<see cref="FaultException{T}">FaultException&lt;OrganizationServiceFault&gt;</see>,
/// confirmed live - see <see cref="ThrottlingFaultErrorCodes"/>'s remarks).
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
    /// The three documented Dataverse service-protection-limit SOAP fault codes, confirmed live
    /// against a real tenant (see PLAN-2026-09-30.md, Phase 2): <c>NumberOfRequests</c>
    /// (-2147015902), <c>ExecutionTime</c> (-2147015903), and <c>ConcurrentRequests</c>
    /// (-2147015898, the one actually observed in that run - a 150-call real-write burst produced
    /// 63 <see cref="FaultException{T}">FaultException&lt;OrganizationServiceFault&gt;</see>s with this code and <c>Retry-After</c>
    /// values from ~5 to ~13 minutes). These are the SOAP-path counterpart to the Web API's HTTP 429;
    /// unlike the Web API path, there is no HTTP status code to key off - the fault's
    /// <c>ErrorCode</c> is the only signal.
    /// </summary>
    private static readonly int[] ThrottlingFaultErrorCodes =
    [
        unchecked((int)0x80072322), // -2147015902: NumberOfRequests
        unchecked((int)0x80072321), // -2147015903: ExecutionTime
        unchecked((int)0x80072326), // -2147015898: ConcurrentRequests
    ];

    /// <summary>
    /// Upper bound applied to whatever <c>Retry-After</c> Dataverse reports, unless a caller passes
    /// an explicit override. Dataverse's service-protection limits document execution-time budgets
    /// up to 20 minutes per 5-minute sliding window, and real-world 429 responses have been observed
    /// reporting <c>Retry-After</c> values as high as ~17 minutes. Honoring that literally would keep
    /// a member excluded from selection for a very long time from a single throttle signal -
    /// disproportionate for most applications, and risky if only a few members exist (the remaining
    /// ones absorb all traffic for that whole window). 80 seconds is a deliberately conservative
    /// default: long enough to matter, short enough that a single over-reported window doesn't
    /// sideline a member for most of a work session. See docs/adr/0017.
    /// </summary>
    public static readonly TimeSpan DefaultMaxRetryAfter = TimeSpan.FromSeconds(80);

    /// <summary>
    /// Walks <paramref name="exception"/> and its <see cref="Exception.InnerException"/> chain
    /// looking for a Dataverse service-protection-limit rejection - a Web API 429 or a SOAP fault
    /// with one of <see cref="ThrottlingFaultErrorCodes"/>. Returns <c>true</c> and sets
    /// <paramref name="retryAfter"/> if found, capped at <see cref="DefaultMaxRetryAfter"/>.
    /// </summary>
    public static bool TryGetRetryAfter(Exception? exception, out TimeSpan retryAfter) =>
        TryGetRetryAfter(exception, DefaultMaxRetryAfter, out retryAfter);

    /// <summary>
    /// Overload allowing the cap applied to Dataverse's reported <c>Retry-After</c> to be overridden
    /// (see <see cref="DefaultMaxRetryAfter"/> for why a cap exists at all). Pass
    /// <see cref="TimeSpan.MaxValue"/> to effectively disable capping and honor Dataverse's value
    /// verbatim, however large.
    /// </summary>
    public static bool TryGetRetryAfter(Exception? exception, TimeSpan maxRetryAfter, out TimeSpan retryAfter)
    {
        if (maxRetryAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetryAfter), maxRetryAfter, "Must be a positive duration.");
        }

        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is HttpOperationException httpEx && IsThrottlingStatusCode(httpEx.Response?.StatusCode))
            {
                var reported = TryReadRetryAfterHeader(httpEx.Response!.Headers, out var parsed)
                    ? parsed
                    : DefaultRetryAfterWhenUnspecified;
                retryAfter = reported > maxRetryAfter ? maxRetryAfter : reported;
                return true;
            }

            if (ex is FaultException<OrganizationServiceFault> faultEx &&
                ThrottlingFaultErrorCodes.Contains(faultEx.Detail.ErrorCode))
            {
                var reported = TryReadRetryAfterErrorDetail(faultEx.Detail.ErrorDetails, out var parsed)
                    ? parsed
                    : DefaultRetryAfterWhenUnspecified;
                retryAfter = reported > maxRetryAfter ? maxRetryAfter : reported;
                return true;
            }
        }

        retryAfter = default;
        return false;
    }

    /// <summary>
    /// Decodes <i>which</i> service-protection limit an exception reports (<see cref="ThrottleReason"/>).
    /// A SOAP fault is decoded exactly from its error code. A Web API 429 carries the code/message in
    /// its body, so it is decoded best-effort from the response text and message (the three hex codes
    /// or Dataverse's wording) - not yet verified live; anything unrecognized is
    /// <see cref="ThrottleReason.Unknown"/>. Returns <c>false</c> if the exception is not a throttle at
    /// all. Does not apply the <c>Retry-After</c> cap, so it never throws on a bad one.
    /// </summary>
    public static bool TryGetThrottleReason(Exception? exception, out ThrottleReason reason)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is FaultException<OrganizationServiceFault> faultEx &&
                ThrottlingFaultErrorCodes.Contains(faultEx.Detail.ErrorCode))
            {
                reason = faultEx.Detail.ErrorCode switch
                {
                    unchecked((int)0x80072322) => ThrottleReason.RequestCount,
                    unchecked((int)0x80072321) => ThrottleReason.ExecutionTime,
                    _ => ThrottleReason.ConcurrentRequests,
                };
                return true;
            }

            if (ex is HttpOperationException httpEx && IsThrottlingStatusCode(httpEx.Response?.StatusCode))
            {
                reason = DecodeFromText(httpEx.Message + " " + httpEx.Response?.Content);
                return true;
            }
        }

        reason = default;
        return false;
    }

    private static ThrottleReason DecodeFromText(string text)
    {
        bool Has(string marker) => text.Contains(marker, StringComparison.OrdinalIgnoreCase);

        if (Has("80072326") || Has("concurrent requests"))
        {
            return ThrottleReason.ConcurrentRequests;
        }

        if (Has("80072321") || Has("combined execution time"))
        {
            return ThrottleReason.ExecutionTime;
        }

        if (Has("80072322") || Has("number of requests"))
        {
            return ThrottleReason.RequestCount;
        }

        return ThrottleReason.Unknown;
    }

    private static bool IsThrottlingStatusCode(System.Net.HttpStatusCode? statusCode) =>
        statusCode.HasValue && (int)statusCode.Value == 429;

    /// <summary>
    /// Reads the SOAP fault's <c>Retry-After</c> <see cref="ErrorDetailCollection"/> entry. Observed
    /// live as a boxed <see cref="TimeSpan"/> (see <see cref="ThrottlingFaultErrorCodes"/>'s remarks);
    /// also accepts a numeric seconds value defensively, in case a different fault path ever reports
    /// it differently.
    /// </summary>
    private static bool TryReadRetryAfterErrorDetail(ErrorDetailCollection? errorDetails, out TimeSpan retryAfter)
    {
        retryAfter = default;
        if (errorDetails is null || !errorDetails.TryGetValue(RetryAfterHeaderName, out var value))
        {
            return false;
        }

        switch (value)
        {
            case TimeSpan span:
                retryAfter = span;
                return true;
            case int seconds:
                retryAfter = TimeSpan.FromSeconds(Math.Max(0, seconds));
                return true;
            case string raw when int.TryParse(raw, out var parsedSeconds):
                retryAfter = TimeSpan.FromSeconds(Math.Max(0, parsedSeconds));
                return true;
            default:
                return false;
        }
    }

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
