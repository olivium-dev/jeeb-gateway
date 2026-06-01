using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Contract-seam guard for the relocated courier-matching endpoint
/// (DELIVERY-SERVICE-RELOCATION-DESIGN.md §2.1). The rest of the suite stubs
/// <see cref="IDeliveryServiceClient"/>, so the REAL JSON deserialization of the
/// frozen delivery-service (Go) <b>snake_case</b> matching body is never
/// exercised there. This suite drives the REAL
/// <see cref="DeliveryServiceClient"/> against a fake
/// <see cref="HttpMessageHandler"/> that returns the LITERAL Go-shaped body,
/// locking the JSON seam.
///
/// delivery-service emits snake_case (<c>request_id</c>, <c>tier_id</c>,
/// <c>radius_km</c>, <c>notified_count</c>, <c>candidate_count</c>,
/// <c>candidates[].user_id/vehicle_type/distance_km/rating</c>, <c>elapsed_ms</c>).
/// The shared <c>JsonSerializerDefaults.Web</c> options are camelCase, so without
/// the explicit <c>[JsonPropertyName]</c> attributes on the result DTOs the
/// snake_case <c>request_id</c> would not bind onto the <c>required</c>
/// <see cref="DeliveryMatchingRunResult.RequestId"/> and STJ would throw on the
/// SUCCESS path — after delivery-service already ran matching + fanned out
/// offers — surfacing as an unhandled 500. These tests FAIL without those
/// attributes. (Same recurring seam bug as the handover OTP DTOs.)
/// </summary>
public class MatchingRunContractTests
{
    [Fact]
    public async Task RunMatchingAsync_Binds_The_Literal_Go_SnakeCase_Body()
    {
        // The LITERAL Go 200 body for POST /api/v1/matching/run (design §2.1).
        var client = ClientReturning(
            HttpStatusCode.OK,
            """
            {
              "request_id":"del_123","tier_id":"tier_express","radius_km":5,
              "notified_count":4,"candidate_count":9,
              "candidates":[{"user_id":"u1","vehicle_type":"car","distance_km":1.2,"rating":4.8}],
              "elapsed_ms":38
            }
            """);

        var result = await client.RunMatchingAsync(
            new DeliveryMatchingRunRequest { RequestId = "del_123", TenantId = "default" },
            CancellationToken.None);

        result.RequestId.Should().Be("del_123");
        result.TierId.Should().Be("tier_express");
        result.RadiusKm.Should().Be(5.0);
        result.NotifiedCount.Should().Be(4);
        result.CandidateCount.Should().Be(9);
        result.ElapsedMs.Should().Be(38);

        result.Candidates.Should().ContainSingle();
        var c = result.Candidates[0];
        c.UserId.Should().Be("u1");
        c.VehicleType.Should().Be("car");
        c.DistanceKm.Should().Be(1.2);
        c.Rating.Should().Be(4.8);
    }

    [Fact]
    public async Task RunMatchingAsync_Sends_SnakeCase_Request_Body()
    {
        // The REQUEST is consumed by Go and must be snake_case too. Capture the
        // serialized body the real client puts on the wire and assert the field
        // names delivery-service expects.
        string? sentBody = null;
        var handler = new CapturingHandler(
            HttpStatusCode.OK,
            """{"request_id":"r","tier_id":"t","radius_km":1,"notified_count":0,"candidate_count":0,"candidates":[],"elapsed_ms":1}""",
            body => sentBody = body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://delivery.test/") };
        var client = new DeliveryServiceClient(http);

        await client.RunMatchingAsync(new DeliveryMatchingRunRequest
        {
            RequestId = "del_1",
            PickupLat = 24.71,
            PickupLng = 46.67,
            TierId = "tier_express",
            AllowedVehicleTypes = new[] { "car", "motorbike" },
            TenantId = "default"
        }, CancellationToken.None);

        sentBody.Should().NotBeNull();
        sentBody!.Should().Contain("\"request_id\":\"del_1\"");
        sentBody.Should().Contain("\"pickup_lat\":24.71");
        sentBody.Should().Contain("\"pickup_lng\":46.67");
        sentBody.Should().Contain("\"tier_id\":\"tier_express\"");
        sentBody.Should().Contain("\"allowed_vehicle_types\":[\"car\",\"motorbike\"]");
        sentBody.Should().Contain("\"tenant_id\":\"default\"");
        // No camelCase leakage from the shared web-default options.
        sentBody.Should().NotContain("requestId");
        sentBody.Should().NotContain("pickupLat");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "unknown_vehicle")]
    [InlineData(HttpStatusCode.NotFound, "unknown_tier")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "tier_radius_non_positive")]
    public async Task RunMatchingAsync_NonSuccess_Maps_To_DeliveryMatchingException(
        HttpStatusCode status, string reason)
    {
        var client = ClientReturning(status, $$"""{"reason":"{{reason}}"}""");

        var act = async () => await client.RunMatchingAsync(
            new DeliveryMatchingRunRequest { TierId = "x", TenantId = "default" },
            CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<DeliveryMatchingException>()).Which;
        ex.StatusCode.Should().Be((int)status);
        ex.Reason.Should().Be(reason);
    }

    private static DeliveryServiceClient ClientReturning(HttpStatusCode status, string jsonBody)
    {
        var handler = new CapturingHandler(status, jsonBody, _ => { });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://delivery.test/") };
        return new DeliveryServiceClient(http);
    }

    /// <summary>
    /// Returns one fixed status + literal JSON body for every request, capturing
    /// the request body verbatim so the request-side snake_case seam is asserted
    /// against the EXACT bytes the client emits.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _jsonBody;
        private readonly Action<string> _onBody;

        public CapturingHandler(HttpStatusCode status, string jsonBody, Action<string> onBody)
        {
            _status = status;
            _jsonBody = jsonBody;
            _onBody = onBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                _onBody(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_jsonBody, Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
        }
    }
}
