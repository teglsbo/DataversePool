using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Opt-in, live-Dataverse tests that measure whether a single <see cref="ServiceClient"/>
/// instance actually serializes concurrent async requests, and whether sharing one instance
/// across concurrent callers impersonating different identities is safe.
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

    // Two distinct users' Entra (Azure AD) object ids - *not* systemuserids - for the
    // impersonation-race test. Impersonation for an OAuth/client-secret-authenticated
    // ServiceClient goes through CallerAADObjectId, not the older CallerId (systemuserid)
    // property; CallerId is silently ignored for this auth type (confirmed empirically - it
    // produces no error and simply doesn't impersonate, unlike CallerAADObjectId which the
    // server actually validates). The calling app user also needs the "Act on Behalf of Another
    // User" privilege (built into Dataverse's standard "Delegate" security role) to impersonate
    // at all. Deliberately separate from ConnectionString so the timing test can run without any
    // of this set up.
    private static string? CallerAadObjectIdA =>
        Environment.GetEnvironmentVariable("DVPOOL_IT_CALLER_AAD_OBJECT_ID_A");

    private static string? CallerAadObjectIdB =>
        Environment.GetEnvironmentVariable("DVPOOL_IT_CALLER_AAD_OBJECT_ID_B");

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
    /// <para>
    /// Directly tests the specific race the "single request at a time" framing glossed over:
    /// <c>CallerAADObjectId</c> is a plain, non-thread-local property on
    /// <see cref="ServiceClient"/>, read at call time. Two threads sharing one instance and
    /// setting different impersonation targets before each call could, in principle, race:
    /// thread A sets the identity to userA, thread B overwrites it with userB before A's request
    /// is actually dispatched, and A's request executes (and is attributed to) userB.
    /// </para>
    /// <para>
    /// <see cref="WhoAmIRequest"/> cannot be used to detect this: Dataverse deliberately makes
    /// <c>WhoAmI</c> ignore impersonation and always return the real, non-impersonated caller
    /// (documented Microsoft behavior) - it would report the app user's own id even when
    /// impersonation is fully working. This test instead creates a real record per iteration
    /// while impersonating, then reads back the <c>createdby</c> field, which *does* reflect the
    /// impersonated identity that actually performed the create. Records are deleted again while
    /// still impersonating their owner (the app user itself has no direct access to a record
    /// owned by someone else, as confirmed empirically), then trying the other candidate identity
    /// as a fallback for records whose ownership ended up mixed up by a race.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ConcurrentRequests_WithDifferentCallerAadObjectIds_OnSameInstance_DoNotMixUpIdentity()
    {
        if (string.IsNullOrEmpty(ConnectionString) ||
            string.IsNullOrEmpty(CallerAadObjectIdA) ||
            string.IsNullOrEmpty(CallerAadObjectIdB))
        {
            _output.WriteLine("Skipped: DVPOOL_IT_CONNECTION_STRING / DVPOOL_IT_CALLER_AAD_OBJECT_ID_A / " +
                               "DVPOOL_IT_CALLER_AAD_OBJECT_ID_B not all set.");
            return;
        }

        var aadA = Guid.Parse(CallerAadObjectIdA);
        var aadB = Guid.Parse(CallerAadObjectIdB);

        using var client = new ServiceClient(ConnectionString);
        Assert.True(client.IsReady, client.LastError);

        var systemUserIdA = await ResolveSystemUserIdAsync(client, aadA);
        var systemUserIdB = await ResolveSystemUserIdAsync(client, aadB);

        const int IterationsPerUser = 15;
        var runTag = Guid.NewGuid().ToString("N");
        var created = new ConcurrentBag<(Guid RecordId, Guid Aad, Guid ExpectedSystemUserId, string Label)>();

        async Task RunLoopAsync(Guid aad, Guid expectedSystemUserId, string label)
        {
            for (var i = 0; i < IterationsPerUser; i++)
            {
                client.CallerAADObjectId = aad;
                var recordId = await client.CreateAsync(new Entity("task")
                {
                    ["subject"] = $"DataversePool-IT-{runTag}-{label}-{i}",
                });
                created.Add((recordId, aad, expectedSystemUserId, label));
            }
        }

        var mismatches = new List<string>();
        try
        {
            await Task.WhenAll(
                RunLoopAsync(aadA, systemUserIdA, "A"),
                RunLoopAsync(aadB, systemUserIdB, "B"));

            foreach (var (recordId, aad, expectedSystemUserId, label) in created)
            {
                client.CallerAADObjectId = aad;
                try
                {
                    var record = await client.RetrieveAsync("task", recordId, new ColumnSet("createdby"));
                    var createdBy = record.GetAttributeValue<EntityReference>("createdby")?.Id;
                    if (createdBy != expectedSystemUserId)
                    {
                        mismatches.Add($"{label} record {recordId}: createdby={createdBy}, expected {expectedSystemUserId}");
                    }
                }
                catch (Exception ex)
                {
                    mismatches.Add($"{label} record {recordId}: could not retrieve while impersonating " +
                                   $"its intended owner ({ex.GetType().Name}) - likely owned by the other " +
                                   $"user due to a race.");
                }
            }

            foreach (var mismatch in mismatches)
            {
                _output.WriteLine(mismatch);
            }

            Assert.True(
                mismatches.Count == 0,
                $"{mismatches.Count}/{IterationsPerUser * 2} records were created under the wrong " +
                $"impersonated identity when CallerAADObjectId was set concurrently on a shared " +
                $"ServiceClient instance. This is exactly the identity-mixup risk that made sharing " +
                $"one instance across concurrent callers with different identities unsafe, " +
                $"independent of raw throughput.");
        }
        finally
        {
            foreach (var (recordId, aad, _, _) in created)
            {
                foreach (var candidateAad in new[] { aad, aadA, aadB })
                {
                    try
                    {
                        client.CallerAADObjectId = candidateAad;
                        await client.DeleteAsync("task", recordId);
                        break;
                    }
                    catch
                    {
                        // Try the next candidate identity; if none can delete it, leave it - it's
                        // clearly tagged with runTag and can be cleaned up manually.
                    }
                }
            }

            client.CallerAADObjectId = null;
        }
    }

    private static async Task<Guid> ResolveSystemUserIdAsync(ServiceClient client, Guid aadObjectId)
    {
        var query = new QueryExpression("systemuser")
        {
            ColumnSet = new ColumnSet("systemuserid"),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression("azureactivedirectoryobjectid", ConditionOperator.Equal, aadObjectId) },
            },
            TopCount = 1,
        };

        var result = await client.RetrieveMultipleAsync(query);
        Assert.True(
            result.Entities.Count == 1,
            $"Expected exactly one systemuser with azureactivedirectoryobjectid={aadObjectId}, found {result.Entities.Count}.");
        return result.Entities[0].Id;
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
