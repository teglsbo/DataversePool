using System.Net;
using System.ServiceModel;
using ConnectionPool.Core;
using Microsoft.PowerPlatform.Dataverse.Client.Exceptions;
using Microsoft.PowerPlatform.Dataverse.Client.HttpUtils;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

public class PoolSizingTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static DataverseUserPool NewMember(string name = "u", int maxSize = 8) =>
        new(name, "dummy", new PoolOptions { MaxSize = maxSize });

    private static PoolSizingOperationOutcome Throttle(ThrottleReason reason, TimeSpan? retryAfter = null) =>
        new("op", TimeSpan.FromMilliseconds(5), PoolSizingOutcomeKind.Throttled, reason, retryAfter, 0);

    private static PoolStats Stats(DataverseUserPool m) => m.GetStats();

    // ----- Aimd -----

    [Fact]
    public async Task Aimd_ConcurrencyThrottle_HalvesImmediately()
    {
        await using var m = NewMember();
        var aimd = new AimdPoolSizingStrategy(timeProvider: new ManualTime());
        aimd.GetInitialSize(m, 8);

        var d = aimd.OnOperationCompleted(m, Throttle(ThrottleReason.ConcurrentRequests));

        Assert.Equal(4, d?.TargetMaxSize);
    }

    [Theory]
    [InlineData(ThrottleReason.Unknown)]
    public async Task Aimd_WindowBudgetThrottles_DoNotShrink(ThrottleReason reason)
    {
        await using var m = NewMember();
        var aimd = new AimdPoolSizingStrategy(timeProvider: new ManualTime());
        aimd.GetInitialSize(m, 8);

        Assert.Null(aimd.OnOperationCompleted(m, Throttle(reason)));
    }

    [Fact]
    public async Task Aimd_BurstOfThrottles_DecreasesOnlyOncePerHoldoff()
    {
        await using var m = NewMember();
        var time = new ManualTime();
        var aimd = new AimdPoolSizingStrategy(timeProvider: time);
        aimd.GetInitialSize(m, 8);

        Assert.Equal(4, aimd.OnOperationCompleted(m, Throttle(ThrottleReason.ConcurrentRequests))?.TargetMaxSize);
        Assert.Null(aimd.OnOperationCompleted(m, Throttle(ThrottleReason.ConcurrentRequests)));

        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(2, aimd.OnOperationCompleted(m, Throttle(ThrottleReason.ConcurrentRequests))?.TargetMaxSize);
    }

    [Fact]
    public async Task Aimd_NeverShrinksBelowMinSize()
    {
        await using var m = NewMember(maxSize: 3);
        var time = new ManualTime();
        var aimd = new AimdPoolSizingStrategy(new AimdPoolSizingStrategyOptions { MinSize = 2 }, time);
        aimd.GetInitialSize(m, 3);

        Assert.Equal(2, aimd.OnOperationCompleted(m, Throttle(ThrottleReason.ConcurrentRequests))?.TargetMaxSize);
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(2, aimd.OnOperationCompleted(m, Throttle(ThrottleReason.ConcurrentRequests))?.TargetMaxSize);
    }

    [Fact]
    public async Task Aimd_RegrowsAdditivelyOnlyAfterCooldown_UpToCeiling()
    {
        await using var m = NewMember();
        var time = new ManualTime();
        var aimd = new AimdPoolSizingStrategy(timeProvider: time);
        aimd.GetInitialSize(m, 8);
        aimd.OnOperationCompleted(m, Throttle(ThrottleReason.ConcurrentRequests));

        Assert.Null(aimd.OnTick(m, Stats(m), null));

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(5, aimd.OnTick(m, Stats(m), null)?.TargetMaxSize);
        Assert.Equal(6, aimd.OnTick(m, Stats(m), null)?.TargetMaxSize);
        Assert.Equal(7, aimd.OnTick(m, Stats(m), null)?.TargetMaxSize);
        Assert.Equal(8, aimd.OnTick(m, Stats(m), null)?.TargetMaxSize);
        Assert.Null(aimd.OnTick(m, Stats(m), null));
    }

    [Fact]
    public async Task Aimd_CooldownHonoursLongerRetryAfter()
    {
        await using var m = NewMember();
        var time = new ManualTime();
        var aimd = new AimdPoolSizingStrategy(timeProvider: time);
        aimd.GetInitialSize(m, 8);
        aimd.OnOperationCompleted(m, Throttle(ThrottleReason.ConcurrentRequests, TimeSpan.FromSeconds(60)));

        time.Advance(TimeSpan.FromSeconds(45));
        Assert.Null(aimd.OnTick(m, Stats(m), null));
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.NotNull(aimd.OnTick(m, Stats(m), null));
    }

    [Fact]
    public void Aimd_InvalidOptions_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AimdPoolSizingStrategy(new AimdPoolSizingStrategyOptions { DecreaseFactor = 1.0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AimdPoolSizingStrategy(new AimdPoolSizingStrategyOptions { IncreaseStep = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AimdPoolSizingStrategy(new AimdPoolSizingStrategyOptions { MinSize = 5, Ceiling = 3 }));
    }

    // ----- Aimd probing -----

    private static AimdPoolSizingStrategy Prober(ManualTime time) =>
        new(new AimdPoolSizingStrategyOptions { ProbeInterval = TimeSpan.FromMinutes(10) }, time);

    [Fact]
    public async Task AimdProbe_IsOffByDefault()
    {
        await using var m = NewMember();
        var time = new ManualTime();
        var aimd = new AimdPoolSizingStrategy(timeProvider: time);
        aimd.GetInitialSize(m, 8);
        aimd.OnOperationCompleted(m, Ok(5, inFlight: 8));
        time.Advance(TimeSpan.FromHours(1));
        Assert.Null(aimd.OnTick(m, Stats(m), null));
    }

    [Fact]
    public async Task AimdProbe_NeedsQuietIntervalAndSaturation()
    {
        await using var m = NewMember();
        var time = new ManualTime();
        var aimd = Prober(time);
        aimd.GetInitialSize(m, 8);

        aimd.OnOperationCompleted(m, Ok(5, inFlight: 8));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(aimd.OnTick(m, Stats(m), null)); // not quiet long enough

        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(aimd.OnTick(m, Stats(m), null)); // quiet, but saw no saturation since last tick

        aimd.OnOperationCompleted(m, Ok(5, inFlight: 8));
        Assert.Equal(9, aimd.OnTick(m, Stats(m), null)?.TargetMaxSize);
    }

    [Fact]
    public async Task AimdProbe_UsedWithoutThrottle_RaisesCeiling()
    {
        await using var m = NewMember();
        var time = new ManualTime();
        var aimd = Prober(time);
        aimd.GetInitialSize(m, 8);
        time.Advance(TimeSpan.FromMinutes(11));
        aimd.OnOperationCompleted(m, Ok(5, inFlight: 8));
        Assert.Equal(9, aimd.OnTick(m, Stats(m), null)?.TargetMaxSize);

        aimd.OnOperationCompleted(m, Ok(5, inFlight: 9));
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(9, aimd.OnTick(m, Stats(m), null)?.TargetMaxSize);

        // New ceiling is 9: a later throttle halves from there.
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(5, aimd.OnOperationCompleted(m, Throttle(ThrottleReason.ConcurrentRequests))?.TargetMaxSize);
    }

    [Fact]
    public async Task AimdProbe_Unused_RevertsWithoutRaisingCeiling()
    {
        await using var m = NewMember();
        var time = new ManualTime();
        var aimd = Prober(time);
        aimd.GetInitialSize(m, 8);
        time.Advance(TimeSpan.FromMinutes(11));
        aimd.OnOperationCompleted(m, Ok(5, inFlight: 8));
        aimd.OnTick(m, Stats(m), null);

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(8, aimd.OnTick(m, Stats(m), null)?.TargetMaxSize);
    }

    [Fact]
    public async Task AimdProbe_ThrottleDuringProbe_RevertsToProvenSizeWithoutHalving()
    {
        await using var m = NewMember();
        var time = new ManualTime();
        var aimd = Prober(time);
        aimd.GetInitialSize(m, 8);
        time.Advance(TimeSpan.FromMinutes(11));
        aimd.OnOperationCompleted(m, Ok(5, inFlight: 8));
        aimd.OnTick(m, Stats(m), null);

        var d = aimd.OnOperationCompleted(m, Throttle(ThrottleReason.ConcurrentRequests));

        Assert.Equal(8, d?.TargetMaxSize);
    }

    [Fact]
    public async Task AimdProbe_NeverExceedsMaxProbedSize()
    {
        await using var m = NewMember(maxSize: 8);
        var time = new ManualTime();
        var aimd = new AimdPoolSizingStrategy(new AimdPoolSizingStrategyOptions { ProbeInterval = TimeSpan.FromMinutes(1), MaxProbedSize = 9 }, time);
        aimd.GetInitialSize(m, 8);
        time.Advance(TimeSpan.FromMinutes(2));
        aimd.OnOperationCompleted(m, Ok(5, inFlight: 8));
        aimd.OnTick(m, Stats(m), null); // probe to 9
        aimd.OnOperationCompleted(m, Ok(5, inFlight: 9));
        time.Advance(TimeSpan.FromSeconds(61));
        aimd.OnTick(m, Stats(m), null); // accepted

        time.Advance(TimeSpan.FromMinutes(2));
        aimd.OnOperationCompleted(m, Ok(5, inFlight: 9));
        Assert.Null(aimd.OnTick(m, Stats(m), null));
    }

    // ----- Pacer -----

    [Fact]
    public async Task Pacer_NoLimit_NeverWaits()
    {
        var pacer = new MemberRequestPacer(TimeProvider.System);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            await pacer.WaitAsync(CancellationToken.None);
        }

        Assert.True(sw.ElapsedMilliseconds < 500);
    }

    [Fact]
    public async Task Pacer_OverLimit_WaitsForWindowToSlide()
    {
        var pacer = new MemberRequestPacer(TimeProvider.System);
        pacer.Configure(2, TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await pacer.WaitAsync(CancellationToken.None);
        await pacer.WaitAsync(CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds < 200);

        await pacer.WaitAsync(CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds >= 250, $"waited only {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Pacer_WaitIsCancellable()
    {
        var pacer = new MemberRequestPacer(TimeProvider.System);
        pacer.Configure(1, TimeSpan.FromMinutes(5));
        await pacer.WaitAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pacer.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task Aimd_RequestCountThrottle_CapsRateNotConcurrency_ThenRelaxes()
    {
        await using var m = NewMember();
        var time = new ManualTime();
        var aimd = new AimdPoolSizingStrategy(timeProvider: time);
        aimd.GetInitialSize(m, 8);
        for (var i = 0; i < 99; i++)
        {
            aimd.OnOperationCompleted(m, Ok(5));
        }

        var d = aimd.OnOperationCompleted(m, Throttle(ThrottleReason.RequestCount));

        Assert.Equal(8, d?.TargetMaxSize);
        Assert.Equal(80, d?.MaxRequestsPerWindow); // 100 sent * 0.8
        Assert.Equal(TimeSpan.FromMinutes(5), d?.SampleWindow);

        Assert.Null(aimd.OnTick(m, Stats(m), null)); // still cooling down
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(88, aimd.OnTick(m, Stats(m), null)?.MaxRequestsPerWindow);

        for (var i = 0; i < 30; i++)
        {
            d = aimd.OnTick(m, Stats(m), null);
        }

        Assert.Null(d?.MaxRequestsPerWindow); // grown past the drop point: limit removed
    }

    [Fact]
    public async Task Aimd_SizeDecisions_KeepRepeatingActiveRateLimit()
    {
        await using var m = NewMember();
        var time = new ManualTime();
        var aimd = new AimdPoolSizingStrategy(timeProvider: time);
        aimd.GetInitialSize(m, 8);
        aimd.OnOperationCompleted(m, Throttle(ThrottleReason.RequestCount));
        time.Advance(TimeSpan.FromSeconds(11));

        var d = aimd.OnOperationCompleted(m, Throttle(ThrottleReason.ConcurrentRequests));

        Assert.Equal(4, d?.TargetMaxSize);
        Assert.NotNull(d?.MaxRequestsPerWindow);
    }

    [Fact]
    public async Task Composite_PacingTakesTightestLimit()
    {
        await using var m = NewMember();
        var a = new PacingScripted(100);
        var b = new PacingScripted(40);
        var c = new CompositePoolSizingStrategy(new IPoolSizingStrategy[] { a, b }, PoolSizingCombineMode.Max);
        c.GetInitialSize(m, 8);

        var d = c.OnTick(m, Stats(m), null);

        Assert.Equal(40, d?.MaxRequestsPerWindow);
    }

    private sealed class PacingScripted(int limit) : IPoolSizingStrategy
    {
        public int GetInitialSize(DataverseUserPool member, int configuredMaxSize) => configuredMaxSize;
        public PoolSizingDecision? OnTick(DataverseUserPool member, PoolStats currentStats, PoolSizingDecision? lastDecision) =>
            new(8, limit, null, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Controller_AppliesPacingLimit_AndPacesAttempts()
    {
        await using var m = NewMember();
        var s = new PacingScripted(1);
        using var controller = new PoolSizingController(new[] { m }, new PoolSizingOptions { Strategy = s });
        controller.Tick();

        await controller.BeforeAttemptAsync(m, CancellationToken.None);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await controller.BeforeAttemptAsync(m, cts.Token));
    }

    // ----- Execution-time pacing -----

    [Fact]
    public async Task Aimd_ExecutionTimeThrottle_CapsBusyTimeNotConcurrency_ThenRelaxes()
    {
        await using var m = NewMember();
        var time = new ManualTime();
        var aimd = new AimdPoolSizingStrategy(timeProvider: time);
        aimd.GetInitialSize(m, 8);
        for (var i = 0; i < 100; i++)
        {
            aimd.OnOperationCompleted(m, Ok(1000)); // 100 s of busy time
        }

        var d = aimd.OnOperationCompleted(m, Throttle(ThrottleReason.ExecutionTime));

        Assert.Equal(8, d?.TargetMaxSize);
        Assert.Equal(TimeSpan.FromSeconds(80), d?.MaxExecutionTimePerWindow);
        Assert.Null(d?.MaxRequestsPerWindow);
        Assert.Equal(TimeSpan.FromMinutes(5), d?.SampleWindow);

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(TimeSpan.FromSeconds(88), aimd.OnTick(m, Stats(m), null)?.MaxExecutionTimePerWindow);

        PoolSizingDecision? last = null;
        for (var i = 0; i < 30; i++)
        {
            last = aimd.OnTick(m, Stats(m), null);
        }

        Assert.Null(last?.MaxExecutionTimePerWindow);
    }

    [Fact]
    public async Task Pacer_ExecutionBudget_DelaysUntilBusyTimeLeavesWindow()
    {
        var pacer = new MemberRequestPacer(TimeProvider.System);
        pacer.Configure(null, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100));
        await pacer.WaitAsync(CancellationToken.None);
        pacer.RecordExecution(TimeSpan.FromMilliseconds(150)); // budget (100 ms) now exhausted

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await pacer.WaitAsync(CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds >= 250, $"waited only {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Pacer_ClearingExecutionLimit_StopsDelaying()
    {
        var pacer = new MemberRequestPacer(TimeProvider.System);
        pacer.Configure(null, TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(1));
        pacer.RecordExecution(TimeSpan.FromSeconds(10));
        pacer.Configure(null, TimeSpan.FromMinutes(5), null);
        await pacer.WaitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Composite_ExecutionBudget_TakesTightest()
    {
        await using var m = NewMember();
        var c = new CompositePoolSizingStrategy(new ExecScripted(60), new ExecScripted(20));
        c.GetInitialSize(m, 8);
        Assert.Equal(TimeSpan.FromSeconds(20), c.OnTick(m, Stats(m), null)?.MaxExecutionTimePerWindow);
    }

    private sealed class ExecScripted(int seconds) : IPoolSizingStrategy
    {
        public int GetInitialSize(DataverseUserPool member, int configuredMaxSize) => configuredMaxSize;
        public PoolSizingDecision? OnTick(DataverseUserPool member, PoolStats currentStats, PoolSizingDecision? lastDecision) =>
            new(8, null, TimeSpan.FromSeconds(seconds), TimeSpan.FromMinutes(5));
    }

    // ----- Gradient -----

    private static PoolSizingOperationOutcome Ok(double ms, int inFlight = 8, string op = "op") =>
        new(op, TimeSpan.FromMilliseconds(ms), PoolSizingOutcomeKind.Success, null, null, inFlight);

    private static void Feed(GradientPoolSizingStrategy g, DataverseUserPool m, double ms, int n = 10, int inFlight = 8, string op = "op")
    {
        for (var i = 0; i < n; i++)
        {
            g.OnOperationCompleted(m, Ok(ms, inFlight, op));
        }
    }

    [Fact]
    public async Task Gradient_SlowerThanMinRtt_Shrinks()
    {
        await using var m = NewMember();
        var g = new GradientPoolSizingStrategy(new GradientPoolSizingStrategyOptions { Smoothing = 1 }, new ManualTime());
        g.GetInitialSize(m, 8);
        Feed(g, m, 100);
        Assert.Equal(8, g.OnTick(m, Stats(m), null)?.TargetMaxSize); // baseline window, headroom capped at ceiling

        Feed(g, m, 400);
        Assert.True(g.OnTick(m, Stats(m), null)?.TargetMaxSize < 8);
    }

    [Fact]
    public async Task Gradient_TooFewSamples_HasNoOpinion()
    {
        await using var m = NewMember();
        var g = new GradientPoolSizingStrategy(new GradientPoolSizingStrategyOptions { Smoothing = 1 }, new ManualTime());
        g.GetInitialSize(m, 8);
        Feed(g, m, 100, n: 3);
        Assert.Null(g.OnTick(m, Stats(m), null));
    }

    [Fact]
    public async Task Gradient_WorstOperationWins()
    {
        await using var m = NewMember();
        var g = new GradientPoolSizingStrategy(new GradientPoolSizingStrategyOptions { Smoothing = 1 }, new ManualTime());
        g.GetInitialSize(m, 8);
        Feed(g, m, 100, op: "fast");
        Feed(g, m, 100, op: "slow");
        g.OnTick(m, Stats(m), null);

        Feed(g, m, 100, op: "fast");
        Feed(g, m, 500, op: "slow");
        Assert.True(g.OnTick(m, Stats(m), null)?.TargetMaxSize < 8);
    }

    [Fact]
    public async Task Gradient_IdleMember_DoesNotGrow()
    {
        await using var m = NewMember(maxSize: 8);
        var g = new GradientPoolSizingStrategy(new GradientPoolSizingStrategyOptions { Ceiling = 16 }, new ManualTime());
        g.GetInitialSize(m, 8);
        Feed(g, m, 100, inFlight: 1);
        Assert.Equal(8, g.OnTick(m, Stats(m), null)?.TargetMaxSize);

        var busy = NewMember("busy", 8);
        var g2 = new GradientPoolSizingStrategy(new GradientPoolSizingStrategyOptions { Ceiling = 16 }, new ManualTime());
        g2.GetInitialSize(busy, 8);
        Feed(g2, busy, 100, inFlight: 8);
        Assert.True(g2.OnTick(busy, Stats(busy), null)?.TargetMaxSize > 8);
        await busy.DisposeAsync();
    }

    [Fact]
    public async Task Gradient_IgnoresFailuresAndThrottles()
    {
        await using var m = NewMember();
        var g = new GradientPoolSizingStrategy(timeProvider: new ManualTime());
        g.GetInitialSize(m, 8);
        for (var i = 0; i < 20; i++)
        {
            g.OnOperationCompleted(m, Throttle(ThrottleReason.ConcurrentRequests));
        }

        Assert.Null(g.OnTick(m, Stats(m), null));
    }

    [Fact]
    public void Gradient_InvalidOptions_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GradientPoolSizingStrategy(new GradientPoolSizingStrategyOptions { Smoothing = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GradientPoolSizingStrategy(new GradientPoolSizingStrategyOptions { MinSamples = 0 }));
    }

    // ----- DopHint / Fixed -----

    [Fact]
    public async Task DopHint_NoHintObserved_HasNoOpinion()
    {
        await using var m = NewMember();
        var s = new DopHintPoolSizingStrategy();
        Assert.Equal(8, s.GetInitialSize(m, 8));
        Assert.Null(s.OnTick(m, Stats(m), null));
    }

    [Fact]
    public void DopHint_InvalidOptions_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DopHintPoolSizingStrategy(new DopHintPoolSizingStrategyOptions { Multiplier = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DopHintPoolSizingStrategy(new DopHintPoolSizingStrategyOptions { Floor = 4, Ceiling = 2 }));
    }

    [Fact]
    public async Task Fixed_ReturnsConfiguredSize_AndNeverDecides()
    {
        await using var m = NewMember();
        IPoolSizingStrategy s = new FixedPoolSizingStrategy();
        Assert.Equal(8, s.GetInitialSize(m, 8));
        Assert.Null(s.OnOperationCompleted(m, Throttle(ThrottleReason.ConcurrentRequests)));
        Assert.Null(s.OnTick(m, Stats(m), null));
    }

    // ----- Composite -----

    private sealed class Scripted(int initial) : IPoolSizingStrategy
    {
        public int? NextOnOp;
        public int? NextOnTick;
        public int GetInitialSize(DataverseUserPool member, int configuredMaxSize) => initial;
        public PoolSizingDecision? OnOperationCompleted(DataverseUserPool member, PoolSizingOperationOutcome outcome) =>
            NextOnOp is { } v ? new PoolSizingDecision(v) : null;
        public PoolSizingDecision? OnTick(DataverseUserPool member, PoolStats currentStats, PoolSizingDecision? lastDecision) =>
            NextOnTick is { } v ? new PoolSizingDecision(v) : null;
    }

    [Fact]
    public async Task Composite_Min_RemembersSilentChildsEarlierVote()
    {
        await using var m = NewMember();
        var low = new Scripted(8);
        var high = new Scripted(8);
        var c = new CompositePoolSizingStrategy(low, high);
        Assert.Equal(8, c.GetInitialSize(m, 8));

        low.NextOnOp = 3;
        Assert.Equal(3, c.OnOperationCompleted(m, Throttle(ThrottleReason.ConcurrentRequests))?.TargetMaxSize);

        low.NextOnOp = null;
        high.NextOnTick = 6;
        Assert.Equal(3, c.OnTick(m, Stats(m), null)?.TargetMaxSize);
    }

    [Fact]
    public async Task Composite_Max_TakesLargest_AndNoOpinionYieldsNull()
    {
        await using var m = NewMember();
        var a = new Scripted(4);
        var b = new Scripted(6);
        var c = new CompositePoolSizingStrategy(new IPoolSizingStrategy[] { a, b }, PoolSizingCombineMode.Max);
        Assert.Equal(6, c.GetInitialSize(m, 8));
        Assert.Null(c.OnTick(m, Stats(m), null));
        a.NextOnTick = 9;
        Assert.Equal(9, c.OnTick(m, Stats(m), null)?.TargetMaxSize);
    }

    [Fact]
    public void Composite_RequiresChildren() =>
        Assert.Throws<ArgumentException>(() => new CompositePoolSizingStrategy(Array.Empty<IPoolSizingStrategy>()));

    // ----- Controller / pool wiring -----

    [Fact]
    public async Task Controller_AppliesDecisions_ClampedToFloorAndCeiling()
    {
        await using var m = NewMember(maxSize: 8);
        var s = new Scripted(8);
        using var controller = new PoolSizingController(new[] { m }, new PoolSizingOptions { Strategy = s, MinSizeFloor = 2, MaxSizeCeiling = 10 });

        s.NextOnOp = 1;
        controller.Record(m, Throttle(ThrottleReason.ConcurrentRequests));
        Assert.Equal(2, m.MaxSize);

        s.NextOnOp = 99;
        controller.Record(m, Throttle(ThrottleReason.ConcurrentRequests));
        Assert.Equal(10, m.MaxSize);
    }

    [Fact]
    public async Task Controller_InitialSizeIsApplied_AndTickDrivesStrategy()
    {
        await using var m = NewMember(maxSize: 8);
        var s = new Scripted(5);
        using var controller = new PoolSizingController(new[] { m }, new PoolSizingOptions { Strategy = s });
        Assert.Equal(5, m.MaxSize);

        s.NextOnTick = 7;
        controller.Tick();
        Assert.Equal(7, m.MaxSize);
    }

    [Fact]
    public async Task Controller_FaultyStrategyOnTick_DoesNotThrow()
    {
        await using var m = NewMember();
        using var controller = new PoolSizingController(new[] { m }, new PoolSizingOptions { Strategy = new Throwing() });
        controller.Tick();
    }

    private sealed class Throwing : IPoolSizingStrategy
    {
        public int GetInitialSize(DataverseUserPool member, int configuredMaxSize) => configuredMaxSize;
        public PoolSizingDecision? OnTick(DataverseUserPool member, PoolStats currentStats, PoolSizingDecision? lastDecision) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Pool_WithFixedOrNoOptions_HasNoSink_AndAimdHasOne()
    {
        var a = NewMember("a");
        await using var none = new DataversePool(a);
        Assert.Null(none.SizingSink);

        var b = NewMember("b");
        await using var fixedPool = new DataversePool(b, sizingOptions: new PoolSizingOptions());
        Assert.Null(fixedPool.SizingSink);

        var c = NewMember("c");
        await using var aimd = new DataversePool(c, sizingOptions: new PoolSizingOptions { Strategy = new AimdPoolSizingStrategy() });
        Assert.NotNull(aimd.SizingSink);
    }

    [Fact]
    public void Options_Validate_RejectsBadValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PoolSizingOptions { TickInterval = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PoolSizingOptions { MinSizeFloor = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PoolSizingOptions { MinSizeFloor = 4, MaxSizeCeiling = 2 }.Validate());
    }

    // ----- Throttle reason decoding -----

    private static FaultException<OrganizationServiceFault> Fault(uint code) =>
        new(new OrganizationServiceFault { ErrorCode = unchecked((int)code) }, "fault");

    [Theory]
    [InlineData(0x80072322u, ThrottleReason.RequestCount)]
    [InlineData(0x80072321u, ThrottleReason.ExecutionTime)]
    [InlineData(0x80072326u, ThrottleReason.ConcurrentRequests)]
    public void ThrottleReason_SoapFaultCodes_AreDecoded(uint code, ThrottleReason expected)
    {
        Assert.True(DataverseThrottleDetector.TryGetThrottleReason(Fault(code), out var reason));
        Assert.Equal(expected, reason);
    }

    [Theory]
    [InlineData("Number of requests exceeded the limit of 6000", ThrottleReason.RequestCount)]
    [InlineData("Combined execution time exceeded the limit", ThrottleReason.ExecutionTime)]
    [InlineData("Number of concurrent requests exceeded the limit of 52", ThrottleReason.ConcurrentRequests)]
    [InlineData("something else", ThrottleReason.Unknown)]
    public void ThrottleReason_WebApi429_IsDecodedFromMessage(string message, ThrottleReason expected)
    {
        var ex = new HttpOperationException(message)
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage((HttpStatusCode)429), content: null),
        };
        Assert.True(DataverseThrottleDetector.TryGetThrottleReason(ex, out var reason));
        Assert.Equal(expected, reason);
    }

    [Fact]
    public void ThrottleReason_WebApi429_IsDecodedFromResponseBody_AsSeenLive()
    {
        // Verbatim shape captured live: the SDK's message is generic, the body carries the code.
        var ex = new HttpOperationException("Operation returned an invalid status code 'TooManyRequests'")
        {
            Response = new HttpResponseMessageWrapper(
                new HttpResponseMessage((HttpStatusCode)429),
                content: "{\"error\":{\"code\":\"0x80072326\",\"message\":\"Number of concurrent requests exceeded the limit of 100.\"}}"),
        };
        Assert.True(DataverseThrottleDetector.TryGetThrottleReason(ex, out var reason));
        Assert.Equal(ThrottleReason.ConcurrentRequests, reason);
    }

    [Fact]
    public void ThrottleReason_NonThrottle_ReturnsFalse() =>
        Assert.False(DataverseThrottleDetector.TryGetThrottleReason(new InvalidOperationException(), out _));
}
