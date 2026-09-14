using ConnectionPool.Core;
using global::Polly;
using global::Polly.CircuitBreaker;
using global::Polly.Retry;
using Xunit;

namespace ConnectionPool.Dataverse.Polly.Tests;

/// <summary>
/// Verifies docs/adr/0005: Polly retry/circuit-breaker triggers call lease.MarkUnhealthy, existing
/// user-supplied callbacks are preserved, and the pool actually evicts the resource as a result
/// (end-to-end through ConnectionPool.Core, not just a mock assertion).
/// </summary>
public class PollyPoolHealthSignalExtensionsTests
{
    [Fact]
    public async Task RetryPipeline_MarksLeaseUnhealthy_OnEachRetriedException()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1 });
        var lease = await pool.AcquireAsync();

        var attempts = 0;
        var pipeline = new ResiliencePipelineBuilder<string>()
            .AddRetryWithPoolHealthSignal(lease, new RetryStrategyOptions<string>
            {
                ShouldHandle = new PredicateBuilder<string>().Handle<InvalidOperationException>(),
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
            })
            .Build();

        var result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new InvalidOperationException("transient");
            }

            return "ok";
        });

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);

        // The pool must have observed the MarkUnhealthy signal: after disposing the lease, the
        // resource should be recycled rather than reused as-is.
        var originalResource = lease.Resource;
        await lease.DisposeAsync();

        FakeResource? replacement = null;
        for (var i = 0; i < 50 && replacement is null; i++)
        {
            await using var probe = await pool.AcquireAsync();
            if (!ReferenceEquals(probe.Resource, originalResource))
            {
                replacement = probe.Resource;
            }
            else
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(replacement);
        Assert.True(originalResource.Disposed);
    }

    [Fact]
    public async Task RetryPipeline_PreservesExistingOnRetryCallback()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1 });
        var lease = await pool.AcquireAsync();

        var userCallbackInvocations = 0;
        var baseOptions = new RetryStrategyOptions<string>
        {
            ShouldHandle = new PredicateBuilder<string>().Handle<InvalidOperationException>(),
            MaxRetryAttempts = 1,
            Delay = TimeSpan.Zero,
            OnRetry = _ =>
            {
                userCallbackInvocations++;
                return default;
            },
        };

        var pipeline = new ResiliencePipelineBuilder<string>()
            .AddRetryWithPoolHealthSignal(lease, baseOptions)
            .Build();

        var attempts = 0;
        await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            if (attempts < 2)
            {
                throw new InvalidOperationException("transient");
            }

            return "ok";
        });

        Assert.Equal(1, userCallbackInvocations);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task CircuitBreakerPipeline_MarksLeaseUnhealthy_WhenCircuitOpens()
    {
        var policy = new FakePolicy();
        await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 1 });
        var lease = await pool.AcquireAsync();
        var originalResource = lease.Resource;

        var pipeline = new ResiliencePipelineBuilder<string>()
            .AddCircuitBreakerWithPoolHealthSignal(lease, new CircuitBreakerStrategyOptions<string>
            {
                ShouldHandle = new PredicateBuilder<string>().Handle<InvalidOperationException>(),
                FailureRatio = 0.1,
                MinimumThroughput = 2,
                SamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(30),
            })
            .Build();

        for (var i = 0; i < 2; i++)
        {
            try
            {
                await pipeline.ExecuteAsync<string>(_ => throw new InvalidOperationException("boom"));
            }
            catch (InvalidOperationException) { }
            catch (BrokenCircuitException) { }
        }

        await lease.DisposeAsync();

        FakeResource? replacement = null;
        for (var i = 0; i < 50 && replacement is null; i++)
        {
            await using var probe = await pool.AcquireAsync();
            if (!ReferenceEquals(probe.Resource, originalResource))
            {
                replacement = probe.Resource;
            }
            else
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(replacement);
        Assert.True(originalResource.Disposed);
    }
}
