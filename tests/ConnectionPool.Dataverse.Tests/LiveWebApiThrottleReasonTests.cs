using System.Collections.Concurrent;
using Microsoft.PowerPlatform.Dataverse.Client;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Opt-in live check that a real Web API 429 surfaced <i>through the SDK</i> is decoded by
/// <see cref="DataverseThrottleDetector.TryGetThrottleReason"/> (ADR-0026). The raw-HTTP body was
/// confirmed separately (<c>LivePinnedWebApiConcurrencyTests</c>: code 0x80072326, "Number of
/// concurrent requests exceeded the limit of 100."); this checks the SDK exception's own text.
/// Not run in CI; self-skips without <c>DVPOOL_IT_*</c> credentials.
/// </summary>
[Trait("Category", "Integration")]
public class LiveWebApiThrottleReasonTests
{
    private readonly ITestOutputHelper _output;

    public LiveWebApiThrottleReasonTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public async Task SdkWebApi429_IsDecodedAsConcurrentRequests()
    {
        var connectionString = LiveDataverseCredentials.GetConnectionString(0);
        Skip.If(string.IsNullOrEmpty(connectionString), "Set DVPOOL_IT_* credentials.");

        using var client = new ServiceClient(connectionString);
        Assert.True(client.IsReady, client.LastError);
        client.MaxRetryCount = 0; // surface the 429 instead of letting the SDK absorb it

        var failures = new ConcurrentBag<Exception>();
        var ok = 0;
        var workers = Enumerable.Range(0, 128).Select(async _ =>
        {
            try
            {
                using var response = await client.ExecuteWebRequestAsync(
                    HttpMethod.Get, "solutioncomponents?$top=5000", string.Empty, null, "application/json", CancellationToken.None);
                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref ok);
                }
                else
                {
                    failures.Add(new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode));
                    _output.WriteLine($"Non-success response returned (not thrown): {(int)response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }).ToArray();
        await Task.WhenAll(workers);

        _output.WriteLine($"ok={ok}, failures={failures.Count}");
        var decoded = 0;
        foreach (var group in failures.GroupBy(e => e.GetType().FullName))
        {
            var sample = group.First();
            var isThrottle = DataverseThrottleDetector.TryGetThrottleReason(sample, out var reason);
            var retry = DataverseThrottleDetector.TryGetRetryAfter(sample, out var retryAfter);
            _output.WriteLine($"{group.Count()}x {group.Key}: throttle={isThrottle}, reason={(isThrottle ? reason.ToString() : "-")}, retryAfter={(retry ? retryAfter.ToString() : "-")}");
            _output.WriteLine($"  message: {sample.Message}");
            if (sample is Microsoft.PowerPlatform.Dataverse.Client.Exceptions.HttpOperationException http)
            {
                _output.WriteLine($"  response content: {http.Response?.Content}");
            }

            if (isThrottle)
            {
                decoded += group.Count();
                Assert.Equal(ThrottleReason.ConcurrentRequests, reason);
            }
        }

        Skip.If(decoded == 0, "No throttle surfaced through the SDK in this run; nothing to decode.");
    }
}
