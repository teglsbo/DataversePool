using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Xunit;
using Xunit.Abstractions;

namespace ConnectionPool.Dataverse.Tests;

public class ResponseObservationLoadTests
{
    private readonly ITestOutputHelper _output;

    public ResponseObservationLoadTests(ITestOutputHelper output) => _output = output;

    private sealed class Sink : IOperationOutcomeSink
    {
        public ConcurrentBag<int> Seen { get; } = new();
        public bool ObservesResponses => true;
        public void Record(object member, PoolSizingOperationOutcome outcome) { }
        public void OnResponse(object member, ResponseBudget budget) => Seen.Add((int)budget.BurstRemainingRequests!.Value);
    }

    private static HttpListener StartEchoServer(string prefix)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(() =>
                {
                    ctx.Response.Headers["x-ms-ratelimit-burst-remaining-xrm-requests"] = ctx.Request.Url!.AbsolutePath.Trim('/');
                    ctx.Response.Close();
                });
            }
        });
        return listener;
    }

    [Fact]
    public async Task ManyConcurrentAttempts_EachResponseIsAttributedToItsOwnMember()
    {
        const int members = 50, callsPerMember = 40;
        var prefix = $"http://127.0.0.1:{Random.Shared.Next(40001, 60000)}/";
        using var server = StartEchoServer(prefix);
        using var observer = ResponseObservation.Start();
        using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 64 });
        var sinks = Enumerable.Range(0, members).Select(_ => new Sink()).ToArray();

        var sw = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, members).Select(i => Task.Run(async () =>
        {
            for (var n = 0; n < callsPerMember; n++)
            {
                ResponseObservation.Current.Value = new ResponseObservation.Target(sinks[i], sinks[i]);
                try
                {
                    using var _ = await http.GetAsync(prefix + i);
                }
                finally
                {
                    ResponseObservation.Current.Value = null;
                }
            }
        }));

        // Unattributed background traffic must be ignored while the load runs.
        var background = Task.Run(async () =>
        {
            for (var n = 0; n < 200; n++) { using var _ = await http.GetAsync(prefix + "999999"); }
        });

        await Task.WhenAll(workers.Append(background));
        _output.WriteLine($"{members * callsPerMember + 200} calls in {sw.ElapsedMilliseconds} ms");

        for (var i = 0; i < members; i++)
        {
            Assert.Equal(callsPerMember, sinks[i].Seen.Count);
            Assert.All(sinks[i].Seen, v => Assert.Equal(i, v));
        }
        server.Stop();
    }
}
