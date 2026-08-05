using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class DeliveryReadContractTests
{
    [Fact]
    public async Task GetCanonicalDeliveryAsync_BindsRealGoScalarPickupShape()
    {
        const string json = """
        {
          "delivery_id": "delivery-42",
          "client_id": "customer-nour",
          "jeeber_id": "jeeber-karim",
          "status": "InTransit",
          "tier_id": "flash",
          "pickup_lat": 33.8886,
          "pickup_lng": 35.4955,
          "evidence_url": "s3://proof-of-delivery/delivery-42.jpg",
          "created_at": "2026-08-05T12:00:00Z"
        }
        """;
        var http = new HttpClient(new JsonResponseHandler(json))
        {
            BaseAddress = new Uri("http://delivery.test/")
        };
        var client = new DeliveryServiceClient(http);

        var result = await client.GetCanonicalDeliveryAsync("delivery-42", CancellationToken.None);

        result.Should().NotBeNull();
        result!.PickupLat.Should().Be(33.8886);
        result.PickupLng.Should().Be(35.4955);
        result.EvidenceUrl.Should().Be("s3://proof-of-delivery/delivery-42.jpg");
    }

    [Fact]
    public async Task CanonicalTransitionAsync_SerializesProofAsEvidenceUrl()
    {
        const string responseJson = """
        {
          "delivery_id": "delivery-42",
          "status": "AtDoor",
          "transition_id": "transition-1",
          "transitioned_at": "2026-08-05T12:00:00Z"
        }
        """;
        var handler = new RecordingJsonResponseHandler(responseJson);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://delivery.test/")
        };
        var client = new DeliveryServiceClient(http);

        await client.CanonicalTransitionAsync(
            "delivery-42",
            "AtDoor",
            "jeeber",
            "jeeber-karim",
            "jeeber",
            "s3://proof-of-delivery/delivery-42.jpg",
            CancellationToken.None);

        handler.RequestBody.Should().Contain(
            "\"evidence_url\":\"s3://proof-of-delivery/delivery-42.jpg\"");
    }

    private sealed class JsonResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
    }

    private sealed class RecordingJsonResponseHandler(string json) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }
}
