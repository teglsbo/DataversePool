using ConnectionPool.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectionPool.Dataverse;

/// <summary>
/// DI registration helpers, following the modern .NET pattern of keyed services for multiple named
/// pools (one per service user) plus an <see cref="IHostedService"/> that sequentially warms up each
/// pool at startup (docs/adr/0002).
/// </summary>
public static class DataversePoolServiceCollectionExtensions
{
    /// <summary>
    /// Registers a named, single-user Dataverse connection pool as a keyed singleton, and a hosted
    /// service that sequentially prewarms it at startup.
    /// </summary>
    public static IServiceCollection AddDataverseUserPool(
        this IServiceCollection services,
        string name,
        string connectionString,
        Action<PoolOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new PoolOptions();
        configureOptions?.Invoke(options);

        services.AddKeyedSingleton<DataverseUserPool>(name, (sp, key) =>
        {
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<DataverseUserPool>();
            return new DataverseUserPool((string)key!, connectionString, options, logger);
        });

        services.AddSingleton<IHostedService>(sp =>
            new DataversePoolWarmupHostedService(sp.GetRequiredKeyedService<DataverseUserPool>(name)));

        return services;
    }

    /// <summary>
    /// Registers a named group pool composed of previously-registered named user pools
    /// (see <see cref="AddDataverseUserPool"/>), round-robin by default (docs/adr/0006).
    /// </summary>
    public static IServiceCollection AddDataverseGroupPool(
        this IServiceCollection services,
        string groupName,
        IReadOnlyList<string> memberPoolNames,
        Func<IServiceProvider, ISlotSelectionStrategy>? strategyFactory = null,
        GroupAllUnavailableBehavior allUnavailableBehavior = GroupAllUnavailableBehavior.FailOpen)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        if (memberPoolNames is null || memberPoolNames.Count == 0)
        {
            throw new ArgumentException("At least one member pool name is required.", nameof(memberPoolNames));
        }

        services.AddKeyedSingleton<DataverseGroupPool>(groupName, (sp, _) =>
        {
            var members = memberPoolNames.Select(sp.GetRequiredKeyedService<DataverseUserPool>);
            var strategy = strategyFactory?.Invoke(sp);
            return new DataverseGroupPool(members, strategy, allUnavailableBehavior);
        });

        return services;
    }
}

/// <summary>
/// Sequentially warms up a single <see cref="DataverseUserPool"/> at host startup. One instance is
/// registered per pool by <see cref="DataversePoolServiceCollectionExtensions.AddDataverseUserPool"/>,
/// so that multiple pools' prewarming is naturally serialized by the generic host's sequential
/// IHostedService.StartAsync invocation - keeping with docs/adr/0002 at the whole-application level.
/// </summary>
internal sealed class DataversePoolWarmupHostedService : IHostedService
{
    private readonly DataverseUserPool _pool;

    public DataversePoolWarmupHostedService(DataverseUserPool pool) => _pool = pool;

    public Task StartAsync(CancellationToken cancellationToken) => _pool.WarmupAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
