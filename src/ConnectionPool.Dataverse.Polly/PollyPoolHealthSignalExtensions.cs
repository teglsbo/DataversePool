using ConnectionPool.Core;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace ConnectionPool.Dataverse.Polly;

/// <summary>
/// Wires Polly's retry/circuit-breaker callbacks to <see cref="PooledLease{T}.MarkUnhealthy"/>, so
/// that a lease is proactively evicted/recycled as soon as the caller's own resilience pipeline
/// detects trouble - without ConnectionPool.Core or ConnectionPool.Dataverse taking a hard
/// dependency on Polly. See docs/adr/0005-polly-as-optional-adapter-not-core-dependency.md.
///
/// Generic over the pooled resource type (not hardcoded to Dataverse's ServiceClient) so it can be
/// unit tested with a fake resource, and reused for any ConnectionPool.Core-based pool; the
/// package name/namespace reflects its primary intended consumer (Dataverse pools) but the
/// implementation has no Dataverse dependency.
///
/// Any <c>OnRetry</c>/<c>OnOpened</c> callback already present on the supplied options is preserved
/// and invoked first - these extensions only add the MarkUnhealthy signal, they never replace
/// existing behavior.
/// </summary>
public static class PollyPoolHealthSignalExtensions
{
    /// <summary>
    /// Adds a retry strategy to the pipeline that also calls <paramref name="lease"/>.MarkUnhealthy
    /// whenever a retry is triggered by an exception.
    /// </summary>
    public static ResiliencePipelineBuilder<TResult> AddRetryWithPoolHealthSignal<TResult, TResource>(
        this ResiliencePipelineBuilder<TResult> builder,
        PooledLease<TResource> lease,
        RetryStrategyOptions<TResult>? baseOptions = null)
        where TResource : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(lease);

        var options = baseOptions ?? new RetryStrategyOptions<TResult>();
        var previousOnRetry = options.OnRetry;
        options.OnRetry = async args =>
        {
            if (previousOnRetry is not null)
            {
                await previousOnRetry(args).ConfigureAwait(false);
            }

            if (args.Outcome.Exception is { } exception)
            {
                lease.MarkUnhealthy(exception);
            }
        };

        return builder.AddRetry(options);
    }

    /// <summary>
    /// Adds a circuit breaker strategy to the pipeline that also calls
    /// <paramref name="lease"/>.MarkUnhealthy whenever the circuit opens due to an exception.
    /// </summary>
    public static ResiliencePipelineBuilder<TResult> AddCircuitBreakerWithPoolHealthSignal<TResult, TResource>(
        this ResiliencePipelineBuilder<TResult> builder,
        PooledLease<TResource> lease,
        CircuitBreakerStrategyOptions<TResult>? baseOptions = null)
        where TResource : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(lease);

        var options = baseOptions ?? new CircuitBreakerStrategyOptions<TResult>();
        var previousOnOpened = options.OnOpened;
        options.OnOpened = async args =>
        {
            if (previousOnOpened is not null)
            {
                await previousOnOpened(args).ConfigureAwait(false);
            }

            if (args.Outcome.Exception is { } exception)
            {
                lease.MarkUnhealthy(exception);
            }
        };

        return builder.AddCircuitBreaker(options);
    }

    /// <summary>Non-generic (object-result) overload of <see cref="AddRetryWithPoolHealthSignal{TResult,TResource}"/>.</summary>
    public static ResiliencePipelineBuilder AddRetryWithPoolHealthSignal<TResource>(
        this ResiliencePipelineBuilder builder,
        PooledLease<TResource> lease,
        RetryStrategyOptions? baseOptions = null)
        where TResource : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(lease);

        var options = baseOptions ?? new RetryStrategyOptions();
        var previousOnRetry = options.OnRetry;
        options.OnRetry = async args =>
        {
            if (previousOnRetry is not null)
            {
                await previousOnRetry(args).ConfigureAwait(false);
            }

            if (args.Outcome.Exception is { } exception)
            {
                lease.MarkUnhealthy(exception);
            }
        };

        return builder.AddRetry(options);
    }

    /// <summary>Non-generic (object-result) overload of <see cref="AddCircuitBreakerWithPoolHealthSignal{TResult,TResource}"/>.</summary>
    public static ResiliencePipelineBuilder AddCircuitBreakerWithPoolHealthSignal<TResource>(
        this ResiliencePipelineBuilder builder,
        PooledLease<TResource> lease,
        CircuitBreakerStrategyOptions? baseOptions = null)
        where TResource : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(lease);

        var options = baseOptions ?? new CircuitBreakerStrategyOptions();
        var previousOnOpened = options.OnOpened;
        options.OnOpened = async args =>
        {
            if (previousOnOpened is not null)
            {
                await previousOnOpened(args).ConfigureAwait(false);
            }

            if (args.Outcome.Exception is { } exception)
            {
                lease.MarkUnhealthy(exception);
            }
        };

        return builder.AddCircuitBreaker(options);
    }
}
