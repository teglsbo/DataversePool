using System.Collections.Concurrent;
using System.Diagnostics;
using ConnectionPool.Core;
using Microsoft.Crm.Sdk.Messages;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Opt-in exploration: which response headers or cookies identify the Dataverse front-end server that answered,
/// and does the rate-limit budget header follow it? Prints header names with their distinct values (cookie and
/// authorization values are reduced to length, since they can be session secrets).
/// </summary>
[Trait("Category", "Integration")]
public class LiveServerIdentityTests
{
    private readonly ITestOutputHelper _output;

    public LiveServerIdentityTests(ITestOutputHelper output) => _output = output;

    private sealed record Seen(string Transport, Dictionary<string, string> Headers);

    private sealed class Collector : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        public ConcurrentBag<Seen> Events { get; } = new();
        public ConcurrentQueue<string> ArrRaw { get; } = new();

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "HttpHandlerDiagnosticListener")
            {
                listener.Subscribe(this, static (name, _, _) => name is "System.Net.Http.HttpRequestOut" or "System.Net.Http.HttpRequestOut.Stop");
            }
        }

        public void OnNext(KeyValuePair<string, object?> evt)
        {
            if (evt.Value is null || !evt.Key.EndsWith("Stop", StringComparison.Ordinal))
            {
                return;
            }

            var type = evt.Value.GetType();
            if (type.GetProperty("Response")?.GetValue(evt.Value) is not HttpResponseMessage response)
            {
                return;
            }

            var path = (type.GetProperty("Request")?.GetValue(evt.Value) as HttpRequestMessage)?.RequestUri?.AbsolutePath ?? "?";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers.Concat(response.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
            {
                var value = string.Join("|", h.Value);
                headers[h.Key] = h.Key.Contains("cookie", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    ? $"<{value.Length} chars, names: {string.Join(",", h.Value.Select(v => v.Split('=')[0]))}>"
                    : value;
            }

            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                var arr = cookies.FirstOrDefault(c => c.StartsWith("ARRAffinity=", StringComparison.Ordinal));
                if (arr is not null)
                {
                    var value = arr.Split(';')[0]["ARRAffinity=".Length..];
                    ArrRaw.Enqueue(value);
                    headers["_arr"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..8];
                }
            }

            if (response.Headers.TryGetValues("X-Source", out var src))
            {
                headers["_xsource"] = string.Join("|", src);
            }

            Events.Add(new Seen(path.Contains("/api/data", StringComparison.OrdinalIgnoreCase) ? "web" : "soap", headers));
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    [SkippableFact]
    public async Task PinnedAffinityCookie_ShowsWhetherItIdentifiesTheServer()
    {
        var connectionString = LiveDataverseCredentials.GetConnectionString(0);
        Skip.If(string.IsNullOrEmpty(connectionString), "Set DVPOOL_IT_* credentials.");

        var member = new DataverseUserPool("A", connectionString!, new PoolOptions { MaxSize = 8 });
        await using var pool = new DataversePool(member);
        var collector = new Collector();
        using var subscription = DiagnosticListener.AllListeners.Subscribe(collector);

        async Task CallAsync(string? cookie)
        {
            await pool.ExecuteWithThrottleRetryAsync("web", async (c, ct) =>
            {
                var custom = cookie is null ? null : new Dictionary<string, List<string>> { ["Cookie"] = new() { "ARRAffinity=" + cookie } };
                using var r = await c.ExecuteWebRequestAsync(HttpMethod.Get, "WhoAmI()", string.Empty, custom, "application/json", ct);
                return (int)r.StatusCode;
            });
        }

        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => CallAsync(null)));
        var cookies = collector.ArrRaw.Distinct().Take(3).ToArray();
        _output.WriteLine($"captured {cookies.Length} distinct cookies");

        foreach (var cookie in cookies)
        {
            collector.Events.Clear();
            collector.ArrRaw.Clear();
            for (var i = 0; i < 20; i++)
            {
                await CallAsync(cookie);
            }

            var events = collector.Events.ToArray();
            var returned = collector.ArrRaw.Distinct().ToArray();
            var budgets = events.Select(e => double.Parse(e.Headers["x-ms-ratelimit-burst-remaining-xrm-requests"].Replace(",", string.Empty), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var parts = events.Where(e => e.Headers.ContainsKey("_xsource")).Select(e => e.Headers["_xsource"].Split('|').Last()).Distinct().Count();
            _output.WriteLine($"pinned cookie {cookie.GetHashCode():X}: {events.Length} responses, new cookie values returned={returned.Length}, same as sent={returned.Contains(cookie)}, burst {budgets.Min()}..{budgets.Max()}, X-Source part1 distinct={parts}");
        }
    }

    [SkippableFact]
    public async Task ResponseHeaders_ShowWhichServerAnswered()
    {
        var connectionString = LiveDataverseCredentials.GetConnectionString(0);
        Skip.If(string.IsNullOrEmpty(connectionString), "Set DVPOOL_IT_* credentials.");

        var member = new DataverseUserPool("A", connectionString!, new PoolOptions { MaxSize = 8 });
        await using var pool = new DataversePool(member);
        var collector = new Collector();
        using var subscription = DiagnosticListener.AllListeners.Subscribe(collector);

        await Task.WhenAll(Enumerable.Range(0, 80).Select(i => i % 2 == 0
            ? pool.ExecuteWithThrottleRetryAsync("soap", async (c, ct) => (await c.ExecuteAsync(new WhoAmIRequest(), ct)).ResponseName.Length)
            : pool.ExecuteWithThrottleRetryAsync("web", async (c, ct) =>
            {
                using var r = await c.ExecuteWebRequestAsync(HttpMethod.Get, "WhoAmI()", string.Empty, null, "application/json", ct);
                return (int)r.StatusCode;
            })));

        var events = collector.Events.ToArray();
        _output.WriteLine($"{events.Length} responses ({events.Count(e => e.Transport == "web")} web, {events.Count(e => e.Transport == "soap")} soap)");
        foreach (var transport in new[] { "web", "soap" })
        {
            var set = events.Where(e => e.Transport == transport).ToArray();
            _output.WriteLine($"--- {transport}: headers by distinct values ---");
            foreach (var name in set.SelectMany(e => e.Headers.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var values = set.Select(e => e.Headers.TryGetValue(name, out var v) ? v : "<absent>").GroupBy(v => v).OrderByDescending(g => g.Count()).ToArray();
                var shown = string.Join("; ", values.Take(4).Select(g => $"{(g.Key.Length > 70 ? g.Key[..70] + "..." : g.Key)} x{g.Count()}"));
                _output.WriteLine($"{name}: {values.Length} distinct :: {shown}");
            }
        }
    
        foreach (var part in new[] { 0, 1 })
        {
            _output.WriteLine($"=== budget by X-Source part {part} ===");
            var groups = events.Where(e => e.Headers.ContainsKey("_xsource") && e.Headers.ContainsKey("x-ms-ratelimit-burst-remaining-xrm-requests") && e.Headers["_xsource"].Split('|').Length > part)
                .GroupBy(e => e.Headers["_xsource"].Split('|')[part]);
            foreach (var g in groups.OrderByDescending(g => g.Count()))
            {
                var budgets = g.Select(e => double.Parse(e.Headers["x-ms-ratelimit-burst-remaining-xrm-requests"].Replace(",", string.Empty), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                _output.WriteLine($"part{part} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(g.Key)))[..6]}: n={g.Count()} burst min={budgets.Min()} max={budgets.Max()} range={budgets.Max() - budgets.Min()}");
            }
        }

        foreach (var key in new[] { "_arr", "_xsource" })
        {
            _output.WriteLine($"=== budget by {key} ===");
            var groups = events.Where(e => e.Headers.ContainsKey(key) && e.Headers.ContainsKey("x-ms-ratelimit-burst-remaining-xrm-requests"))
                .GroupBy(e => e.Headers[key]);
            foreach (var g in groups.OrderByDescending(g => g.Count()))
            {
                var budgets = g.Select(e => double.Parse(e.Headers["x-ms-ratelimit-burst-remaining-xrm-requests"].Replace(",", string.Empty), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var label = key == "_xsource" ? "..." + g.Key[^16..] + $" (len {g.Key.Length})" : g.Key;
                _output.WriteLine($"{label}: n={g.Count()} burst min={budgets.Min()} max={budgets.Max()} range={budgets.Max() - budgets.Min()}");
            }
        }
    }
}
