using System.Diagnostics;
using ConnectionPool.Core;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Opt-in, spends the second identity's request budget (8000 per 5 min) while pinned to ONE backend node
/// with its ARRAffinity cookie, then checks whether calls that land on other nodes are throttled too.
/// If they are, the request budget is per user; if they succeed, it is per (user, node). Set <c>DVPOOL_IT_NODESCOPE=1</c>.
/// </summary>
[Trait("Category", "Integration")]
public class LiveNodeBudgetScopeTests
{
    private readonly ITestOutputHelper _output;

    public LiveNodeBudgetScopeTests(ITestOutputHelper output) => _output = output;

    private sealed record Probe(string Kind, int Status, string Node, string Burst);

    [SkippableFact]
    public async Task ThrottlingOnePinnedNode_ShowsWhetherTheBudgetIsPerUserOrPerNode()
    {
        Skip.If(Environment.GetEnvironmentVariable("DVPOOL_IT_NODESCOPE") != "1", "Set DVPOOL_IT_NODESCOPE=1 to spend a user's request budget.");
        var connectionString = LiveDataverseCredentials.GetConnectionString(1);
        Skip.If(string.IsNullOrEmpty(connectionString), "Set a second DVPOOL_IT_* identity.");

        static async Task<Probe> CallAsync(DataversePool pool, string? cookie, string kind)
        {
            try
            {
                return await pool.ExecuteWithThrottleRetryAsync<Probe>("nodescope", async (client, ct) =>
                {
                    var custom = cookie is null ? null : new Dictionary<string, List<string>> { ["Cookie"] = new() { "ARRAffinity=" + cookie } };
                    using var r = await client.ExecuteWebRequestAsync(HttpMethod.Get, "WhoAmI()", string.Empty, custom, "application/json", ct);
                    var node = r.Headers.TryGetValues("X-Source", out var s) ? s.Last().Split('|').Last() : "?";
                    var burst = r.Headers.TryGetValues("x-ms-ratelimit-burst-remaining-xrm-requests", out var b) ? b.First() : "?";
                    var set = r.Headers.TryGetValues("Set-Cookie", out var sc) ? sc.FirstOrDefault(c => c.StartsWith("ARRAffinity=", StringComparison.Ordinal)) : null;
                    return new Probe(kind + (set is null ? string.Empty : "|" + set.Split(';')[0]["ARRAffinity=".Length..]), (int)r.StatusCode, node, burst);
                }, maxAttempts: 1);
            }
            catch (Exception ex)
            {
                var decoded = DataverseThrottleDetector.TryGetThrottleReason(ex, out var reason);
                return new Probe(kind, decoded ? 429 : -1, "-", decoded ? reason.ToString() : ex.GetType().Name + ": " + ex.Message);
            }
        }

        var drainMember = new DataverseUserPool("B", connectionString!, new PoolOptions { MaxSize = 16 });
        await using var drainPool = new DataversePool(drainMember);

        // Find a cookie, then confirm it pins: the next calls must keep answering from one node.
        string? cookie = null;
        for (var i = 0; i < 20 && cookie is null; i++)
        {
            cookie = (await CallAsync(drainPool, null, "capture")).Kind.Split('|').ElementAtOrDefault(1);
        }

        Assert.NotNull(cookie);
        var pinNode = (await CallAsync(drainPool, cookie, "pin")).Node;
        _output.WriteLine($"pinned to node {Short(pinNode)}");

        var clock = Stopwatch.StartNew();
        var calls = 0;
        var offNode = 0;
        Probe? firstThrottle = null;
        var stop = false;

        async Task Worker()
        {
            while (!Volatile.Read(ref stop) && clock.Elapsed < TimeSpan.FromMinutes(9))
            {
                var p = await CallAsync(drainPool, cookie, "drain");
                var n = Interlocked.Increment(ref calls);
                if (p.Status == 429)
                {
                    Interlocked.CompareExchange(ref firstThrottle, p, null);
                    Volatile.Write(ref stop, true);
                    return;
                }

                if (p.Node != pinNode)
                {
                    Interlocked.Increment(ref offNode);
                }

                if (n % 1000 == 0)
                {
                    _output.WriteLine($"{n} calls, burst {p.Burst}, off-pinned-node responses {offNode}, {clock.Elapsed:mm\\:ss}");
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(Worker)));
        _output.WriteLine($"drain done: calls={calls}, first throttle={(firstThrottle is null ? "none" : firstThrottle.Burst)}, responses from another node while pinned={offNode}, {clock.Elapsed}");

        // Fresh pool, so no quarantine from the drain pool can mask the result.
        await using var probePool = new DataversePool(new DataverseUserPool("B", connectionString!, new PoolOptions { MaxSize = 4 }));
        var results = new List<Probe>();
        for (var i = 0; i < 30; i++)
        {
            results.Add(await CallAsync(probePool, null, "unpinned"));
            results.Add(await CallAsync(probePool, cookie, "pinned"));
        }

        foreach (var g in results.GroupBy(r => r.Kind.Split('|')[0]))
        {
            var ok = g.Count(r => r.Status == 200);
            var thr = g.Count(r => r.Status == 429);
            var nodes = g.Where(r => r.Node != "-").Select(r => r.Node).Distinct().Count();
            _output.WriteLine($"{g.Key}: {g.Count()} calls, ok={ok}, throttled={thr}, other={g.Count() - ok - thr}, distinct nodes answering={nodes}");
        }

        _output.WriteLine("sample: " + string.Join(" ", results.Take(12).Select(r => $"{r.Kind.Split('|')[0]}:{r.Status}@{Short(r.Node)}")));
    }

    private static string Short(string node) => node.Length > 8 ? node[..4] + ".." + node[^4..] : node;
}
