using System.Net;
using System.Net.Http;
using ConnectionPool.Dataverse;
using Microsoft.PowerPlatform.Dataverse.Client.Exceptions;
using Microsoft.PowerPlatform.Dataverse.Client.HttpUtils;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Verifies docs/adr/0008: throttle detection uses the real SDK exception/response-wrapper types
/// (not mocks/fakes of our own), so this test would fail if the SDK's actual shape ever changes.
/// </summary>
public class DataverseThrottleDetectorTests
{
    private static HttpOperationException BuildThrottlingException(int? retryAfterSeconds, string? httpDate = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)429);
        if (retryAfterSeconds is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfterSeconds.Value.ToString());
        }
        else if (httpDate is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", httpDate);
        }

        return new HttpOperationException("Number of requests exceeded the limit.")
        {
            Response = new HttpResponseMessageWrapper(response, content: null),
        };
    }

    [Fact]
    public void TryGetRetryAfter_ParsesSecondsForm_On429()
    {
        var ex = BuildThrottlingException(retryAfterSeconds: 30);

        var found = DataverseThrottleDetector.TryGetRetryAfter(ex, out var retryAfter);

        Assert.True(found);
        Assert.Equal(TimeSpan.FromSeconds(30), retryAfter);
    }

    [Fact]
    public void TryGetRetryAfter_ParsesHttpDateForm_On429()
    {
        var when = DateTimeOffset.UtcNow.AddSeconds(45);
        var ex = BuildThrottlingException(retryAfterSeconds: null, httpDate: when.ToString("R"));

        var found = DataverseThrottleDetector.TryGetRetryAfter(ex, out var retryAfter);

        Assert.True(found);
        Assert.InRange(retryAfter.TotalSeconds, 40, 46);
    }

    [Fact]
    public void TryGetRetryAfter_UsesDefault_WhenHeaderMissing()
    {
        var ex = BuildThrottlingException(retryAfterSeconds: null);

        var found = DataverseThrottleDetector.TryGetRetryAfter(ex, out var retryAfter);

        Assert.True(found);
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void TryGetRetryAfter_ReturnsFalse_ForNon429Status()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var ex = new HttpOperationException("boom") { Response = new HttpResponseMessageWrapper(response, content: null) };

        Assert.False(DataverseThrottleDetector.TryGetRetryAfter(ex, out _));
    }

    [Fact]
    public void TryGetRetryAfter_ReturnsFalse_ForUnrelatedException()
    {
        Assert.False(DataverseThrottleDetector.TryGetRetryAfter(new InvalidOperationException("unrelated"), out _));
    }

    [Fact]
    public void TryGetRetryAfter_FindsThrottlingException_WrappedInsideAnotherException()
    {
        var inner = BuildThrottlingException(retryAfterSeconds: 12);
        var outer = new InvalidOperationException("wrapper", inner);

        var found = DataverseThrottleDetector.TryGetRetryAfter(outer, out var retryAfter);

        Assert.True(found);
        Assert.Equal(TimeSpan.FromSeconds(12), retryAfter);
    }
}
