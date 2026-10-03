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
}
