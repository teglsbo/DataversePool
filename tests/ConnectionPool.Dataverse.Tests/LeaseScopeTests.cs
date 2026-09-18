using ConnectionPool.Dataverse;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Tests <see cref="LeaseScope"/> directly against a fake lease type - not
/// <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient"/>, which cannot be constructed
/// standalone (see e.g. <see cref="DataverseClientOptionsTests"/>'s remarks). This is deliberately
/// where the actual "lease is always released, exception identity is preserved" risk in
/// <see cref="PooledOrganizationService"/> is unit-tested, since that behavior lives entirely in
/// this generic helper - see docs/adr/0020.
/// </summary>
public class LeaseScopeTests
{
    private sealed class FakeLease : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_WithResult_ReturnsOperationResult_AndReleasesLeaseExactlyOnce()
    {
        var lease = new FakeLease();
        var result = await LeaseScope.RunAsync<FakeLease, int>(
            acquire: _ => Task.FromResult(lease),
            operation: l => Task.FromResult(42),
            cancellationToken: CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_WithoutResult_ReleasesLeaseExactlyOnce()
    {
        var lease = new FakeLease();
        var ran = false;
        await LeaseScope.RunAsync<FakeLease>(
            acquire: _ => Task.FromResult(lease),
            operation: l => { ran = true; return Task.CompletedTask; },
            cancellationToken: CancellationToken.None);

        Assert.True(ran);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_WithResult_ReleasesLease_WhenOperationThrows_AndPropagatesSameException()
    {
        var lease = new FakeLease();
        var thrown = new InvalidOperationException("boom");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LeaseScope.RunAsync<FakeLease, int>(
                acquire: _ => Task.FromResult(lease),
                operation: l => throw thrown,
                cancellationToken: CancellationToken.None));

        Assert.Same(thrown, actual); // exact same exception instance, not wrapped/rethrown differently
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_WithoutResult_ReleasesLease_WhenOperationThrows_AndPropagatesSameException()
    {
        var lease = new FakeLease();
        var thrown = new InvalidOperationException("boom");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LeaseScope.RunAsync<FakeLease>(
                acquire: _ => Task.FromResult(lease),
                operation: l => throw thrown,
                cancellationToken: CancellationToken.None));

        Assert.Same(thrown, actual);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_ReleasesLease_WhenOperationIsCanceled()
    {
        var lease = new FakeLease();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            LeaseScope.RunAsync<FakeLease, int>(
                acquire: _ => Task.FromResult(lease),
                operation: l => throw new OperationCanceledException(),
                cancellationToken: CancellationToken.None));

        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_DoesNotAttemptRelease_WhenAcquireItselfThrows()
    {
        // No lease was ever produced, so there is nothing to dispose - this just documents that
        // acquire-failure is not swallowed or retried by LeaseScope itself.
        var thrown = new InvalidOperationException("acquire failed");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LeaseScope.RunAsync<FakeLease, int>(
                acquire: _ => throw thrown,
                operation: l => Task.FromResult(1),
                cancellationToken: CancellationToken.None));

        Assert.Same(thrown, actual);
    }

    [Fact]
    public async Task RunAsync_PropagatesCancellationToken_ToAcquire()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        CancellationToken? observed = null;

        var lease = new FakeLease();
        await LeaseScope.RunAsync<FakeLease, int>(
            acquire: ct => { observed = ct; return Task.FromResult(lease); },
            operation: l => Task.FromResult(1),
            cancellationToken: cts.Token);

        Assert.Equal(cts.Token, observed);
    }
}
