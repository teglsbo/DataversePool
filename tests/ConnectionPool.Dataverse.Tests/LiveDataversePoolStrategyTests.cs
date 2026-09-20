using Microsoft.Crm.Sdk.Messages;
using ConnectionPool.Core;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Opt-in, live-Dataverse tests that exercise the three <see cref="ISlotSelectionStrategy"/>
/// implementations against real members, not just fakes: does plain round-robin really ignore
/// load, does least-connections really steer away from an already-loaded member, and does
/// health-aware round-robin really stop routing to a genuinely broken member after enough
/// consecutive failures.
/// </summary>
/// <remarks>
/// Not run in CI (excluded via <c>Category!=Integration</c>). Requires two real, distinct
/// Dataverse application users' credentials via environment variables; self-<c>Skip</c>s when they
/// are not set.
/// </remarks>
[Trait("Category", "Integration")]
public class LiveDataversePoolStrategyTests
{
    private readonly ITestOutputHelper _output;

    public LiveDataversePoolStrategyTests(ITestOutputHelper output) => _output = output;

    private static string? ConnectionStringA =>
        Environment.GetEnvironmentVariable("DVPOOL_IT_CONNECTION_STRING");

    private static string? ConnectionStringB =>
        Environment.GetEnvironmentVariable("DVPOOL_IT_CONNECTION_STRING_B");

    /// <summary>
    /// Plain round-robin has no concept of load at all - it should keep alternating strictly 1/N
    /// even when one member is already holding an extra outstanding lease. This is the trade-off
    /// documented on <see cref="LeastConnectionsSlotSelectionStrategy"/>: round-robin will keep
    /// piling new acquires onto an already-busy member.
    /// </summary>
    [Fact]
    public async Task RoundRobin_AlternatesStrictly_EvenWhenOneMemberAlreadyHasAnExtraLease()
    {
        if (string.IsNullOrEmpty(ConnectionStringA) || string.IsNullOrEmpty(ConnectionStringB))
        {
            _output.WriteLine("Skipped: DVPOOL_IT_CONNECTION_STRING / DVPOOL_IT_CONNECTION_STRING_B not both set.");
            return;
        }

        await using var poolA = new DataverseUserPool("A", ConnectionStringA, new PoolOptions { MaxSize = 4 });
        await using var poolB = new DataverseUserPool("B", ConnectionStringB, new PoolOptions { MaxSize = 4 });
        await using var group = new DataversePool(new[] { poolA, poolB }, new RoundRobinSlotSelectionStrategy());

        // Give member A an extra, still-open lease before the alternation loop, held via the group
        // itself so it's attributed to whichever member round-robin picks first - then just record
        // which one that was and keep it open for the rest of the test.
        var heldLease = await group.AcquireAsync();
        _output.WriteLine($"Extra held lease is on member: {heldLease.Member.Name}");

        var sequence = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            await using var lease = await group.AcquireAsync();
            sequence.Add(lease.Member.Name);
        }

        await heldLease.DisposeAsync();

        _output.WriteLine("Selection sequence: " + string.Join(", ", sequence));

        // Strict alternation: no two consecutive picks are the same member, regardless of the
        // extra load sitting on whichever member got the held lease.
        for (var i = 1; i < sequence.Count; i++)
        {
            Assert.True(
                sequence[i] != sequence[i - 1],
                $"Expected strict alternation (round-robin ignores load), but position {i} repeated " +
                $"'{sequence[i]}' right after position {i - 1}. Full sequence: {string.Join(", ", sequence)}");
        }
    }

    /// <summary>
    /// Least-connections should steer new acquires toward whichever member has fewer currently
    /// checked-out leases - the opposite of the round-robin behavior above.
    /// </summary>
    [Fact]
    public async Task LeastConnections_PrefersTheMemberWithFewerActiveLeases()
    {
        if (string.IsNullOrEmpty(ConnectionStringA) || string.IsNullOrEmpty(ConnectionStringB))
        {
            _output.WriteLine("Skipped: DVPOOL_IT_CONNECTION_STRING / DVPOOL_IT_CONNECTION_STRING_B not both set.");
            return;
        }

        await using var poolA = new DataverseUserPool("A", ConnectionStringA, new PoolOptions { MaxSize = 4 });
        await using var poolB = new DataverseUserPool("B", ConnectionStringB, new PoolOptions { MaxSize = 4 });
        await using var group = new DataversePool(new[] { poolA, poolB }, new LeastConnectionsSlotSelectionStrategy());

        // Force an extra outstanding lease specifically onto A (bypassing the group/strategy) so
        // A's PoolStats.LeasedCount is 1 higher than B's for the rest of this test.
        var heldOnA = await poolA.AcquireAsync();

        var sequence = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            await using var lease = await group.AcquireAsync();
            sequence.Add(lease.Member.Name);
        }

        await heldOnA.DisposeAsync();

        _output.WriteLine("Selection sequence (A already has 1 extra held lease throughout): " + string.Join(", ", sequence));

        Assert.All(sequence, name => Assert.Equal("B", name));
    }

    /// <summary>
    /// Health-aware round-robin should stop routing to a member whose connections genuinely and
    /// consistently fail to authenticate, after <c>failureThreshold</c> consecutive failures -
    /// instead of continuing to send 1/N of traffic into a member that will just fail every time.
    /// The "broken" member here is a real Dataverse connection string with a deliberately invalid
    /// client secret, so <c>CreateAsync</c> genuinely throws (an MSAL auth failure), not a
    /// simulated/mocked failure.
    /// </summary>
    [Fact]
    public async Task HealthAwareRoundRobin_StopsRoutingToAGenuinelyBrokenMember_AfterConsecutiveFailures()
    {
        if (string.IsNullOrEmpty(ConnectionStringA) || string.IsNullOrEmpty(ConnectionStringB))
        {
            _output.WriteLine("Skipped: DVPOOL_IT_CONNECTION_STRING / DVPOOL_IT_CONNECTION_STRING_B not both set.");
            return;
        }

        var brokenConnectionString = CorruptSecret(ConnectionStringB);

        await using var poolA = new DataverseUserPool("A", ConnectionStringA, new PoolOptions { MaxSize = 4 });
        await using var poolBroken = new DataverseUserPool("Broken", brokenConnectionString, new PoolOptions { MaxSize = 4 });

        const int FailureThreshold = 2;
        await using var group = new DataversePool(
            new[] { poolA, poolBroken },
            new HealthAwareRoundRobinSlotSelectionStrategy(FailureThreshold, cooldownPeriod: TimeSpan.FromMinutes(5)));

        var results = new List<bool>();

        for (var i = 0; i < 8; i++)
        {
            const int MaxRetriesPerLogicalCall = 5;
            for (var attempt = 0; attempt < MaxRetriesPerLogicalCall; attempt++)
            {
                DataverseLease? lease = null;
                try
                {
                    lease = await group.AcquireAsync();
                    await lease.Resource.ExecuteAsync(new WhoAmIRequest());
                    results.Add(true);
                    break;
                }
                catch (Exception ex)
                {
                    results.Add(false);
                    _output.WriteLine($"Call {i}, attempt {attempt}: failed ({ex.GetType().Name}) - retrying, as a real caller-side resilience wrapper would.");
                }
                finally
                {
                    if (lease is not null)
                    {
                        await lease.DisposeAsync();
                    }
                }
            }
        }

        var brokenStats = poolBroken.GetStats();
        var totalFailures = results.Count(r => !r);

        _output.WriteLine($"Total failed attempts across the run: {totalFailures}.");
        _output.WriteLine($"Broken member's ConsecutiveCreateFailures at end: {brokenStats.ConsecutiveCreateFailures}.");

        // The broken member must have genuinely been tried and failed at least FailureThreshold
        // times (no false positives - it really is broken), and every logical call must have
        // eventually succeeded via the healthy member once the circuit opened and stopped
        // selecting it (the caller-side retry loop above never exhausted its attempts).
        Assert.True(totalFailures >= FailureThreshold, $"Expected at least {FailureThreshold} failures against the broken member, got {totalFailures}.");
        Assert.Equal(8, results.Count(r => r));
        Assert.True(
            brokenStats.ConsecutiveCreateFailures >= FailureThreshold,
            $"Expected the broken member's ConsecutiveCreateFailures ({brokenStats.ConsecutiveCreateFailures}) " +
            $"to have reached the circuit's failure threshold ({FailureThreshold}).");

        // Once the circuit opens after FailureThreshold consecutive failures, the strategy must stop
        // selecting the broken member entirely - so the broken member should never be picked again,
        // and total failures across the whole run should be exactly FailureThreshold, not more.
        Assert.Equal(FailureThreshold, totalFailures);
    }

    private static string CorruptSecret(string connectionString)
    {
        // Connection strings are "Key1=Value1;Key2=Value2;...". Replace ClientSecret's value with
        // something guaranteed to fail MSAL auth, leaving everything else (Url, ClientId, TenantId)
        // untouched, so the failure is a real auth rejection, not a malformed connection string.
        var parts = connectionString.Split(';');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("ClientSecret=", StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = "ClientSecret=deliberately-invalid-secret-for-testing-0000";
            }
        }

        return string.Join(';', parts);
    }
}
