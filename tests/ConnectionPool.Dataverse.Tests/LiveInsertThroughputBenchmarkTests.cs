using System.Collections.Concurrent;
using System.Diagnostics;
using ConnectionPool.Core;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Opt-in live benchmark comparing one and two application users on independent inserts into the
/// purpose-built <c>new_loadthin</c> standard table.
/// </summary>
[Trait("Category", "Integration")]
public class LiveInsertThroughputBenchmarkTests
{
    private const string EntityName = "new_loadthin";
    private const string PrimaryNameAttribute = "new_name";
    private readonly ITestOutputHelper _output;

    public LiveInsertThroughputBenchmarkTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Runs a balanced A-AB-AB-B sequence so each identity receives the same total number of
    /// inserts across the complete experiment. Every row is one individual <c>CreateAsync</c>
    /// through a real <see cref="DataversePool"/> lease; no bulk message or retry can hide the
    /// operation-level behavior being compared.
    /// </summary>
    [SkippableFact]
    public async Task IndependentCreates_CompareOneAndTwoApplicationUsers()
    {
        var connectionStringA = GetConnectionString();
        var connectionStringB = GetConnectionString("_B");
        Skip.If(
            connectionStringA is null || connectionStringB is null,
            "Set credentials for both application users.");

        var rowCount = GetPositiveEnvironmentInteger("DVPOOL_IT_INSERT_ROWS", 1_000);
        var concurrencyPerUser =
            GetPositiveEnvironmentInteger("DVPOOL_IT_INSERT_CONCURRENCY_PER_USER", 32);
        Assert.True(rowCount >= concurrencyPerUser * 2);
        Assert.True(concurrencyPerUser > 0);

        var options = new PoolOptions
        {
            MaxSize = concurrencyPerUser,
            PrewarmCount = concurrencyPerUser,
        };
        var clientOptions = new DataverseClientOptions { MaxRetryCount = 0 };
        var memberA = new DataverseUserPool(
            "A",
            connectionStringA,
            options,
            clientOptions: clientOptions);
        var memberB = new DataverseUserPool(
            "B",
            connectionStringB,
            options,
            clientOptions: clientOptions);
        await using var twoUserPool = new DataversePool(
            new[] { memberA, memberB },
            new RoundRobinSlotSelectionStrategy());
        var singleUserPoolA = new DataversePool(memberA);
        var singleUserPoolB = new DataversePool(memberB);

        await twoUserPool.WarmupAsync();

        var benchmarkId = Guid.NewGuid();
        var allIds = new ConcurrentBag<Guid>();
        var runs = new List<InsertRunResult>();

        try
        {
            runs.Add(await RunAsync(
                "single-A",
                singleUserPoolA,
                benchmarkId,
                runNumber: 1,
                rowCount,
                concurrencyPerUser,
                allIds));
            runs.Add(await RunAsync(
                "two-users-1",
                twoUserPool,
                benchmarkId,
                runNumber: 2,
                rowCount,
                concurrencyPerUser * 2,
                allIds));
            runs.Add(await RunAsync(
                "two-users-2",
                twoUserPool,
                benchmarkId,
                runNumber: 3,
                rowCount,
                concurrencyPerUser * 2,
                allIds));
            runs.Add(await RunAsync(
                "single-B",
                singleUserPoolB,
                benchmarkId,
                runNumber: 4,
                rowCount,
                concurrencyPerUser,
                allIds));

            WriteComparison(runs);

            Assert.All(runs, run => Assert.Equal(rowCount, run.Outcomes.Count));
            Assert.All(runs, run => Assert.Equal(rowCount, run.SuccessCount));
            Assert.All(runs, run => Assert.Equal(0, run.ThrottledCount));
            Assert.All(runs, run => Assert.Equal(0, run.ErrorCount));
            Assert.All(
                runs.Where(run => run.Mode == "single"),
                run => Assert.Equal(concurrencyPerUser, run.PeakInFlight));
            Assert.All(
                runs.Where(run => run.Mode == "two-users"),
                run => Assert.Equal(concurrencyPerUser * 2, run.PeakInFlight));

            var singleRate = runs
                .Where(run => run.Mode == "single")
                .Average(run => run.SuccessfulRowsPerSecond);
            var twoUserRate = runs
                .Where(run => run.Mode == "two-users")
                .Average(run => run.SuccessfulRowsPerSecond);
            _output.WriteLine(
                $"Average successful throughput: single={singleRate:F1} rows/s, " +
                $"two-users={twoUserRate:F1} rows/s, ratio={twoUserRate / singleRate:F2}x.");
        }
        finally
        {
            await CleanupAsync(twoUserPool, allIds.Distinct().ToArray());
        }
    }

    private async Task<InsertRunResult> RunAsync(
        string label,
        DataversePool pool,
        Guid benchmarkId,
        int runNumber,
        int rowCount,
        int concurrency,
        ConcurrentBag<Guid> allIds)
    {
        var outcomes = new ConcurrentBag<InsertOutcome>();
        var readyWorkers = 0;
        var nextRow = -1;
        var currentInFlight = 0;
        var peakInFlight = 0;
        var allWorkersReady =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startGate =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var workers = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            if (Interlocked.Increment(ref readyWorkers) == concurrency)
            {
                allWorkersReady.SetResult();
            }

            await startGate.Task;

            while (true)
            {
                var rowIndex = Interlocked.Increment(ref nextRow);
                if (rowIndex >= rowCount)
                {
                    return;
                }

                var id = CreateDeterministicGuid(benchmarkId, runNumber, rowIndex);
                allIds.Add(id);
                var totalWatch = Stopwatch.StartNew();

                try
                {
                    await using var lease = await pool.AcquireAsync();
                    var nowInFlight = Interlocked.Increment(ref currentInFlight);
                    InterlockedMax(ref peakInFlight, nowInFlight);
                    try
                    {
                        var entity = new Entity(EntityName, id)
                        {
                            [PrimaryNameAttribute] =
                                $"DataversePool {benchmarkId:N} {label} {rowIndex:D6}",
                        };
                        await lease.Resource.CreateAsync(entity);
                        outcomes.Add(new InsertOutcome(
                            lease.Member.Name,
                            "success",
                            totalWatch.Elapsed,
                            null));
                    }
                    catch (Exception exception)
                    {
                        // The string-match fallback this used to have is gone: PLAN-2026-09-30.md's
                        // Phase 2 confirmed live that DataverseThrottleDetector now recognizes the
                        // SOAP-path FaultException<OrganizationServiceFault> throttle shape directly,
                        // so no inference from the exception's message text is needed any more.
                        var isThrottle = DataverseThrottleDetector.TryGetRetryAfter(
                            exception,
                            out var retryAfter);
                        outcomes.Add(new InsertOutcome(
                            lease.Member.Name,
                            isThrottle ? "throttled" : "error",
                            totalWatch.Elapsed,
                            isThrottle && retryAfter > TimeSpan.Zero
                                ? $"Retry-After={retryAfter}; {DescribeException(exception)}"
                                : DescribeException(exception)));
                    }
                    finally
                    {
                        Interlocked.Decrement(ref currentInFlight);
                    }
                }
                catch (Exception exception)
                {
                    outcomes.Add(new InsertOutcome(
                        "acquire",
                        "acquire-error",
                        totalWatch.Elapsed,
                        DescribeException(exception)));
                }
            }
        }).ToArray();

        await allWorkersReady.Task;
        var runWatch = Stopwatch.StartNew();
        startGate.SetResult();
        await Task.WhenAll(workers);
        runWatch.Stop();

        var result = new InsertRunResult(
            label.StartsWith("single", StringComparison.Ordinal) ? "single" : "two-users",
            label,
            runWatch.Elapsed,
            peakInFlight,
            outcomes.ToArray());
        WriteRun(result);
        return result;
    }

    private void WriteRun(InsertRunResult run)
    {
        var latencies = run.Outcomes.Select(outcome => outcome.Latency).Order().ToArray();
        _output.WriteLine(
            $"{run.Label}: duration={run.Duration}, successes={run.SuccessCount}, " +
            $"successful throughput={run.SuccessfulRowsPerSecond:F1} rows/s, " +
            $"throttled={run.ThrottledCount}, errors={run.ErrorCount}, " +
            $"peak executing through leases={run.PeakInFlight}.");
        _output.WriteLine(
            $"  latency: p50={Percentile(latencies, 0.50)}, " +
            $"p95={Percentile(latencies, 0.95)}, p99={Percentile(latencies, 0.99)}, " +
            $"max={latencies[^1]}.");
        _output.WriteLine(
            "  members: " +
            string.Join(
                ", ",
                run.Outcomes
                    .GroupBy(outcome => outcome.Member)
                    .OrderBy(group => group.Key)
                    .Select(group =>
                        $"{group.Key}={group.Count()} " +
                        $"({string.Join("/", group.GroupBy(outcome => outcome.Outcome).Select(outcomeGroup => $"{outcomeGroup.Key}:{outcomeGroup.Count()}"))})")));

        foreach (var failure in run.Outcomes
                     .Where(outcome => outcome.Outcome != "success")
                     .GroupBy(outcome => outcome.Detail ?? "no detail")
                     .OrderByDescending(group => group.Count())
                     .Take(5))
        {
            _output.WriteLine($"  failure ({failure.Count()}x): {failure.Key}");
        }
    }

    private void WriteComparison(IReadOnlyList<InsertRunResult> runs)
    {
        _output.WriteLine("Insert benchmark summary:");
        foreach (var run in runs)
        {
            _output.WriteLine(
                $"  {run.Label,-12} {run.SuccessCount,5} successes in " +
                $"{run.Duration.TotalSeconds,7:F2}s = " +
                $"{run.SuccessfulRowsPerSecond,7:F1} rows/s; " +
                $"throttled={run.ThrottledCount}, errors={run.ErrorCount}");
        }
    }

    private async Task CleanupAsync(DataversePool pool, Guid[] ids)
    {
        if (ids.Length == 0)
        {
            return;
        }

        const int BatchSize = 100;
        var deleted = 0;
        var alreadyAbsent = 0;
        var cleanupFailures = new ConcurrentBag<string>();
        var batches = ids.Chunk(BatchSize).ToArray();

        await Parallel.ForEachAsync(
            batches,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (batch, cancellationToken) =>
            {
                try
                {
                    await using var lease = await pool.AcquireAsync(cancellationToken);
                    var request = new ExecuteMultipleRequest
                    {
                        Requests = new OrganizationRequestCollection(),
                        Settings = new ExecuteMultipleSettings
                        {
                            ContinueOnError = true,
                            ReturnResponses = true,
                        },
                    };

                    foreach (var id in batch)
                    {
                        request.Requests.Add(new DeleteRequest
                        {
                            Target = new EntityReference(EntityName, id),
                        });
                    }

                    var response =
                        (ExecuteMultipleResponse)await lease.Resource.ExecuteAsync(request);
                    var faults = response.Responses
                        .Where(item => item.Fault is not null)
                        .ToArray();
                    var absent = faults
                        .Where(item => IsNotFound(item.Fault))
                        .ToArray();
                    var realFaults = faults
                        .Where(item => !IsNotFound(item.Fault))
                        .ToArray();
                    if (realFaults.Length > 0)
                    {
                        cleanupFailures.Add(
                            $"{realFaults.Length}/{batch.Length} delete faults: " +
                            string.Join(
                                " | ",
                                realFaults.Take(3).Select(item => item.Fault.Message)));
                    }

                    Interlocked.Add(ref alreadyAbsent, absent.Length);
                    Interlocked.Add(ref deleted, batch.Length - faults.Length);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(DescribeException(exception));
                }
            });

        _output.WriteLine(
            $"Cleanup: deleted={deleted}, already absent={alreadyAbsent}, expected={ids.Length}, " +
            $"batch failures={cleanupFailures.Count}.");
        foreach (var failure in cleanupFailures.Distinct().Take(5))
        {
            _output.WriteLine($"  cleanup failure: {failure}");
        }

        Assert.Empty(cleanupFailures);
        Assert.Equal(ids.Length, deleted + alreadyAbsent);
    }

    private static bool IsNotFound(OrganizationServiceFault fault) =>
        fault.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
        fault.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static Guid CreateDeterministicGuid(Guid benchmarkId, int runNumber, int rowIndex)
    {
        var bytes = benchmarkId.ToByteArray();
        BitConverter.GetBytes(runNumber).CopyTo(bytes, 8);
        BitConverter.GetBytes(rowIndex).CopyTo(bytes, 12);
        return new Guid(bytes);
    }

    private static string? GetConnectionString(string suffix = "")
    {
        var connectionString =
            Environment.GetEnvironmentVariable($"DVPOOL_IT_CONNECTION_STRING{suffix}");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        var url = Environment.GetEnvironmentVariable("DVPOOL_IT_URL");
        var tenantId = Environment.GetEnvironmentVariable($"DVPOOL_IT_TENANT_ID{suffix}");
        var clientId = Environment.GetEnvironmentVariable($"DVPOOL_IT_CLIENT_ID{suffix}");
        var clientSecret = Environment.GetEnvironmentVariable($"DVPOOL_IT_CLIENT_SECRET{suffix}");

        return new[] { url, tenantId, clientId, clientSecret }
            .All(value => !string.IsNullOrWhiteSpace(value))
            ? $"AuthType=ClientSecret;Url={url};TenantId={tenantId};ClientId={clientId};ClientSecret={clientSecret};"
            : null;
    }

    private static int GetPositiveEnvironmentInteger(string name, int defaultValue)
    {
        var rawValue = Environment.GetEnvironmentVariable(name);
        return int.TryParse(rawValue, out var value) && value > 0 ? value : defaultValue;
    }

    private static TimeSpan Percentile(TimeSpan[] sortedValues, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sortedValues.Length) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Length - 1)];
    }

    private static string DescribeException(Exception exception)
    {
        var chain = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            chain.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(" -> ", chain);
    }

    private static void InterlockedMax(ref int target, int candidate)
    {
        int initial;
        do
        {
            initial = Volatile.Read(ref target);
            if (candidate <= initial)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref target, candidate, initial) != initial);
    }

    private sealed record InsertOutcome(
        string Member,
        string Outcome,
        TimeSpan Latency,
        string? Detail);

    private sealed record InsertRunResult(
        string Mode,
        string Label,
        TimeSpan Duration,
        int PeakInFlight,
        IReadOnlyList<InsertOutcome> Outcomes)
    {
        public int SuccessCount => Outcomes.Count(outcome => outcome.Outcome == "success");
        public int ThrottledCount => Outcomes.Count(outcome => outcome.Outcome == "throttled");
        public int ErrorCount => Outcomes.Count - SuccessCount - ThrottledCount;
        public double SuccessfulRowsPerSecond =>
            SuccessCount / Math.Max(Duration.TotalSeconds, 0.001);
    }
}
