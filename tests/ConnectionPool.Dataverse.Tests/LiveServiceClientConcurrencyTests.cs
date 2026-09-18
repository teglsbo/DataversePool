using System.Diagnostics;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Opt-in, live-Dataverse tests that measure whether a single <see cref="ServiceClient"/>
/// instance actually serializes concurrent async requests, and whether sharing one instance
/// across concurrent callers with different <c>CallerId</c> values is safe.
/// </summary>
/// <remarks>
/// <para>
/// Not run in CI (excluded via <c>Category!=Integration</c>, see <c>ci.yml</c>). Requires real
/// Dataverse credentials via environment variables; every test <c>Skip</c>s itself (rather than
/// failing) when the variables it needs are not set, so this file is safe to have present in a
/// checkout with no Dataverse access at all.
/// </para>
/// <para>
/// Run manually with, e.g.:
/// <c>dotnet test --filter "Category=Integration&amp;FullyQualifiedName~LiveServiceClientConcurrencyTests"</c>
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class LiveServiceClientConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    public LiveServiceClientConcurrencyTests(ITestOutputHelper output) => _output = output;

    // Single connection string for the "does it serialize?" timing test.
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("DVPOOL_IT_CONNECTION_STRING");

    // Two distinct systemuser ids (with impersonation privilege granted to the caller's app
    // user) for the CallerId cross-thread race test. Deliberately separate from
    // ConnectionString so the timing test can run without needing impersonation set up.
    private static string? CallerIdA =>
        Environment.GetEnvironmentVariable("DVPOOL_IT_CALLER_ID_A");

    private static string? CallerIdB =>
        Environment.GetEnvironmentVariable("DVPOOL_IT_CALLER_ID_B");

    /// <summary>
    /// Compares wall-clock time for N sequential vs. N concurrent <c>WhoAmIRequest</c> calls
    /// issued through the *same* <see cref="ServiceClient"/> instance. If the SDK's async path
    /// truly serializes every request against that instance (as a naive reading of "one request
    /// at a time" would suggest), concurrent time should be roughly N times a single call's
    /// latency, the same as sequential. If it does not serialize, concurrent time should be much
    /// closer to a single call's latency.
    /// </summary>
    [Fact]
    public async Task ConcurrentRequests_OnSameInstance_AreNotSerializedByTheSdk()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            _output.WriteLine("Skipped: DVPOOL_IT_CONNECTION_STRING not set.");
            return;
        }

        var requestCount = int.TryParse(
            Environment.GetEnvironmentVariable("DVPOOL_IT_REQUEST_COUNT"),
            out var configured) ? configured : 20;

        using var client = new ServiceClient(ConnectionString);
        Assert.True(client.IsReady, client.LastError);

        // Warm-up call: excludes token/discovery cold-start cost from both measurements below,
        // consistent with ADR-0002's finding that only the *first* call/clone pays that cost.
        await client.ExecuteAsync(new WhoAmIRequest());

        var sequentialElapsed = await TimeSequentialAsync(client, requestCount);
        var concurrentElapsed = await TimeConcurrentAsync(client, requestCount);

        _output.WriteLine($"Sequential total: {sequentialElapsed.TotalMilliseconds:F0}ms " +
                           $"({sequentialElapsed.TotalMilliseconds / requestCount:F0}ms/call avg)");
        _output.WriteLine($"Concurrent total:  {concurrentElapsed.TotalMilliseconds:F0}ms");
        _output.WriteLine($"Ratio (concurrent/sequential): " +
                           $"{concurrentElapsed.TotalMilliseconds / sequentialElapsed.TotalMilliseconds:F2}");

        // Soft assertion: if requests were truly serialized client-side, concurrent time would be
        // close to (or worse than) sequential time. A generous threshold (0.6) tolerates network
        // jitter while still failing loudly if serialization reappears (e.g. a future SDK version
        // adds a lock to the async path).
        Assert.True(
            concurrentElapsed < sequentialElapsed * 0.6,
            $"Expected concurrent execution to be substantially faster than sequential if the SDK " +
            $"does not serialize the async path, but concurrent ({concurrentElapsed.TotalMilliseconds:F0}ms) " +
            $"was not < 60% of sequential ({sequentialElapsed.TotalMilliseconds:F0}ms). This may mean the " +
            $"SDK version under test *does* serialize async requests - re-check before assuming otherwise.");
    }

    /// <summary>
    /// Directly tests the specific race the "single request at a time" framing glossed over:
    /// <c>CallerId</c> is a plain, non-thread-local property on <see cref="ServiceClient"/>, read
    /// at call time. Two threads sharing one instance and setting different <c>CallerId</c>
    /// values before each call could, in principle, race: thread A sets CallerId=userA, thread B
    /// sets CallerId=userB before A's request is actually sent, and A's request executes as
    /// userB. This runs many concurrent iterations and asserts every response's impersonated
    /// identity matches the CallerId that iteration intended to use.
    /// </summary>
    [Fact]
    public async Task ConcurrentRequests_WithDifferentCallerIds_OnSameInstance_DoNotMixUpIdentity()
    {
        if (string.IsNullOrEmpty(ConnectionString) ||
            string.IsNullOrEmpty(CallerIdA) ||
            string.IsNullOrEmpty(CallerIdB))
        {
            _output.WriteLine("Skipped: DVPOOL_IT_CONNECTION_STRING / DVPOOL_IT_CALLER_ID_A / " +
                               "DVPOOL_IT_CALLER_ID_B not all set.");
            return;
        }

        var userA = Guid.Parse(CallerIdA);
        var userB = Guid.Parse(CallerIdB);

        using var client = new ServiceClient(ConnectionString);
        Assert.True(client.IsReady, client.LastError);
        await client.ExecuteAsync(new WhoAmIRequest());

        const int IterationsPerUser = 50;
        var mismatches = new System.Collections.Concurrent.ConcurrentBag<string>();

        async Task RunLoopAsync(Guid callerId, string label)
        {
            for (var i = 0; i < IterationsPerUser; i++)
            {
                client.CallerId = callerId;
                var response = (WhoAmIResponse)await client.ExecuteAsync(new WhoAmIRequest());
                if (response.UserId != callerId)
                {
                    mismatches.Add($"{label} iteration {i}: expected {callerId}, got {response.UserId}");
                }
            }
        }

        await Task.WhenAll(
            RunLoopAsync(userA, "A"),
            RunLoopAsync(userB, "B"));

        foreach (var mismatch in mismatches)
        {
            _output.WriteLine(mismatch);
        }

        Assert.True(
            mismatches.IsEmpty,
            $"{mismatches.Count}/{IterationsPerUser * 2} calls executed as the wrong impersonated " +
            $"user when CallerId was set concurrently on a shared ServiceClient instance. This is " +
            $"exactly the identity-mixup risk that made sharing one instance across concurrent " +
            $"callers with different identities unsafe, independent of raw throughput.");
    }

    private static async Task<TimeSpan> TimeSequentialAsync(ServiceClient client, int count)
    {
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            await client.ExecuteAsync(new WhoAmIRequest());
        }

        sw.Stop();
        return sw.Elapsed;
    }

    private static async Task<TimeSpan> TimeConcurrentAsync(ServiceClient client, int count)
    {
        var sw = Stopwatch.StartNew();
        var tasks = new Task[count];
        for (var i = 0; i < count; i++)
        {
            tasks[i] = client.ExecuteAsync(new WhoAmIRequest());
        }

        await Task.WhenAll(tasks);
        sw.Stop();
        return sw.Elapsed;
    }
}
