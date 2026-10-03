using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using static ConnectionPool.Dataverse.DataverseOperationMetrics;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Tests <see cref="DataverseOperationExecutor"/> - the shared acquire/run/report/release/retry loop
/// behind <see cref="PooledOrganizationService"/> and
/// <see cref="DataversePool.ExecuteWithThrottleRetryAsync{T}(string, Func{Microsoft.PowerPlatform.Dataverse.Client.ServiceClient, CancellationToken, Task{T}}, int?, TimeSpan?, CancellationToken)"/>
/// - against fake leases, since a real <c>ServiceClient</c> cannot be constructed without a live
/// connection. Covers the lease-release/exception-identity guarantees formerly tested on
/// <c>LeaseScope</c> (docs/adr/0020), the throttle-retry loop (docs/adr/0019), and the operation
/// metrics (docs/adr/0024), observed through a real BCL <see cref="MeterListener"/>.
/// </summary>
public class DataverseOperationExecutorTests
{
    private sealed class FakeMember(string name)
    {
        public string Name { get; } = name;
    }

    private sealed class FakeLease(FakeMember member) : IAsyncDisposable
    {
        public FakeMember Member { get; } = member;
        public int DisposeCount { get; private set; }
        public int ThrottleReports { get; set; }
        public List<Exception> FaultReports { get; } = new();

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeThrottleException(TimeSpan retryAfter) : Exception("throttled: secret-record-id 1234")
    {
        public TimeSpan RetryAfter { get; } = retryAfter;
    }

    // Mirrors DataverseLease.ReportIfThrottled: recognizes a throttle, applies the cap, records it.
    private static readonly LeaseAccessors<FakeLease> Accessors = new(
        static l => l.Member,
        static l => l.Member.Name,
        static (FakeLease lease, Exception ex, TimeSpan? cap, out TimeSpan retryAfter) =>
        {
            if (ex is FakeThrottleException t)
            {
                lease.ThrottleReports++;
                retryAfter = cap is { } c && t.RetryAfter > c ? c : t.RetryAfter;
                return true;
            }

            retryAfter = TimeSpan.Zero;
            return false;
        });

    private static readonly LeaseAccessors<FakeLease> FaultAccessors = new(
        static l => l.Member,
        static l => l.Member.Name,
        static (FakeLease lease, Exception ex, TimeSpan? cap, out TimeSpan retryAfter) =>
        {
            retryAfter = TimeSpan.Zero;
            if (ex is not FakeThrottleException t)
            {
                return false;
            }

            retryAfter = t.RetryAfter;
            return true;
        },
        static (lease, ex) =>
        {
            lease.FaultReports.Add(ex);
            return true;
        });

    private static readonly LeaseAccessors<FakeLease> ThrowingFaultReporterAccessors = new(
        static l => l.Member,
        static l => l.Member.Name,
        static (FakeLease lease, Exception ex, TimeSpan? cap, out TimeSpan retryAfter) =>
        {
            retryAfter = TimeSpan.Zero;
            return false;
        },
        static (lease, ex) => throw new InvalidOperationException("fault reporter bug"));

    private static readonly LeaseAccessors<FakeLease> ThrowingReporterAccessors = new(
        static l => l.Member,
        static l => l.Member.Name,
        static (FakeLease lease, Exception ex, TimeSpan? cap, out TimeSpan retryAfter) =>
            throw new InvalidOperationException("reporter bug"));

    private static DataverseOperationRecorder NewRecorder() => new($"test.{Guid.NewGuid():N}");

    private static (DataverseOperationRecorder Recorder, MetricCollector Collector, OperationMetricsScope Scope) Instrumented(
        string operationName = "op", bool throwFromCallback = false)
    {
        var meterName = $"test.{Guid.NewGuid():N}";
        var recorder = new DataverseOperationRecorder(meterName);
        var collector = new MetricCollector(meterName, throwFromCallback);
        return (recorder, collector, new OperationMetricsScope(recorder, "pool-x", operationName));
    }

    private static Func<CancellationToken, Task<FakeLease>> Sequence(params FakeLease[] leases)
    {
        var queue = new Queue<FakeLease>(leases);
        return _ => Task.FromResult(queue.Dequeue());
    }

    private static Task<int> Run(
        OperationMetricsScope scope,
        Func<CancellationToken, Task<FakeLease>> acquire,
        Func<FakeLease, CancellationToken, Task<int>> operation,
        int maxAttempts = 1,
        TimeSpan? maxRetryAfter = null,
        CancellationToken cancellationToken = default,
        LeaseAccessors<FakeLease>? accessors = null) =>
        DataverseOperationExecutor.ExecuteAsync(scope, accessors ?? Accessors, acquire, operation, maxAttempts, maxRetryAfter, cancellationToken);

    private static OperationMetricsScope Uninstrumented() => new(NewRecorder(), "pool-x", "op");

    // ----- Connection-fault health signal (PLAN Phase 3 item 6) -----

    [Fact]
    public async Task NonThrottleFailure_IsReportedToFaultReporter_AndPropagatesUnchanged()
    {
        var lease = new FakeLease(new("a"));
        var boom = new InvalidOperationException("boom");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(Uninstrumented(), Sequence(lease), (_, _) => throw boom, accessors: FaultAccessors));

        Assert.Same(boom, thrown);
        Assert.Same(boom, Assert.Single(lease.FaultReports));
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task ThrottledFailure_IsNotReportedAsFault()
    {
        var lease = new FakeLease(new("a"));

        await Assert.ThrowsAsync<FakeThrottleException>(() =>
            Run(Uninstrumented(), Sequence(lease), (_, _) => throw new FakeThrottleException(TimeSpan.FromSeconds(1)), accessors: FaultAccessors));

        Assert.Empty(lease.FaultReports);
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsFault()
    {
        var lease = new FakeLease(new("a"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Run(Uninstrumented(), Sequence(lease), (_, ct) => throw new OperationCanceledException(ct), cancellationToken: cts.Token, accessors: FaultAccessors));

        Assert.Empty(lease.FaultReports);
    }

    [Fact]
    public async Task ThrowingFaultReporter_DoesNotReplaceOperationException()
    {
        var lease = new FakeLease(new("a"));
        var boom = new InvalidOperationException("boom");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(Uninstrumented(), Sequence(lease), (_, _) => throw boom, accessors: ThrowingFaultReporterAccessors));

        Assert.Same(boom, thrown);
        Assert.Equal(1, lease.DisposeCount);
    }

    // ----- Lease release / exception identity (formerly LeaseScopeTests, docs/adr/0020) -----

    [Fact]
    public async Task WithResult_ReturnsOperationResult_AndReleasesLeaseExactlyOnce()
    {
        var lease = new FakeLease(new("a"));
        var result = await Run(Uninstrumented(), Sequence(lease), (_, _) => Task.FromResult(42));

        Assert.Equal(42, result);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task WithoutResult_RunsOperation_AndReleasesLeaseExactlyOnce()
    {
        var lease = new FakeLease(new("a"));
        var ran = false;
        await DataverseOperationExecutor.ExecuteAsync(
            Uninstrumented(), Accessors, Sequence(lease),
            (_, _) => { ran = true; return Task.CompletedTask; },
            maxAttempts: 1, maxRetryAfter: null, CancellationToken.None);

        Assert.True(ran);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task ReleasesLease_WhenOperationThrows_AndPropagatesSameExceptionInstance()
    {
        var lease = new FakeLease(new("a"));
        var thrown = new InvalidOperationException("boom");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Run(Uninstrumented(), Sequence(lease), (_, _) => throw thrown));

        Assert.Same(thrown, actual);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task ReleasesLease_WhenOperationIsCanceled()
    {
        var lease = new FakeLease(new("a"));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Run(Uninstrumented(), Sequence(lease), (_, _) => throw new OperationCanceledException()));

        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task AcquireFailure_PropagatesSameInstance_WithoutRunningOperation()
    {
        var thrown = new InvalidOperationException("acquire failed");
        var ran = false;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Run(Uninstrumented(), _ => throw thrown, (_, _) => { ran = true; return Task.FromResult(1); }, maxAttempts: 5));

        Assert.Same(thrown, actual);
        Assert.False(ran);
    }

    [Fact]
    public async Task PropagatesCancellationToken_ToAcquireAndOperation()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken? acquireToken = null, operationToken = null;
        var lease = new FakeLease(new("a"));

        await Run(
            Uninstrumented(),
            ct => { acquireToken = ct; return Task.FromResult(lease); },
            (_, ct) => { operationToken = ct; return Task.FromResult(1); },
            cancellationToken: cts.Token);

        Assert.Equal(cts.Token, acquireToken);
        Assert.Equal(cts.Token, operationToken);
    }

    [Fact]
    public async Task ReportsThrottle_BeforeReleasingLease_EvenOnFinalAttempt()
    {
        var lease = new FakeLease(new("a"));
        var thrown = new FakeThrottleException(TimeSpan.FromSeconds(5));

        var actual = await Assert.ThrowsAsync<FakeThrottleException>(
            () => Run(Uninstrumented(), Sequence(lease), (_, _) => throw thrown, maxAttempts: 1));

        Assert.Same(thrown, actual);
        Assert.Equal(1, lease.ThrottleReports);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task ThrowingReporter_IsTreatedAsNotThrottled_AndOriginalExceptionPropagates()
    {
        var lease = new FakeLease(new("a"));
        var thrown = new FakeThrottleException(TimeSpan.FromSeconds(1));

        var actual = await Assert.ThrowsAsync<FakeThrottleException>(
            () => Run(Uninstrumented(), Sequence(lease), (_, _) => throw thrown, maxAttempts: 3, accessors: ThrowingReporterAccessors));

        Assert.Same(thrown, actual);
        Assert.Equal(1, lease.DisposeCount); // not retried
    }

    // ----- Throttle retry loop (docs/adr/0019) -----

    [Fact]
    public async Task NonThrottleFailure_IsNotRetried()
    {
        var acquires = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(
            Uninstrumented(),
            _ => { acquires++; return Task.FromResult(new FakeLease(new("a"))); },
            (_, _) => throw new InvalidOperationException(),
            maxAttempts: 5));

        Assert.Equal(1, acquires);
    }

    [Fact]
    public async Task Throttle_OnDifferentNextMember_RetriesImmediately()
    {
        var l1 = new FakeLease(new("a"));
        var l2 = new FakeLease(new("b"));
        var calls = 0;

        var result = await Run(
            Uninstrumented(), Sequence(l1, l2),
            (l, _) => ++calls == 1 ? throw new FakeThrottleException(TimeSpan.FromMinutes(10)) : Task.FromResult(l.Member.Name.Length),
            maxAttempts: 3);

        Assert.Equal(1, result);
        Assert.Equal(1, l1.DisposeCount);
        Assert.Equal(1, l2.DisposeCount);
    }

    [Fact]
    public async Task Throttle_OnSameNextMember_ReleasesLeaseBeforeWaiting_ThenReacquires()
    {
        var member = new FakeMember("a");
        var l1 = new FakeLease(member);
        var l2 = new FakeLease(member);
        var l3 = new FakeLease(member);
        var acquired = new List<FakeLease> { l1, l2, l3 };
        var acquireCount = 0;
        var l2DisposedBeforeThirdAcquire = false;
        FakeLease? ranOn = null;

        await Run(
            Uninstrumented(),
            _ =>
            {
                if (acquireCount == 2)
                {
                    l2DisposedBeforeThirdAcquire = l2.DisposeCount == 1;
                }

                return Task.FromResult(acquired[acquireCount++]);
            },
            (l, _) =>
            {
                if (l == l1)
                {
                    throw new FakeThrottleException(TimeSpan.FromMilliseconds(20));
                }

                ranOn = l;
                return Task.FromResult(1);
            },
            maxAttempts: 2);

        Assert.Equal(3, acquireCount);
        Assert.True(l2DisposedBeforeThirdAcquire, "the same-member lease must be released before the Retry-After wait");
        Assert.Same(l3, ranOn);
        Assert.All(acquired, l => Assert.Equal(1, l.DisposeCount));
    }

    [Fact]
    public async Task Throttle_OnSameNextMember_CancelDuringWait_HoldsNoLease()
    {
        var member = new FakeMember("a");
        var l1 = new FakeLease(member);
        var l2 = new FakeLease(member);
        using var cts = new CancellationTokenSource();

        var task = Run(
            Uninstrumented(), Sequence(l1, l2),
            (_, _) => throw new FakeThrottleException(TimeSpan.FromMinutes(10)),
            maxAttempts: 3, cancellationToken: cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, l1.DisposeCount);
        Assert.Equal(1, l2.DisposeCount);
    }

    [Fact]
    public async Task Throttle_ExhaustingAttempts_PropagatesLastThrottleInstance()
    {
        var last = new FakeThrottleException(TimeSpan.Zero);
        var attempt = 0;

        var actual = await Assert.ThrowsAsync<FakeThrottleException>(() => Run(
            Uninstrumented(),
            _ => Task.FromResult(new FakeLease(new($"m{attempt}"))),
            (_, _) => ++attempt == 3 ? throw last : throw new FakeThrottleException(TimeSpan.Zero),
            maxAttempts: 3));

        Assert.Same(last, actual);
        Assert.Equal(3, attempt);
    }

    // ----- Operation metrics (docs/adr/0024) -----

    [Fact]
    public async Task Success_RecordsAllDurations_OneAttemptAndCall_AndBalancesUpDownCounters()
    {
        var (recorder, collector, scope) = Instrumented("retrieve");
        using var _r = recorder;
        using var _c = collector;

        await Run(scope, Sequence(new FakeLease(new("member-1"))), (_, _) => Task.FromResult(1));

        var acquire = Assert.Single(collector.Of(AcquireDuration));
        var op = Assert.Single(collector.Of(OperationDuration));
        var total = Assert.Single(collector.Of(TotalDuration));
        var call = Assert.Single(collector.Of(Calls));
        Assert.Single(collector.Of(Attempts));
        Assert.Empty(collector.Of(Retries));
        Assert.Empty(collector.Of(RetryAfter));

        foreach (var m in new[] { acquire, op, total, call })
        {
            Assert.Equal(OutcomeSuccess, m.Tags[OutcomeTag]);
            Assert.Equal("pool-x", m.Tags[PoolNameTag]);
            Assert.Equal("retrieve", m.Tags[OperationNameTag]);
            Assert.Equal("member-1", m.Tags[MemberNameTag]);
            Assert.False(m.Tags.ContainsKey(ErrorTypeTag));
        }

        Assert.True(total.Value >= op.Value);
        Assert.Equal(0, collector.Sum(Active));
        Assert.Equal(0, collector.Sum(Waiting));
        Assert.Equal(2, collector.Of(Active).Count);
        Assert.Equal(2, collector.Of(Waiting).Count);
        Assert.False(collector.Of(Waiting)[0].Tags.ContainsKey(MemberNameTag)); // member unknown while waiting
    }

    [Fact]
    public async Task Error_RecordsErrorOutcomeAndTypeOnly_AndKeepsSameException()
    {
        var (recorder, collector, scope) = Instrumented();
        using var _r = recorder;
        using var _c = collector;
        var thrown = new InvalidOperationException("contains secret-record-id 1234");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Run(scope, Sequence(new FakeLease(new("m"))), (_, _) => throw thrown));

        Assert.Same(thrown, actual);
        var call = Assert.Single(collector.Of(Calls));
        Assert.Equal(OutcomeError, call.Tags[OutcomeTag]);
        Assert.Equal(typeof(InvalidOperationException).FullName, call.Tags[ErrorTypeTag]);
        Assert.Equal(OutcomeError, Assert.Single(collector.Of(Attempts)).Tags[OutcomeTag]);
        Assert.Equal(0, collector.Sum(Active));
    }

    [Fact]
    public async Task CallerCancellation_IsCanceled_ButUnrelatedOperationCanceledException_IsError()
    {
        var (recorder, collector, scope) = Instrumented();
        using var _r = recorder;
        using var _c = collector;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(
            scope, Sequence(new FakeLease(new("m"))),
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(1); },
            cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(
            scope, Sequence(new FakeLease(new("m"))),
            (_, _) => throw new TaskCanceledException("SDK HTTP timeout")));

        var outcomes = collector.Of(Calls).Select(m => m.Tags[OutcomeTag]).ToList();
        Assert.Equal(new object?[] { OutcomeCanceled, OutcomeError }, outcomes);
    }

    [Fact]
    public async Task AcquireFailure_RecordsAcquireAndCallErrors_WithoutMember_AndNoAttempt()
    {
        var (recorder, collector, scope) = Instrumented();
        using var _r = recorder;
        using var _c = collector;

        await Assert.ThrowsAsync<TimeoutException>(() => Run(scope, _ => throw new TimeoutException(), (_, _) => Task.FromResult(1)));

        var acquire = Assert.Single(collector.Of(AcquireDuration));
        Assert.Equal(OutcomeError, acquire.Tags[OutcomeTag]);
        Assert.False(acquire.Tags.ContainsKey(MemberNameTag));
        var call = Assert.Single(collector.Of(Calls));
        Assert.Equal(OutcomeError, call.Tags[OutcomeTag]);
        Assert.False(call.Tags.ContainsKey(MemberNameTag));
        Assert.Empty(collector.Of(Attempts));
        Assert.Equal(0, collector.Sum(Waiting));
    }

    [Fact]
    public async Task FinalThrottle_RecordsThrottledOutcome_AndCappedRetryAfter()
    {
        var (recorder, collector, scope) = Instrumented();
        using var _r = recorder;
        using var _c = collector;

        await Assert.ThrowsAsync<FakeThrottleException>(() => Run(
            scope, Sequence(new FakeLease(new("m"))),
            (_, _) => throw new FakeThrottleException(TimeSpan.FromMinutes(17)),
            maxAttempts: 1, maxRetryAfter: TimeSpan.FromSeconds(30)));

        Assert.Equal(30, Assert.Single(collector.Of(RetryAfter)).Value);
        Assert.Equal(OutcomeThrottled, Assert.Single(collector.Of(Calls)).Tags[OutcomeTag]);
        Assert.Equal(OutcomeThrottled, Assert.Single(collector.Of(Attempts)).Tags[OutcomeTag]);
        Assert.Empty(collector.Of(Retries)); // nothing was scheduled: attempts exhausted
    }

    [Fact]
    public async Task TwoThrottlesThenSuccess_RecordsThreeAttempts_TwoRetries_OneSuccessfulCall()
    {
        var (recorder, collector, scope) = Instrumented();
        using var _r = recorder;
        using var _c = collector;
        var attempt = 0;

        var result = await Run(
            scope,
            _ => Task.FromResult(new FakeLease(new($"m{attempt}"))), // different member each time: no wait
            (_, _) => ++attempt <= 2 ? throw new FakeThrottleException(TimeSpan.FromSeconds(2)) : Task.FromResult(7),
            maxAttempts: 5);

        Assert.Equal(7, result);
        var attempts = collector.Of(Attempts).Select(m => m.Tags[OutcomeTag]).ToList();
        Assert.Equal(new object?[] { OutcomeThrottled, OutcomeThrottled, OutcomeSuccess }, attempts);
        Assert.Equal(2, collector.Sum(Retries));
        Assert.Equal(new[] { 2.0, 2.0 }, collector.Of(RetryAfter).Select(m => m.Value));
        var call = Assert.Single(collector.Of(Calls));
        Assert.Equal(OutcomeSuccess, call.Tags[OutcomeTag]);
        Assert.Equal("m2", call.Tags[MemberNameTag]); // the member that finally served it
        Assert.Equal(3, collector.Of(AcquireDuration).Count);
        Assert.Equal(0, collector.Sum(Active));
        Assert.Equal(0, collector.Sum(Waiting));
    }

    [Fact]
    public async Task TagKeys_AreLimitedToTheDocumentedSet_AndNeverIncludeExceptionMessages()
    {
        var (recorder, collector, scope) = Instrumented();
        using var _r = recorder;
        using var _c = collector;

        await Run(scope, Sequence(new FakeLease(new("m")), new FakeLease(new("n"))), (l, _) =>
            l.Member.Name == "m" ? throw new FakeThrottleException(TimeSpan.Zero) : Task.FromResult(1), maxAttempts: 2);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Run(scope, Sequence(new FakeLease(new("m"))), (_, _) => throw new InvalidOperationException("secret-record-id 1234")));

        var allowed = new HashSet<string> { PoolNameTag, MemberNameTag, OperationNameTag, OutcomeTag, ErrorTypeTag };
        Assert.NotEmpty(collector.Measurements);
        foreach (var m in collector.Measurements)
        {
            Assert.Subset(allowed, m.Tags.Keys.ToHashSet());
            Assert.DoesNotContain(m.Tags.Values, v => v is string s && s.Contains("secret-record-id"));
        }
    }

    [Fact]
    public async Task ThrowingListener_DoesNotAlterOutcome()
    {
        var (recorder, collector, scope) = Instrumented(throwFromCallback: true);
        using var _r = recorder;
        using var _c = collector;
        var lease = new FakeLease(new("m"));

        Assert.Equal(5, await Run(scope, Sequence(lease), (_, _) => Task.FromResult(5)));
        Assert.Equal(1, lease.DisposeCount);

        var thrown = new InvalidOperationException();
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Run(scope, Sequence(new FakeLease(new("m"))), (_, _) => throw thrown));
        Assert.Same(thrown, actual);
        Assert.NotEmpty(collector.Measurements); // the listener really was invoked (and threw)
    }

    [Fact]
    public async Task NoListener_RecordsNothing_AndAllocatesNoMoreThanAPlainAcquireTryFinally()
    {
        var scope = Uninstrumented();
        Assert.False(scope.Recorder.IsEnabled);

        var lease = new FakeLease(new("m"));
        var leaseTask = Task.FromResult(lease);
        Func<CancellationToken, Task<FakeLease>> acquire = _ => leaseTask;
        Func<FakeLease, CancellationToken, Task<bool>> operation = (_, _) => Task.FromResult(true); // cached task

        async Task<bool> Baseline()
        {
            var l = await acquire(CancellationToken.None).ConfigureAwait(false);
            try
            {
                return await operation(l, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await l.DisposeAsync().ConfigureAwait(false);
            }
        }

        Task<bool> Instrumented() =>
            DataverseOperationExecutor.ExecuteAsync(scope, Accessors, acquire, operation, 1, null, CancellationToken.None);

        static long Measure(Func<Task<bool>> run)
        {
            for (var i = 0; i < 100; i++)
            {
                run().GetAwaiter().GetResult(); // warm up (JIT, tiering)
            }

            const int iterations = 1000;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                run().GetAwaiter().GetResult();
            }

            return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        }

        var baseline = Measure(Baseline);
        var instrumented = Measure(Instrumented);

        Assert.True(instrumented <= baseline, $"executor allocated {instrumented} B/call vs baseline {baseline} B/call with no listener");
        await Task.CompletedTask;
    }
}
