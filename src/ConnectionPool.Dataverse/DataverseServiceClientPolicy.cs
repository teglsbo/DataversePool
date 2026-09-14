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
///
/// <para>
/// Every client this policy produces (base and clones) has <see cref="ServiceClient.EnableAffinityCookie"/>
/// forced to <c>false</c> in code, regardless of what the connection string says - Dataverse's server
/// affinity cookie pins all requests from one client to a single backend node, which is
/// counter-productive for a pool that exists specifically to spread concurrent requests out. Setting
/// this in code (not just documenting it as a connection-string recommendation) means it can't be
/// forgotten or silently overridden by a connection string that includes
/// <c>EnableAffinityCookie=true</c>.
/// </para>
///
/// <para>
/// Optionally, <see cref="DataverseClientOptions"/> passed to the constructor overrides
/// <see cref="ServiceClient.MaxRetryCount"/>/<see cref="ServiceClient.RetryPauseTime"/> on both the
/// base client and every clone - see that type's docs for why this (unlike affinity cookies) is
/// left to the caller rather than forced to a fixed value.
/// </para>
/// </summary>
public sealed class DataverseServiceClientPolicy : IPooledResourcePolicy<ServiceClient>, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger? _logger;
    private readonly DataverseClientOptions? _clientOptions;
    private readonly SemaphoreSlim _baseInitGate = new(1, 1);
    private ServiceClient? _baseClient;

    public DataverseServiceClientPolicy(string connectionString, ILogger? logger = null, DataverseClientOptions? clientOptions = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string must not be empty.", nameof(connectionString));
        }

        clientOptions?.Validate();

        _connectionString = connectionString;
        _logger = logger;
        _clientOptions = clientOptions;
    }

    public async Task<ServiceClient> CreateAsync(CancellationToken cancellationToken)
    {
        var baseClient = await GetOrCreateBaseClientAsync(cancellationToken).ConfigureAwait(false);
        var clone = baseClient.Clone(_logger);
        clone.EnableAffinityCookie = false;
        ApplyRetryOverrides(clone);
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

            // See docs/adr/0002 - this base client is never leased out directly, only cloned, but
            // Clone() may copy session-level settings from it, so keep it consistent with clones.
            _baseClient.EnableAffinityCookie = false;
            ApplyRetryOverrides(_baseClient);

            return _baseClient;
        }
        finally
        {
            _baseInitGate.Release();
        }
    }

    /// <summary>
    /// Applies any configured <see cref="DataverseClientOptions.MaxRetryCount"/>/
    /// <see cref="DataverseClientOptions.RetryPauseTime"/> overrides to <paramref name="client"/>.
    /// Applied to both the base client and every clone (same defensive redundancy as
    /// <see cref="ServiceClient.EnableAffinityCookie"/> above) since it is not guaranteed that
    /// <see cref="ServiceClient.Clone(ILogger)"/> copies these settings from its source.
    /// No-op (SDK defaults apply) when <see cref="_clientOptions"/> is null or a given value is unset.
    /// </summary>
    private void ApplyRetryOverrides(ServiceClient client)
    {
        if (_clientOptions?.MaxRetryCount is { } maxRetryCount)
        {
            client.MaxRetryCount = maxRetryCount;
        }

        if (_clientOptions?.RetryPauseTime is { } retryPauseTime)
        {
            client.RetryPauseTime = retryPauseTime;
        }
    }
}
