using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.PowerPlatform.Dataverse.Client;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Opt-in tests that bypass <see cref="ServiceClient"/> request dispatch so a shared affinity
/// cookie and a deliberately slow Web API query can keep many requests active against one
/// Dataverse web server at the same time.
/// </summary>
[Trait("Category", "Integration")]
public class LivePinnedWebApiConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    public LivePinnedWebApiConcurrencyTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Sends a synchronized burst of expensive reads through one <see cref="HttpClient"/> and one
    /// <c>ARRAffinity</c> cookie. This avoids the ambiguity in the SDK-based tests where many calls
    /// can be locally outstanding while SDK, transport, gateway, or server queues serialize part of
    /// the work before it reaches Dataverse.
    /// </summary>
    /// <remarks>
    /// The query was measured at roughly 5-5.5 seconds in this test environment by the earlier
    /// dvduck ThrottleSpike. The worker count defaults to 128 and can be overridden with
    /// <c>DVPOOL_IT_PINNED_CONCURRENCY</c>. Each worker sends exactly one request; there are no
    /// retries, so every 429 and every other HTTP or transport failure remains visible.
    /// </remarks>
    [SkippableFact]
    public async Task SlowRequests_WithPinnedAffinity_ExposeTheRealConcurrentRequestOutcome()
    {
        var connectionString = GetConnectionString();
        Skip.If(
            connectionString is null,
            "Set DVPOOL_IT_CONNECTION_STRING or DVPOOL_IT_URL, DVPOOL_IT_TENANT_ID, DVPOOL_IT_CLIENT_ID, and DVPOOL_IT_CLIENT_SECRET.");

        var concurrency = GetPositiveEnvironmentInteger("DVPOOL_IT_PINNED_CONCURRENCY", 128);

        using var authenticationClient = new ServiceClient(connectionString);
        Assert.True(
            authenticationClient.IsReady,
            $"Could not authenticate the live test client: {authenticationClient.LastError}");

        var accessToken = authenticationClient.CurrentAccessToken;
        Assert.False(string.IsNullOrWhiteSpace(accessToken));

        var connectedOrgUri = authenticationClient.ConnectedOrgUriActual;
        Assert.NotNull(connectedOrgUri);
        var environmentUri = GetEnvironmentRoot(connectedOrgUri);

        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            MaxConnectionsPerServer = concurrency,
        };
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(3),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
        http.DefaultRequestHeaders.Add("OData-Version", "4.0");

        var whoAmIUri = new Uri(environmentUri, "api/data/v9.2/WhoAmI");
        using (var warmup = await http.GetAsync(whoAmIUri))
        {
            Assert.True(
                warmup.IsSuccessStatusCode,
                $"Affinity warmup failed with HTTP {(int)warmup.StatusCode} {warmup.StatusCode}.");
        }

        var affinityCookies = cookies.GetCookies(environmentUri)
            .Cast<Cookie>()
            .Where(cookie => cookie.Name.StartsWith("ARRAffinity", StringComparison.OrdinalIgnoreCase))
            .Select(cookie => cookie.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToArray();

        Assert.NotEmpty(affinityCookies);
        _output.WriteLine(
            $"Pinned affinity cookie(s): {string.Join(", ", affinityCookies)}. " +
            $"Concurrency: {concurrency}.");

        var slowRequestUri = new Uri(
            environmentUri,
            "api/data/v9.2/solutioncomponents?$top=5000");
        var readyWorkers = 0;
        var currentInFlight = 0;
        var peakInFlight = 0;
        var allWorkersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomes = new ConcurrentBag<RequestOutcome>();
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
            var nowInFlight = Interlocked.Increment(ref currentInFlight);
            InterlockedMax(ref peakInFlight, nowInFlight);
            var requestWatch = Stopwatch.StartNew();

            try
            {
                using var response = await http.GetAsync(slowRequestUri);
                requestWatch.Stop();

                if (response.IsSuccessStatusCode)
                {
                    outcomes.Add(new RequestOutcome(
                        (int)response.StatusCode,
                        requestWatch.Elapsed,
                        null,
                        null,
                        null));
                    return;
                }

                var error = await ReadErrorAsync(response);
                outcomes.Add(new RequestOutcome(
                    (int)response.StatusCode,
                    requestWatch.Elapsed,
                    GetRetryAfter(response),
                    error.Code,
                    error.Message));
            }
            catch (Exception exception)
            {
                requestWatch.Stop();
                outcomes.Add(new RequestOutcome(
                    null,
                    requestWatch.Elapsed,
                    null,
                    exception.GetType().Name,
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

        var results = outcomes.ToArray();
        var latencies = results.Select(result => result.Latency).Order().ToArray();
        var launchSpread = Stopwatch.GetElapsedTime(firstStartedAt, lastStartedAt);
        var sumLatency = results.Sum(result => result.Latency.TotalSeconds);

        _output.WriteLine(
            $"Pinned slow-request burst completed in {burstWatch.Elapsed}. " +
            $"Launch spread={launchSpread}, peak locally outstanding HTTP calls={peakInFlight}, " +
            $"average locally outstanding calls={sumLatency / burstWatch.Elapsed.TotalSeconds:F1}.");
        _output.WriteLine(
            $"Latency: p50={Percentile(latencies, 0.50)}, p95={Percentile(latencies, 0.95)}, " +
            $"p99={Percentile(latencies, 0.99)}, max={latencies[^1]}.");

        foreach (var statusGroup in results
                     .GroupBy(result => result.StatusCode?.ToString() ?? "transport-exception")
                     .OrderBy(group => group.Key))
        {
            _output.WriteLine($"Outcome {statusGroup.Key}: {statusGroup.Count()}.");
        }

        var throttles = results.Where(result => result.StatusCode == 429).ToArray();
        if (throttles.Length > 0)
        {
            var retryAfters = throttles
                .Where(result => result.RetryAfter is not null)
                .Select(result => result.RetryAfter!.Value)
                .ToArray();

            _output.WriteLine(
                retryAfters.Length == 0
                    ? "HTTP 429 responses did not include a parseable Retry-After value."
                    : $"HTTP 429 Retry-After: min={retryAfters.Min()}, max={retryAfters.Max()}.");
        }

        foreach (var failureGroup in results
                     .Where(result => result.StatusCode is null or >= 400)
                     .GroupBy(result => $"{result.ErrorCode ?? "unknown"}: {result.ErrorMessage ?? "no message"}")
                     .OrderBy(group => group.Key))
        {
            _output.WriteLine($"Failure ({failureGroup.Count()}x): {failureGroup.Key}");
        }

        Assert.Equal(concurrency, results.Length);
        Assert.Equal(concurrency, peakInFlight);
        Assert.True(
            launchSpread < TimeSpan.FromSeconds(2),
            $"The synchronized workers took {launchSpread} to issue their requests; " +
            "the burst did not start tightly enough to test concurrency.");
    }

    /// <summary>
    /// Repeats the pinned slow-request burst with two distinct application users sharing the same
    /// affinity cookie and Dataverse web server. The default is 160 aggregate requests, split 80/80:
    /// above the measured single-user limit of 100, but below it for each identity independently.
    /// </summary>
    [SkippableFact]
    public async Task SlowRequests_WithTwoAppUsers_SustainMoreThanOneUsersConcurrentLimit()
    {
        var connectionStringA = GetConnectionString();
        var connectionStringB = GetConnectionString("_B");
        Skip.If(
            connectionStringA is null || connectionStringB is null,
            "Set credentials for both application users; the second user's variables use suffix _B.");

        var aggregateConcurrency = GetPositiveEnvironmentInteger(
            "DVPOOL_IT_PINNED_TWO_USER_CONCURRENCY",
            160);
        Assert.True(
            aggregateConcurrency > 100 && aggregateConcurrency % 2 == 0,
            "DVPOOL_IT_PINNED_TWO_USER_CONCURRENCY must be an even number above 100.");
        var concurrencyPerUser = aggregateConcurrency / 2;

        using var authenticationClientA = CreateReadyClient(connectionStringA, "A");
        using var authenticationClientB = CreateReadyClient(connectionStringB, "B");

        var accessTokenA = authenticationClientA.CurrentAccessToken;
        var accessTokenB = authenticationClientB.CurrentAccessToken;
        Assert.False(string.IsNullOrWhiteSpace(accessTokenA));
        Assert.False(string.IsNullOrWhiteSpace(accessTokenB));

        var connectedOrgUri = authenticationClientA.ConnectedOrgUriActual;
        Assert.NotNull(connectedOrgUri);
        var environmentUri = GetEnvironmentRoot(connectedOrgUri);

        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            MaxConnectionsPerServer = aggregateConcurrency,
        };
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(3),
        };
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
        http.DefaultRequestHeaders.Add("OData-Version", "4.0");

        var whoAmIUri = new Uri(environmentUri, "api/data/v9.2/WhoAmI");
        var userIdA = await WarmupAndGetUserIdAsync(http, whoAmIUri, accessTokenA);
        var affinityAfterA = GetAffinityCookie(cookies, environmentUri);
        var userIdB = await WarmupAndGetUserIdAsync(http, whoAmIUri, accessTokenB);
        var affinityAfterB = GetAffinityCookie(cookies, environmentUri);

        Assert.NotEqual(userIdA, userIdB);
        Assert.Equal(affinityAfterA.Value, affinityAfterB.Value);
        _output.WriteLine(
            $"Distinct users confirmed. Both pinned by {affinityAfterB.Name}. " +
            $"Aggregate concurrency={aggregateConcurrency}, per user={concurrencyPerUser}.");

        var slowRequestUri = new Uri(
            environmentUri,
            "api/data/v9.2/solutioncomponents?$top=5000");
        var assignments = Enumerable.Range(0, aggregateConcurrency)
            .Select(index => index % 2 == 0
                ? new RequestAssignment("A", accessTokenA)
                : new RequestAssignment("B", accessTokenB))
            .ToArray();

        var readyWorkers = 0;
        var currentInFlight = 0;
        var peakInFlight = 0;
        var allWorkersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomes = new ConcurrentBag<RequestOutcome>();
        long firstStartedAt = long.MaxValue;
        long lastStartedAt = 0;

        var workers = assignments.Select(async assignment =>
        {
            if (Interlocked.Increment(ref readyWorkers) == aggregateConcurrency)
            {
                allWorkersReady.SetResult();
            }

            await startGate.Task;

            var startedAt = Stopwatch.GetTimestamp();
            InterlockedMin(ref firstStartedAt, startedAt);
            InterlockedMax(ref lastStartedAt, startedAt);
            var nowInFlight = Interlocked.Increment(ref currentInFlight);
            InterlockedMax(ref peakInFlight, nowInFlight);
            var requestWatch = Stopwatch.StartNew();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, slowRequestUri);
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", assignment.AccessToken);
                using var response = await http.SendAsync(request);
                requestWatch.Stop();

                if (response.IsSuccessStatusCode)
                {
                    outcomes.Add(new RequestOutcome(
                        (int)response.StatusCode,
                        requestWatch.Elapsed,
                        null,
                        null,
                        null,
                        assignment.User));
                    return;
                }

                var error = await ReadErrorAsync(response);
                outcomes.Add(new RequestOutcome(
                    (int)response.StatusCode,
                    requestWatch.Elapsed,
                    GetRetryAfter(response),
                    error.Code,
                    error.Message,
                    assignment.User));
            }
            catch (Exception exception)
            {
                requestWatch.Stop();
                outcomes.Add(new RequestOutcome(
                    null,
                    requestWatch.Elapsed,
                    null,
                    exception.GetType().Name,
                    DescribeException(exception),
                    assignment.User));
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

        var results = outcomes.ToArray();
        var launchSpread = Stopwatch.GetElapsedTime(firstStartedAt, lastStartedAt);
        var latencies = results.Select(result => result.Latency).Order().ToArray();
        _output.WriteLine(
            $"Two-user pinned burst completed in {burstWatch.Elapsed}. Launch spread={launchSpread}, " +
            $"peak locally outstanding HTTP calls={peakInFlight}.");
        _output.WriteLine(
            $"Latency: p50={Percentile(latencies, 0.50)}, p95={Percentile(latencies, 0.95)}, " +
            $"p99={Percentile(latencies, 0.99)}, max={latencies[^1]}.");

        foreach (var userGroup in results.GroupBy(result => result.User).OrderBy(group => group.Key))
        {
            _output.WriteLine(
                $"User {userGroup.Key}: " +
                string.Join(
                    ", ",
                    userGroup
                        .GroupBy(result => result.StatusCode?.ToString() ?? "transport-exception")
                        .OrderBy(group => group.Key)
                        .Select(group => $"{group.Key}={group.Count()}")));
        }

        foreach (var failureGroup in results
                     .Where(result => result.StatusCode is null or >= 400)
                     .GroupBy(result =>
                         $"{result.User} {result.ErrorCode ?? "unknown"}: " +
                         $"{result.ErrorMessage ?? "no message"}")
                     .OrderBy(group => group.Key))
        {
            _output.WriteLine($"Failure ({failureGroup.Count()}x): {failureGroup.Key}");
        }

        Assert.Equal(aggregateConcurrency, results.Length);
        Assert.Equal(aggregateConcurrency, peakInFlight);
        Assert.True(
            results.Count(result => result.StatusCode == 200) > 100,
            "Two application users did not complete more successful requests than the measured " +
            "single-user concurrent-request limit of 100.");
        if (aggregateConcurrency == 160)
        {
            Assert.All(results, result => Assert.Equal(200, result.StatusCode));
            Assert.Equal(
                concurrencyPerUser,
                results.Count(result => result.User == "A" && result.StatusCode == 200));
            Assert.Equal(
                concurrencyPerUser,
                results.Count(result => result.User == "B" && result.StatusCode == 200));
        }
        Assert.True(
            launchSpread < TimeSpan.FromSeconds(2),
            $"The synchronized workers took {launchSpread} to issue their requests.");
    }

    private static ServiceClient CreateReadyClient(string connectionString, string name)
    {
        var client = new ServiceClient(connectionString);
        Assert.True(client.IsReady, $"Could not authenticate live test client {name}: {client.LastError}");
        return client;
    }

    private static async Task<Guid> WarmupAndGetUserIdAsync(
        HttpClient http,
        Uri whoAmIUri,
        string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, whoAmIUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Affinity warmup failed with HTTP {(int)response.StatusCode} {response.StatusCode}.");

        await using var content = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(content);
        return document.RootElement.GetProperty("UserId").GetGuid();
    }

    private static Cookie GetAffinityCookie(CookieContainer cookies, Uri environmentUri)
    {
        var affinityCookie = cookies.GetCookies(environmentUri)
            .Cast<Cookie>()
            .FirstOrDefault(cookie =>
                cookie.Name.Equals("ARRAffinity", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(affinityCookie);
        return affinityCookie;
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

        return new[] { url, tenantId, clientId, clientSecret }.All(value => !string.IsNullOrWhiteSpace(value))
            ? $"AuthType=ClientSecret;Url={url};TenantId={tenantId};ClientId={clientId};ClientSecret={clientSecret};"
            : null;
    }

    private static Uri GetEnvironmentRoot(Uri connectedOrgUri)
    {
        var configuredUrl = Environment.GetEnvironmentVariable("DVPOOL_IT_URL");
        if (Uri.TryCreate(configuredUrl, UriKind.Absolute, out var environmentUri))
        {
            return environmentUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
                ? environmentUri
                : new Uri($"{environmentUri.AbsoluteUri}/");
        }

        return new Uri($"{connectedOrgUri.GetLeftPart(UriPartial.Authority)}/");
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

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        return response.Headers.RetryAfter?.Date is { } date
            ? date - DateTimeOffset.UtcNow
            : null;
    }

    private static async Task<(string? Code, string? Message)> ReadErrorAsync(
        HttpResponseMessage response)
    {
        try
        {
            await using var content = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(content);
            if (!document.RootElement.TryGetProperty("error", out var error))
            {
                return (null, null);
            }

            var code = error.TryGetProperty("code", out var codeValue)
                ? codeValue.GetString()
                : null;
            var message = error.TryGetProperty("message", out var messageValue)
                ? messageValue.GetString()
                : null;
            return (code, Truncate(message, 500));
        }
        catch (JsonException)
        {
            return (null, "Response body was not valid JSON.");
        }
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    private static string DescribeException(Exception exception)
    {
        var chain = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            chain.Add($"{current.GetType().Name}: {current.Message}");
        }

        return Truncate(string.Join(" -> ", chain), 500)!;
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

    private sealed record RequestOutcome(
        int? StatusCode,
        TimeSpan Latency,
        TimeSpan? RetryAfter,
        string? ErrorCode,
        string? ErrorMessage,
        string User = "A");

    private sealed record RequestAssignment(string User, string AccessToken);
}
