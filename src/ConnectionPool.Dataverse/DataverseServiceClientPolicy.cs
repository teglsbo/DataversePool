using ConnectionPool.Core;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace ConnectionPool.Dataverse;

/// <summary>
/// <see cref="IPooledResourcePolicy{T}"/> for Dataverse <see cref="ServiceClient"/>. Holds one real,
/// authenticated "base" connection (created lazily on first use, ~500ms cold cost) and produces
/// every pooled slot via <see cref="ServiceClient.Clone(ILogger)"/>, which reuses the cached
/// token/discovery info of the base connection (~1ms per sequential clone, per empirical
/// measurement). See docs/adr/0002-serial-creation-gate-no-parallel-cloning.md - the pool above this
/// policy guarantees CreateAsync is never invoked concurrently.
/// </summary>
public sealed class DataverseServiceClientPolicy : IPooledResourcePolicy<ServiceClient>, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _baseInitGate = new(1, 1);
    private ServiceClient? _baseClient;

    public DataverseServiceClientPolicy(string connectionString, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string must not be empty.", nameof(connectionString));
        }

        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<ServiceClient> CreateAsync(CancellationToken cancellationToken)
    {
        var baseClient = await GetOrCreateBaseClientAsync(cancellationToken).ConfigureAwait(false);
        var clone = baseClient.Clone(_logger);
        if (!clone.IsReady)
        {
            var error = clone.LastError;
            clone.Dispose();
            throw new InvalidOperationException($"Failed to clone Dataverse ServiceClient: {error}");
        }

        return clone;
    }

    public bool IsHealthy(ServiceClient resource, PoolIncidentInfo? lastIncident)
        => lastIncident is null && resource.IsReady;

    /// <summary>
    /// Clears <see cref="ServiceClient.CallerId"/> (Dataverse's "act as another user" impersonation
    /// field) before a returned client goes back on the idle stack. Without this, a caller that
    /// impersonates a user and disposes its lease without resetting <c>CallerId</c> would leave the
    /// exact same <see cref="ServiceClient"/> instance impersonating that user for whichever
    /// unrelated caller acquires it next - a real cross-caller identity leak. See
    /// docs/adr/0009-return-scrubbing-hook-caller-id-leak.md.
    /// </summary>
    public void OnReturned(ServiceClient resource) => resource.CallerId = Guid.Empty;

    public ValueTask DisposeResourceAsync(ServiceClient resource)
    {
        resource.Dispose();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _baseInitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _baseClient?.Dispose();
            _baseClient = null;
        }
        finally
        {
            _baseInitGate.Release();
        }
    }

    private async Task<ServiceClient> GetOrCreateBaseClientAsync(CancellationToken cancellationToken)
    {
        if (_baseClient is { IsReady: true })
        {
            return _baseClient;
        }

        // Defensive guard: the owning ResourcePool<T> already serializes CreateAsync calls
        // (docs/adr/0002), but this policy could in principle be reused outside a pool too.
        await _baseInitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_baseClient is { IsReady: true })
            {
                return _baseClient;
            }

            _baseClient?.Dispose();
            _baseClient = new ServiceClient(_connectionString, _logger);
            if (!_baseClient.IsReady)
            {
                var error = _baseClient.LastError;
                throw new InvalidOperationException($"Failed to establish base Dataverse connection: {error}");
            }

            return _baseClient;
        }
        finally
        {
            _baseInitGate.Release();
        }
    }
}
