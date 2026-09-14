using ConnectionPool.Dataverse;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Covers <see cref="DataverseClientOptions"/> validation and its wiring into
/// <see cref="DataverseServiceClientPolicy"/>'s constructor. This is the only part of the
/// MaxRetryCount/RetryPauseTime override feature testable without a live Dataverse connection -
/// see the class docs on <see cref="DataverseServiceClientPolicy"/> for why the actual override
/// application (base client + clone) cannot be unit-tested (ServiceClient requires a live
/// connection and cannot be subclassed/mocked).
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
}
