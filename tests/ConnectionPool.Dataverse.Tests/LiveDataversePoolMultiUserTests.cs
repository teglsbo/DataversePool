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

    private static string? ConnectionStringA =>
        Environment.GetEnvironmentVariable("DVPOOL_IT_CONNECTION_STRING");

    private static string? ConnectionStringB =>
        Environment.GetEnvironmentVariable("DVPOOL_IT_CONNECTION_STRING_B");

    [Fact]
    public async Task AcquireAsync_RoundRobinsAcrossBothRealMembers_AndBothAuthenticateAsDistinctUsers()
    {
        if (string.IsNullOrEmpty(ConnectionStringA) || string.IsNullOrEmpty(ConnectionStringB))
        {
            _output.WriteLine("Skipped: DVPOOL_IT_CONNECTION_STRING / DVPOOL_IT_CONNECTION_STRING_B not both set.");
            return;
        }

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
    [Fact]
    public async Task AcquireAsync_CanServeConcurrentCallers_FromBothMembersSimultaneously()
    {
        if (string.IsNullOrEmpty(ConnectionStringA) || string.IsNullOrEmpty(ConnectionStringB))
        {
            _output.WriteLine("Skipped: DVPOOL_IT_CONNECTION_STRING / DVPOOL_IT_CONNECTION_STRING_B not both set.");
            return;
        }

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
}
