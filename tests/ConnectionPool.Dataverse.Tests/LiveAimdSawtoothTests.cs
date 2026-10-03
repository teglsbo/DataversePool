using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ConnectionPool.Core;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Opt-in live check of <see cref="AimdPoolSizingStrategy"/> against an unbounded consumer: many workers loop
/// without limit against a pool whose MaxSize is far above Dataverse's concurrency limit. Everything else is the library default (no tuned Aimd settings). Records pool size,
/// in-flight calls, throughput and throttles per second, to show the back-off / regrow pattern.
/// Set <c>DVPOOL_IT_SAWTOOTH=1</c>; <c>DVPOOL_IT_SAWTOOTH_MINUTES</c> (default 10), <c>_WORKERS</c> (300), <c>_MAXSIZE</c> (160).
/// Uses identity index 1 and spends its request budget.
/// </summary>
[Trait("Category", "Integration")]
public class LiveAimdSawtoothTests
{
    private readonly ITestOutputHelper _output;

    public LiveAimdSawtoothTests(ITestOutputHelper output) => _output = output;

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    [SkippableFact]
    public async Task UnboundedConsumer_ShowsAimdBackOffAndRegrowth()
    {
        Skip.If(Environment.GetEnvironmentVariable("DVPOOL_IT_SAWTOOTH") != "1", "Set DVPOOL_IT_SAWTOOTH=1 to run.");
        var connectionString = LiveDataverseCredentials.GetConnectionString(1);
        Skip.If(string.IsNullOrEmpty(connectionString), "Set a second DVPOOL_IT_* identity.");

        var minutes = EnvInt("DVPOOL_IT_SAWTOOTH_MINUTES", 10);
        var workers = EnvInt("DVPOOL_IT_SAWTOOTH_WORKERS", 1000);
        var maxSize = EnvInt("DVPOOL_IT_SAWTOOTH_MAXSIZE", 400);
        var slow = Environment.GetEnvironmentVariable("DVPOOL_IT_SAWTOOTH_SLOW") == "1";

        // MaxRetryCount 0: the SDK must not absorb 429s, or the pool never sees them.
        var member = new DataverseUserPool(
            "B",
            connectionString!,
            new PoolOptions { MaxSize = maxSize, PrewarmCount = Math.Min(maxSize, EnvInt("DVPOOL_IT_SAWTOOTH_PREWARM", 64)) },
            clientOptions: new DataverseClientOptions { MaxRetryCount = 0 });
        await member.WarmupAsync();

        await using var pool = new DataversePool(member, sizingOptions: new PoolSizingOptions
        {
            Strategy = new AimdPoolSizingStrategy(new AimdPoolSizingStrategyOptions
            {
                BinaryRecovery = Environment.GetEnvironmentVariable("DVPOOL_IT_SAWTOOTH_BINARY") == "1",
            }),
            ObserveResponses = true,
        });

        var run = Stopwatch.StartNew();
        var end = TimeSpan.FromMinutes(minutes);
        var ok = 0L;
        var failed = 0L;
        var inFlight = 0;
        var reasons = new ConcurrentDictionary<string, long>();

        async Task Worker()
        {
            while (run.Elapsed < end)
            {
                try
                {
                    await pool.ExecuteWithThrottleRetryAsync("sawtooth", async (client, ct) =>
                    {
                        Interlocked.Increment(ref inFlight);
                        try
                        {
                            if (slow)
                            {
                                var meta = (RetrieveAllEntitiesResponse)await client.ExecuteAsync(
                                    new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = false }, ct);
                                return meta.EntityMetadata.Length;
                            }

                            return (await client.ExecuteAsync(new WhoAmIRequest(), ct)).ResponseName.Length;
                        }
                        catch (Exception ex) when (DataverseThrottleDetector.TryGetThrottleReason(ex, out var reason))
                        {
                            reasons.AddOrUpdate(reason.ToString(), 1, (_, n) => n + 1);
                            throw;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref inFlight);
                        }
                    });
                    Interlocked.Increment(ref ok);
                }
                catch (Exception ex)
                {
                    reasons.AddOrUpdate("ex:" + ex.GetType().Name + ":" + ex.Message[..Math.Min(60, ex.Message.Length)].Replace(',', ';'), 1, (_, n) => n + 1);
                    Interlocked.Increment(ref failed);
                    await Task.Delay(250);
                }
            }
        }

        var csv = new StringBuilder("t_s,max_size,in_flight,leased,ok_per_s,failed_total,concurrency_429,requestcount_429,exectime_429,burst_left\n");
        var sizes = new List<int>();
        var lastOk = 0L;
        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(Worker)).ToArray();
        var next = TimeSpan.FromSeconds(1);
        while (run.Elapsed < end)
        {
            await Task.Delay(next - run.Elapsed > TimeSpan.Zero ? next - run.Elapsed : TimeSpan.Zero);
            next += TimeSpan.FromSeconds(1);
            var okNow = Interlocked.Read(ref ok);
            var stats = member.GetStats();
            sizes.Add(member.MaxSize);
            var line = string.Join(',',
                (int)run.Elapsed.TotalSeconds,
                member.MaxSize,
                Volatile.Read(ref inFlight),
                stats.LeasedCount,
                okNow - lastOk,
                Interlocked.Read(ref failed),
                reasons.GetValueOrDefault("ConcurrentRequests"),
                reasons.GetValueOrDefault("RequestCount"),
                reasons.GetValueOrDefault("ExecutionTime"),
                pool.GetResponseBudget(member)?.BurstRemainingRequests?.ToString("F0", CultureInfo.InvariantCulture) ?? "NaN");
            csv.AppendLine(line);
            lastOk = okNow;
            if ((int)run.Elapsed.TotalSeconds % 5 == 0)
            {
                _output.WriteLine(line);
            }
        }

        await Task.WhenAll(tasks);

        var dir = Environment.GetEnvironmentVariable("DVPOOL_SOAK_REPORT_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "sawtooth.csv"), csv.ToString());
        }

        var drops = 0;
        var rises = 0;
        for (var i = 1; i < sizes.Count; i++)
        {
            drops += sizes[i] < sizes[i - 1] ? 1 : 0;
            rises += sizes[i] > sizes[i - 1] ? 1 : 0;
        }

        _output.WriteLine($"SUMMARY ok={ok} failed={failed} size min={sizes.Min()} max={sizes.Max()} drops={drops} rises={rises} throttles=[{string.Join(", ", reasons.Select(kv => kv.Key + "=" + kv.Value))}]");
        Assert.True(ok > 0);
    }
}
