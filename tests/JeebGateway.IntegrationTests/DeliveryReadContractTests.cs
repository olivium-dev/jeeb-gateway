using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class DeliveryReadContractTests
{
    [Fact]
    public async Task GetCanonicalDeliveryAsync_BindsStoredCoordinateObjects()
    {
        const string json = """
        {
          "delivery_id": "delivery-42",
          "client_id": "customer-nour",
          "jeeber_id": "jeeber-karim",
          "status": "InTransit",
          "tier_id": "flash",
          "pickup": { "lat": 33.8886, "lng": 35.4955 },
          "dropoff": { "lat": 33.9001, "lng": 35.5034 },
          "pickup_address": "Hamra, Beirut",
          "dropoff_address": "Achrafieh, Beirut",
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
        result!.Pickup.Should().NotBeNull();
        result.Pickup!.Lat.Should().Be(33.8886);
        result.Pickup.Lng.Should().Be(35.4955);
        result.Dropoff.Should().NotBeNull();
        result.Dropoff!.Lat.Should().Be(33.9001);
        result.Dropoff.Lng.Should().Be(35.5034);
        result.PickupAddress.Should().Be("Hamra, Beirut");
        result.DropoffAddress.Should().Be("Achrafieh, Beirut");
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
}
