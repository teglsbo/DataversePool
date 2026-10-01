using ConnectionPool.Core;
using ConnectionPool.Dataverse;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Covers <see cref="DataverseClientOptions"/> validation and its wiring into
/// <see cref="DataverseServiceClientPolicy"/>'s constructor. This is the only part of the
/// MaxRetryCount/RetryPauseTime/UseExponentialRetryDelayForConcurrencyThrottle override feature
/// testable without a live Dataverse connection. The integration test below verifies real
/// ServiceClient option application and return scrubbing when credentials are available.
/// </summary>
public class DataverseClientOptionsTests
{
    [Fact]
    public void Validate_AllowsNullValues()
    {
        var options = new DataverseClientOptions();
        options.Validate(); // should not throw
    }

    [Fact]
    public void Validate_AllowsZero()
    {
        var options = new DataverseClientOptions { MaxRetryCount = 0, RetryPauseTime = TimeSpan.Zero };
        options.Validate(); // should not throw
    }

    [Fact]
    public void Validate_ThrowsOnNegativeMaxRetryCount()
    {
        var options = new DataverseClientOptions { MaxRetryCount = -1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void Validate_ThrowsOnNegativeRetryPauseTime()
    {
        var options = new DataverseClientOptions { RetryPauseTime = TimeSpan.FromSeconds(-1) };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validate_AllowsEitherUseExponentialRetryDelayValue(bool value)
    {
        var options = new DataverseClientOptions { UseExponentialRetryDelayForConcurrencyThrottle = value };
        options.Validate(); // should not throw - no range to violate for a bool
    }

    [Fact]
    public void PolicyConstructor_ValidatesClientOptionsBeforeTouchingConnectionString()
    {
        var invalidOptions = new DataverseClientOptions { MaxRetryCount = -5 };

        // Connection string is a dummy value - if this throws ArgumentOutOfRangeException (not a
        // connection failure), it proves validation runs eagerly in the constructor, before any
        // network attempt (which only happens lazily on first CreateAsync).
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DataverseServiceClientPolicy("AuthType=OAuth;", clientOptions: invalidOptions));
    }

    [Fact]
    public void PolicyConstructor_AcceptsUseExponentialRetryDelayWithoutThrowing()
    {
        var options = new DataverseClientOptions { UseExponentialRetryDelayForConcurrencyThrottle = true };

        // Should not throw during construction - the property itself has no invalid range, only
        // application to a real ServiceClient (untestable without a live connection) can fail.
        _ = new DataverseServiceClientPolicy("AuthType=OAuth;", clientOptions: options);
    }

    [Fact]
    public void PolicyConstructor_AcceptsWebApiAndSessionTrackingOptions()
    {
        var options = new DataverseClientOptions
        {
            UseWebApi = true,
            SessionTrackingId = Guid.NewGuid(),
        };

        _ = new DataverseServiceClientPolicy("AuthType=OAuth;", clientOptions: options);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ReturnedClient_RestoresConfiguredBaseline()
    {
        var connectionString = Environment.GetEnvironmentVariable("DVPOOL_IT_CONNECTION_STRING");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set DVPOOL_IT_CONNECTION_STRING.");

        var trackingId = Guid.NewGuid();
        var clientOptions = new DataverseClientOptions
        {
            UseWebApi = true,
            SessionTrackingId = trackingId,
            MaxRetryCount = 0,
        };
        await using var pool = new DataverseUserPool(
            "baseline-probe", connectionString!,
            new PoolOptions { MaxSize = 1 }, clientOptions: clientOptions);

        var lease = await pool.AcquireAsync();
        var client = lease.Resource;
        Assert.True(client.UseWebApi);
        Assert.Equal(trackingId, client.SessionTrackingId);
        Assert.Equal(0, client.MaxRetryCount);
        Assert.False(client.EnableAffinityCookie);

        client.CallerId = Guid.NewGuid();
        client.CallerAADObjectId = Guid.NewGuid();
        client.UseWebApi = false;
        client.SessionTrackingId = Guid.NewGuid();
        client.MaxRetryCount = 5;
        client.EnableAffinityCookie = true;
        await lease.DisposeAsync();

        await using var next = await pool.AcquireAsync();
        Assert.Same(client, next.Resource);
        Assert.Equal(Guid.Empty, next.Resource.CallerId);
        Assert.Null(next.Resource.CallerAADObjectId);
        Assert.True(next.Resource.UseWebApi);
        Assert.Equal(trackingId, next.Resource.SessionTrackingId);
        Assert.Equal(0, next.Resource.MaxRetryCount);
        Assert.False(next.Resource.EnableAffinityCookie);
    }
}
