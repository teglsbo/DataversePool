namespace ConnectionPool.Dataverse;

/// <summary>
/// Implements the "acquire a lease, run an operation against it, always release the lease
/// afterwards" pattern used by <see cref="PooledOrganizationService"/>. Extracted as a small,
/// generic, fully unit-testable primitive with no dependency on <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient"/>
/// or Dataverse at all, so the one genuinely risky part of the facade - the lease is always
/// released, exactly once, even when the operation throws or the acquire itself is canceled, and
/// the original exception's type/identity is never altered - can be verified directly against a
/// fake lease type in tests (see <c>LeaseScopeTests</c>). The per-method SDK plumbing in
/// <see cref="PooledOrganizationService"/> itself is deliberately trivial (one-line delegation per
/// interface member) precisely so that all of its real risk lives here, in one place that can
/// actually be tested - see docs/adr/0020.
/// </summary>
internal static class LeaseScope
{
    /// <summary>Runs <paramref name="operation"/> against a lease acquired via
    /// <paramref name="acquire"/>, returning its result. The lease is released via
    /// <see cref="IAsyncDisposable.DisposeAsync"/> before this method returns, whether
    /// <paramref name="operation"/> completes successfully, throws, or is canceled.</summary>
    public static async Task<TResult> RunAsync<TLease, TResult>(
        Func<CancellationToken, Task<TLease>> acquire,
        Func<TLease, Task<TResult>> operation,
        CancellationToken cancellationToken)
        where TLease : IAsyncDisposable
    {
        var lease = await acquire(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(lease).ConfigureAwait(false);
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Same as <see cref="RunAsync{TLease, TResult}"/>, for operations with no result.</summary>
    public static async Task RunAsync<TLease>(
        Func<CancellationToken, Task<TLease>> acquire,
        Func<TLease, Task> operation,
        CancellationToken cancellationToken)
        where TLease : IAsyncDisposable
    {
        var lease = await acquire(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(lease).ConfigureAwait(false);
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}
