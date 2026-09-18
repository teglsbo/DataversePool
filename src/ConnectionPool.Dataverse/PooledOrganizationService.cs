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
/// This is a convenience bridge, not a replacement for the pool API: it does not retry, and does not
/// detect/report Dataverse throttling back to the pool (see
/// <see cref="DataversePool.ExecuteWithThrottleRetryAsync{T}"/> for that, which needs a
/// <see cref="DataverseLease"/> in hand to report against - something this facade deliberately does
/// not expose, to keep its surface identical to the plain SDK interface). Exceptions from the
/// underlying <see cref="ServiceClient"/> call propagate completely unchanged (same type, same
/// stack) - a caller catching e.g. a fault exception around a call today keeps working exactly the
/// same through this facade.
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

    /// <summary>Wraps a multi-member <see cref="DataversePool"/>. Each call acquires a lease from
    /// whichever member the pool's <see cref="ISlotSelectionStrategy"/> selects - see
    /// <see cref="DataversePool.AcquireAsync"/>.</summary>
    public PooledOrganizationService(DataversePool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        _acquireLease = pool.AcquireAsync;
    }

    /// <summary>Wraps a single-member <see cref="DataverseUserPool"/> directly, without requiring
    /// the caller to construct a one-member <see cref="DataversePool"/> just to get this facade.
    /// </summary>
    public PooledOrganizationService(DataverseUserPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        _acquireLease = async ct => new DataverseLease(pool, await pool.AcquireAsync(ct).ConfigureAwait(false));
    }

    private Task<TResult> RunAsync<TResult>(Func<ServiceClient, Task<TResult>> operation, CancellationToken cancellationToken) =>
        LeaseScope.RunAsync(_acquireLease, lease => operation(lease.Resource), cancellationToken);

    private Task RunAsync(Func<ServiceClient, Task> operation, CancellationToken cancellationToken) =>
        LeaseScope.RunAsync(_acquireLease, lease => operation(lease.Resource), cancellationToken);

    // ----- IOrganizationServiceAsync2 (cancellable) -----

    public Task<Guid> CreateAsync(Entity entity, CancellationToken cancellationToken) =>
        RunAsync(svc => svc.CreateAsync(entity, cancellationToken), cancellationToken);

    public Task<Entity> CreateAndReturnAsync(Entity entity, CancellationToken cancellationToken) =>
        RunAsync(svc => svc.CreateAndReturnAsync(entity, cancellationToken), cancellationToken);

    public Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet, CancellationToken cancellationToken) =>
        RunAsync(svc => svc.RetrieveAsync(entityName, id, columnSet, cancellationToken), cancellationToken);

    public Task UpdateAsync(Entity entity, CancellationToken cancellationToken) =>
        RunAsync(svc => svc.UpdateAsync(entity, cancellationToken), cancellationToken);

    public Task DeleteAsync(string entityName, Guid id, CancellationToken cancellationToken) =>
        RunAsync(svc => svc.DeleteAsync(entityName, id, cancellationToken), cancellationToken);

    public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken cancellationToken) =>
        RunAsync(svc => svc.ExecuteAsync(request, cancellationToken), cancellationToken);

    public Task AssociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken) =>
        RunAsync(svc => svc.AssociateAsync(entityName, entityId, relationship, relatedEntities, cancellationToken), cancellationToken);

    public Task DisassociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken) =>
        RunAsync(svc => svc.DisassociateAsync(entityName, entityId, relationship, relatedEntities, cancellationToken), cancellationToken);

    public Task<EntityCollection> RetrieveMultipleAsync(QueryBase query, CancellationToken cancellationToken) =>
        RunAsync(svc => svc.RetrieveMultipleAsync(query, cancellationToken), cancellationToken);

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
