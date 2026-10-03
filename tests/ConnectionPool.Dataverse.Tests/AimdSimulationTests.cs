using ConnectionPool.Core;
using Xunit;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Deterministic simulation of <see cref="AimdPoolSizingStrategy"/> (default options) against a modelled Dataverse
/// user: an unbounded consumer always has work, the server enforces a concurrency limit and a request-count
/// budget. Shows how the pool size backs off and regrows without any per-environment tuning.
/// </summary>
public class AimdSimulationTests
{
    private readonly ITestOutputHelper _output;

    public AimdSimulationTests(ITestOutputHelper output) => _output = output;

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record Scenario(
        string Name,
        int ConcurrencyLimit,
        int RequestsPerWindow,
        int StartSize,
        TimeSpan Latency,
        TimeSpan RetryAfter,
        TimeSpan Duration,
        AimdPoolSizingStrategyOptions? Options = null);

    private sealed record Result(
        int MinSize,
        int FinalSize,
        int Decreases,
        int Increases,
        double SteadyMin,
        double SteadyMax,
        double SteadyMean,
        double ThrottleFraction,
        double Efficiency,
        TimeSpan? FirstBackOff,
        TimeSpan? Settled);

    private Result Simulate(Scenario sc)
    {
        var time = new ManualTime();
        var start = time.GetUtcNow();
        var member = new DataverseUserPool("sim", "dummy", new PoolOptions { MaxSize = sc.StartSize });
        var aimd = new AimdPoolSizingStrategy(sc.Options, time);
        var decision = new PoolSizingDecision(aimd.GetInitialSize(member, sc.StartSize));
        var size = decision.TargetMaxSize;

        var step = TimeSpan.FromMilliseconds(50);
        var tick = TimeSpan.FromSeconds(10);
        var nextTick = tick;
        var inflight = new List<TimeSpan>();
        var sleepers = new List<TimeSpan>();
        var started = new Queue<TimeSpan>();
        var sizes = new List<(TimeSpan At, int Size)> { (TimeSpan.Zero, size) };
        long ok = 0;
        long throttled = 0;
        long okEfficiencyBase = 0;
        const int Workers = 1000;
        var sample = TimeSpan.FromSeconds(10);
        var nextSample = sample;
        long lastOk = 0;

        _output.WriteLine($"--- {sc.Name}: server concurrency limit {sc.ConcurrencyLimit}, {sc.RequestsPerWindow} req/5min, start size {sc.StartSize}, latency {sc.Latency.TotalMilliseconds} ms");
        _output.WriteLine("  t(s)  size  inflight  ok/s  throttles");
        long lastThrottled = 0;

        for (var now = TimeSpan.Zero; now < sc.Duration; now += step)
        {
            time.Advance(step);

            // Completions.
            for (var i = inflight.Count - 1; i >= 0; i--)
            {
                if (inflight[i] <= now)
                {
                    var count = inflight.Count;
                    inflight.RemoveAt(i);
                    ok++;
                    Apply(aimd.OnOperationCompleted(member, new PoolSizingOperationOutcome("op", sc.Latency, PoolSizingOutcomeKind.Success, null, null, count)));
                }
            }

            sleepers.RemoveAll(w => w <= now);
            while (started.Count > 0 && now - started.Peek() >= TimeSpan.FromMinutes(5))
            {
                started.Dequeue();
            }

            var ready = Workers - inflight.Count - sleepers.Count;
            var attempts = 0;
            while (ready > 0 && inflight.Count < size && attempts < size)
            {
                attempts++;
                ready--;
                if (decision.MaxRequestsPerWindow is { } cap && started.Count >= cap)
                {
                    break; // pacer holds the attempt
                }

                if (started.Count >= sc.RequestsPerWindow)
                {
                    throttled++;
                    sleepers.Add(now + sc.RetryAfter);
                    Apply(aimd.OnOperationCompleted(member, new PoolSizingOperationOutcome("op", TimeSpan.FromMilliseconds(5), PoolSizingOutcomeKind.Throttled, ThrottleReason.RequestCount, sc.RetryAfter, inflight.Count)));
                    continue;
                }

                if (inflight.Count + 1 > sc.ConcurrencyLimit)
                {
                    throttled++;
                    sleepers.Add(now + sc.RetryAfter);
                    Apply(aimd.OnOperationCompleted(member, new PoolSizingOperationOutcome("op", TimeSpan.FromMilliseconds(5), PoolSizingOutcomeKind.Throttled, ThrottleReason.ConcurrentRequests, sc.RetryAfter, inflight.Count)));
                    continue;
                }

                inflight.Add(now + sc.Latency);
                started.Enqueue(now);
            }

            if (now >= nextTick)
            {
                nextTick += tick;
                var stats = new PoolStats(size, size, 0, inflight.Count, 0, 0);
                Apply(aimd.OnTick(member, stats, decision));
            }

            if (now >= nextSample)
            {
                nextSample += sample;
                sizes.Add((now, size));
                if ((int)now.TotalSeconds % 60 == 0)
                {
                    _output.WriteLine($"  {(int)now.TotalSeconds,4}  {size,4}  {inflight.Count,8}  {(ok - lastOk) / sample.TotalSeconds,4:F0}  {throttled - lastThrottled}");
                }

                lastOk = ok;
                lastThrottled = throttled;
            }
        }

        void Apply(PoolSizingDecision? d)
        {
            if (d is null)
            {
                return;
            }

            decision = d.Value;
            if (d.Value.TargetMaxSize != size)
            {
                size = d.Value.TargetMaxSize;
                sizes.Add((time.GetUtcNow() - start, size));
            }
        }

        var distinct = sizes.GroupBy(s => s.At).Select(g => g.Last()).OrderBy(s => s.At).ToList();
        var decreases = 0;
        var increases = 0;
        for (var i = 1; i < distinct.Count; i++)
        {
            decreases += distinct[i].Size < distinct[i - 1].Size ? 1 : 0;
            increases += distinct[i].Size > distinct[i - 1].Size ? 1 : 0;
        }

        var half = sc.Duration / 2;
        var late = distinct.Where(s => s.At >= half).Select(s => (double)s.Size).DefaultIfEmpty(size).ToList();
        var firstBack = distinct.Skip(1).Cast<(TimeSpan At, int Size)?>().FirstOrDefault(s => s!.Value.Size < sc.StartSize);
        var theoreticalMax = Math.Min(sc.ConcurrencyLimit, Math.Max(1, sc.StartSize)) / sc.Latency.TotalSeconds * sc.Duration.TotalSeconds;
        var budgetMax = sc.RequestsPerWindow / 300.0 * sc.Duration.TotalSeconds + sc.RequestsPerWindow;
        okEfficiencyBase = (long)Math.Min(theoreticalMax, budgetMax);

        // Settled: first time after which the size never leaves the final-half band.
        TimeSpan? settled = null;
        var lo = late.Min();
        var hi = late.Max();
        for (var i = distinct.Count - 1; i >= 0; i--)
        {
            if (distinct[i].Size < lo || distinct[i].Size > hi)
            {
                break;
            }

            settled = distinct[i].At;
        }

        var result = new Result(
            distinct.Min(s => s.Size),
            size,
            decreases,
            increases,
            lo,
            hi,
            late.Average(),
            throttled / (double)Math.Max(1, ok + throttled),
            ok / (double)Math.Max(1, okEfficiencyBase),
            firstBack?.At,
            settled);
        member.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _output.WriteLine($"  => min size {result.MinSize}, final {result.FinalSize}, decreases {decreases}, increases {increases}, late-half size {lo:F0}..{hi:F0} (mean {result.SteadyMean:F0}), throttled {result.ThrottleFraction:P1} of attempts, served {result.Efficiency:P0} of ideal, first back-off at {result.FirstBackOff?.TotalSeconds:F0}s");
        return result;
    }

    [Fact]
    public void DefaultAimd_FindsAnUnknownConcurrencyLimit_AcrossEnvironments()
    {
        foreach (var limit in new[] { 20, 52, 100 })
        {
            var r = Simulate(new Scenario(
                $"concurrency limit {limit}",
                ConcurrencyLimit: limit,
                RequestsPerWindow: int.MaxValue,
                StartSize: 400,
                Latency: TimeSpan.FromMilliseconds(150),
                RetryAfter: TimeSpan.FromSeconds(2),
                Duration: TimeSpan.FromMinutes(30)));

            Assert.True(r.MinSize < 400);
            Assert.True(r.SteadyMax <= limit * 2.2, $"late size {r.SteadyMax} far above limit {limit}");
        }
    }

    [Fact]
    public void DefaultAimd_WithABindingRequestBudget_StaysStable()
    {
        var r = Simulate(new Scenario(
            "request budget 8000/5min, concurrency limit 100",
            ConcurrencyLimit: 100,
            RequestsPerWindow: 8000,
            StartSize: 400,
            Latency: TimeSpan.FromMilliseconds(150),
            RetryAfter: TimeSpan.FromSeconds(30),
            Duration: TimeSpan.FromMinutes(30)));

        Assert.True(r.ThrottleFraction < 0.5);
    }
}
