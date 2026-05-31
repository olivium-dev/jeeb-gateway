using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-BE-019 (JEB-55) contract-seam guard. The rest of the suite stubs
/// <see cref="IDeliveryServiceClient"/>, so the REAL JSON deserialization of the
/// frozen delivery-service (Go) handover bodies is never exercised. This suite
/// drives the REAL <see cref="DeliveryServiceClient"/> against a fake
/// <see cref="HttpMessageHandler"/> that returns the LITERAL Go-shaped
/// <b>snake_case</b> bodies, locking the JSON seam:
///
/// <list type="bullet">
///   <item>200 /otp/issue  → <c>{ "delivery_id":..., "issued":true }</c></item>
///   <item>200 /otp/verify → <c>{ "delivery_id":..., "verified":true, "status":"Done" }</c></item>
///   <item>401 /otp/verify → <c>{ "reason":"invalid_code", "attempts_remaining":2 }</c></item>
///   <item>423 /otp/verify → <c>{ "reason":"locked", "escalation_id":..., "locked_at":... }</c></item>
/// </list>
///
/// Before the fix, the success-path DTOs deserialized with
/// <c>JsonSerializerDefaults.Web</c> (camelCase), so the snake_case
/// <c>delivery_id</c> did not bind onto the <c>required</c> DeliveryId and STJ
/// threw a JsonException on the SUCCESS path — surfacing as an unhandled 500
/// even though delivery-service had already committed AtDoor→Done + settlement.
/// These tests FAIL without the explicit <c>[JsonPropertyName]</c> attributes.
/// </summary>
public class DeliveryHandoverContractTests
{
    private const string DeliveryId = "del_abc123";

    [Fact]
    public async Task IssueHandoverOtpAsync_Binds_SnakeCase_DeliveryId_And_Issued()
    {
        // The LITERAL Go body for 200 POST /api/v1/deliveries/{id}/otp/issue.
        var client = ClientReturning(
            HttpStatusCode.OK,
            $$"""{"delivery_id":"{{DeliveryId}}","issued":true}""");

        var result = await client.IssueHandoverOtpAsync(DeliveryId, codeHash: null, CancellationToken.None);

        result.DeliveryId.Should().Be(DeliveryId);
        result.Issued.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyHandoverOtpAsync_Binds_SnakeCase_DeliveryId_Verified_Status()
    {
        // The LITERAL Go body for 200 POST /api/v1/deliveries/{id}/otp/verify.
        var client = ClientReturning(
            HttpStatusCode.OK,
            $$"""{"delivery_id":"{{DeliveryId}}","verified":true,"status":"Done"}""");

        var result = await client.VerifyHandoverOtpAsync(DeliveryId, success: true, CancellationToken.None);

        result.DeliveryId.Should().Be(DeliveryId);
        result.Verified.Should().BeTrue();
        result.Status.Should().Be("Done");
    }

    [Fact]
    public async Task VerifyHandoverOtpAsync_401_Maps_To_DeliveryHandoverException_With_AttemptsRemaining()
    {
        // The LITERAL Go 401 body — invalid_code with attempts_remaining.
        var client = ClientReturning(
            HttpStatusCode.Unauthorized,
            """{"reason":"invalid_code","attempts_remaining":2}""");

        var act = async () =>
            await client.VerifyHandoverOtpAsync(DeliveryId, success: false, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<DeliveryHandoverException>()).Which;
        ex.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        ex.Reason.Should().Be("invalid_code");
        ex.AttemptsRemaining.Should().Be(2);
        ex.EscalationId.Should().BeNull();
    }

    [Fact]
    public async Task VerifyHandoverOtpAsync_423_Maps_To_DeliveryHandoverException_With_EscalationId_And_LockedAt()
    {
        // The LITERAL Go 423 body — locked with escalation_id + locked_at (RFC3339).
        var client = ClientReturning(
            HttpStatusCode.Locked,
            """{"reason":"locked","escalation_id":"esc_fixed","locked_at":"2026-05-31T12:00:00Z"}""");

        var act = async () =>
            await client.VerifyHandoverOtpAsync(DeliveryId, success: false, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<DeliveryHandoverException>()).Which;
        ex.StatusCode.Should().Be((int)HttpStatusCode.Locked);
        ex.Reason.Should().Be("locked");
        ex.EscalationId.Should().Be("esc_fixed");
        // P3 nit guard: the upstream locked_at is parsed and surfaced so the
        // controller can echo it instead of synthesizing a gateway clock value.
        ex.LockedAt.Should().Be(DateTimeOffset.Parse("2026-05-31T12:00:00Z"));
        ex.AttemptsRemaining.Should().BeNull();
    }

    private static DeliveryServiceClient ClientReturning(HttpStatusCode status, string jsonBody)
    {
        var handler = new SingleResponseHandler(status, jsonBody);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://delivery.test/") };
        return new DeliveryServiceClient(http);
    }

    /// <summary>
    /// Returns one fixed status + literal JSON body for every request. The body
    /// is written verbatim so the test asserts against the EXACT bytes
    /// delivery-service emits, not a round-tripped re-serialization.
    /// </summary>
    private sealed class SingleResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _jsonBody;

        public SingleResponseHandler(HttpStatusCode status, string jsonBody)
        {
            _status = status;
            _jsonBody = jsonBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_jsonBody, Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
            return Task.FromResult(response);
        }
    }
}
