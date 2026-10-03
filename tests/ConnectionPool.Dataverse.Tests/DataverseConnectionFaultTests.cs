using System.Net;
using System.Net.Sockets;
using System.ServiceModel;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>PLAN Phase 3 item 6: <see cref="DataverseFailureClassifier.IsConnectionFault"/>.</summary>
public class DataverseConnectionFaultTests
{
    public static TheoryData<Exception> TransportFaults => new()
    {
        new HttpRequestException("connection reset"),
        new SocketException(),
        new IOException("broken pipe"),
        new WebException("tls"),
        new TimeoutException(),
        new InvalidOperationException("wrapped", new HttpRequestException("inner")),
        new AggregateException(new InvalidOperationException("x"), new IOException("io")),
    };

    [Theory]
    [MemberData(nameof(TransportFaults))]
    public void TransportFailures_AreConnectionFaults(Exception exception) =>
        Assert.True(DataverseFailureClassifier.IsConnectionFault(exception));

    public static TheoryData<Exception?> NotFaults => new()
    {
        null,
        new InvalidOperationException("business rule"),
        new ArgumentException("bad input"),
        new OperationCanceledException(),
        new TaskCanceledException("http timeout", new TimeoutException()),
        new FaultException("record not found"),
        new AggregateException(new FaultException("plugin error"), new IOException("x")),
    };

    [Theory]
    [MemberData(nameof(NotFaults))]
    public void ServiceFaultsCancellationAndUnknownErrors_AreNotConnectionFaults(Exception? exception) =>
        Assert.False(DataverseFailureClassifier.IsConnectionFault(exception));

    [Fact]
    public void CyclicExceptionChain_Terminates()
    {
        var a = new AggregateException("a");
        Assert.False(DataverseFailureClassifier.IsConnectionFault(a));
    }
}
