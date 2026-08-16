using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using JeebGateway.Notifications;
using JeebGateway.service.ServicePushNotification;
using Xunit;

namespace JeebGateway.UnitTests;

// PHASE-V D3 — 26 POSTs for one request (1 x 201, 25 x 404): five device-less recipients,
// re-attempted five times each. A relay 404 is terminal; no retry can invent a device.
public class PushSendFailureClassificationTests
{
    [Fact]
    public void Relay_404_Is_NoRegisteredDevice_Not_A_Retryable_Failure()
        => Assert.Equal(
            PushSendFailureKind.NoRegisteredDevice,
            PushSendFailure.Classify(Api(404, "Push notification records for user u1 not found")));

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(408)]
    [InlineData(429)]
    public void Transient_Statuses_Stay_Retryable(int status)
        => Assert.Equal(PushSendFailureKind.Retryable, PushSendFailure.Classify(Api(status, "boom")));

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(422)]
    public void Caller_Side_Refusals_Are_Terminal(int status)
        => Assert.Equal(PushSendFailureKind.Terminal, PushSendFailure.Classify(Api(status, "nope")));

    [Fact]
    public void Transport_And_Timeout_Faults_Are_Retryable()
    {
        Assert.Equal(
            PushSendFailureKind.Retryable, PushSendFailure.Classify(new HttpRequestException("connect")));
        Assert.Equal(
            PushSendFailureKind.Retryable, PushSendFailure.Classify(new TaskCanceledException("budget")));
    }

    [Fact]
    public void An_Unrecognised_Fault_Is_Never_Downgraded_To_Expected()
        => Assert.Equal(
            PushSendFailureKind.Retryable,
            PushSendFailure.Classify(new InvalidOperationException("who knows")));

    private static ApiException Api(int status, string detail)
        => new("relay", status, "{\"detail\":\"" + detail + "\"}",
               new Dictionary<string, IEnumerable<string>>(), null!);
}
