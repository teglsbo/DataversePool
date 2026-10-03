using ConnectionPool.Core;
using Microsoft.Crm.Sdk.Messages;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Opt-in, live-Dataverse check of the correlation ids (PLAN Phase 3 item 5): per-connection
/// <c>SessionTrackingId</c> and the <c>RequestId</c> stamped by <see cref="PooledOrganizationService"/>.
/// Not run in CI (<c>Category!=Integration</c>); self-skips without <c>DVPOOL_IT_*</c> credentials.
/// </summary>
[Trait("Category", "Integration")]
public class LiveCorrelationIdTests
{
    private readonly ITestOutputHelper _output;

    public LiveCorrelationIdTests(ITestOutputHelper output) => _output = output;

    private static string? ConnectionString => LiveDataverseCredentials.GetConnectionString(0);

    [SkippableFact]
    public async Task PerConnectionSessionTrackingId_IsUniquePerConnection_AndRestoredOnReturn()
    {
        Skip.If(string.IsNullOrEmpty(ConnectionString), "Set DVPOOL_IT_* credentials.");

        await using var pool = new DataverseUserPool(
            "A", ConnectionString, new PoolOptions { MaxSize = 3 },
            clientOptions: new DataverseClientOptions { PerConnectionSessionTrackingId = true });

        await using var first = await pool.AcquireAsync();
        await using var second = await pool.AcquireAsync();
        var firstId = first.Resource.SessionTrackingId;
        var secondId = second.Resource.SessionTrackingId;
        _output.WriteLine($"Session ids: {firstId} / {secondId}");

        Assert.NotNull(firstId);
        Assert.NotNull(secondId);
        Assert.NotEqual(firstId, secondId);

        // A caller overriding the id mid-lease must not leak it to the next lease of that connection.
        first.Resource.SessionTrackingId = Guid.NewGuid();
        var firstClient = first.Resource;
        await first.DisposeAsync();
        await using var again = await pool.AcquireAsync();
        Assert.Same(firstClient, again.Resource);
        Assert.Equal(firstId, again.Resource.SessionTrackingId);

        // The tagged connection still works end to end.
        var who = (WhoAmIResponse)await again.Resource.ExecuteAsync(new WhoAmIRequest());
        Assert.NotEqual(Guid.Empty, who.UserId);
    }

    [SkippableFact]
    public async Task Facade_StampsRequestId_AndKeepsACallerSuppliedOne()
    {
        Skip.If(string.IsNullOrEmpty(ConnectionString), "Set DVPOOL_IT_* credentials.");

        await using var pool = new DataverseUserPool("A", ConnectionString, new PoolOptions { MaxSize = 2 });
        var service = new PooledOrganizationService(pool);

        var stamped = new WhoAmIRequest();
        var response = (WhoAmIResponse)await service.ExecuteAsync(stamped);
        Assert.NotEqual(Guid.Empty, response.UserId);
        Assert.NotNull(stamped.RequestId);
        _output.WriteLine($"Stamped RequestId: {stamped.RequestId}");

        var supplied = Guid.NewGuid();
        var own = new WhoAmIRequest { RequestId = supplied };
        await service.ExecuteAsync(own);
        Assert.Equal(supplied, own.RequestId);
    }
}
