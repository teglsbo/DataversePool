using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Crm.Sdk.Messages;
using ConnectionPool.Core;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Opt-in, live-Dataverse test that exercises <see cref="DataversePool"/>'s core, previously
/// never-verified-live value proposition: round-robin across multiple distinct application
/// users to raise the effective per-application-user concurrency ceiling. Everything else in this
/// project (concurrency-not-serialized, impersonation correctness/overhead) was already verified
/// against a real environment; this was the one piece still only ever exercised against fakes.
/// </summary>
/// <remarks>
/// Not run in CI (excluded via <c>Category!=Integration</c>). Requires two real, distinct
/// Dataverse application users' credentials via environment variables; self-<c>Skip</c>s when they
/// are not set.
/// </remarks>
[Trait("Category", "Integration")]
public class LiveDataversePoolMultiUserTests
{
    private readonly ITestOutputHelper _output;

    public LiveDataversePoolMultiUserTests(ITestOutputHelper output) => _output = output;

    private static string? ConnectionStringA => LiveDataverseCredentials.GetConnectionString(0);

    private static string? ConnectionStringB => LiveDataverseCredentials.GetConnectionString(1);

    [SkippableFact]
    public async Task AcquireAsync_RoundRobinsAcrossBothRealMembers_AndBothAuthenticateAsDistinctUsers()
    {
        Skip.If(
            string.IsNullOrEmpty(ConnectionStringA) || string.IsNullOrEmpty(ConnectionStringB),
            "Set DVPOOL_IT_CONNECTION_STRING and DVPOOL_IT_CONNECTION_STRING_B.");

        await using var poolA = new DataverseUserPool("A", ConnectionStringA);
        await using var poolB = new DataverseUserPool("B", ConnectionStringB);
        await using var group = new DataversePool(new[] { poolA, poolB });

        // Confirm the two members really are distinct Dataverse identities, not an accidental
        // duplicate (this bit us during setup: a misconfigured second app user authenticated fine
        // but resolved to the same systemuserid as the first).
        var userIds = new HashSet<Guid>();
        var memberCounts = new Dictionary<string, int>();

        const int TotalAcquires = 20;
        for (var i = 0; i < TotalAcquires; i++)
        {
            await using var lease = await group.AcquireAsync();
            var who = (WhoAmIResponse)await lease.Resource.ExecuteAsync(new WhoAmIRequest());
            userIds.Add(who.UserId);
            memberCounts[lease.Member.Name] = memberCounts.GetValueOrDefault(lease.Member.Name) + 1;
        }

        foreach (var (name, count) in memberCounts)
        {
            _output.WriteLine($"Member {name}: {count}/{TotalAcquires} acquires");
        }

        _output.WriteLine($"Distinct authenticated UserIds seen: {userIds.Count}");

        Assert.Equal(2, userIds.Count); // two genuinely distinct Dataverse identities
        Assert.Equal(2, memberCounts.Count); // both members were actually selected at least once
        Assert.True(
            memberCounts.Values.All(c => c >= TotalAcquires / 2 - 2),
            "Round-robin selection was noticeably lopsided rather than roughly even across the two members: " +
            string.Join(", ", memberCounts.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    /// <summary>
    /// The actual reason to round-robin: raising the effective concurrency ceiling beyond what one
    /// application user's Dataverse service-protection limit allows. This does not (and safely
    /// cannot) drive traffic high enough to hit the real server-side limit - it only confirms the
    /// pool can genuinely dispatch concurrently *across* members (not serialized member-by-member),
    /// which is the mechanism the ceiling-raising claim depends on.
    /// </summary>
    [SkippableFact]
    public async Task AcquireAsync_CanServeConcurrentCallers_FromBothMembersSimultaneously()
    {
        Skip.If(
            string.IsNullOrEmpty(ConnectionStringA) || string.IsNullOrEmpty(ConnectionStringB),
            "Set DVPOOL_IT_CONNECTION_STRING and DVPOOL_IT_CONNECTION_STRING_B.");

        await using var poolA = new DataverseUserPool("A", ConnectionStringA, new PoolOptions { MaxSize = 4 });
        await using var poolB = new DataverseUserPool("B", ConnectionStringB, new PoolOptions { MaxSize = 4 });
        await using var group = new DataversePool(new[] { poolA, poolB });

        const int Concurrency = 8;
        var memberNames = new System.Collections.Concurrent.ConcurrentBag<string>();

        async Task WorkAsync()
        {
            await using var lease = await group.AcquireAsync();
            memberNames.Add(lease.Member.Name);
            await lease.Resource.ExecuteAsync(new WhoAmIRequest());
        }

        await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => WorkAsync()));

        var distinctMembers = memberNames.Distinct().Count();
        _output.WriteLine($"{Concurrency} concurrent acquires used {distinctMembers} distinct member(s): " +
                           string.Join(", ", memberNames.GroupBy(n => n).Select(g => $"{g.Key}={g.Count()}")));

        Assert.Equal(2, distinctMembers);
    }

    /// <summary>
    /// Sends more than one application's measured 100-request ceiling through the real
    /// <see cref="DataversePool"/> API. Every operation acquires a group lease, records the selected
    /// member, and invokes the slow Web API query through that lease's <see cref="ServiceClient"/>.
    /// </summary>
    [SkippableFact]
    public async Task TwoRealMembers_ExecuteMoreThanOneUsersConcurrentLimitThroughDataversePool()
    {
        var connectionStringA = GetConnectionString();
        var connectionStringB = GetConnectionString("_B");
        Skip.If(
            connectionStringA is null || connectionStringB is null,
            "Set credentials for both application users.");

        var concurrency = GetPositiveEnvironmentInteger("DVPOOL_IT_POOL_CONCURRENCY", 160);
        Assert.True(concurrency > 100 && concurrency % 2 == 0);

        var clientOptions = new DataverseClientOptions { MaxRetryCount = 0 };
        var options = new PoolOptions
        {
            MaxSize = concurrency / 2,
            PrewarmCount = concurrency / 2,
        };
        await using var poolA = new DataverseUserPool(
            "A",
            connectionStringA,
            options,
            clientOptions: clientOptions);
        await using var poolB = new DataverseUserPool(
            "B",
            connectionStringB,
            options,
            clientOptions: clientOptions);
        await using var group = new DataversePool(
            new[] { poolA, poolB },
            new RoundRobinSlotSelectionStrategy());

        await group.WarmupAsync();

        var readyWorkers = 0;
        var currentInFlight = 0;
        var peakInFlight = 0;
        var allWorkersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new ConcurrentBag<PoolRequestOutcome>();
        long firstStartedAt = long.MaxValue;
        long lastStartedAt = 0;

        var workers = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            if (Interlocked.Increment(ref readyWorkers) == concurrency)
            {
                allWorkersReady.SetResult();
            }

            await startGate.Task;
            var startedAt = Stopwatch.GetTimestamp();
            InterlockedMin(ref firstStartedAt, startedAt);
            InterlockedMax(ref lastStartedAt, startedAt);

            await using var lease = await group.AcquireAsync();
            var nowInFlight = Interlocked.Increment(ref currentInFlight);
            InterlockedMax(ref peakInFlight, nowInFlight);
            var requestWatch = Stopwatch.StartNew();

            try
            {
                using var response = await lease.Resource.ExecuteWebRequestAsync(
                    HttpMethod.Get,
                    "solutioncomponents?$top=5000",
                    string.Empty,
                    null,
                    "application/json",
                    CancellationToken.None);
                requestWatch.Stop();

                results.Add(new PoolRequestOutcome(
                    lease.Member.Name,
                    (int)response.StatusCode,
                    requestWatch.Elapsed,
                    response.StatusCode == HttpStatusCode.TooManyRequests
                        ? response.Headers.RetryAfter?.ToString()
                        : null));
            }
            catch (Exception exception)
            {
                requestWatch.Stop();
                results.Add(new PoolRequestOutcome(
                    lease.Member.Name,
                    null,
                    requestWatch.Elapsed,
                    DescribeException(exception)));
            }
            finally
            {
                Interlocked.Decrement(ref currentInFlight);
            }
        }).ToArray();

        await allWorkersReady.Task;
        var burstWatch = Stopwatch.StartNew();
        startGate.SetResult();
        await Task.WhenAll(workers);
        burstWatch.Stop();

        var outcomes = results.ToArray();
        var launchSpread = Stopwatch.GetElapsedTime(firstStartedAt, lastStartedAt);
        var latencies = outcomes.Select(outcome => outcome.Latency).Order().ToArray();

        _output.WriteLine(
            $"DataversePool burst completed in {burstWatch.Elapsed}. Launch spread={launchSpread}, " +
            $"peak operations executing through acquired leases={peakInFlight}.");
        _output.WriteLine(
            $"Latency: p50={Percentile(latencies, 0.50)}, p95={Percentile(latencies, 0.95)}, " +
            $"p99={Percentile(latencies, 0.99)}, max={latencies[^1]}.");

        foreach (var memberGroup in outcomes.GroupBy(outcome => outcome.Member).OrderBy(group => group.Key))
        {
            _output.WriteLine(
                $"Member {memberGroup.Key}: " +
                string.Join(
                    ", ",
                    memberGroup
                        .GroupBy(outcome => outcome.StatusCode?.ToString() ?? "exception")
                        .OrderBy(group => group.Key)
                        .Select(group => $"{group.Key}={group.Count()}")));
        }

        foreach (var failure in outcomes
                     .Where(outcome => outcome.StatusCode is null or >= 400)
                     .GroupBy(outcome => outcome.Detail ?? "no detail"))
        {
            _output.WriteLine($"Failure ({failure.Count()}x): {failure.Key}");
        }

        Assert.Equal(concurrency, outcomes.Length);
        Assert.Equal(concurrency, peakInFlight);
        Assert.Equal(concurrency / 2, outcomes.Count(outcome => outcome.Member == "A"));
        Assert.Equal(concurrency / 2, outcomes.Count(outcome => outcome.Member == "B"));
        Assert.True(
            outcomes.Count(outcome => outcome.StatusCode == 200) > 100,
            "DataversePool did not complete more successful requests than one user's measured limit.");
        if (concurrency == 160)
        {
            Assert.All(outcomes, outcome => Assert.Equal(200, outcome.StatusCode));
        }
        Assert.True(launchSpread < TimeSpan.FromSeconds(2));
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

    private static void InterlockedMax(ref long target, long candidate)
    {
        long initial;
        do
        {
            initial = Volatile.Read(ref target);
            if (candidate <= initial)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref target, candidate, initial) != initial);
    }

    private static void InterlockedMin(ref long target, long candidate)
    {
        long initial;
        do
        {
            initial = Volatile.Read(ref target);
            if (candidate >= initial)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref target, candidate, initial) != initial);
    }

    private sealed record PoolRequestOutcome(
        string Member,
        int? StatusCode,
        TimeSpan Latency,
        string? Detail);
}
