using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Phase 5 spike (PLAN): can a <c>DiagnosticListener</c> on the .NET HTTP handler observe the SDK's
/// responses, including the <c>x-ms-ratelimit-*</c>, <c>x-ms-service-request-id</c> and
/// <c>x-ms-dop-hint</c> headers, for both transports? Findings are printed, not asserted, apart from
/// the existence of at least the Web API response. Opt-in; self-skips without credentials.
/// </summary>
[Trait("Category", "Integration")]
public class LiveResponseObserverSpikeTests
{
    private readonly ITestOutputHelper _output;

    public LiveResponseObserverSpikeTests(ITestOutputHelper output) => _output = output;

    private sealed record Seen(string Listener, string Event, string Uri, int Status, string[] Headers);

    private sealed class Collector : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly List<IDisposable> _subscriptions = new();
        public ConcurrentBag<Seen> Events { get; } = new();
        public ConcurrentBag<string> ListenerNames { get; } = new();

        public void OnNext(DiagnosticListener listener)
        {
            ListenerNames.Add(listener.Name);
            if (listener.Name is "HttpHandlerDiagnosticListener")
            {
                lock (_subscriptions)
                {
                    _subscriptions.Add(listener.Subscribe(this));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> evt)
        {
            if (evt.Value is null || !evt.Key.EndsWith("Stop", StringComparison.Ordinal))
            {
                return;
            }

            var type = evt.Value.GetType();
            var response = type.GetProperty("Response")?.GetValue(evt.Value) as HttpResponseMessage;
            var request = type.GetProperty("Request")?.GetValue(evt.Value) as HttpRequestMessage;
            if (response is null)
            {
                return;
            }

            var headers = response.Headers.Concat(response.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .Where(h => h.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
                .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .Select(h => $"{h.Key}={string.Join("|", h.Value)}")
                .ToArray();
            Events.Add(new Seen("HttpHandlerDiagnosticListener", evt.Key, request?.RequestUri?.AbsolutePath ?? "?", (int)response.StatusCode, headers));
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }

        public void Dispose()
        {
            lock (_subscriptions)
            {
                foreach (var s in _subscriptions)
                {
                    s.Dispose();
                }
            }
        }
    }

    [SkippableFact]
    public async Task HttpHandlerDiagnosticListener_ObservesSdkResponses()
    {
        var connectionString = LiveDataverseCredentials.GetConnectionString(0);
        Skip.If(string.IsNullOrEmpty(connectionString), "Set DVPOOL_IT_* credentials.");

        var collector = new Collector();
        using var allListeners = DiagnosticListener.AllListeners.Subscribe(collector);

        using var client = new ServiceClient(connectionString);
        Assert.True(client.IsReady, client.LastError);
        collector.Events.Clear();

        using (var web = await client.ExecuteWebRequestAsync(
                   HttpMethod.Get, "WhoAmI()", string.Empty, null, "application/json", CancellationToken.None))
        {
            Assert.True(web.IsSuccessStatusCode);
        }

        var soakedBeforeSoap = collector.Events.Count;
        _ = await client.ExecuteAsync(new WhoAmIRequest());
        var soapSeen = collector.Events.Count - soakedBeforeSoap;

        _output.WriteLine($"Listeners seen: {string.Join(", ", collector.ListenerNames.Distinct().Order())}");
        foreach (var e in collector.Events)
        {
            _output.WriteLine($"{e.Listener} {e.Event} {e.Uri} -> {e.Status}; x-ms/retry headers: {string.Join(", ", e.Headers)}");
        }

        _output.WriteLine($"Events from the Web API call: {soakedBeforeSoap}; events from the SOAP (ExecuteAsync) call: {soapSeen}.");
        Assert.True(soakedBeforeSoap > 0, "No HttpHandlerDiagnosticListener event for the Web API call.");
    }
}
