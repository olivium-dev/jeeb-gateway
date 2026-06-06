using System.Net;
using FluentAssertions;
using JeebGateway.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// OTP-429 / S02 N2 — guards the standard outbound resilience pipeline's retry
/// predicate (<see cref="ServiceClientExtensions.ShouldRetryStandard"/>).
///
/// <para>Regression context: the default <c>HttpRetryStrategyOptions</c> predicate
/// treats HTTP 429 as transient AND honors <c>Retry-After</c>, so a shared
/// one-time-password verify-lockout (429 + Retry-After: 60) caused the gateway to
/// SLEEP ~60s per retry attempt and the inbound request to HANG / drop instead of
/// returning a clean 429 <c>too_many_attempts</c>. The predicate now forwards a 429
/// immediately (does not retry it) while still retrying genuine transient faults.
/// </para>
/// </summary>
public class StandardResilienceRetryPredicateTests
{
    [Fact]
    public void Does_Not_Retry_429_TooManyRequests()
    {
        // Negative path (the bug): a 429 must NOT be retried — it is a deliberate,
        // client-actionable throttle that the controller forwards verbatim.
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        var retry = ServiceClientExtensions.ShouldRetryStandard(exception: null, response: response);

        retry.Should().BeFalse(
            "a 429 is client-actionable, not transient; retrying it (with Retry-After) is what hung the OTP verify-lockout");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)] // 500
    [InlineData(HttpStatusCode.BadGateway)]          // 502
    [InlineData(HttpStatusCode.ServiceUnavailable)]  // 503
    [InlineData(HttpStatusCode.GatewayTimeout)]      // 504
    [InlineData(HttpStatusCode.RequestTimeout)]      // 408
    public void Retries_Transient_5xx_And_408(HttpStatusCode status)
    {
        // Happy path: genuine transient faults are still retried.
        using var response = new HttpResponseMessage(status);

        var retry = ServiceClientExtensions.ShouldRetryStandard(exception: null, response: response);

        retry.Should().BeTrue($"HTTP {(int)status} is transient and must remain retryable");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]           // 200 — success, never retried
    [InlineData(HttpStatusCode.BadRequest)]   // 400 — client error, not transient
    [InlineData(HttpStatusCode.Unauthorized)] // 401 — client error, not transient
    [InlineData(HttpStatusCode.NotFound)]     // 404 — client error, not transient
    public void Does_Not_Retry_2xx_Or_Non_429_4xx(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status);

        var retry = ServiceClientExtensions.ShouldRetryStandard(exception: null, response: response);

        retry.Should().BeFalse($"HTTP {(int)status} is not a transient fault and must not be retried");
    }

    [Fact]
    public void Retries_Network_Exception()
    {
        // A transport-level fault (no response) remains retryable, matching the
        // default IsTransient behaviour for HttpRequestException / timeouts.
        var retry = ServiceClientExtensions.ShouldRetryStandard(
            exception: new HttpRequestException("connection reset"),
            response: null);

        retry.Should().BeTrue("a network/transport fault is transient and must remain retryable");
    }
}
