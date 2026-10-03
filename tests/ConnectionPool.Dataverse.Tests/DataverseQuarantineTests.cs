using System.Net;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// autoscaling.md §9.6 item 4: permanent-failure classification and member quarantine.
/// </summary>
public class DataverseQuarantineTests
{
    [Theory]
    [InlineData("AADSTS7000215: Invalid client secret provided.")]
    [InlineData("AADSTS7000222: The provided client secret keys are expired.")]
    [InlineData("AADSTS700016: Application with identifier 'x' was not found in the directory.")]
    [InlineData("error: invalid_client")]
    [InlineData("The user is not a member of the organization.")]
    public void IsPermanent_True_ForKnownAuthMarkers(string message)
    {
        Assert.True(DataverseFailureClassifier.IsPermanent(new InvalidOperationException(message), out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void IsPermanent_True_WhenMarkerIsInAnInnerException()
    {
        var ex = new InvalidOperationException("Failed to establish base Dataverse connection: ",
            new Exception("wrapper", new Exception("AADSTS7000215 bad secret")));

        Assert.True(DataverseFailureClassifier.IsPermanent(ex, out _));
    }

    [Fact]
    public void IsPermanent_True_ForAggregateContainingMarker()
    {
        var ex = new AggregateException(new TimeoutException(), new Exception("AADSTS7000222"));

        Assert.True(DataverseFailureClassifier.IsPermanent(ex, out _));
    }

    [Fact]
    public void IsPermanent_True_ForHttp401()
    {
        var ex = new HttpRequestException("nope", null, HttpStatusCode.Unauthorized);

        Assert.True(DataverseFailureClassifier.IsPermanent(ex, out var reason));
        Assert.Contains("401", reason);
    }

    [Theory]
    [InlineData("A task was canceled.")]
    [InlineData("The remote name could not be resolved")]
    [InlineData("503 Service Unavailable")]
    [InlineData("Failed to clone Dataverse ServiceClient: some unknown error")]
    public void IsPermanent_False_ForTransientOrUnknownErrors(string message)
    {
        Assert.False(DataverseFailureClassifier.IsPermanent(new InvalidOperationException(message), out _));
    }

    [Fact]
    public void IsPermanent_False_ForNullAndHttp503()
    {
        Assert.False(DataverseFailureClassifier.IsPermanent(null, out _));
        Assert.False(DataverseFailureClassifier.IsPermanent(
            new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable), out _));
    }

    [Fact]
    public void IsPermanent_HandlesCyclicInnerChains()
    {
        var a = new AggregateException(new Exception("x"));
        Assert.False(DataverseFailureClassifier.IsPermanent(a, out _));
    }

    [Fact]
    public async Task AcquireAsync_PermanentFactoryFailure_QuarantinesMember()
    {
        await using var member = new DataverseUserPool("m",
            _ => throw new InvalidOperationException("AADSTS7000215: Invalid client secret"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => member.AcquireAsync());

        Assert.True(member.IsQuarantined);
        Assert.NotNull(member.QuarantinedUntil);
        Assert.Equal("invalid client secret", member.QuarantineReason);
    }

    [Fact]
    public async Task AcquireAsync_TransientFactoryFailure_DoesNotQuarantine()
    {
        await using var member = new DataverseUserPool("m", _ => throw new InvalidOperationException("socket reset"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => member.AcquireAsync());

        Assert.False(member.IsQuarantined);
        Assert.Null(member.QuarantineReason);
    }

    [Fact]
    public void Quarantine_ExpiresAfterDuration_AndClearQuarantineEndsItEarly()
    {
        var member = new DataverseUserPool("m", "dummy") { QuarantineDuration = TimeSpan.FromMilliseconds(30) };
        member.Quarantine("test");
        Assert.True(member.IsQuarantined);

        Thread.Sleep(60);
        Assert.False(member.IsQuarantined);

        member.QuarantineDuration = TimeSpan.FromMinutes(5);
        member.Quarantine("test");
        Assert.True(member.IsQuarantined);
        member.ClearQuarantine();
        Assert.False(member.IsQuarantined);
    }

    [Fact]
    public void QuarantineDuration_Throws_WhenNotPositive()
    {
        var member = new DataverseUserPool("m", "dummy");

        Assert.Throws<ArgumentOutOfRangeException>(() => member.QuarantineDuration = TimeSpan.Zero);
    }

    [Fact]
    public void Strategies_SkipQuarantinedMember()
    {
        var bad = new DataverseUserPool("bad", "dummy-a");
        bad.Quarantine("test");
        var members = new[] { bad, new DataverseUserPool("ok", "dummy-b") };
        var stats = new[] { Stats(0), Stats(6) };

        ISlotSelectionStrategy[] strategies =
        {
            new HealthAwareRoundRobinSlotSelectionStrategy(),
            new LeastConnectionsSlotSelectionStrategy(),
            new HealthWeightedLeastConnectionsSlotSelectionStrategy(),
        };

        foreach (var strategy in strategies)
        {
            for (var i = 0; i < 3; i++)
            {
                var selection = strategy.SelectNext(members, stats);
                Assert.Equal("ok", selection.Member.Name);
                Assert.False(selection.AllMembersUnavailable);
            }
        }
    }

    [Fact]
    public void Metrics_QuarantinedGauge_ReportsReasonTag()
    {
        var meterName = Guid.NewGuid().ToString();
        var member = new DataverseUserPool("m", "dummy");
        member.Quarantine("invalid client secret");
        using var collector = new MetricCollector(meterName);
        using var metrics = new DataverseUserPoolMetrics(member, breaker: null, meterName);

        collector.Collect();

        var m = Assert.Single(collector.Of("dataversepool.member.quarantined"));
        Assert.Equal(1, m.Value);
        Assert.Equal("invalid client secret", m.Tags["quarantine_reason"]);
    }

    private static ConnectionPool.Core.PoolStats Stats(int leased) =>
        new(MaxSize: 8, CreatedCount: 0, IdleCount: 0, LeasedCount: leased,
            UnhealthyOrRecyclingCount: 0, WaitingCount: 0, ConsecutiveCreateFailures: 0);
}
