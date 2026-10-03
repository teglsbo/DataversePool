using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using ConnectionPool.Core;
using Microsoft.PowerPlatform.Dataverse.Client;
using Xunit;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Opt-in live comparison (<c>DVPOOL_IT_DAN=dan|pool</c>) of two ways of writing accounts through the Web API with
/// both identities: "dan" mimics github.com/DanAakesen/DataversePerformanceTool (fixed workers per app user, a
/// client-side 5-minute counter that waits at 92% of 6000, honours Retry-After as-is, 3 retries), "pool" uses
/// DataversePool with default Aimd and no sizing configuration. Run them one after another, never together.
/// <c>DVPOOL_IT_DAN_MINUTES</c> (6), <c>DVPOOL_IT_DAN_WORKERS</c> (workers per identity, 25).
/// </summary>
public class LiveDanModeComparisonTests
{
    private readonly ITestOutputHelper _output;

    public LiveDanModeComparisonTests(ITestOutputHelper output) => _output = output;

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static Task<HttpResponseMessage> PostAccount(ServiceClient client, CancellationToken ct) =>
        client.ExecuteWebRequestAsync(
            HttpMethod.Post, "accounts", "{\"name\":\"dvpool-dan-" + Guid.NewGuid().ToString("N") + "\"}", null, "application/json", ct);

    [SkippableFact]
    public async Task WriteAccounts()
    {
        var mode = Environment.GetEnvironmentVariable("DVPOOL_IT_DAN");
        Skip.If(mode is not ("dan" or "pool"), "Set DVPOOL_IT_DAN=dan or pool.");
        var conn = new[] { LiveDataverseCredentials.GetConnectionString(0), LiveDataverseCredentials.GetConnectionString(1) };
        Skip.If(conn.Any(string.IsNullOrEmpty), "Needs two DVPOOL_IT_* identities.");

        var minutes = EnvInt("DVPOOL_IT_DAN_MINUTES", 6);
        var perIdentity = EnvInt("DVPOOL_IT_DAN_WORKERS", 25);
        var run = Stopwatch.StartNew();
        var end = TimeSpan.FromMinutes(minutes);
        long ok = 0, failed = 0, throttled = 0;
        var errors = new ConcurrentDictionary<string, long>();

        Task[] tasks;
        Func<Task> dispose = () => Task.CompletedTask;

        if (mode == "dan")
        {
            var clients = new List<ServiceClient>();
            var limiter = new ConcurrentDictionary<string, Queue<long>>();
            const int safe = 5520; // 92% of 6000, his default
            tasks = [];
            for (var i = 0; i < 2; i++)
            {
                var user = "user" + i;
                var pool = new DataverseUserPool("D" + i, conn[i]!, new PoolOptions { MaxSize = perIdentity, PrewarmCount = perIdentity }, clientOptions: new DataverseClientOptions { MaxRetryCount = 0 });
                await pool.WarmupAsync();
                var q = limiter.GetOrAdd(user, _ => new Queue<long>());
                var userPool = new DataversePool(pool);
                var worker = Enumerable.Range(0, perIdentity).Select(_ => Task.Run(async () =>
                {
                    while (run.Elapsed < end)
                    {
                        var attempt = 0;
                        while (true)
                        {
                            long wait = 0;
                            lock (q)
                            {
                                var now = Environment.TickCount64;
                                while (q.Count > 0 && now - q.Peek() > 300_000) q.Dequeue();
                                q.Enqueue(now);
                                if (q.Count >= safe) wait = q.Peek() + 300_000 - now;
                            }

                            if (wait > 0) await Task.Delay((int)Math.Min(wait, 300_000));
                            if (run.Elapsed >= end) break;
                            await using var lease = await userPool.AcquireAsync();
                            try
                            {
                                using var response = await PostAccount(lease.Resource, CancellationToken.None);
                                Interlocked.Increment(ref ok);
                                break;
                            }
                            catch (Exception ex) when (DataverseThrottleDetector.TryGetRetryAfter(ex, TimeSpan.FromMinutes(30), out var retry))
                            {
                                Interlocked.Increment(ref throttled);
                                if (++attempt > 3)
                                {
                                    Interlocked.Increment(ref failed);
                                    errors.AddOrUpdate("max-retries", 1, (_, n) => n + 1);
                                    break;
                                }

                                await lease.DisposeAsync();
                                await Task.Delay(retry);
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref failed);
                                errors.AddOrUpdate(ex.GetType().Name, 1, (_, n) => n + 1);
                                break;
                            }
                        }
                    }
                })).ToArray();
                tasks = tasks.Concat(worker).ToArray();
                var captured = userPool;
                var prev = dispose;
                dispose = async () => { await prev(); await captured.DisposeAsync(); };
            }
        }
        else
        {
            var members = conn.Select((c, i) => new DataverseUserPool(
                "P" + i, c!, new PoolOptions { MaxSize = perIdentity * 4, PrewarmCount = 32 }, clientOptions: new DataverseClientOptions { MaxRetryCount = 0 })).ToArray();
            var pool = new DataversePool(members, sizingOptions: new PoolSizingOptions { Strategy = new AimdPoolSizingStrategy(), ObserveResponses = true });
            await pool.WarmupAsync();
            dispose = async () => await pool.DisposeAsync();
            tasks = Enumerable.Range(0, perIdentity * 8).Select(_ => Task.Run(async () =>
            {
                while (run.Elapsed < end)
                {
                    try
                    {
                        await pool.ExecuteWithThrottleRetryAsync("dan-pool", async (client, ct) =>
                        {
                            using var response = await PostAccount(client, ct);
                            return (int)response.StatusCode;
                        });
                        Interlocked.Increment(ref ok);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        errors.AddOrUpdate(ex.GetType().Name, 1, (_, n) => n + 1);
                        await Task.Delay(250);
                    }
                }
            })).ToArray();
        }

        var last = 0L;
        var series = new List<long>();
        while (run.Elapsed < end)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            var now = Interlocked.Read(ref ok);
            series.Add(now - last);
            last = now;
        }

        await Task.WhenAll(tasks);
        await dispose();
        _output.WriteLine($"MODE={mode} minutes={minutes} ok={ok} ok_per_s={ok / run.Elapsed.TotalSeconds:F1} failed={failed} throttled={throttled} errors=[{string.Join(", ", errors.Select(kv => kv.Key + "=" + kv.Value))}]");
        _output.WriteLine("ok per 10s: " + string.Join(' ', series));
        Assert.True(ok > 0);
    }
}
