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
///
/// <para>
/// <paramref name="onException"/> (accepted by both overloads below) lets a caller observe an
/// exception from <c>operation</c> against the lease that produced it, without altering
/// propagation - it always runs before the lease is released, and whatever it does (or throws) has
/// no bearing on the original exception, which is always what ultimately propagates. This is how
/// <see cref="PooledOrganizationService"/> reports Dataverse throttling signals back to the pool
/// (see docs/adr/0020) while keeping this type itself fully generic/Dataverse-agnostic - the
/// callback is supplied by the caller, not baked in here.
/// </para>
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
        CancellationToken cancellationToken,
        Action<TLease, Exception>? onException = null)
        where TLease : IAsyncDisposable
    {
        var lease = await acquire(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(lease).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            onException?.Invoke(lease, ex);
            throw;
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
        CancellationToken cancellationToken,
        Action<TLease, Exception>? onException = null)
        where TLease : IAsyncDisposable
    {
        var lease = await acquire(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(lease).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            onException?.Invoke(lease, ex);
            throw;
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}
