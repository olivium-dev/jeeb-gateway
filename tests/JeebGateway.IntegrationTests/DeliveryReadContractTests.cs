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
