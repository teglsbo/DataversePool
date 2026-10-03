using ConnectionPool.Core;
using Microsoft.Crm.Sdk.Messages;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests.Soak;

/// <summary>
/// Refills at a fixed rate up to a burst capacity. Keeps the harness inside Dataverse's 8000 requests per
/// 300 s per user (the worst case is every request landing on one user: capacity + refill * 300 s).
/// </summary>
internal sealed class SoakTokenBucket
{
    private readonly double _capacity;
    private readonly double _perSecond;
    private readonly object _gate = new();
    private double _tokens;
    private long _last = Environment.TickCount64;

    public SoakTokenBucket(double capacity, double perSecond)
    {
        _capacity = capacity;
        _perSecond = perSecond;
        _tokens = capacity;
    }

    public double Available
    {
        get
        {
            lock (_gate)
            {
                Refill();
                return _tokens;
            }
        }
    }

    public async Task<TimeSpan> TakeAsync(CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        while (true)
        {
            lock (_gate)
            {
                Refill();
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
                }
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        var now = Environment.TickCount64;
        _tokens = Math.Min(_capacity, _tokens + (now - _last) / 1000.0 * _perSecond);
        _last = now;
    }
}

/// <summary>
/// Burn-in against a real environment: spikes, steady load and idle periods through the full pool (Aimd sizing,
/// response observer, health-aware selection), watching for leaks and slowdowns. Opt-in:
/// <list type="bullet">
/// <item><c>DVPOOL_SOAK=1</c> enables it.</item>
/// <item><c>DVPOOL_SOAK_MINUTES</c> run length (default 30); <c>DVPOOL_SOAK_SCALE</c> phase-length multiplier (default 1; 0.15 for a smoke test).</item>
/// <item><c>DVPOOL_SOAK_SEED</c> (default 1); <c>DVPOOL_SOAK_MAX_SPIKE</c> peak concurrency (default 48).</item>
/// <item><c>DVPOOL_SOAK_ALLOW_THROTTLE=1</c> removes the request-rate guard so spikes can hit real throttles.</item>
/// <item><c>DVPOOL_SOAK_REPORT_DIR</c> output folder (default a temp folder); progress is appended to <c>progress.log</c>.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Suite", "Soak")]
public class LiveSoakTests
{
    private readonly ITestOutputHelper _output;

    public LiveSoakTests(ITestOutputHelper output) => _output = output;

    private static double EnvDouble(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    [SkippableFact]
    public async Task BurnIn_SpikesAndIdle_NoLeaksNoSlowdowns()
    {
        Skip.If(Environment.GetEnvironmentVariable("DVPOOL_SOAK") != "1", "Set DVPOOL_SOAK=1 to run the burn-in.");
        var a = LiveDataverseCredentials.GetConnectionString(0);
        var b = LiveDataverseCredentials.GetConnectionString(1);
        Skip.If(string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b), "Set two DVPOOL_IT_* identities.");

        var minutes = EnvDouble("DVPOOL_SOAK_MINUTES", 30);
        var scale = EnvDouble("DVPOOL_SOAK_SCALE", 1);
        var seed = (int)EnvDouble("DVPOOL_SOAK_SEED", 1);
        var maxSpike = (int)EnvDouble("DVPOOL_SOAK_MAX_SPIKE", 48);
        var allowThrottle = Environment.GetEnvironmentVariable("DVPOOL_SOAK_ALLOW_THROTTLE") == "1";
        var dir = Environment.GetEnvironmentVariable("DVPOOL_SOAK_REPORT_DIR")
                  ?? Path.Combine(Path.GetTempPath(), $"dvpool-soak-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(dir);
        var progress = Path.Combine(dir, "progress.log");

        var memberA = new DataverseUserPool("A", a!, new PoolOptions { MaxSize = 8 });
        var memberB = new DataverseUserPool("B", b!, new PoolOptions { MaxSize = 8 });
        await using var pool = new DataversePool(new[] { memberA, memberB }, sizingOptions: new PoolSizingOptions
        {
            Strategy = new AimdPoolSizingStrategy(),
            ObserveResponses = true,
        });

        var bucket = allowThrottle ? null : new SoakTokenBucket(capacity: 2500, perSecond: 15);
        var phases = SoakProfile.Generate(TimeSpan.FromMinutes(minutes), seed, scale, maxSpike);
        var header = $"Live soak: {minutes} min, scale {scale}, seed {seed}, max spike {maxSpike}, rate guard {(allowThrottle ? "OFF" : "on")}, {phases.Count} phases, report in {dir}";
        _output.WriteLine(header);
        File.AppendAllText(progress, header + Environment.NewLine);

        var runner = new SoakRunner(
            new SoakOptions
            {
                SampleInterval = TimeSpan.FromSeconds(Math.Clamp(15 * scale, 2, 15)),
                QuietSettle = TimeSpan.FromSeconds(Math.Clamp(10 * scale, 2, 10)),
                OnSample = s => File.AppendAllText(progress,
                    $"{s.Elapsed:hh\\:mm\\:ss} {s.PhaseKind,-6} {s.PhaseLabel,-10} inflight={s.InFlight,3} ops/s={s.OpsPerSecond,6:F1} p50={s.P50Ms,6:F0} p95={s.P95Ms,6:F0} err={s.Errors} heap={s.HeapBytes / 1048576.0:F1}MB threads={s.Threads} fds={s.Handles}{(s.Quiet ? " [rest]" : string.Empty)}{Environment.NewLine}"),
            },
            async (phase, ct) =>
            {
                var waited = bucket is null ? TimeSpan.Zero : await bucket.TakeAsync(ct);
                switch (Random.Shared.Next(5))
                {
                    case 0 or 1:
                        await pool.ExecuteWithThrottleRetryAsync("soak-soap", async (client, token) =>
                            (await client.ExecuteAsync(new WhoAmIRequest(), token)).ResponseName.Length, cancellationToken: ct);
                        break;
                    case 2 or 3:
                        await pool.ExecuteWithThrottleRetryAsync("soak-web", async (client, token) =>
                        {
                            using var r = await client.ExecuteWebRequestAsync(HttpMethod.Get, "WhoAmI()", string.Empty, null, "application/json", token);
                            return (int)r.StatusCode;
                        }, cancellationToken: ct);
                        break;
                    default:
                        await pool.ExecuteWithThrottleRetryAsync("soak-query", async (client, token) =>
                        {
                            using var r = await client.ExecuteWebRequestAsync(HttpMethod.Get, "systemusers?$select=fullname&$top=5", string.Empty, null, "application/json", token);
                            return (int)r.StatusCode;
                        }, cancellationToken: ct);
                        break;
                }

                return waited;
            },
            classify: ex => DataverseThrottleDetector.TryGetThrottleReason(ex, out var reason)
                ? "Throttle:" + reason
                : ex.GetType().Name,
            probe: () => new Dictionary<string, double>
            {
                ["a_burst_left"] = pool.GetResponseBudget(memberA)?.BurstRemainingRequests ?? double.NaN,
                ["b_burst_left"] = pool.GetResponseBudget(memberB)?.BurstRemainingRequests ?? double.NaN,
                ["a_max_size"] = memberA.MaxSize,
                ["b_max_size"] = memberB.MaxSize,
                ["guard_tokens"] = bucket?.Available ?? double.NaN,
                ["a_nodes"] = pool.GetServerNodeStats(memberA).Count,
                ["b_nodes"] = pool.GetServerNodeStats(memberB).Count,
            });

        var result = await runner.RunAsync(phases);
        // With the guard on, spike throughput is set by the guard's refill rate, so it says nothing about the pool.
        var verdict = SoakAnalyzer.Analyze(result, new SoakThresholds { ThroughputFloorFraction = bucket is null ? 0.65 : 0 });
        var report = SoakAnalyzer.Report(result, verdict) + NodeReport("A", pool.GetServerNodeStats(memberA)) + NodeReport("B", pool.GetServerNodeStats(memberB));
        _output.WriteLine(report);
        File.WriteAllText(Path.Combine(dir, "report.txt"), report);
        SoakAnalyzer.WriteCsv(result, Path.Combine(dir, "samples.csv"));

        Assert.True(verdict.Passed, report);
    }

    private static string NodeReport(string name, IReadOnlyList<ServerNodeStats> nodes) =>
        $"{Environment.NewLine}Backend nodes for identity {name}: {nodes.Count}{Environment.NewLine}" +
        string.Concat(nodes.Select(n => $"  {n.ServerId}: {n.Responses} responses, burst left {n.MinBurst:0}..{n.MaxBurst:0}{Environment.NewLine}"));
}
