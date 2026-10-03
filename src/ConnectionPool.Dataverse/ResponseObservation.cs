using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace ConnectionPool.Dataverse;

/// <summary>
/// The service-protection budget Dataverse reported on one response (EXPERIMENTAL; PLAN Phase 5).
/// Dataverse sends these headers on every response, success included, so they are a leading
/// indicator - unlike a 429, which only reports the limit after it was hit.
/// </summary>
/// <param name="BurstRemainingRequests"><c>x-ms-ratelimit-burst-remaining-xrm-requests</c>: requests left in the 5-minute window.</param>
/// <param name="TimeRemainingSeconds"><c>x-ms-ratelimit-time-remaining-xrm-requests</c>: execution time left in the window, in seconds.</param>
/// <param name="DopHint"><c>x-ms-dop-hint</c>.</param>
/// <param name="ServiceRequestId"><c>x-ms-service-request-id</c>, for Microsoft support correlation.</param>
/// <param name="ObservedAt">When the response was observed.</param>
public sealed record ResponseBudget(
    double? BurstRemainingRequests,
    double? TimeRemainingSeconds,
    int? DopHint,
    string? ServiceRequestId,
    DateTimeOffset ObservedAt);

/// <summary>
/// Reads <see cref="ResponseBudget"/> from the SDK's own HTTP responses through the .NET HTTP
/// <c>DiagnosticListener</c> (no reflection into the SDK; verified live for both the Web API and the
/// SOAP transport). A response is attributed to a pool member through an <see cref="AsyncLocal{T}"/>
/// the executor sets around each attempt, so unrelated HTTP traffic in the process is ignored.
/// </summary>
internal static class ResponseObservation
{
    internal readonly record struct Target(IOperationOutcomeSink Sink, object Member);

    internal static readonly AsyncLocal<Target?> Current = new();

    private static readonly ConcurrentDictionary<Type, PropertyInfo?> ResponseProperties = new();

    private static readonly object Gate = new();
    private static Observer? _shared;
    private static int _leases;

    /// <summary>
    /// Starts listening; dispose to stop. One process-wide listener is shared by all callers, so a response
    /// is delivered once however many pools observe.
    /// </summary>
    public static IDisposable Start()
    {
        lock (Gate)
        {
            if (_leases++ == 0)
            {
                _shared = new Observer();
                _shared.Subscription = DiagnosticListener.AllListeners.Subscribe(_shared);
            }
        }

        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (Gate)
            {
                if (--_leases == 0)
                {
                    _shared?.Dispose();
                    _shared = null;
                }
            }
        }
    }

    internal static bool TryParse(HttpResponseMessage response, DateTimeOffset observedAt, out ResponseBudget budget)
    {
        double? burst = ReadDouble(response, "x-ms-ratelimit-burst-remaining-xrm-requests");
        double? time = ReadDouble(response, "x-ms-ratelimit-time-remaining-xrm-requests");
        var dopRaw = ReadRaw(response, "x-ms-dop-hint");
        int? dop = int.TryParse(dopRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null;
        budget = new ResponseBudget(burst, time, dop, ReadRaw(response, "x-ms-service-request-id"), observedAt);
        return burst is not null || time is not null || dop is not null;
    }

    private static string? ReadRaw(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static readonly NumberFormatInfo CommaDecimal = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };

    private static readonly System.Text.RegularExpressions.Regex InvariantShape =
        new(@"^-?(\d{1,3}(,\d{3})+|\d+)(\.\d+)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex CommaDecimalShape =
        new(@"^-?\d{1,3}(\.\d{3})+,\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // The server sends "1,199.97". Culture-independent: a value is read as invariant only if its separators are
    // well-formed, otherwise as grouped comma-decimal ("1.199,97"). Anything else, such as "1,5", is rejected, not guessed.
    private static double? ReadDouble(HttpResponseMessage response, string name)
    {
        var raw = ReadRaw(response, name)?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        const NumberStyles styles = NumberStyles.Float | NumberStyles.AllowThousands;
        if (InvariantShape.IsMatch(raw) && double.TryParse(raw, styles, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }

        if (CommaDecimalShape.IsMatch(raw) && double.TryParse(raw, styles, CommaDecimal, out v))
        {
            return v;
        }

        return null;
    }

    private sealed class Observer : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private IDisposable? _handlerSubscription;
        public IDisposable? Subscription;

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "HttpHandlerDiagnosticListener")
            {
                _handlerSubscription = listener.Subscribe(this, static (name, _, _) => name is "System.Net.Http.HttpRequestOut" or "System.Net.Http.HttpRequestOut.Stop");
            }
        }

        public void OnNext(KeyValuePair<string, object?> evt)
        {
            // The runtime asks IsEnabled for the activity name before it emits Stop, so the filter admits both.
            // Cheap check first: most HTTP traffic is not ours.
            if (Current.Value is not { } target || evt.Value is null)
            {
                return;
            }

            try
            {
                var property = ResponseProperties.GetOrAdd(evt.Value.GetType(), static t => t.GetProperty("Response"));
                if (property?.GetValue(evt.Value) is HttpResponseMessage response &&
                    TryParse(response, DateTimeOffset.UtcNow, out var budget))
                {
                    target.Sink.OnResponse(target.Member, budget);
                }
            }
            catch
            {
                // Observation must never affect the call.
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }

        public void Dispose()
        {
            Subscription?.Dispose();
            _handlerSubscription?.Dispose();
        }
    }
}
