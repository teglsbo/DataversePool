using System.Net;
using ConnectionPool.Core;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

public class ResponseObservationTests
{
    private sealed class CapturingSink : IOperationOutcomeSink
    {
        public List<(object Member, ResponseBudget Budget)> Responses { get; } = new();
        public bool ObservesResponses => true;
        public void Record(object member, PoolSizingOperationOutcome outcome) { }
        public void OnResponse(object member, ResponseBudget budget) => Responses.Add((member, budget));
    }

    private static HttpResponseMessage Response(params (string Name, string Value)[] headers)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK);
        foreach (var (name, value) in headers)
        {
            r.Headers.TryAddWithoutValidation(name, value);
        }

        return r;
    }

    [Fact]
    public void TryParse_ReadsTheBudgetHeaders_IncludingThousandsSeparators()
    {
        using var r = Response(
            ("x-ms-ratelimit-burst-remaining-xrm-requests", "7995"),
            ("x-ms-ratelimit-time-remaining-xrm-requests", "1,199.97"),
            ("x-ms-dop-hint", "4"),
            ("x-ms-service-request-id", "abc"));

        Assert.True(ResponseObservation.TryParse(r, DateTimeOffset.UnixEpoch, out var b));

        Assert.Equal(7995, b.BurstRemainingRequests);
        Assert.Equal(1199.97, b.TimeRemainingSeconds);
        Assert.Equal(4, b.DopHint);
        Assert.Equal("abc", b.ServiceRequestId);
    }

    [Theory]
    [InlineData("1,199.97", 1199.97)]
    [InlineData("1200", 1200.0)]
    [InlineData("0.5", 0.5)]
    [InlineData("1.199,97", 1199.97)]
    [InlineData("1.200", 1.2)] // invariant is the server format, so this is not a thousands group
    [InlineData("12,5", null)] // ambiguous, rejected rather than guessed
    [InlineData("1,5", null)]
    [InlineData("1,19.9", null)]
    public void TryParse_NumberFormats_AreCultureIndependent(string raw, double? expected)
    {
        using var r = Response(("x-ms-ratelimit-time-remaining-xrm-requests", raw), ("x-ms-dop-hint", "4"));
        ResponseObservation.TryParse(r, DateTimeOffset.UnixEpoch, out var b);
        Assert.Equal(expected, b.TimeRemainingSeconds);
    }

    [Fact]
    public void TryParse_NoRelevantHeaders_ReturnsFalse()
    {
        using var r = Response(("x-ms-service-request-id", "abc"));
        Assert.False(ResponseObservation.TryParse(r, DateTimeOffset.UnixEpoch, out _));
    }

    [Fact]
    public void TryParse_GarbageValues_AreIgnored()
    {
        using var r = Response(("x-ms-ratelimit-burst-remaining-xrm-requests", "lots"), ("x-ms-dop-hint", "4"));
        Assert.True(ResponseObservation.TryParse(r, DateTimeOffset.UnixEpoch, out var b));
        Assert.Null(b.BurstRemainingRequests);
        Assert.Equal(4, b.DopHint);
    }

    // Real HttpClient against a local server, so the runtime's own diagnostic events fire.
    private static async Task<HttpListener> StartServerAsync(string prefix)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    ctx.Response.Headers["x-ms-ratelimit-burst-remaining-xrm-requests"] = "123";
                    ctx.Response.Headers["x-ms-dop-hint"] = "6";
                    ctx.Response.Close();
                }
                catch
                {
                    return;
                }
            }
        });
        return listener;
    }

    [Fact]
    public async Task Listener_AttributesResponsesToTheMemberOnlyWhileAnAttemptIsActive()
    {
        var port = Random.Shared.Next(20000, 40000);
        var prefix = $"http://127.0.0.1:{port}/";
        using var server = await StartServerAsync(prefix);
        using var observer = ResponseObservation.Start();
        using var http = new HttpClient();
        var sink = new CapturingSink();
        var member = new object();

        await http.GetAsync(prefix); // outside an attempt: ignored
        Assert.Empty(sink.Responses);

        ResponseObservation.Current.Value = new ResponseObservation.Target(sink, member);
        try
        {
            using var _ = await http.GetAsync(prefix);
        }
        finally
        {
            ResponseObservation.Current.Value = null;
        }

        var seen = Assert.Single(sink.Responses);
        Assert.Same(member, seen.Member);
        Assert.Equal(123, seen.Budget.BurstRemainingRequests);
        Assert.Equal(6, seen.Budget.DopHint);
        server.Stop();
    }

    [Fact]
    public async Task TwoObservers_DeliverEachResponseOnce()
    {
        var port = Random.Shared.Next(20000, 40000);
        var prefix = $"http://127.0.0.1:{port}/";
        using var server = await StartServerAsync(prefix);
        using var first = ResponseObservation.Start();
        using var second = ResponseObservation.Start();
        using var http = new HttpClient();
        var sink = new CapturingSink();

        ResponseObservation.Current.Value = new ResponseObservation.Target(sink, new object());
        try { using var _ = await http.GetAsync(prefix); }
        finally { ResponseObservation.Current.Value = null; }

        Assert.Single(sink.Responses);
        server.Stop();
    }

    [Fact]
    public async Task Controller_LowBurstBudget_HoldsAttemptsBack_ThenFailsOpen()
    {
        await using var m = new DataverseUserPool("u", "dummy", new PoolOptions { MaxSize = 4 });
        using var controller = new PoolSizingController(new[] { m }, new PoolSizingOptions
        {
            ObserveResponses = true,
            LowBudgetBackoff = TimeSpan.FromMilliseconds(300),
        });

        controller.OnResponse(m, new ResponseBudget(8000, 1200, 4, null, DateTimeOffset.UtcNow));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await controller.BeforeAttemptAsync(m, CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds < 100, "plenty of budget: no hold");

        controller.OnResponse(m, new ResponseBudget(100, 1200, 4, null, DateTimeOffset.UtcNow)); // 1.25% of 8000
        sw.Restart();
        await controller.BeforeAttemptAsync(m, CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds >= 250, $"held only {sw.ElapsedMilliseconds} ms");
        Assert.Equal(100, controller.GetBudget(m)?.BurstRemainingRequests);
    }

    [Fact]
    public async Task Controller_LowTimeBudget_AlsoHolds()
    {
        await using var m = new DataverseUserPool("u", "dummy", new PoolOptions { MaxSize = 4 });
        using var controller = new PoolSizingController(new[] { m }, new PoolSizingOptions
        {
            ObserveResponses = true,
            LowBudgetBackoff = TimeSpan.FromMilliseconds(300),
        });
        controller.OnResponse(m, new ResponseBudget(null, 1200, null, null, DateTimeOffset.UtcNow));
        controller.OnResponse(m, new ResponseBudget(null, 10, null, null, DateTimeOffset.UtcNow));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await controller.BeforeAttemptAsync(m, CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds >= 250);
    }

    [Fact]
    public async Task Pool_ObserveResponses_WorksWithDefaultFixedStrategy_AndIsOffOtherwise()
    {
        var a = new DataverseUserPool("a", "dummy");
        await using var on = new DataversePool(a, sizingOptions: new PoolSizingOptions { ObserveResponses = true });
        Assert.NotNull(on.SizingSink);
        Assert.Null(on.GetResponseBudget(a));

        var b = new DataverseUserPool("b", "dummy");
        await using var off = new DataversePool(b);
        Assert.Null(off.GetResponseBudget(b));
    }

    [Fact]
    public void TryParse_ReadsBackendNodeFromXSource()
    {
        using var r = new HttpResponseMessage();
        r.Headers.TryAddWithoutValidation("x-ms-dop-hint", "4");
        r.Headers.TryAddWithoutValidation("X-Source", "constant|node-17");

        Assert.True(ResponseObservation.TryParse(r, DateTimeOffset.UnixEpoch, out var b));
        Assert.Equal("node-17", b.ServerId);
    }

    [Fact]
    public void TryParse_ReadsBackendNodeFromSecondXSourceValue()
    {
        using var r = new HttpResponseMessage();
        r.Headers.TryAddWithoutValidation("x-ms-dop-hint", "4");
        r.Headers.TryAddWithoutValidation("X-Source", new[] { "constant", "node-17" });

        Assert.True(ResponseObservation.TryParse(r, DateTimeOffset.UnixEpoch, out var b));
        Assert.Equal("node-17", b.ServerId);
    }

    [Fact]
    public void Options_Validate_RejectsBadBudgetSettings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PoolSizingOptions { LowBudgetFraction = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PoolSizingOptions { LowBudgetBackoff = TimeSpan.Zero }.Validate());
    }
}
