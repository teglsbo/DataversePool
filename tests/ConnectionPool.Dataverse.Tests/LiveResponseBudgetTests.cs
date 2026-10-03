using ConnectionPool.Core;
using Microsoft.Crm.Sdk.Messages;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>Opt-in live check that the response budget reaches the pool, for both transports.</summary>
[Trait("Category", "Integration")]
public class LiveResponseBudgetTests
{
    private readonly ITestOutputHelper _output;

    public LiveResponseBudgetTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public async Task Facade_And_ExecuteWithThrottleRetry_PublishTheMemberBudget()
    {
        var connectionString = LiveDataverseCredentials.GetConnectionString(0);
        Skip.If(string.IsNullOrEmpty(connectionString), "Set DVPOOL_IT_* credentials.");

        var member = new DataverseUserPool("A", connectionString, new PoolOptions { MaxSize = 2 });
        await using var pool = new DataversePool(member, sizingOptions: new PoolSizingOptions { ObserveResponses = true });
        Assert.Null(pool.GetResponseBudget(member));

        var service = new PooledOrganizationService(pool);
        await service.ExecuteAsync(new WhoAmIRequest()); // SOAP transport
        var soap = pool.GetResponseBudget(member);
        _output.WriteLine($"After facade call: {soap}");
        Assert.NotNull(soap);
        Assert.NotNull(soap!.BurstRemainingRequests);

        await pool.ExecuteWithThrottleRetryAsync<int>(
            "web", async (client, ct) =>
            {
                using var response = await client.ExecuteWebRequestAsync(HttpMethod.Get, "WhoAmI()", string.Empty, null, "application/json", ct);
                return (int)response.StatusCode;
            });
        var web = pool.GetResponseBudget(member);
        _output.WriteLine($"After Web API call: {web}");
        Assert.True(web!.ObservedAt >= soap.ObservedAt);
        Assert.NotNull(web.ServiceRequestId);
    }

    [SkippableFact]
    public async Task TwoMembers_UnderLoad_EachGetsItsOwnBudget()
    {
        var a = LiveDataverseCredentials.GetConnectionString(0);
        var b = LiveDataverseCredentials.GetConnectionString(1);
        Skip.If(string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b), "Set two DVPOOL_IT_* identities.");

        var memberA = new DataverseUserPool("A", a!, new PoolOptions { MaxSize = 4 });
        var memberB = new DataverseUserPool("B", b!, new PoolOptions { MaxSize = 4 });
        await using var pool = new DataversePool(new[] { memberA, memberB }, sizingOptions: new PoolSizingOptions { ObserveResponses = true });

        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 300).Select(async i =>
        {
            // Alternate transports so both are observed concurrently on both members.
            if (i % 2 == 0)
            {
                await pool.ExecuteWithThrottleRetryAsync<int>("soap", async (client, ct) =>
                    (await client.ExecuteAsync(new WhoAmIRequest(), ct)).ResponseName.Length);
            }
            else
            {
                await pool.ExecuteWithThrottleRetryAsync<int>("web", async (client, ct) =>
                {
                    using var r = await client.ExecuteWebRequestAsync(HttpMethod.Get, "WhoAmI()", string.Empty, null, "application/json", ct);
                    return (int)r.StatusCode;
                });
            }
        }));
        _output.WriteLine($"300 calls in {sw.ElapsedMilliseconds} ms");

        var ba = pool.GetResponseBudget(memberA);
        var bb = pool.GetResponseBudget(memberB);
        _output.WriteLine($"A: {ba}");
        _output.WriteLine($"B: {bb}");
        Assert.NotNull(ba);
        Assert.NotNull(bb);
        Assert.NotEqual(ba!.ServiceRequestId, bb!.ServiceRequestId);
        Assert.True(ba.BurstRemainingRequests < 8000 && bb.BurstRemainingRequests < 8000, "both members spent budget");
    }
}
