using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Adapts a <see cref="DataversePool"/> or <see cref="DataverseUserPool"/> to the standard
/// <see cref="IOrganizationServiceAsync2"/> SDK interface (which itself extends
/// <see cref="IOrganizationServiceAsync"/> and <see cref="IOrganizationService"/>), so existing code
/// built around a long-lived, constructor-injected <c>IOrganizationServiceAsync</c>/
/// <c>IOrganizationServiceAsync2</c> - the standard pattern for consuming the Dataverse SDK - can
/// adopt pooling without restructuring every call site to an explicit
/// acquire-lease/use/dispose-lease pattern. Every interface method acquires exactly one lease from
/// the wrapped pool, invokes the corresponding method on the leased <see cref="ServiceClient"/>, and
/// releases the lease before returning - including when the call throws or is canceled. See
/// docs/adr/0020.
///
/// <para>
/// This is a convenience bridge, not a replacement for the full pool API: it does not retry a
/// throttled call. It does, however, report a recognized Dataverse throttling signal (HTTP 429)
/// back to whichever member served the failing call, via the same
/// <see cref="DataverseLease.ReportIfThrottled"/> mechanism <see cref="DataversePool.ExecuteWithThrottleRetryAsync{T}(string, Func{ServiceClient, CancellationToken, Task{T}}, int?, TimeSpan?, CancellationToken)"/>
/// uses - so a multi-member <see cref="DataversePool"/> consumed only through this facade still
/// steers future acquires away from a member that Dataverse just throttled, not just plain
/// round-robin distribution with no throttle-awareness. Exceptions from the underlying
/// <see cref="ServiceClient"/> call always propagate completely unchanged (same type, same stack) -
/// this reporting is a side effect observed on the way out, never something that alters or
/// suppresses what the caller sees. See docs/adr/0020.
/// </para>
///
/// <para>
/// The synchronous <see cref="IOrganizationService"/> members this interface also requires (e.g.
/// <see cref="Create"/>) block on their async equivalent via <c>GetAwaiter().GetResult()</c> rather
/// than duplicating the lease-acquire logic synchronously - a pool is fundamentally an
/// async-acquire abstraction (see <see cref="ConnectionPool.Core.ResourcePool{T}.AcquireAsync"/>),
/// so there is no synchronous acquire path to call into instead. Prefer the <c>*Async</c> members
/// directly wherever the caller can.
/// </para>
/// </summary>
public sealed class PooledOrganizationService : IOrganizationServiceAsync2
{
    private readonly Func<CancellationToken, Task<DataverseLease>> _acquireLease;
    private readonly DataverseOperationRecorder _recorder;
    private readonly string _poolName;
    private readonly IOperationOutcomeSink? _sizingSink;

    /// <summary>Wraps a multi-member <see cref="DataversePool"/>. Each call acquires a lease from
    /// whichever member the pool's <see cref="ISlotSelectionStrategy"/> selects - see
    /// <see cref="DataversePool.AcquireAsync"/>.</summary>
    /// <param name="pool">The pool to lease from.</param>
    /// <param name="metricsOptions">Optional operation-metrics settings (docs/adr/0024). When
    /// omitted, the <c>pool.name</c> tag is the one the pool itself was configured with.</param>
    public PooledOrganizationService(DataversePool pool, DataverseOperationMetricsOptions? metricsOptions = null)
        : this(pool, metricsOptions, DataverseOperationRecorder.Shared)
    {
    }

    internal PooledOrganizationService(DataversePool pool, DataverseOperationMetricsOptions? metricsOptions, DataverseOperationRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(pool);
        metricsOptions?.Validate();
        _acquireLease = pool.AcquireAsync;
        _sizingSink = pool.SizingSink;
        _recorder = recorder;
        _poolName = metricsOptions?.PoolName ?? pool.MetricsPoolName;
    }

    /// <summary>Wraps a single-member <see cref="DataverseUserPool"/> directly, without requiring
    /// the caller to construct a one-member <see cref="DataversePool"/> just to get this facade.
    /// </summary>
    /// <param name="pool">The pool to lease from.</param>
    /// <param name="metricsOptions">Optional operation-metrics settings (docs/adr/0024).</param>
    public PooledOrganizationService(DataverseUserPool pool, DataverseOperationMetricsOptions? metricsOptions = null)
        : this(pool, metricsOptions, DataverseOperationRecorder.Shared)
    {
    }

    internal PooledOrganizationService(DataverseUserPool pool, DataverseOperationMetricsOptions? metricsOptions, DataverseOperationRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(pool);
        metricsOptions?.Validate();
        _acquireLease = async ct => new DataverseLease(pool, await pool.AcquireAsync(ct).ConfigureAwait(false));
        _recorder = recorder;
        _poolName = metricsOptions?.PoolName ?? DataverseOperationMetricsOptions.DefaultPoolName;
    }

    // Single attempt (this facade never retries), but throttle reporting still steers the pool
    // away from the member that was just throttled. See docs/adr/0020 and docs/adr/0024.
    private Task<TResult> RunAsync<TResult>(string operationName, Func<ServiceClient, Task<TResult>> operation, CancellationToken cancellationToken) =>
        DataverseOperationExecutor.ExecuteAsync(
            new OperationMetricsScope(_recorder, _poolName, operationName),
            DataverseLeaseAccessors.Instance,
            _acquireLease,
            (lease, _) => operation(lease.Resource),
            maxAttempts: 1,
            maxRetryAfter: null,
            cancellationToken,
            _sizingSink);

    private Task RunAsync(string operationName, Func<ServiceClient, Task> operation, CancellationToken cancellationToken) =>
        DataverseOperationExecutor.ExecuteAsync(
            new OperationMetricsScope(_recorder, _poolName, operationName),
            DataverseLeaseAccessors.Instance,
            _acquireLease,
            (lease, _) => operation(lease.Resource),
            maxAttempts: 1,
            maxRetryAfter: null,
            cancellationToken,
            _sizingSink);

    // ----- IOrganizationServiceAsync2 (cancellable) -----

    public Task<Guid> CreateAsync(Entity entity, CancellationToken cancellationToken) =>
        RunAsync("create", svc => svc.CreateAsync(entity, cancellationToken), cancellationToken);

    public Task<Entity> CreateAndReturnAsync(Entity entity, CancellationToken cancellationToken) =>
        RunAsync("create_and_return", svc => svc.CreateAndReturnAsync(entity, cancellationToken), cancellationToken);

    public Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet, CancellationToken cancellationToken) =>
        RunAsync("retrieve", svc => svc.RetrieveAsync(entityName, id, columnSet, cancellationToken), cancellationToken);

    public Task UpdateAsync(Entity entity, CancellationToken cancellationToken) =>
        RunAsync("update", svc => svc.UpdateAsync(entity, cancellationToken), cancellationToken);

    public Task DeleteAsync(string entityName, Guid id, CancellationToken cancellationToken) =>
        RunAsync("delete", svc => svc.DeleteAsync(entityName, id, cancellationToken), cancellationToken);

    public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Stamp a request id (kept if the caller already set one) so this call can be matched against
        // Dataverse's server-side telemetry; the caller can read it back from the request they hold.
        request.RequestId ??= Guid.NewGuid();
        return RunAsync("execute", svc => svc.ExecuteAsync(request, cancellationToken), cancellationToken);
    }

    public Task AssociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken) =>
        RunAsync("associate", svc => svc.AssociateAsync(entityName, entityId, relationship, relatedEntities, cancellationToken), cancellationToken);

    public Task DisassociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken) =>
        RunAsync("disassociate", svc => svc.DisassociateAsync(entityName, entityId, relationship, relatedEntities, cancellationToken), cancellationToken);

    public Task<EntityCollection> RetrieveMultipleAsync(QueryBase query, CancellationToken cancellationToken) =>
        RunAsync("retrieve_multiple", svc => svc.RetrieveMultipleAsync(query, cancellationToken), cancellationToken);

    // ----- IOrganizationServiceAsync (no CancellationToken overload in the SDK's own interface) -----

    public Task<Guid> CreateAsync(Entity entity) => CreateAsync(entity, CancellationToken.None);

    public Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet) =>
        RetrieveAsync(entityName, id, columnSet, CancellationToken.None);

    public Task UpdateAsync(Entity entity) => UpdateAsync(entity, CancellationToken.None);

    public Task DeleteAsync(string entityName, Guid id) => DeleteAsync(entityName, id, CancellationToken.None);

    public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request) =>
        ExecuteAsync(request, CancellationToken.None);

    public Task AssociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        AssociateAsync(entityName, entityId, relationship, relatedEntities, CancellationToken.None);

    public Task DisassociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        DisassociateAsync(entityName, entityId, relationship, relatedEntities, CancellationToken.None);

    public Task<EntityCollection> RetrieveMultipleAsync(QueryBase query) =>
        RetrieveMultipleAsync(query, CancellationToken.None);

    // ----- IOrganizationService (synchronous) -----

    public Guid Create(Entity entity) => CreateAsync(entity).GetAwaiter().GetResult();

    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) =>
        RetrieveAsync(entityName, id, columnSet).GetAwaiter().GetResult();

    public void Update(Entity entity) => UpdateAsync(entity).GetAwaiter().GetResult();

    public void Delete(string entityName, Guid id) => DeleteAsync(entityName, id).GetAwaiter().GetResult();

    public OrganizationResponse Execute(OrganizationRequest request) => ExecuteAsync(request).GetAwaiter().GetResult();

    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        AssociateAsync(entityName, entityId, relationship, relatedEntities).GetAwaiter().GetResult();

    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        DisassociateAsync(entityName, entityId, relationship, relatedEntities).GetAwaiter().GetResult();

    public EntityCollection RetrieveMultiple(QueryBase query) => RetrieveMultipleAsync(query).GetAwaiter().GetResult();
}
