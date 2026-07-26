using System.Net;
using FluentAssertions;
using JeebGateway.Extensions;
using Polly.Timeout;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// PUSH-BREAKER — guards the push client's circuit-breaker predicate
/// (<see cref="ServiceClientExtensions.ShouldBreakOnPushOutcome"/>).
///
/// <para>Regression context: the shared push service answers
/// <c>POST /api/v1/sent-payload/user/{id}</c> with HTTP 500 when every stored
/// device token for THAT ONE user is dead. Counting that per-recipient permanent
/// failure toward the breaker pinned the circuit open (133 opens / 132 half-opens
/// / 5 closes over 5h — Polly v8 re-opens on a single failed half-open probe, and
/// the poisoned recipient's next 500 was nearly always the probe), denying pushes
/// with <c>BrokenCircuitException</c> to unrelated users on the same named client.
/// Only that exact shape is excluded; every genuine outage signal still trips.</para>
/// </summary>
public class PushCircuitBreakerPredicateTests
{
    private const string PerUserSend = "http://push:10040/api/v1/sent-payload/user/b52eb018-3ece-44e9-856f-87f27ec32b7f";

    [Fact]
    public void Does_Not_Break_On_PerUser_DeadToken_500()
    {
        using var response = Respond(HttpStatusCode.InternalServerError, PerUserSend);

        var breaks = ServiceClientExtensions.ShouldBreakOnPushOutcome(exception: null, response: response);

        breaks.Should().BeFalse(
            "a per-user all-tokens-dead 500 says nothing about push-service health and must not " +
            "black out pushes for every other user");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]         // 502
    [InlineData(HttpStatusCode.ServiceUnavailable)] // 503
    [InlineData(HttpStatusCode.GatewayTimeout)]     // 504
    [InlineData(HttpStatusCode.RequestTimeout)]     // 408
    public void Breaks_On_Genuine_Outage_Statuses_Even_On_The_PerUser_Path(HttpStatusCode status)
    {
        using var response = Respond(status, PerUserSend);

        var breaks = ServiceClientExtensions.ShouldBreakOnPushOutcome(exception: null, response: response);

        breaks.Should().BeTrue($"HTTP {(int)status} is a real push-service outage signal, not a dead-token 500");
    }

    [Theory]
    [InlineData("http://push:10040/api/v1/sent-payload/broadcast")]
    [InlineData("http://push:10040/api/v1/sent-payload/topic/jeeb_jeebers")]
    [InlineData("http://push:10040/api/v1/sent-payload/device/device-1")]
    [InlineData("http://push:10040/api/v1/register/user")]
    [InlineData("http://push:10040/health")]
    public void Breaks_On_500_From_Any_Other_Push_Endpoint(string url)
    {
        using var response = Respond(HttpStatusCode.InternalServerError, url);

        var breaks = ServiceClientExtensions.ShouldBreakOnPushOutcome(exception: null, response: response);

        breaks.Should().BeTrue(
            "the exclusion is scoped to the per-user send endpoint; a 500 anywhere else still trips the breaker");
    }

    [Fact]
    public void Breaks_On_500_When_The_Originating_Request_Is_Unknown()
    {
        // Fail-safe: without the request we cannot prove it is the benign shape.
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var breaks = ServiceClientExtensions.ShouldBreakOnPushOutcome(exception: null, response: response);

        breaks.Should().BeTrue("an unattributable 500 must keep counting toward the breaker");
    }

    [Fact]
    public void Breaks_On_Transport_Exception()
    {
        var breaks = ServiceClientExtensions.ShouldBreakOnPushOutcome(
            exception: new HttpRequestException("connection refused"),
            response: null);

        breaks.Should().BeTrue("a connection failure is exactly the outage the breaker exists for");
    }

    [Fact]
    public void Breaks_On_Timeout()
    {
        var breaks = ServiceClientExtensions.ShouldBreakOnPushOutcome(
            exception: new TimeoutRejectedException("push timed out"),
            response: null);

        breaks.Should().BeTrue("a hung push service must still trip the breaker");
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]  // 201 — the push service's success code
    [InlineData(HttpStatusCode.NotFound)] // 404 — no such user, client-side fact
    public void Does_Not_Break_On_Success_Or_Client_Errors(HttpStatusCode status)
    {
        using var response = Respond(status, PerUserSend);

        var breaks = ServiceClientExtensions.ShouldBreakOnPushOutcome(exception: null, response: response);

        breaks.Should().BeFalse($"HTTP {(int)status} is not a transient upstream failure");
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string url) =>
        new(status)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Post, url),
        };
}
