using ConnectionPool.Core;
using ConnectionPool.Dataverse;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Verifies DI wiring only (resolution + hosted-service registration). PrewarmCount is left at the
/// default of 0 so the hosted service's StartAsync does not attempt a real Dataverse connection.
/// </summary>
public class DataversePoolServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddDataverseUserPool_RegistersResolvableKeyedPool_AndHostedService()
    {
        var services = new ServiceCollection();
        services.AddDataverseUserPool("main", "dummy-connection-string");
        await using var provider = services.BuildServiceProvider();

        var pool = provider.GetRequiredKeyedService<DataverseUserPool>("main");
        Assert.Equal("main", pool.Name);

        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        Assert.Single(hostedServices);

        // Safe to start: default PrewarmCount is 0, so no real connection is attempted.
        await hostedServices[0].StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AddDataverseGroupPool_ComposesRegisteredMemberPools()
    {
        var services = new ServiceCollection();
        services.AddDataverseUserPool("user-a", "dummy-a");
        services.AddDataverseUserPool("user-b", "dummy-b");
        services.AddDataverseGroupPool("group-1", new[] { "user-a", "user-b" });

        await using var provider = services.BuildServiceProvider();
        var group = provider.GetRequiredKeyedService<DataverseGroupPool>("group-1");

        Assert.Equal(new[] { "user-a", "user-b" }, group.Members.Select(m => m.Name));
    }
}
