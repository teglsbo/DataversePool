using System.Net;
using System.Net.Http;
using System.ServiceModel;
using ConnectionPool.Dataverse;
using Microsoft.PowerPlatform.Dataverse.Client.Exceptions;
using Microsoft.PowerPlatform.Dataverse.Client.HttpUtils;
using Microsoft.Xrm.Sdk;
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

    [Fact]
    public void TryGetRetryAfter_CapsAtDefaultMaxRetryAfter_WhenDataverseReportsAnExcessiveValue()
    {
        // Real-world Dataverse 429s have been observed reporting Retry-After as high as ~17 minutes
        // (1020s) - honoring that verbatim would exclude a group member from selection for a very
        // long time from a single throttle signal. The default cap protects against that.
        var ex = BuildThrottlingException(retryAfterSeconds: 1020);

        var found = DataverseThrottleDetector.TryGetRetryAfter(ex, out var retryAfter);

        Assert.True(found);
        Assert.Equal(DataverseThrottleDetector.DefaultMaxRetryAfter, retryAfter);
    }

    [Fact]
    public void TryGetRetryAfter_DoesNotCap_WhenReportedValueIsBelowTheDefaultCap()
    {
        var ex = BuildThrottlingException(retryAfterSeconds: 30);

        var found = DataverseThrottleDetector.TryGetRetryAfter(ex, out var retryAfter);

        Assert.True(found);
        Assert.Equal(TimeSpan.FromSeconds(30), retryAfter); // well under the 80s default cap - untouched
    }

    [Fact]
    public void TryGetRetryAfter_HonorsExplicitOverrideCap_InsteadOfTheDefault()
    {
        var ex = BuildThrottlingException(retryAfterSeconds: 1020);

        var found = DataverseThrottleDetector.TryGetRetryAfter(ex, TimeSpan.FromSeconds(10), out var retryAfter);

        Assert.True(found);
        Assert.Equal(TimeSpan.FromSeconds(10), retryAfter);
    }

    [Fact]
    public void TryGetRetryAfter_Throws_WhenMaxRetryAfterIsNotPositive()
    {
        var ex = BuildThrottlingException(retryAfterSeconds: 30);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => DataverseThrottleDetector.TryGetRetryAfter(ex, TimeSpan.Zero, out _));
    }

    // --- SOAP path: FaultException<OrganizationServiceFault> ---
    //
    // These codes and the boxed-TimeSpan ErrorDetails shape were confirmed live against a real
    // tenant (PLAN-2026-09-30.md, Phase 2): a 150-call real-write burst produced 63 faults with
    // ErrorCode -2147015898 (ConcurrentRequests) and a "Retry-After" ErrorDetails entry holding a
    // TimeSpan, ranging ~5 to ~13 minutes across the run.

    private static FaultException<OrganizationServiceFault> BuildSoapThrottlingFault(
        int errorCode,
        TimeSpan? retryAfter)
    {
        var fault = new OrganizationServiceFault { ErrorCode = errorCode };
        if (retryAfter is { } value)
        {
            fault.ErrorDetails["Retry-After"] = value;
        }

        return new FaultException<OrganizationServiceFault>(fault);
    }

    [Theory]
    [InlineData(unchecked((int)0x80072326))] // -2147015898: ConcurrentRequests (observed live)
    [InlineData(unchecked((int)0x80072322))] // -2147015902: NumberOfRequests
    [InlineData(unchecked((int)0x80072321))] // -2147015903: ExecutionTime
    public void TryGetRetryAfter_RecognizesEachSoapThrottlingFaultCode(int errorCode)
    {
        var ex = BuildSoapThrottlingFault(errorCode, TimeSpan.FromSeconds(30));

        var found = DataverseThrottleDetector.TryGetRetryAfter(ex, out var retryAfter);

        Assert.True(found);
        Assert.Equal(TimeSpan.FromSeconds(30), retryAfter);
    }

    [Fact]
    public void TryGetRetryAfter_UsesDefault_ForSoapFault_WhenRetryAfterDetailMissing()
    {
        var ex = BuildSoapThrottlingFault(unchecked((int)0x80072326), retryAfter: null);

        var found = DataverseThrottleDetector.TryGetRetryAfter(ex, out var retryAfter);

        Assert.True(found);
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void TryGetRetryAfter_CapsAtDefaultMaxRetryAfter_ForSoapFault()
    {
        // Observed live up to ~13 minutes - well above the 80s default cap.
        var ex = BuildSoapThrottlingFault(unchecked((int)0x80072326), TimeSpan.FromMinutes(13));

        var found = DataverseThrottleDetector.TryGetRetryAfter(ex, out var retryAfter);

        Assert.True(found);
        Assert.Equal(DataverseThrottleDetector.DefaultMaxRetryAfter, retryAfter);
    }

    [Fact]
    public void TryGetRetryAfter_ReturnsFalse_ForSoapFault_WithUnrelatedErrorCode()
    {
        var ex = BuildSoapThrottlingFault(errorCode: -2147220970, TimeSpan.FromMinutes(1));

        Assert.False(DataverseThrottleDetector.TryGetRetryAfter(ex, out _));
    }

    [Fact]
    public void TryGetRetryAfter_FindsSoapThrottlingFault_WrappedInsideAnotherException()
    {
        var inner = BuildSoapThrottlingFault(unchecked((int)0x80072326), TimeSpan.FromSeconds(30));
        var outer = new InvalidOperationException("wrapper", inner);

        var found = DataverseThrottleDetector.TryGetRetryAfter(outer, out var retryAfter);

        Assert.True(found);
        Assert.Equal(TimeSpan.FromSeconds(30), retryAfter);
    }
}
