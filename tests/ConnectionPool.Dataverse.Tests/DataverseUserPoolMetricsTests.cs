using Xunit;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Tests for <see cref="DataverseUserPoolMetrics"/> - the Phase 3 member-level gauges
/// (dop_hint, throttled, throttled_until_seconds, breaker_state). Uses a dummy-connection-string
/// <see cref="DataverseUserPool"/> (never connects live) plus <see cref="MetricCollector"/>, the
/// same real-<c>MeterListener</c> pattern <c>ConnectionPool.Metrics.Tests</c> uses.
/// </summary>
public class DataverseUserPoolMetricsTests
{
    [Fact]
    public void DopHint_NotEmitted_UntilObserved()
    {
        var meterName = Guid.NewGuid().ToString();
        var member = new DataverseUserPool("a", "dummy-a");
        using var collector = new MetricCollector(meterName);
        using var metrics = new DataverseUserPoolMetrics(member, breaker: null, meterName);

        collector.Collect();

        Assert.Empty(collector.Of("dataversepool.member.dop_hint"));
    }

    [Fact]
    public void Throttled_ReportsZero_WhenMemberNotThrottled()
    {
        var meterName = Guid.NewGuid().ToString();
        var member = new DataverseUserPool("a", "dummy-a");
        using var collector = new MetricCollector(meterName);
        using var metrics = new DataverseUserPoolMetrics(member, breaker: null, meterName);

        collector.Collect();

        var throttled = collector.Of("dataversepool.member.throttled");
        Assert.Single(throttled);
        Assert.Equal(0, throttled[0].Value);

        var remaining = collector.Of("dataversepool.member.throttled_until_seconds");
        Assert.Single(remaining);
        Assert.Equal(0, remaining[0].Value);
    }

    [Fact]
    public void Throttled_ReportsOne_AndPositiveRemainingSeconds_WhenMemberIsThrottled()
    {
        var meterName = Guid.NewGuid().ToString();
        var member = new DataverseUserPool("a", "dummy-a");
        member.ReportThrottled(TimeSpan.FromSeconds(30));

        using var collector = new MetricCollector(meterName);
        using var metrics = new DataverseUserPoolMetrics(member, breaker: null, meterName);

        collector.Collect();

        var throttled = collector.Of("dataversepool.member.throttled");
        Assert.Single(throttled);
        Assert.Equal(1, throttled[0].Value);

        var remaining = collector.Of("dataversepool.member.throttled_until_seconds");
        Assert.Single(remaining);
        Assert.True(remaining[0].Value is > 0 and <= 30);
    }

    [Fact]
    public void BreakerState_NotCreated_WhenBreakerIsNull()
    {
        var meterName = Guid.NewGuid().ToString();
        var member = new DataverseUserPool("a", "dummy-a");
        using var collector = new MetricCollector(meterName);
        using var metrics = new DataverseUserPoolMetrics(member, breaker: null, meterName);

        collector.Collect();

        Assert.Empty(collector.Of("dataversepool.member.breaker_state"));
    }

    [Fact]
    public void BreakerState_ReportsClosed_WhenBreakerHealthy()
    {
        var meterName = Guid.NewGuid().ToString();
        var member = new DataverseUserPool("a", "dummy-a");
        var breaker = new MemberCircuitBreaker(failureThreshold: 3, cooldownPeriod: TimeSpan.FromSeconds(30));

        using var collector = new MetricCollector(meterName);
        using var metrics = new DataverseUserPoolMetrics(member, breaker, meterName);

        collector.Collect();

        var state = collector.Of("dataversepool.member.breaker_state");
        Assert.Single(state);
        Assert.Equal((double)MemberCircuitState.Closed, state[0].Value);
    }

    [Fact]
    public void AllGauges_TagMemberNameCorrectly()
    {
        var meterName = Guid.NewGuid().ToString();
        var member = new DataverseUserPool("my-member", "dummy-a");
        using var collector = new MetricCollector(meterName);
        using var metrics = new DataverseUserPoolMetrics(member, breaker: null, meterName);

        collector.Collect();

        var throttled = collector.Of("dataversepool.member.throttled");
        Assert.Single(throttled);
        Assert.Equal("my-member", throttled[0].Tags[DataverseOperationMetrics.MemberNameTag]);
    }
}
