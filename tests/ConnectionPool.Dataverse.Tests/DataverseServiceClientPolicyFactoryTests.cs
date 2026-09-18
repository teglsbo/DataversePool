using ConnectionPool.Dataverse;
using Microsoft.PowerPlatform.Dataverse.Client;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Tests for the <see cref="DataverseServiceClientPolicy(Func{CancellationToken, Task{ServiceClient}}, Microsoft.Extensions.Logging.ILogger?, DataverseClientOptions?)"/>
/// base-client-factory constructor, added for callers whose authentication (e.g. MSAL/custom
/// token-provider) doesn't fit the connection-string constructor. As with the rest of this policy,
/// a real <see cref="ServiceClient"/> cannot be constructed in a unit test without a live Dataverse
/// connection, so these tests cover wiring, failure propagation, and cancellation - not a successful
/// base-client construction.
/// </summary>
public class DataverseServiceClientPolicyFactoryTests
{
    [Fact]
    public void Constructor_NullFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DataverseServiceClientPolicy((Func<CancellationToken, Task<ServiceClient>>)null!));
    }

    [Fact]
    public async Task CreateAsync_FactoryThrows_PropagatesException()
    {
        var expected = new InvalidOperationException("factory boom");
        var policy = new DataverseServiceClientPolicy(_ => throw expected);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => policy.CreateAsync(CancellationToken.None));
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task CreateAsync_FactoryReturnsNonReadyClient_ThrowsInvalidOperationException()
    {
        // A ServiceClient can't be constructed as "ready" outside a real Dataverse connection, but
        // the factory contract only requires it to return *some* ServiceClient - a null return
        // (the other failure shape a caller's factory could produce) must also be rejected cleanly
        // rather than surfacing a NullReferenceException from deeper in the policy.
        var policy = new DataverseServiceClientPolicy(_ => Task.FromResult<ServiceClient>(null!));

        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.CreateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_PreCanceledToken_ThrowsWithoutInvokingFactory()
    {
        var invocationCount = 0;
        var policy = new DataverseServiceClientPolicy(_ =>
        {
            Interlocked.Increment(ref invocationCount);
            throw new InvalidOperationException("should never be invoked");
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.CreateAsync(cts.Token));
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task CreateAsync_FactoryFailsThenIsRetried_InvokedAgainOnNextAcquire()
    {
        // Mirrors the connection-string constructor's existing behavior: a failed base-client
        // attempt leaves _baseClient null, so the *next* CreateAsync retries construction from
        // scratch rather than caching the failure forever.
        var invocationCount = 0;
        var policy = new DataverseServiceClientPolicy(_ =>
        {
            Interlocked.Increment(ref invocationCount);
            throw new InvalidOperationException("still failing");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.CreateAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.CreateAsync(CancellationToken.None));

        Assert.Equal(2, invocationCount);
    }

    [Fact]
    public void DataverseUserPool_FactoryConstructor_NullFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DataverseUserPool("member", (Func<CancellationToken, Task<ServiceClient>>)null!));
    }

    [Fact]
    public async Task DataverseUserPool_FactoryConstructor_AcquireAsync_PropagatesFactoryException()
    {
        var expected = new InvalidOperationException("factory boom");
        await using var pool = new DataverseUserPool("member", _ => throw expected);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.AcquireAsync());
        Assert.Same(expected, actual);
    }
}
