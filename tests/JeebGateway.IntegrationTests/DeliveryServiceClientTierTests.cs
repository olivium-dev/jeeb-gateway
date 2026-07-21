using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests;

public class DeliveryServiceClientTierTests
{
    [Fact]
    public async Task ListTiersAsync_Maps_SnakeCase_TtlSeconds()
    {
        var client = ClientReturning(
            TierJson("0be308ce-01b5-5cb9-a3e8-9adb60668d9c", "flash", "\"ttl_seconds\":1800,\"ttl_minutes\":999,"));

        var tier = (await client.ListTiersAsync(CancellationToken.None)).Should().ContainSingle().Subject;

        tier.RequestTtlSeconds.Should().Be(1800);
    }

    [Fact]
    public async Task ListTiersAsync_Falls_Back_To_SnakeCase_TtlMinutes()
    {
        var client = ClientReturning(
            TierJson("efe0629b-0b50-555c-b182-4bd41fcd6507", "express", "\"ttl_minutes\":120,"));

        var tier = (await client.ListTiersAsync(CancellationToken.None)).Should().ContainSingle().Subject;

        tier.RequestTtlSeconds.Should().Be(7200);
    }

    [Fact]
    public async Task ListTiersAsync_Leaves_Zero_When_Upstream_Ttl_Is_Missing()
    {
        var client = ClientReturning(
            TierJson("2bd0d5df-db76-5d14-9e4d-741d60b2fa12", "standard", string.Empty));

        var tier = (await client.ListTiersAsync(CancellationToken.None)).Should().ContainSingle().Subject;

        tier.RequestTtlSeconds.Should().Be(0);
    }

    private static string TierJson(string id, string name, string ttlFields) => $$"""
        [{
          "id":"{{id}}",
          "name":"{{name}}",
          "slaHours":1,
          "radiusKm":3.0,
          {{ttlFields}}
          "commissionRate":0.10,
          "priceHint":"test",
          "createdAt":"2026-07-21T00:00:00Z",
          "updatedAt":"2026-07-21T00:00:00Z"
        }]
        """;

    private static DeliveryServiceClient ClientReturning(string jsonBody)
    {
        var http = new HttpClient(new SingleResponseHandler(jsonBody))
        {
            BaseAddress = new Uri("http://delivery.test/")
        };
        return new DeliveryServiceClient(http);
    }

    private sealed class SingleResponseHandler : HttpMessageHandler
    {
        private readonly string _jsonBody;

        public SingleResponseHandler(string jsonBody) => _jsonBody = jsonBody;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_jsonBody, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
    }
}
