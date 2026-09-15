// DataversePool sample client.
//
// Two modes:
//  1. Always runs: an in-memory pool-mechanics demo using a fake resource (no network, no
//     credentials needed) — shows acquire/release, MarkUnhealthy-driven recycling, and the
//     Polly integration.
//  2. Optionally, if DATAVERSEPOOL_SAMPLE_CONNECTION_STRING is set, runs a real live smoke test against
//     an actual Dataverse environment: builds a DataverseUserPool, warms it up, acquires a lease,
//     and executes a WhoAmIRequest to prove the pooled ServiceClient actually works end-to-end.
//     If DATAVERSEPOOL_SAMPLE_CONNECTION_STRING_2/_3 are also set, it additionally demonstrates the
//     group (round-robin) pool across multiple application users.
using ConnectionPool.Core;
using ConnectionPool.Dataverse;
using ConnectionPool.Dataverse.Polly;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Polly;

Console.WriteLine("=== DataversePool sample ===");
Console.WriteLine();

await RunInMemoryDemoAsync();

var connectionString = Environment.GetEnvironmentVariable("DATAVERSEPOOL_SAMPLE_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine();
    Console.WriteLine("DATAVERSEPOOL_SAMPLE_CONNECTION_STRING is not set — skipping the live Dataverse smoke test.");
    Console.WriteLine("To run it for real:");
    Console.WriteLine("  export DATAVERSEPOOL_SAMPLE_CONNECTION_STRING=\"AuthType=ClientSecret;Url=https://yourorg.crm.dynamics.com;ClientId=...;ClientSecret=...;\"");
    Console.WriteLine("  dotnet run --project samples/DataversePool.Sample");
    Console.WriteLine("Optionally also set _2 / _3 suffixed variants to additionally exercise the group pool.");
    return;
}

await RunLiveSingleUserSmokeTestAsync(connectionString);

var connectionString2 = Environment.GetEnvironmentVariable("DATAVERSEPOOL_SAMPLE_CONNECTION_STRING_2");
var connectionString3 = Environment.GetEnvironmentVariable("DATAVERSEPOOL_SAMPLE_CONNECTION_STRING_3");
var groupMembers = new[] { connectionString, connectionString2, connectionString3 }
    .Where(s => !string.IsNullOrWhiteSpace(s))
    .ToArray();

if (groupMembers.Length > 1)
{
    await RunLiveGroupSmokeTestAsync(groupMembers!);
}
else
{
    Console.WriteLine();
    Console.WriteLine("Only one connection string provided — skipping the group-pool smoke test.");
    Console.WriteLine("Set DATAVERSEPOOL_SAMPLE_CONNECTION_STRING_2 (and optionally _3) to also exercise DataversePool.");
}

return;

static async Task RunInMemoryDemoAsync()
{
    Console.WriteLine("--- In-memory pool mechanics demo (no network) ---");

    var policy = new FakeResourcePolicy();
    await using var pool = new ResourcePool<FakeResource>(policy, new PoolOptions { MaxSize = 2, PrewarmCount = 1 });
    await pool.WarmupAsync();

    await using (var lease = await pool.AcquireAsync())
    {
        Console.WriteLine($"Acquired {lease.Resource.Id}. Simulating a failure and marking it unhealthy...");
        lease.MarkUnhealthy(new InvalidOperationException("simulated transient failure"));
    }

    await using (var lease = await pool.AcquireAsync())
    {
        Console.WriteLine($"Acquired {lease.Resource.Id} after recycling (should be a different instance).");

        // Wire a Polly retry pipeline so that any exception thrown while using this lease also
        // marks it unhealthy, in addition to whatever OnRetry handling you already have.
        var pipeline = new ResiliencePipelineBuilder<string>()
            .AddRetryWithPoolHealthSignal(lease, new Polly.Retry.RetryStrategyOptions<string>
            {
                ShouldHandle = new PredicateBuilder<string>().Handle<InvalidOperationException>(),
                MaxRetryAttempts = 1,
                Delay = TimeSpan.Zero,
            })
            .Build();

        var attempt = 0;
        var result = await pipeline.ExecuteAsync(async _ =>
        {
            attempt++;
            if (attempt == 1)
            {
                throw new InvalidOperationException("simulated failure on first attempt");
            }

            return "ok";
        });

        Console.WriteLine($"Polly-wrapped call result: {result} (this lease is now marked unhealthy and will be recycled on dispose).");
    }

    Console.WriteLine("In-memory demo complete.");
}

static async Task RunLiveSingleUserSmokeTestAsync(string connectionString)
{
    Console.WriteLine();
    Console.WriteLine("--- Live single-user smoke test ---");

    using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var logger = loggerFactory.CreateLogger<DataverseUserPool>();

    await using var pool = new DataverseUserPool(
        name: "sample-user",
        connectionString: connectionString,
        options: new PoolOptions { MaxSize = 4, PrewarmCount = 1 },
        logger: logger);

    Console.WriteLine("Warming up (sequential clone, see ADR-0002)...");
    await pool.WarmupAsync();

    await using var lease = await pool.AcquireAsync();
    Console.WriteLine($"Acquired a ServiceClient. IsReady={lease.Resource.IsReady}, EnableAffinityCookie={lease.Resource.EnableAffinityCookie}");

    var response = (WhoAmIResponse)lease.Resource.Execute(new WhoAmIRequest());
    Console.WriteLine($"WhoAmI succeeded: UserId={response.UserId}, OrganizationId={response.OrganizationId}");
}

static async Task RunLiveGroupSmokeTestAsync(string[] connectionStrings)
{
    Console.WriteLine();
    Console.WriteLine($"--- Live group-pool smoke test ({connectionStrings.Length} members) ---");

    var options = new PoolOptions { MaxSize = 4, PrewarmCount = 1 };
    var members = connectionStrings
        .Select((cs, i) => new DataverseUserPool($"sample-group-member-{i + 1}", cs, options))
        .ToArray();

    await using var group = new DataversePool(members);
    Console.WriteLine("Warming up all members sequentially...");
    await group.WarmupAsync();

    for (var i = 0; i < connectionStrings.Length; i++)
    {
        await using var lease = await group.AcquireAsync();
        try
        {
            var response = (WhoAmIResponse)lease.Resource.Execute(new WhoAmIRequest());
            Console.WriteLine($"Round-robin call #{i + 1}: UserId={response.UserId} (member={lease.Member.Name})");
        }
        catch (Exception ex) when (lease.ReportIfThrottled(ex))
        {
            // Dataverse returned a 429 for this member; DataverseThrottleDetector parsed the
            // Retry-After header and ReportIfThrottled already recorded it on lease.Member, so the
            // group's selection strategy will steer subsequent acquires to other members until it
            // expires. Re-throw (or retry) as appropriate for your application.
            Console.WriteLine($"Round-robin call #{i + 1}: throttled on member={lease.Member.Name}, backing off.");
        }
    }

    foreach (var member in members)
    {
        await member.DisposeAsync();
    }
}

/// <summary>Trivial fake resource used only for the in-memory demo (no network dependency).</summary>
internal sealed class FakeResource
{
    private static int _counter;
    public string Id { get; } = $"fake-resource-{Interlocked.Increment(ref _counter)}";
}

internal sealed class FakeResourcePolicy : IPooledResourcePolicy<FakeResource>
{
    public Task<FakeResource> CreateAsync(CancellationToken cancellationToken) => Task.FromResult(new FakeResource());

    public bool IsHealthy(FakeResource resource, PoolIncidentInfo? lastIncident) => lastIncident is null;

    public ValueTask DisposeResourceAsync(FakeResource resource) => ValueTask.CompletedTask;
}
