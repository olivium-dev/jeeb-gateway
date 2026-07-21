using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task ListExpiredDeliveriesAsync_Parses_Envelope()
    {
        var client = ClientReturning("""
            {
              "since":"2026-07-21T18:02:17Z",
              "count":2,
              "deliveries":[
                {
                  "delivery_id":"31087407-0b18-405e-b01e-f3a6dbd1cbf5",
                  "client_id":"client-1",
                  "tier_id":"0be308ce-01b5-5cb9-a3e8-9adb60668d9c",
                  "created_at":"2026-07-21T17:33:04.454578Z",
                  "expired_at":"2026-07-21T18:03:04.454578Z"
                },
                {
                  "delivery_id":"delivery-2",
                  "created_at":"2026-07-21T17:40:00Z",
                  "expired_at":"2026-07-21T18:10:00Z"
                }
              ]
            }
            """);

        var rows = await client.ListExpiredDeliveriesAsync(
            DateTimeOffset.Parse("2026-07-21T18:02:17Z"),
            200,
            CancellationToken.None);

        rows.Should().HaveCount(2);
        rows[0].DeliveryId.Should().Be("31087407-0b18-405e-b01e-f3a6dbd1cbf5");
        rows[1].DeliveryId.Should().Be("delivery-2");
    }

    [Fact]
    public async Task ListExpiredDeliveriesAsync_Malformed_Body_Yields_Empty()
    {
        var logger = new RecordingLogger<DeliveryServiceClient>();
        var client = ClientReturning("{not-json", logger);

        var act = () => client.ListExpiredDeliveriesAsync(
            DateTimeOffset.Parse("2026-07-21T18:02:17Z"),
            200,
            CancellationToken.None);

        var rows = await act.Should().NotThrowAsync();
        rows.Which.Should().BeEmpty();
        logger.Levels.Should().Equal(LogLevel.Warning);
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

    private static DeliveryServiceClient ClientReturning(
        string jsonBody,
        ILogger<DeliveryServiceClient>? logger = null)
    {
        var http = new HttpClient(new SingleResponseHandler(jsonBody))
        {
            BaseAddress = new Uri("http://delivery.test/")
        };
        return new DeliveryServiceClient(http, logger);
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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }
}
