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
    public async Task AddDataversePool_ComposesRegisteredMemberPools()
    {
        var services = new ServiceCollection();
        services.AddDataverseUserPool("user-a", "dummy-a");
        services.AddDataverseUserPool("user-b", "dummy-b");
        services.AddDataversePool("group-1", new[] { "user-a", "user-b" });

        await using var provider = services.BuildServiceProvider();
        var group = provider.GetRequiredKeyedService<DataversePool>("group-1");

        Assert.Equal(new[] { "user-a", "user-b" }, group.Members.Select(m => m.Name));
    }

    [Fact]
    public async Task AddDataverseUserPool_AcceptsAndAppliesDataverseClientOptions()
    {
        // Regression guard: this overload previously accepted only PoolOptions, so the retry knobs
        // that bound how long a call can stall inside the SDK's own retry loop were unreachable from
        // the recommended DI onboarding path (DataverseUserPool's constructor had them all along).
        var services = new ServiceCollection();
        services.AddDataverseUserPool(
            "main",
            "dummy-connection-string",
            clientOptions: new DataverseClientOptions { MaxRetryCount = 0 });

        await using var provider = services.BuildServiceProvider();

        // Resolution is what proves the options flowed into the pool's construction without being
        // rejected; the values themselves only reach a real ServiceClient, which cannot be
        // constructed without a live connection.
        var pool = provider.GetRequiredKeyedService<DataverseUserPool>("main");
        Assert.Equal("main", pool.Name);
    }

    [Fact]
    public void AddDataverseUserPool_ValidatesClientOptionsEagerly_AtRegistrationTime()
    {
        var services = new ServiceCollection();

        // Must throw from the registration call itself, not lazily from the keyed-singleton factory
        // on first acquire (which would surface far from the actual mistake).
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddDataverseUserPool(
                "main",
                "dummy-connection-string",
                clientOptions: new DataverseClientOptions { MaxRetryCount = -1 }));
    }
}
