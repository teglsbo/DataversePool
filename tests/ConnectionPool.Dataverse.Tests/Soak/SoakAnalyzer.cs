using System.Text;

namespace ConnectionPool.Dataverse.Tests.Soak;

public sealed class SoakThresholds
{
    /// <summary>Samples before this fraction of the run are warm-up and ignored for leak checks.</summary>
    public double WarmupFraction { get; init; } = 0.15;

    public int MinQuietSamples { get; init; } = 9;

    public long HeapGrowthBytes { get; init; } = 20L * 1024 * 1024;
    public double HeapGrowthFraction { get; init; } = 0.25;
    public int ThreadGrowth { get; init; } = 25;
    public int HandleGrowth { get; init; } = 60;

    /// <summary>A rising trend must also explain this much of the variance, so one noisy sample is not a leak.</summary>
    public double MinRSquared { get; init; } = 0.4;

    public double LatencyFactor { get; init; } = 1.6;
    public double LatencyMinIncreaseMs { get; init; } = 40;
    public double ThroughputFloorFraction { get; init; } = 0.65;
    public double MaxErrorFraction { get; init; } = 0.02;
}

public enum SoakSeverity
{
    Info,
    Warning,
    Failure,
}

public sealed record SoakFinding(SoakSeverity Severity, string Message);

public sealed class SoakVerdict
{
    public required IReadOnlyList<SoakFinding> Findings { get; init; }
    public bool Passed => Findings.All(f => f.Severity != SoakSeverity.Failure);

    public override string ToString() =>
        string.Join(Environment.NewLine, Findings.Select(f => $"[{f.Severity}] {f.Message}"));
}

/// <summary>
/// Leaks: compares process measurements taken <i>at rest</i> (idle, nothing in flight, settled) early and late in
/// the run, plus a trend line. Slowdowns: compares the same phase kind early and late (spike latency and
/// throughput), because comparing a spike with an idle period says nothing.
/// </summary>
public static class SoakAnalyzer
{
    public static SoakVerdict Analyze(SoakResult result, SoakThresholds? thresholds = null)
    {
        thresholds ??= new SoakThresholds();
        var findings = new List<SoakFinding>();
        var samples = result.Samples;

        var cutoff = TimeSpan.FromTicks((long)(result.Elapsed.Ticks * thresholds.WarmupFraction));
        var quiet = samples.Where(s => s.Quiet && s.Elapsed >= cutoff).ToList();

        if (quiet.Count < thresholds.MinQuietSamples)
        {
            findings.Add(new SoakFinding(SoakSeverity.Warning,
                $"Only {quiet.Count} at-rest samples after warm-up (need {thresholds.MinQuietSamples}); leak check is inconclusive. Run longer or add idle time."));
        }
        else
        {
            CheckGrowth(findings, "managed heap (after full GC)", quiet, s => s.HeapBytes, thresholds.HeapGrowthBytes, thresholds.HeapGrowthFraction, thresholds.MinRSquared, FormatBytes);
            CheckGrowth(findings, "threads", quiet, s => s.Threads, thresholds.ThreadGrowth, 0, thresholds.MinRSquared, v => v.ToString("F0"));
            CheckGrowth(findings, "open handles/fds", quiet, s => s.Handles, thresholds.HandleGrowth, 0, thresholds.MinRSquared, v => v.ToString("F0"));
            CheckGrowth(findings, "thread pool threads", quiet, s => s.PoolThreads, thresholds.ThreadGrowth, 0, thresholds.MinRSquared, v => v.ToString("F0"));

            // Working set is informational: the runtime holds on to memory after a spike.
            var ws = Early(quiet, s => s.WorkingSetBytes);
            var wl = Late(quiet, s => s.WorkingSetBytes);
            findings.Add(new SoakFinding(SoakSeverity.Info, $"working set at rest: {FormatBytes(ws)} early, {FormatBytes(wl)} late"));
        }

        CheckSlowdown(findings, result, SoakPhaseKind.Spike, thresholds);
        CheckSlowdown(findings, result, SoakPhaseKind.Steady, thresholds);

        var ops = result.Phases.Sum(p => p.Ok + p.Failed);
        var failed = result.Phases.Sum(p => p.Failed);
        if (ops > 0)
        {
            var fraction = failed / (double)ops;
            var severity = fraction > thresholds.MaxErrorFraction ? SoakSeverity.Failure : SoakSeverity.Info;
            findings.Add(new SoakFinding(severity, $"{failed} of {ops} operations failed ({fraction:P2}); limit {thresholds.MaxErrorFraction:P1}"));
        }

        var stuck = samples.Where(s => s.PhaseKind == SoakPhaseKind.Idle && s.Elapsed >= cutoff && s.InFlight > 0).ToList();
        var lastIdle = samples.LastOrDefault(s => s.PhaseKind == SoakPhaseKind.Idle);
        if (lastIdle is { InFlight: > 0 })
        {
            findings.Add(new SoakFinding(SoakSeverity.Failure, $"{lastIdle.InFlight} operations still in flight at the end: stuck or leaked leases"));
        }
        else if (stuck.Count > 0)
        {
            findings.Add(new SoakFinding(SoakSeverity.Info, $"{stuck.Count} idle samples had operations still draining"));
        }

        return new SoakVerdict { Findings = findings };
    }

    private static double Early(List<SoakSample> s, Func<SoakSample, double> v) => Median(s.Take(Math.Max(3, s.Count / 3)).Select(v));

    private static double Late(List<SoakSample> s, Func<SoakSample, double> v) => Median(s.Skip(s.Count - Math.Max(3, s.Count / 3)).Select(v));

    private static void CheckGrowth(
        List<SoakFinding> findings, string name, List<SoakSample> quiet, Func<SoakSample, double> value,
        double absThreshold, double fractionThreshold, double minR2, Func<double, string> format)
    {
        var early = Early(quiet, value);
        var late = Late(quiet, value);
        var growth = late - early;
        var (slope, r2) = Regress(quiet.Select(s => (s.Elapsed.TotalSeconds, value(s))).ToList());
        var perHour = slope * 3600;
        var big = growth > absThreshold && (fractionThreshold <= 0 || growth > early * fractionThreshold);
        var trending = r2 >= minR2 && slope > 0;
        var summary = $"{name} at rest: {format(early)} early -> {format(late)} late ({(growth >= 0 ? "+" : "-")}{format(Math.Abs(growth))}), trend {(perHour >= 0 ? "+" : "-")}{format(Math.Abs(perHour))}/h, R2={r2:F2}";
        findings.Add(big && trending
            ? new SoakFinding(SoakSeverity.Failure, "LEAK? " + summary)
            : new SoakFinding(SoakSeverity.Info, summary));
    }

    private static void CheckSlowdown(List<SoakFinding> findings, SoakResult result, SoakPhaseKind kind, SoakThresholds t)
    {
        var phases = result.Phases.Where(p => p.Phase.Kind == kind && p.Latency.Count > 20).ToList();
        if (phases.Count < 6)
        {
            if (phases.Count > 0)
            {
                findings.Add(new SoakFinding(SoakSeverity.Info, $"{kind}: only {phases.Count} phases, slowdown check skipped"));
            }

            return;
        }

        var third = phases.Count / 3;
        (double P50, double P95, double Rate, long N) Summarise(IEnumerable<SoakPhaseResult> ps)
        {
            var h = new LatencyHistogram();
            double seconds = 0;
            long ok = 0;
            foreach (var p in ps)
            {
                p.Latency.MergeInto(h);
                seconds += p.Actual.TotalSeconds;
                ok += p.Ok;
            }

            return (h.Percentile(0.5), h.Percentile(0.95), ok / Math.Max(1, seconds), h.Count);
        }

        var early = Summarise(phases.Take(third));
        var late = Summarise(phases.Skip(phases.Count - third));
        var slowP95 = late.P95 > early.P95 * t.LatencyFactor && late.P95 - early.P95 > t.LatencyMinIncreaseMs;
        var slowP50 = late.P50 > early.P50 * t.LatencyFactor && late.P50 - early.P50 > t.LatencyMinIncreaseMs;
        var slowRate = late.Rate < early.Rate * t.ThroughputFloorFraction;
        var text = $"{kind} phases: p50 {early.P50:F0}->{late.P50:F0} ms, p95 {early.P95:F0}->{late.P95:F0} ms, throughput {early.Rate:F1}->{late.Rate:F1} ops/s (first vs last third of {phases.Count})";
        findings.Add(slowP95 || slowP50 || slowRate
            ? new SoakFinding(SoakSeverity.Failure, "SLOWDOWN? " + text)
            : new SoakFinding(SoakSeverity.Info, text));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    // Least squares: slope per second and the share of variance the line explains.
    private static (double Slope, double R2) Regress(IReadOnlyList<(double X, double Y)> points)
    {
        var n = points.Count;
        var mx = points.Average(p => p.X);
        var my = points.Average(p => p.Y);
        var sxx = points.Sum(p => (p.X - mx) * (p.X - mx));
        var syy = points.Sum(p => (p.Y - my) * (p.Y - my));
        var sxy = points.Sum(p => (p.X - mx) * (p.Y - my));
        if (sxx <= 0 || n < 3)
        {
            return (0, 0);
        }

        var r2 = syy <= 0 ? 0 : sxy * sxy / (sxx * syy);
        return (sxy / sxx, r2);
    }

    public static string FormatBytes(double bytes)
    {
        var abs = Math.Abs(bytes);
        return abs >= 1024 * 1024 ? $"{bytes / 1048576:F1} MB" : abs >= 1024 ? $"{bytes / 1024:F1} KB" : $"{bytes:F0} B";
    }

    public static string Report(SoakResult result, SoakVerdict verdict)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Soak run: {result.Elapsed:hh\\:mm\\:ss}, {result.Phases.Count} phases, {result.Samples.Count} samples");
        sb.AppendLine($"Phases: spike={Count(result, SoakPhaseKind.Spike)} steady={Count(result, SoakPhaseKind.Steady)} idle={Count(result, SoakPhaseKind.Idle)}");
        sb.AppendLine($"Ops: {result.Phases.Sum(p => p.Ok)} ok, {result.Phases.Sum(p => p.Failed)} failed");
        foreach (var e in result.ErrorsByType.OrderByDescending(e => e.Value).Take(8))
        {
            sb.AppendLine($"  error {e.Key}: {e.Value}");
        }

        sb.AppendLine(verdict.Passed ? "VERDICT: PASS" : "VERDICT: FAIL");
        sb.AppendLine(verdict.ToString());
        return sb.ToString();
    }

    private static int Count(SoakResult r, SoakPhaseKind k) => r.Phases.Count(p => p.Phase.Kind == k);

    public static void WriteCsv(SoakResult result, string path)
    {
        var extraKeys = result.Samples.SelectMany(s => s.Extras.Keys).Distinct().OrderBy(k => k).ToList();
        using var w = new StreamWriter(path);
        w.WriteLine("elapsed_s,phase,label,quiet,in_flight,heap_mb,workingset_mb,threads,handles,pool_threads,pending_work,gen2,ops,errors,ops_per_s,p50_ms,p95_ms,p99_ms" + string.Concat(extraKeys.Select(k => "," + k)));
        foreach (var s in result.Samples)
        {
            w.WriteLine(string.Join(",",
                s.Elapsed.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), s.PhaseKind, s.PhaseLabel, s.Quiet ? 1 : 0, s.InFlight,
                (s.HeapBytes / 1048576.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                (s.WorkingSetBytes / 1048576.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                s.Threads, s.Handles, s.PoolThreads, s.PendingWork, s.Gen2Collections, s.Ops, s.Errors,
                s.OpsPerSecond.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                s.P50Ms.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                s.P95Ms.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                s.P99Ms.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)) +
                string.Concat(extraKeys.Select(k => "," + (s.Extras.TryGetValue(k, out var v) ? v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : string.Empty))));
        }
    }
}
