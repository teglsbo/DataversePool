using System.Collections.Concurrent;
using System.Diagnostics;

namespace ConnectionPool.Dataverse.Tests.Soak;

/// <summary>Fixed-size log-bucket latency histogram: bounded memory however long the run.</summary>
public sealed class LatencyHistogram
{
    private const int Buckets = 256;
    private const double Growth = 1.06; // ~6 % resolution from 0.1 ms up past an hour
    private readonly long[] _counts = new long[Buckets];
    private long _total;

    public long Count => Interlocked.Read(ref _total);

    public void Record(double milliseconds)
    {
        var index = milliseconds <= 0.1 ? 0 : Math.Min(Buckets - 1, (int)(Math.Log(milliseconds / 0.1) / Math.Log(Growth)) + 1);
        Interlocked.Increment(ref _counts[index]);
        Interlocked.Increment(ref _total);
    }

    public double Percentile(double p)
    {
        var total = Interlocked.Read(ref _total);
        if (total == 0)
        {
            return double.NaN;
        }

        var target = Math.Max(1, (long)Math.Ceiling(total * p));
        long seen = 0;
        for (var i = 0; i < Buckets; i++)
        {
            seen += Interlocked.Read(ref _counts[i]);
            if (seen >= target)
            {
                return i == 0 ? 0.1 : 0.1 * Math.Pow(Growth, i);
            }
        }

        return 0.1 * Math.Pow(Growth, Buckets - 1);
    }

    public void MergeInto(LatencyHistogram other)
    {
        for (var i = 0; i < Buckets; i++)
        {
            var c = Interlocked.Read(ref _counts[i]);
            if (c != 0)
            {
                Interlocked.Add(ref other._counts[i], c);
                Interlocked.Add(ref other._total, c);
            }
        }
    }
}

public sealed record SoakSample(
    TimeSpan Elapsed,
    int PhaseIndex,
    SoakPhaseKind PhaseKind,
    string PhaseLabel,
    bool Quiet,
    int InFlight,
    long HeapBytes,
    long WorkingSetBytes,
    int Threads,
    int Handles,
    int PoolThreads,
    long PendingWork,
    int Gen2Collections,
    long Ops,
    long Errors,
    double OpsPerSecond,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    IReadOnlyDictionary<string, double> Extras);

public sealed class SoakPhaseResult
{
    public SoakPhaseResult(int index, SoakPhase phase) => (Index, Phase) = (index, phase);

    public int Index { get; }
    public SoakPhase Phase { get; }
    public TimeSpan Actual { get; internal set; }
    public long Ok;
    public long Failed;
    public LatencyHistogram Latency { get; } = new();
}

public sealed class SoakOptions
{
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>A sample counts as "at rest" only this long after an idle phase started (queues drained, GC settled).</summary>
    public TimeSpan QuietSettle { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan SteadyThinkTime { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Called once a sample is taken (progress output).</summary>
    public Action<SoakSample>? OnSample { get; init; }
}

public sealed class SoakResult
{
    public required IReadOnlyList<SoakSample> Samples { get; init; }
    public required IReadOnlyList<SoakPhaseResult> Phases { get; init; }
    public required IReadOnlyDictionary<string, long> ErrorsByType { get; init; }
    public required TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Drives <paramref name="operation"/> through a <see cref="SoakPhase"/> list while a monitor samples process
/// health. The operation returns time it spent waiting on the harness itself (for example a rate guard), which is
/// excluded from the recorded latency.
/// </summary>
public sealed class SoakRunner
{
    private sealed class Window
    {
        public readonly LatencyHistogram Latency = new();
        public long Ok;
        public long Failed;
    }

    private readonly SoakOptions _options;
    private readonly Func<SoakPhase, CancellationToken, Task<TimeSpan>> _operation;
    private readonly Func<Exception, string> _classify;
    private readonly Func<IReadOnlyDictionary<string, double>>? _probe;
    private readonly ConcurrentDictionary<string, long> _errors = new();
    private readonly List<SoakSample> _samples = new();

    private Window _window = new();
    private int _inFlight;
    private int _phaseIndex = -1;
    private SoakPhase _phase = new(SoakPhaseKind.Idle, TimeSpan.Zero, 0, "start");
    private long _phaseStartedTicks;
    private long _totalOps;
    private long _totalErrors;

    public SoakRunner(
        SoakOptions options,
        Func<SoakPhase, CancellationToken, Task<TimeSpan>> operation,
        Func<Exception, string>? classify = null,
        Func<IReadOnlyDictionary<string, double>>? probe = null)
    {
        _options = options;
        _operation = operation;
        _classify = classify ?? (static ex => ex.GetType().Name);
        _probe = probe;
    }

    public async Task<SoakResult> RunAsync(IReadOnlyList<SoakPhase> phases, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var results = new List<SoakPhaseResult>();
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monitor = Task.Run(() => MonitorAsync(clock, monitorStop.Token));

        try
        {
            for (var i = 0; i < phases.Count; i++)
            {
                var phase = phases[i];
                var result = new SoakPhaseResult(i, phase);
                results.Add(result);
                _phase = phase;
                Volatile.Write(ref _phaseIndex, i);
                Volatile.Write(ref _phaseStartedTicks, clock.ElapsedTicks);
                var phaseClock = Stopwatch.StartNew();

                if (phase.Concurrency == 0)
                {
                    await Task.Delay(phase.Duration, cancellationToken);
                }
                else
                {
                    await Task.WhenAll(Enumerable.Range(0, phase.Concurrency)
                        .Select(_ => Task.Run(() => WorkerAsync(phase, result, phaseClock, cancellationToken), CancellationToken.None)));
                }

                result.Actual = phaseClock.Elapsed;
            }
        }
        finally
        {
            monitorStop.Cancel();
            try { await monitor; } catch (OperationCanceledException) { }
        }

        return new SoakResult
        {
            Samples = _samples,
            Phases = results,
            ErrorsByType = new Dictionary<string, long>(_errors),
            Elapsed = clock.Elapsed,
        };
    }

    private async Task WorkerAsync(SoakPhase phase, SoakPhaseResult result, Stopwatch phaseClock, CancellationToken cancellationToken)
    {
        var rng = new Random(Environment.CurrentManagedThreadId ^ Environment.TickCount);
        while (phaseClock.Elapsed < phase.Duration && !cancellationToken.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.OperationTimeout);
            var sw = Stopwatch.StartNew();
            Interlocked.Increment(ref _inFlight);
            try
            {
                var excluded = await _operation(phase, timeout.Token).ConfigureAwait(false);
                var ms = Math.Max(0, (sw.Elapsed - excluded).TotalMilliseconds);
                _window.Latency.Record(ms);
                result.Latency.Record(ms);
                Interlocked.Increment(ref _window.Ok);
                Interlocked.Increment(ref result.Ok);
                Interlocked.Increment(ref _totalOps);
            }
            catch (Exception ex)
            {
                _errors.AddOrUpdate(_classify(ex), 1, static (_, n) => n + 1);
                Interlocked.Increment(ref _window.Failed);
                Interlocked.Increment(ref result.Failed);
                Interlocked.Increment(ref _totalOps);
                Interlocked.Increment(ref _totalErrors);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }

            if (phase.Kind is SoakPhaseKind.Steady or SoakPhaseKind.Warmup)
            {
                var think = _options.SteadyThinkTime;
                await Task.Delay(think * (0.5 + rng.NextDouble()), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task MonitorAsync(Stopwatch clock, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_options.SampleInterval);
        var last = clock.Elapsed;
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                var window = Interlocked.Exchange(ref _window, new Window());
                var now = clock.Elapsed;
                var interval = (now - last).TotalSeconds;
                last = now;

                var phaseStart = TimeSpan.FromTicks((long)(Volatile.Read(ref _phaseStartedTicks) * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));
                var inFlight = Volatile.Read(ref _inFlight);
                var phase = _phase;
                var quiet = phase.Kind == SoakPhaseKind.Idle && inFlight == 0 && now - phaseStart >= _options.QuietSettle;

                // Full blocking collection so the heap figure is live data, not garbage waiting to be collected.
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                var heap = GC.GetTotalMemory(false);
                using var process = Process.GetCurrentProcess();

                var sample = new SoakSample(
                    now,
                    Volatile.Read(ref _phaseIndex),
                    phase.Kind,
                    phase.Label,
                    quiet,
                    inFlight,
                    heap,
                    process.WorkingSet64,
                    process.Threads.Count,
                    ProcessHandles.Count(),
                    ThreadPool.ThreadCount,
                    ThreadPool.PendingWorkItemCount,
                    GC.CollectionCount(2),
                    window.Ok + window.Failed,
                    window.Failed,
                    (window.Ok + window.Failed) / Math.Max(0.001, interval),
                    window.Latency.Percentile(0.50),
                    window.Latency.Percentile(0.95),
                    window.Latency.Percentile(0.99),
                    _probe?.Invoke() ?? new Dictionary<string, double>());
                _samples.Add(sample);
                _options.OnSample?.Invoke(sample);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

internal static class ProcessHandles
{
    public static int Count()
    {
        try
        {
            if (Directory.Exists("/proc/self/fd"))
            {
                return Directory.GetFileSystemEntries("/proc/self/fd").Length;
            }
        }
        catch
        {
        }

        using var p = Process.GetCurrentProcess();
        return p.HandleCount;
    }
}
