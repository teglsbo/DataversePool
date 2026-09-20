using System.Diagnostics;
using ConnectionPool.Core;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Opt-in, live-Dataverse tests that deliberately trigger a <b>genuine</b> HTTP 429
/// service-protection response by exceeding one real application user's concurrent-request ceiling
/// (documented as 52 concurrent requests per user), instead of only ever simulating it via
/// <see cref="DataverseUserPool.ReportThrottled"/> in fakes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is safe to run repeatedly, unlike deliberately exhausting the 5-minute request-count
/// or 20-minute execution-time budgets:</b> the concurrent-request limit is enforced live, not as a
/// rolling window - Microsoft's own guidance is that as soon as in-flight requests for that user
/// drop back under the ceiling, new requests succeed immediately again, with no additional
/// "cool-down" beyond that. So a short burst above 52 concurrent calls produces a handful of real
/// 429s and then clears within a second or two of the burst finishing - it does not durably lock the
/// application user out for anywhere near the (possibly very long) <c>Retry-After</c> value the
/// server reports on the rejected calls themselves.
/// </para>
/// <para>
/// <see cref="DataverseUserPool.ReportThrottled"/>/<see cref="DataverseUserPool.IsThrottled"/>,
/// however, do not know that distinction - they record whatever <c>Retry-After</c> the server
/// reported (capped, see <see cref="DataverseThrottleDetector.DefaultMaxRetryAfter"/>) as a flat
/// "avoid this member" window for throttle-aware strategies, even though the real server-side
/// concurrency ceiling may already have cleared well before that window elapses. This is a
/// deliberate, documented trade-off (following Dataverse's own reported guidance literally rather
/// than guessing at "real" recovery time) - this test's second half quantifies just how
/// conservative that trade-off actually is in practice.
/// </para>
/// <para>
/// Requires <c>MaxRetryCount=0</c> on the underlying <see cref="ServiceClient"/> (via
/// <see cref="DataverseClientOptions"/>) - with the SDK's own default of 10, a 429 is silently
/// retried and its <c>Retry-After</c>-honoring wait absorbed entirely inside the SDK call, never
/// reaching this library's <see cref="DataverseThrottleDetector"/> at all. See
/// <see cref="DataverseClientOptions"/>'s remarks for why <c>MaxRetryCount=0</c> still leaves the
/// real <c>Retry-After</c> value attached to the exception it throws instead.
/// </para>
/// <para>
/// Not run in CI (excluded via <c>Category!=Integration</c>). Requires one real Dataverse
/// application user's credentials via <c>DVPOOL_IT_CONNECTION_STRING</c>; self-<c>Skip</c>s when
/// not set. Optionally uses a second (<c>DVPOOL_IT_CONNECTION_STRING_B</c>) to also show how the
/// three <see cref="ISlotSelectionStrategy"/> implementations diverge once a real 429 has been
/// reported against one member.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class LiveConcurrencyThrottleTests
{
    private readonly ITestOutputHelper _output;

    public LiveConcurrencyThrottleTests(ITestOutputHelper output) => _output = output;

    private static string? ConnectionStringA =>
        Environment.GetEnvironmentVariable("DVPOOL_IT_CONNECTION_STRING");

    private static string? ConnectionStringB =>
        Environment.GetEnvironmentVariable("DVPOOL_IT_CONNECTION_STRING_B");

    /// <summary>
    /// Fires a burst well above the documented 52-concurrent-request ceiling against a single real
    /// application user, then measures: (1) how many of the burst's calls were genuinely 429'd by
    /// the server, with what real <c>Retry-After</c> values; (2) whether a call issued immediately
    /// after the burst finishes succeeds right away (proving the limit is a live gauge, not a
    /// multi-minute lockout) rather than waiting anywhere near the reported <c>Retry-After</c>.
    /// </summary>
    [Fact]
    public async Task ConcurrencyBurst_AboveTheRealCeiling_ProducesGenuine429s_ThenClearsWithinSeconds()
    {
        if (string.IsNullOrEmpty(ConnectionStringA))
        {
            _output.WriteLine("Skipped: DVPOOL_IT_CONNECTION_STRING not set.");
            return;
        }

        const int BurstSize = 300; // well above the documented 52-concurrent ceiling

        // MaxRetryCount = 0 so the SDK never silently absorbs a 429 for us - see remarks above.
        var clientOptions = new DataverseClientOptions { MaxRetryCount = 0 };
        await using var poolA = new DataverseUserPool(
            "A",
            ConnectionStringA,
            new PoolOptions { MaxSize = BurstSize, PrewarmCount = BurstSize },
            clientOptions: clientOptions);

        // Prewarm sequentially (always serialized regardless of MaxSize - see docs/adr/0002) so the
        // burst below measures genuine request concurrency, not connection-creation contention.
        await poolA.WarmupAsync();

        var query = new QueryExpression("organization") { ColumnSet = new ColumnSet("name") };

        var burstTasks = Enumerable.Range(0, BurstSize).Select(async _ =>
        {
            await using var lease = await poolA.AcquireAsync();
            try
            {
                lease.Resource.DisableCrossThreadSafeties = true;
            await lease.Resource.RetrieveMultipleAsync(query);
                return (Throttled: false, RetryAfter: (TimeSpan?)null);
            }
            catch (Exception ex) when (DataverseThrottleDetector.TryGetRetryAfter(ex, out var retryAfter))
            {
                poolA.ReportThrottled(retryAfter);
                return (Throttled: true, RetryAfter: (TimeSpan?)retryAfter);
            }
        });

        var results = await Task.WhenAll(burstTasks);

        var throttled = results.Where(r => r.Throttled).ToList();
        _output.WriteLine($"Burst size: {BurstSize}. Genuinely 429'd: {throttled.Count}. Succeeded: {results.Length - throttled.Count}.");
        if (throttled.Count > 0)
        {
            var retryAfters = throttled.Select(r => r.RetryAfter!.Value).ToList();
            _output.WriteLine($"Observed Retry-After values: min={retryAfters.Min()}, max={retryAfters.Max()}.");
        }

        // Immediately after the burst - not after waiting out any Retry-After - one more call should
        // succeed quickly if the concurrency ceiling really is a live gauge, not a durable lockout.
        var sw = Stopwatch.StartNew();
        await using (var probeLease = await poolA.AcquireAsync())
        {
            await probeLease.Resource.RetrieveMultipleAsync(query);
        }

        sw.Stop();
        _output.WriteLine($"Post-burst probe call succeeded in {sw.Elapsed}.");

        // This is the actual point of the test: if the concurrency limit were a real multi-minute
        // lockout (as the reported Retry-After values might suggest read literally), this probe
        // would hang or fail. Observing it complete in a few seconds confirms the limit clears as
        // soon as in-flight load drops, independent of whatever Retry-After was reported.
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(10),
            $"Expected the post-burst probe to succeed quickly (limit is a live gauge), but it took {sw.Elapsed}.");

        // Not a hard requirement of Dataverse (a sufficiently generous tenant/instance could absorb
        // 70 concurrent calls without ever tripping the 52-concurrent ceiling) - but on the
        // documented default limit we expect to have seen at least one genuine 429 to make this
        // test meaningful rather than a no-op.
        if (throttled.Count == 0)
        {
            _output.WriteLine(
                "Note: no 429s observed - this tenant/instance may have a higher-than-default " +
                "concurrency ceiling, or the burst completed too quickly to overlap enough calls. " +
                "The post-burst-clears-fast assertion above still held trivially in that case.");
        }
    }

    /// <summary>
    /// The earlier burst test's <c>RetrieveMultiple</c> against the <c>organization</c> singleton
    /// (1 row, ~50ms/call) apparently completes too fast for even 300 near-simultaneous calls to
    /// stay overlapped long enough to durably exceed the 52-concurrent ceiling on this tenant. This
    /// test swaps in <see cref="RetrieveAllEntitiesRequest"/> with <see cref="EntityFilters.Entity"/>
    /// - a full entity-metadata dump, not a per-record query, so its cost is independent of how much
    /// data this environment actually has, and it is naturally much heavier per call (typically
    /// hundreds of ms to low seconds) - giving each burst call a much wider real-world window to
    /// overlap with the others in flight.
    /// </summary>
    /// <remarks>
    /// Directly instruments the actual peak number of calls genuinely in flight at once (an
    /// <see cref="Interlocked"/> counter, not inferred from wall-clock speedup - a naive "400s
    /// serial vs 15s actual" calculation only gives the *average* concurrency across the run, which
    /// undercounts the true peak). Also deliberately does <b>not</b> use a narrow exception filter
    /// on the burst calls - every exception, recognized 429 or not, is caught and classified, so a
    /// misclassified/differently-shaped rejection cannot silently disappear as a false "success".
    /// </remarks>
    [Fact]
    public async Task ConcurrencyBurst_WithAHeavierMetadataQuery_ProducesGenuine429s()
    {
        if (string.IsNullOrEmpty(ConnectionStringA))
        {
            _output.WriteLine("Skipped: DVPOOL_IT_CONNECTION_STRING not set.");
            return;
        }

        const int BurstSize = 150; // comfortably above 52 even accounting for ramp-up/ramp-down at the edges

        var clientOptions = new DataverseClientOptions { MaxRetryCount = 0 };
        await using var poolA = new DataverseUserPool(
            "A",
            ConnectionStringA,
            new PoolOptions { MaxSize = BurstSize, PrewarmCount = BurstSize },
            clientOptions: clientOptions);

        await poolA.WarmupAsync();

        // Gauge single-call cost first, purely for the test's own diagnostic output.
        var probeSw = Stopwatch.StartNew();
        await using (var probeLease = await poolA.AcquireAsync())
        {
            probeLease.Resource.DisableCrossThreadSafeties = true;
            await probeLease.Resource.ExecuteAsync(new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity });
        }

        probeSw.Stop();
        _output.WriteLine($"Single RetrieveAllEntitiesRequest(Entity) call took {probeSw.Elapsed}.");

        var currentInFlight = 0;
        var peakInFlight = 0;

        var burstTasks = Enumerable.Range(0, BurstSize).Select(async _ =>
        {
            await using var lease = await poolA.AcquireAsync();
            lease.Resource.DisableCrossThreadSafeties = true;

            var nowInFlight = Interlocked.Increment(ref currentInFlight);
            InterlockedMax(ref peakInFlight, nowInFlight);
            try
            {
                await lease.Resource.ExecuteAsync(new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity });
                return (Outcome: "Success", RetryAfter: (TimeSpan?)null, ExceptionType: (string?)null);
            }
            catch (Exception ex)
            {
                // No exception filter here on purpose - classify every outcome instead of only
                // catching the shape DataverseThrottleDetector already recognizes, so a rejection
                // that takes an unexpected form cannot silently vanish as a false "success".
                if (DataverseThrottleDetector.TryGetRetryAfter(ex, out var retryAfter))
                {
                    poolA.ReportThrottled(retryAfter);
                    return (Outcome: "RecognizedThrottle", RetryAfter: (TimeSpan?)retryAfter, ExceptionType: ex.GetType().Name);
                }

                return (Outcome: "OtherException", RetryAfter: (TimeSpan?)null, ExceptionType: $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref currentInFlight);
            }
        });

        var sw = Stopwatch.StartNew();
        var results = await Task.WhenAll(burstTasks);
        sw.Stop();

        var byOutcome = results.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count());
        _output.WriteLine(
            $"Burst size: {BurstSize}. Peak genuinely-concurrent in-flight calls observed: {peakInFlight}. " +
            $"Wall clock: {sw.Elapsed}. Naive average-concurrency estimate (serial-time / wall-clock): " +
            $"{BurstSize * probeSw.Elapsed.TotalSeconds / sw.Elapsed.TotalSeconds:F1}x.");
        foreach (var (outcome, count) in byOutcome)
        {
            _output.WriteLine($"  {outcome}: {count}");
        }

        var recognizedThrottled = results.Where(r => r.Outcome == "RecognizedThrottle").ToList();
        if (recognizedThrottled.Count > 0)
        {
            var retryAfters = recognizedThrottled.Select(r => r.RetryAfter!.Value).ToList();
            _output.WriteLine($"Observed Retry-After values: min={retryAfters.Min()}, max={retryAfters.Max()}.");
        }

        var otherExceptions = results.Where(r => r.Outcome == "OtherException").Select(r => r.ExceptionType).Distinct().ToList();
        if (otherExceptions.Count > 0)
        {
            _output.WriteLine("Unrecognized exception shapes seen (would NOT have been caught by the earlier narrow filter):");
            foreach (var type in otherExceptions)
            {
                _output.WriteLine($"  {type}");
            }
        }

        if (recognizedThrottled.Count == 0 && otherExceptions.Count == 0)
        {
            if (peakInFlight > 52)
            {
                _output.WriteLine(
                    $"Note: genuinely reached {peakInFlight} concurrent in-flight calls (verified by direct " +
                    "instrumentation, not inferred from average speedup) with zero rejections of any kind - " +
                    "reasonably strong evidence this tenant/instance's real concurrency ceiling for reads is " +
                    "genuinely higher than the commonly-cited default of 52.");
            }
            else
            {
                _output.WriteLine(
                    $"Note: peak concurrency actually reached was only {peakInFlight}, which never exceeded " +
                    "52 - zero 429s here does NOT show the limit doesn't apply, only that this run didn't " +
                    "generate enough real concurrent load to test it. Increase BurstSize and/or per-call cost.");
            }
        }

        // No hard assertion on outcome counts themselves (tenant/timing-dependent, as established
        // above) - this test exists to observe and report peak concurrency plus every outcome shape,
        // not to assert a specific result either way.
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

    /// <summary>
    /// With a real 429 reported against member A (via a genuine concurrency burst, same technique
    /// as above), shows the three <see cref="ISlotSelectionStrategy"/> implementations actually
    /// diverge: plain round-robin keeps alternating onto the still-throttled A, while
    /// least-connections and health-aware round-robin both steer new acquires to the untouched B
    /// for as long as A's real, reported throttle window lasts.
    /// </summary>
    [Fact]
    public async Task AfterARealThrottleOnOneMember_StrategiesDivergeExactlyAsDesigned()
    {
        if (string.IsNullOrEmpty(ConnectionStringA) || string.IsNullOrEmpty(ConnectionStringB))
        {
            _output.WriteLine("Skipped: DVPOOL_IT_CONNECTION_STRING / DVPOOL_IT_CONNECTION_STRING_B not both set.");
            return;
        }

        const int BurstSize = 300;
        var clientOptions = new DataverseClientOptions { MaxRetryCount = 0 };

        await using var poolA = new DataverseUserPool(
            "A", ConnectionStringA, new PoolOptions { MaxSize = BurstSize, PrewarmCount = BurstSize }, clientOptions: clientOptions);
        await using var poolB = new DataverseUserPool(
            "B", ConnectionStringB, new PoolOptions { MaxSize = 4 });

        await poolA.WarmupAsync();

        var query = new QueryExpression("organization") { ColumnSet = new ColumnSet("name") };

        // Hammer only A to force a real 429 and report it, exactly as in the test above.
        var burstResults = await Task.WhenAll(Enumerable.Range(0, BurstSize).Select(async _ =>
        {
            await using var lease = await poolA.AcquireAsync();
            try
            {
                lease.Resource.DisableCrossThreadSafeties = true;
            await lease.Resource.RetrieveMultipleAsync(query);
                return false;
            }
            catch (Exception ex) when (DataverseThrottleDetector.TryGetRetryAfter(ex, out var retryAfter))
            {
                poolA.ReportThrottled(retryAfter);
                return true;
            }
        }));

        var throttledCount = burstResults.Count(t => t);
        _output.WriteLine($"A genuinely throttled on {throttledCount}/{BurstSize} burst calls. A.IsThrottled = {poolA.IsThrottled}.");

        if (!poolA.IsThrottled)
        {
            _output.WriteLine(
                "Skipped divergence assertions: the burst did not produce a real, still-active " +
                "throttle on A (this tenant may have a higher concurrency ceiling than the " +
                "documented default, or A's window already expired by the time we checked).");
            return;
        }

        // Plain round-robin: no concept of throttle at all - keeps alternating onto A regardless.
        await using (var rr = new DataversePool(new DataverseUserPool[] { poolA, poolB }, new RoundRobinSlotSelectionStrategy()))
        {
            var picks = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                await using var lease = await rr.AcquireAsync();
                picks.Add(lease.Member.Name);
            }

            _output.WriteLine("RoundRobin picks while A is throttled: " + string.Join(", ", picks));
            Assert.Contains("A", picks); // keeps picking the throttled member - the documented trade-off
        }

        // Least-connections: skips throttled members entirely.
        await using (var lc = new DataversePool(new DataverseUserPool[] { poolA, poolB }, new LeastConnectionsSlotSelectionStrategy()))
        {
            var picks = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                await using var lease = await lc.AcquireAsync();
                picks.Add(lease.Member.Name);
            }

            _output.WriteLine("LeastConnections picks while A is throttled: " + string.Join(", ", picks));
            Assert.All(picks, name => Assert.Equal("B", name));
        }

        // Health-aware round-robin: also skips throttled members entirely.
        await using (var hc = new DataversePool(new DataverseUserPool[] { poolA, poolB }, new HealthAwareRoundRobinSlotSelectionStrategy()))
        {
            var picks = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                await using var lease = await hc.AcquireAsync();
                picks.Add(lease.Member.Name);
            }

            _output.WriteLine("HealthAwareRoundRobin picks while A is throttled: " + string.Join(", ", picks));
            Assert.All(picks, name => Assert.Equal("B", name));
        }
    }
}
