using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests.Soak;

public class SoakHarnessTests
{
    private static SoakSample Sample(double seconds, bool quiet, long heap = 100_000_000, int threads = 30, int handles = 100, SoakPhaseKind kind = SoakPhaseKind.Idle) =>
        new(TimeSpan.FromSeconds(seconds), 0, kind, "x", quiet, 0, heap, heap * 2, threads, handles, threads, 0, 0, 0, 0, 0, double.NaN, double.NaN, double.NaN, new Dictionary<string, double>());

    private static SoakResult Result(IEnumerable<SoakSample> samples, double seconds, IEnumerable<SoakPhaseResult>? phases = null) =>
        new() { Samples = samples.ToList(), Phases = (phases ?? Array.Empty<SoakPhaseResult>()).ToList(), ErrorsByType = new Dictionary<string, long>(), Elapsed = TimeSpan.FromSeconds(seconds) };

    private static SoakResult Series(Func<int, long> heap, Func<int, int> threads, Func<int, int> handles, int n = 60) =>
        Result(Enumerable.Range(0, n).Select(i => Sample(i * 10, true, heap(i), threads(i), handles(i))), n * 10);

    [Fact]
    public void Profile_IsDeterministic_EndsIdle_AndMixesSpikesAndIdle()
    {
        var a = SoakProfile.Generate(TimeSpan.FromHours(3), seed: 7);
        var b = SoakProfile.Generate(TimeSpan.FromHours(3), seed: 7);
        var c = SoakProfile.Generate(TimeSpan.FromHours(3), seed: 8);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(SoakPhaseKind.Idle, a[^1].Kind);
        Assert.InRange(a.Sum(p => p.Duration.TotalSeconds), 10800 - 30, 10800 + 30);
        Assert.Contains(a, p => p.Label == "long-idle");
        Assert.Contains(a, p => p.Label == "cold-spike");
        Assert.Contains(a, p => p.Label == "blink");
        Assert.True(a.Count(p => p.Kind == SoakPhaseKind.Spike) > 20);
        Assert.InRange(a.Where(p => p.Kind == SoakPhaseKind.Spike).Max(p => p.Concurrency), 32, 64);
    }

    [Fact]
    public void Profile_ThirtyMinutes_HasNoLongIdle_AndFitsDuration()
    {
        var p = SoakProfile.Generate(TimeSpan.FromMinutes(30), seed: 1);
        Assert.InRange(p.Sum(x => x.Duration.TotalSeconds), 1800 - 30, 1800 + 30);
        Assert.Contains(p, x => x.Kind == SoakPhaseKind.Spike);
        Assert.Contains(p, x => x.Kind == SoakPhaseKind.Idle);
    }

    [Fact]
    public void Analyzer_FlatSeries_Passes()
    {
        var rng = new Random(1);
        var verdict = SoakAnalyzer.Analyze(Series(i => 100_000_000 + rng.Next(-500_000, 500_000), i => 30 + rng.Next(0, 2), i => 100 + rng.Next(0, 3)));
        Assert.True(verdict.Passed, verdict.ToString());
    }

    [Fact]
    public void Analyzer_SteadyHeapGrowth_IsALeak()
    {
        var verdict = SoakAnalyzer.Analyze(Series(i => 100_000_000 + i * 2_000_000L, _ => 30, _ => 100));
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Findings, f => f.Severity == SoakSeverity.Failure && f.Message.Contains("managed heap"));
    }

    [Fact]
    public void Analyzer_ThreadAndHandleGrowth_AreLeaks()
    {
        var verdict = SoakAnalyzer.Analyze(Series(_ => 100_000_000, i => 30 + i, i => 100 + i * 3));
        Assert.Contains(verdict.Findings, f => f.Severity == SoakSeverity.Failure && f.Message.Contains("threads"));
        Assert.Contains(verdict.Findings, f => f.Severity == SoakSeverity.Failure && f.Message.Contains("handles"));
    }

    [Fact]
    public void Analyzer_OneSpike_IsNotATrend()
    {
        // A single noisy sample late in the run must not read as a leak.
        var verdict = SoakAnalyzer.Analyze(Series(i => i == 55 ? 400_000_000 : 100_000_000, _ => 30, _ => 100));
        Assert.True(verdict.Passed, verdict.ToString());
    }

    [Fact]
    public void Analyzer_GrowthBeforeWarmupCutoff_IsIgnored()
    {
        var verdict = SoakAnalyzer.Analyze(Series(i => i < 8 ? 100_000_000 + i * 20_000_000L : 260_000_000, _ => 30, _ => 100));
        Assert.True(verdict.Passed, verdict.ToString());
    }

    [Fact]
    public void Analyzer_TooFewRestSamples_WarnsInsteadOfPassingSilently()
    {
        var verdict = SoakAnalyzer.Analyze(Result(Enumerable.Range(0, 5).Select(i => Sample(i * 10, true)), 100));
        Assert.Contains(verdict.Findings, f => f.Severity == SoakSeverity.Warning && f.Message.Contains("inconclusive"));
    }

    private static SoakPhaseResult SpikePhase(int index, double p50Ms, double rate)
    {
        var r = new SoakPhaseResult(index, new SoakPhase(SoakPhaseKind.Spike, TimeSpan.FromSeconds(20), 10, "spike")) { Actual = TimeSpan.FromSeconds(20) };
        var n = (int)(rate * 20);
        for (var i = 0; i < n; i++)
        {
            r.Latency.Record(p50Ms);
        }

        r.Ok = n;
        return r;
    }

    [Fact]
    public void Analyzer_SpikesGettingSlower_AreFlagged()
    {
        var phases = Enumerable.Range(0, 12).Select(i => SpikePhase(i, i < 6 ? 50 : 200, 100)).ToList();
        var verdict = SoakAnalyzer.Analyze(Result(Array.Empty<SoakSample>(), 600, phases));
        Assert.Contains(verdict.Findings, f => f.Severity == SoakSeverity.Failure && f.Message.StartsWith("SLOWDOWN"));
    }

    [Fact]
    public void Analyzer_SpikesAtAConstantSpeed_AreNotFlagged()
    {
        var phases = Enumerable.Range(0, 12).Select(i => SpikePhase(i, 50, 100)).ToList();
        var verdict = SoakAnalyzer.Analyze(Result(Array.Empty<SoakSample>(), 600, phases));
        Assert.DoesNotContain(verdict.Findings, f => f.Severity == SoakSeverity.Failure && f.Message.StartsWith("SLOWDOWN"));
    }

    [Fact]
    public void Analyzer_ThroughputCollapse_IsFlagged()
    {
        var phases = Enumerable.Range(0, 12).Select(i => SpikePhase(i, 50, i < 6 ? 100 : 40)).ToList();
        var verdict = SoakAnalyzer.Analyze(Result(Array.Empty<SoakSample>(), 600, phases));
        Assert.Contains(verdict.Findings, f => f.Severity == SoakSeverity.Failure && f.Message.StartsWith("SLOWDOWN"));
    }

    [Fact]
    public void Histogram_PercentilesAreWithinTheBucketResolution()
    {
        var h = new LatencyHistogram();
        for (var i = 1; i <= 1000; i++)
        {
            h.Record(i);
        }

        Assert.InRange(h.Percentile(0.5), 470, 540);
        Assert.InRange(h.Percentile(0.95), 900, 1010);
        Assert.True(double.IsNaN(new LatencyHistogram().Percentile(0.5)));
    }
}

/// <summary>
/// Runs the whole harness against a simulated backend with injected faults, to prove that it passes a healthy
/// target and catches the failures a burn-in exists to find. About a minute each; excluded from CI.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Suite", "Soak")]
public class SoakHarnessSimulationTests
{
    private readonly ITestOutputHelper _output;

    public SoakHarnessSimulationTests(ITestOutputHelper output) => _output = output;

    private async Task<(SoakResult Result, SoakVerdict Verdict)> RunAsync(
        Func<SoakPhase, int, CancellationToken, Task> operation, int seconds = 100, TimeSpan? timeout = null)
    {
        var opCount = 0;
        var runner = new SoakRunner(
            new SoakOptions { SampleInterval = TimeSpan.FromMilliseconds(500), QuietSettle = TimeSpan.FromMilliseconds(700), OperationTimeout = timeout ?? TimeSpan.FromSeconds(60) },
            async (phase, ct) =>
            {
                await operation(phase, Interlocked.Increment(ref opCount), ct);
                return TimeSpan.Zero;
            });
        var result = await runner.RunAsync(SoakProfile.Generate(TimeSpan.FromSeconds(seconds), seed: 11, scale: 0.06));
        var verdict = SoakAnalyzer.Analyze(result);
        _output.WriteLine(SoakAnalyzer.Report(result, verdict));
        return (result, verdict);
    }

    private static Task Work(SoakPhase phase, int extraMs = 0) => Task.Delay(8 + extraMs + phase.Concurrency / 8);

    [Fact]
    public async Task HealthyTarget_Passes()
    {
        var (result, verdict) = await RunAsync((phase, _, _) => Work(phase));
        Assert.True(verdict.Passed, verdict.ToString());
        Assert.True(result.Samples.Count(s => s.Quiet) >= 9);
        Assert.True(result.Phases.Count(p => p.Phase.Kind == SoakPhaseKind.Spike) >= 6);
    }

    [Fact]
    public async Task LeakingHeap_IsCaught()
    {
        var retained = new List<byte[]>();
        var (_, verdict) = await RunAsync(async (phase, _, _) =>
        {
            await Work(phase);
            lock (retained)
            {
                retained.Add(new byte[2048]);
            }
        });
        GC.KeepAlive(retained);
        Assert.Contains(verdict.Findings, f => f.Severity == SoakSeverity.Failure && f.Message.Contains("managed heap"));
    }

    [Fact]
    public async Task LeakingThreads_AreCaught()
    {
        using var release = new ManualResetEventSlim(false);
        var threads = new List<Thread>();
        try
        {
            var (_, verdict) = await RunAsync(async (phase, n, _) =>
            {
                await Work(phase);
                if (n % 100 == 0)
                {
                    var t = new Thread(() => release.Wait()) { IsBackground = true };
                    lock (threads)
                    {
                        threads.Add(t);
                    }

                    t.Start();
                }
            });
            Assert.Contains(verdict.Findings, f => f.Severity == SoakSeverity.Failure && f.Message.Contains("threads"));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task OperationsThatGetSlowerOverTime_AreCaught()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (_, verdict) = await RunAsync((phase, _, _) => Work(phase, extraMs: (int)(clock.Elapsed.TotalSeconds * 5)));
        Assert.Contains(verdict.Findings, f => f.Severity == SoakSeverity.Failure && f.Message.StartsWith("SLOWDOWN"));
    }

    [Fact]
    public async Task OperationsThatNeverFinish_AreCutOffByTheTimeout_AndCounted()
    {
        var (result, _) = await RunAsync(async (phase, n, ct) =>
        {
            if (n == 1)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            await Work(phase);
        }, seconds: 30, timeout: TimeSpan.FromSeconds(3));
        Assert.Contains(result.ErrorsByType, e => e.Key.Contains("Cancel"));
        Assert.Equal(0, result.Samples[^1].InFlight);
    }
}
