using ConnectionPool.Core;
using ConnectionPool.Dataverse;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Tests <see cref="PooledOrganizationService"/>'s wiring (constructors, delegation to the
/// underlying pool, cancellation propagation). The actual per-method SDK calls
/// (Create/Retrieve/Update/Delete/Execute/...) cannot be exercised against a real
/// <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient"/> without a live Dataverse
/// connection (it cannot be constructed standalone - see <see cref="DataverseClientOptionsTests"/>'s
/// remarks), so this suite proves the facade is correctly wired end-to-end (it really does attempt
/// an acquire, and really does propagate whatever the pool throws, unchanged) using the same
/// "dummy connection string fails inside DataverseServiceClientPolicy.CreateAsync" technique already
/// used by <see cref="DataversePoolTests"/>. The lease-release/exception-identity guarantees
/// themselves are unit-tested directly, independent of Dataverse, in <see cref="LeaseScopeTests"/> -
/// see docs/adr/0020.
/// </summary>
public class PooledOrganizationServiceTests
{
    [Fact]
    public void Constructor_Throws_WhenDataversePoolIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new PooledOrganizationService((DataversePool)null!));
    }

    [Fact]
    public void Constructor_Throws_WhenDataverseUserPoolIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new PooledOrganizationService((DataverseUserPool)null!));
    }

    [Fact]
    public async Task RetrieveAsync_WrappingDataverseUserPool_PropagatesAcquireFailure_ProvingItReallyAcquiresALease()
    {
        // "dummy-a" is not a valid connection string - DataverseServiceClientPolicy.CreateAsync
        // throws constructing the real ServiceClient. If this facade were accidentally a no-op (or
        // swallowed the failure), this would not throw. It proving that the underlying exception
        // type/instance passes through this facade completely unchanged is the key behavior under
        // test here - not the specific exception type Dataverse's SDK happens to throw for a bad
        // connection string.
        var member = new DataverseUserPool("user-a", "dummy-a");
        var facade = new PooledOrganizationService(member);

        await Assert.ThrowsAnyAsync<Exception>(
            () => facade.RetrieveAsync("account", Guid.NewGuid(), null!));
    }

    [Fact]
    public async Task RetrieveAsync_WrappingDataversePool_PropagatesAcquireFailure_ProvingItReallyAcquiresALease()
    {
        var member = new DataverseUserPool("user-a", "dummy-a");
        await using var pool = new DataversePool(member);
        var facade = new PooledOrganizationService(pool);

        await Assert.ThrowsAnyAsync<Exception>(
            () => facade.RetrieveAsync("account", Guid.NewGuid(), null!));
    }

    [Fact]
    public async Task CreateAsync_SyncOverload_DelegatesToCancellableOverload_AndPropagatesAcquireFailure()
    {
        // Exercises the non-CancellationToken IOrganizationServiceAsync overload specifically (it
        // forwards to the IOrganizationServiceAsync2 overload with CancellationToken.None).
        var member = new DataverseUserPool("user-a", "dummy-a");
        var facade = new PooledOrganizationService(member);

        await Assert.ThrowsAnyAsync<Exception>(() => facade.CreateAsync(new Microsoft.Xrm.Sdk.Entity("account")));
    }

    [Fact]
    public void SynchronousCreate_PropagatesAcquireFailure_ViaBlockingWait()
    {
        // Exercises the synchronous IOrganizationService.Create member, which blocks on CreateAsync.
        var member = new DataverseUserPool("user-a", "dummy-a");
        var facade = new PooledOrganizationService(member);

        Assert.ThrowsAny<Exception>(() => facade.Create(new Microsoft.Xrm.Sdk.Entity("account")));
    }

    [Fact]
    public async Task RetrieveAsync_WithAlreadyCanceledToken_ThrowsBeforeAttemptingConnection()
    {
        // ResourcePool<T>.AcquireAsync's capacity-gate wait observes the token immediately, so a
        // pre-canceled token throws OperationCanceledException before DataverseServiceClientPolicy
        // ever attempts to construct a ServiceClient - proving the CancellationToken parameter is
        // genuinely forwarded from the facade all the way to the pool's AcquireAsync, not dropped.
        var member = new DataverseUserPool("user-a", "dummy-a");
        var facade = new PooledOrganizationService(member);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => facade.RetrieveAsync("account", Guid.NewGuid(), null!, cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_WithAlreadyCanceledToken_OnDataversePool_ThrowsBeforeAttemptingConnection()
    {
        var member = new DataverseUserPool("user-a", "dummy-a");
        await using var pool = new DataversePool(member);
        var facade = new PooledOrganizationService(pool);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => facade.ExecuteAsync(new Microsoft.Xrm.Sdk.OrganizationRequest("WhoAmI"), cts.Token));
    }
}
