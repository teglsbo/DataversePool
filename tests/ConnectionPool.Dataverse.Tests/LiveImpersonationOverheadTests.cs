using System.Diagnostics;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Query;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Opt-in, live-Dataverse test that measures the per-call latency cost of impersonating via
/// <see cref="ServiceClient.CallerAADObjectId"/>, as distinct from the earlier correctness-only
/// tests in <see cref="LiveServiceClientConcurrencyTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// Not run in CI (excluded via <c>Category!=Integration</c>). Requires real Dataverse credentials
/// via environment variables; self-<c>Skip</c>s when they are not set.
/// </para>
/// <para>
/// Uses <c>RetrieveMultiple</c> against the <c>organization</c> entity (a small, org-wide-readable
/// singleton table) so the exact same operation runs both impersonated and non-impersonated -
/// isolating the impersonation overhead itself rather than confounding it with a different
/// operation's cost (e.g. <c>WhoAmI</c>, which Dataverse deliberately makes ignore impersonation
/// entirely, so it would not exercise the server-side impersonation path at all).
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class LiveImpersonationOverheadTests
{
    private readonly ITestOutputHelper _output;

    public LiveImpersonationOverheadTests(ITestOutputHelper output) => _output = output;

    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("DVPOOL_IT_CONNECTION_STRING");

    private static string? CallerAadObjectId =>
        Environment.GetEnvironmentVariable("DVPOOL_IT_CALLER_AAD_OBJECT_ID_A");

    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("DVPOOL_IT_OVERHEAD_ITERATIONS"), out var n) ? n : 30;

    [Fact]
    public async Task ImpersonatedCalls_OnSameOperation_AreMeasurablyComparedAgainstNonImpersonated()
    {
        if (string.IsNullOrEmpty(ConnectionString) || string.IsNullOrEmpty(CallerAadObjectId))
        {
            _output.WriteLine("Skipped: DVPOOL_IT_CONNECTION_STRING / DVPOOL_IT_CALLER_AAD_OBJECT_ID_A not set.");
            return;
        }

        var aad = Guid.Parse(CallerAadObjectId);
        var iterations = Iterations;

        using var client = new ServiceClient(ConnectionString);
        Assert.True(client.IsReady, client.LastError);

        var query = new QueryExpression("organization")
        {
            ColumnSet = new ColumnSet("name"),
            TopCount = 1,
        };

        // Warm up (cold-start cost, connection setup) - excluded from both measurements.
        client.CallerAADObjectId = null;
        await client.RetrieveMultipleAsync(query);

        var baseline = await TimeSequentialAsync(client, query, iterations, impersonate: null);
        var impersonated = await TimeSequentialAsync(client, query, iterations, impersonate: aad);
        client.CallerAADObjectId = null;

        var baselinePerCall = baseline.TotalMilliseconds / iterations;
        var impersonatedPerCall = impersonated.TotalMilliseconds / iterations;
        var deltaPerCall = impersonatedPerCall - baselinePerCall;

        _output.WriteLine($"Baseline (no impersonation):     {baseline.TotalMilliseconds:F0}ms total, {baselinePerCall:F1}ms/call");
        _output.WriteLine($"Impersonated (CallerAADObjectId): {impersonated.TotalMilliseconds:F0}ms total, {impersonatedPerCall:F1}ms/call");
        _output.WriteLine($"Delta: {deltaPerCall:+0.0;-0.0}ms/call ({(impersonatedPerCall / baselinePerCall):F2}x baseline)");

        // Sanity bound only - this is a measurement, not a strict performance contract. Guards
        // against a gross regression (e.g. impersonation accidentally forcing a slow path) without
        // asserting on noisy exact numbers.
        Assert.True(
            impersonatedPerCall < baselinePerCall * 5 + 500,
            $"Impersonated calls ({impersonatedPerCall:F1}ms/call) were unexpectedly much slower than " +
            $"baseline ({baselinePerCall:F1}ms/call) - investigate before assuming impersonation overhead is negligible.");
    }

    private static async Task<TimeSpan> TimeSequentialAsync(
        ServiceClient client, QueryExpression query, int count, Guid? impersonate)
    {
        client.CallerAADObjectId = impersonate;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            await client.RetrieveMultipleAsync(query);
        }

        sw.Stop();
        return sw.Elapsed;
    }
}
