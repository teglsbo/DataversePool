using System.Collections.Concurrent;
using System.Diagnostics;
using ConnectionPool.Core;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Opt-in, destructive-ish live check: spends the second identity's request budget (8000 per 5 min) to see
/// the low-budget hold fire and to capture whatever real throttle follows. Set <c>DVPOOL_IT_DRAIN=1</c>.
/// </summary>
[Trait("Category", "Integration")]
public class LiveBudgetDrainTests
{
    private readonly ITestOutputHelper _output;

    public LiveBudgetDrainTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public async Task DrainingTheBudget_TriggersTheLowBudgetHold_AndReportsTheRealThrottle()
    {
        Skip.If(Environment.GetEnvironmentVariable("DVPOOL_IT_DRAIN") != "1", "Set DVPOOL_IT_DRAIN=1 to spend a user's request budget.");
        var connectionString = LiveDataverseCredentials.GetConnectionString(1);
        Skip.If(string.IsNullOrEmpty(connectionString), "Set a second DVPOOL_IT_* identity.");

        // DVPOOL_IT_DRAIN_TO_THROTTLE=1: observer off, keep going until the server throttles, to capture its real body.
        var toThrottle = Environment.GetEnvironmentVariable("DVPOOL_IT_DRAIN_TO_THROTTLE") == "1";
        var member = new DataverseUserPool("B", connectionString!, new PoolOptions { MaxSize = 16 });
        await using var pool = new DataversePool(member, sizingOptions: new PoolSizingOptions
        {
            ObserveResponses = !toThrottle,
            LowBudgetBackoff = TimeSpan.FromSeconds(3),
        });

        var deadline = Stopwatch.StartNew();
        var maxDuration = TimeSpan.FromMinutes(8);
        var calls = 0;
        var minBurst = double.MaxValue;
        var maxBurst = 0d;
        var lowSeen = 0;
        var heldCalls = 0;
        var throttles = new ConcurrentBag<Exception>();
        var stop = false;

        async Task Worker()
        {
            while (!Volatile.Read(ref stop) && deadline.Elapsed < maxDuration)
            {
                var lowBefore = !toThrottle && pool.GetResponseBudget(member)?.BurstRemainingRequests is { } r && r <= maxBurst * 0.05;
                var sw = Stopwatch.StartNew();
                try
                {
                    await pool.ExecuteWithThrottleRetryAsync<int>("drain", async (client, ct) =>
                    {
                        using var response = await client.ExecuteWebRequestAsync(HttpMethod.Get, "WhoAmI()", string.Empty, null, "application/json", ct);
                        return (int)response.StatusCode;
                    }, maxAttempts: 1);
                }
                catch (Exception ex)
                {
                    throttles.Add(ex);
                    Volatile.Write(ref stop, true);
                    return;
                }

                var n = Interlocked.Increment(ref calls);
                if (pool.GetResponseBudget(member)?.BurstRemainingRequests is { } burst)
                {
                    lock (throttles)
                    {
                        minBurst = Math.Min(minBurst, burst);
                        maxBurst = Math.Max(maxBurst, burst);
                    }
                }

                if (lowBefore)
                {
                    Interlocked.Increment(ref lowSeen);
                    if (sw.ElapsedMilliseconds >= 2500)
                    {
                        Interlocked.Increment(ref heldCalls);
                    }
                }

                // Drain well past the threshold, then stop: the point is the hold, not to be throttled.
                if (!toThrottle && lowSeen >= 40)
                {
                    Volatile.Write(ref stop, true);
                }

                if (n % 1000 == 0)
                {
                    _output.WriteLine($"{n} calls, burst remaining ~{pool.GetResponseBudget(member)?.BurstRemainingRequests}, {deadline.Elapsed:mm\\:ss}");
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(Worker)));

        _output.WriteLine($"calls={calls}, burst max={maxBurst}, min={minBurst}, calls after low budget={lowSeen}, held>=2.5s={heldCalls}, elapsed={deadline.Elapsed}");
        foreach (var ex in throttles.Take(3))
        {
            var decoded = DataverseThrottleDetector.TryGetThrottleReason(ex, out var reason);
            var response = ex.GetType().GetProperty("Response")?.GetValue(ex);
            var body = response?.GetType().GetProperty("Content")?.GetValue(response);
            _output.WriteLine($"THROTTLE decoded={decoded} reason={reason} :: {ex.GetType().Name}: {ex.Message} {body}");
        }

        if (toThrottle)
        {
            return; // informational: the output shows whether and how the server throttled
        }

        Assert.True(minBurst <= maxBurst * 0.05 || throttles.Count > 0, "never reached the low-budget threshold or a throttle");
        if (lowSeen > 0)
        {
            Assert.True(heldCalls > 0, "calls after the budget dropped below the threshold were not held back");
        }
    }
}
