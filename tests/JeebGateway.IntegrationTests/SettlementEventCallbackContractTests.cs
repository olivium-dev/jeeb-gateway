using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class SettlementEventCallbackContractTests
{
    private static readonly Guid EventId = Guid.Parse("9c73e8ad-d803-4d1b-b78e-e31dff1418a4");
    private static readonly Guid BatchId = Guid.Parse("540957a7-5c8b-4cf3-94a1-099b28f6c65b");
    private static readonly Guid SettlementId = Guid.Parse("32b1788f-1219-48b8-baf1-ff728cf2cfb6");

    [Fact]
    public async Task OwnerEventPreservesDedupeAndCorrelationHeadersAndUsesStableNotificationId()
    {
        var handler = new CaptureHandler();
        var controller = Controller(handler);
        controller.Request.Headers["X-Event-Id"] = EventId.ToString("D");
        controller.Request.Headers["Idempotency-Key"] = EventId.ToString("D");
        controller.Request.Headers["X-Correlation-Id"] = "settlement-correlation-42";

        var result = await controller.Dispatch(Callback(), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        handler.Request.Should().NotBeNull();
        handler.Request!.Headers.GetValues("Idempotency-Key").Should().Equal(EventId.ToString("D"));
        handler.Request.Headers.GetValues("X-Event-Id").Should().Equal(EventId.ToString("D"));
        handler.Request.Headers.GetValues("X-Correlation-Id").Should().Equal("settlement-correlation-42");
        handler.Request.Headers.Authorization.Should().BeNull();
        handler.Request.Headers.Contains("X-Api-Key").Should().BeFalse();
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("notification_id").GetString().Should().Be(EventId.ToString("D"));
        body.RootElement.GetProperty("receiver").GetString().Should().Be("provider-42");
        body.RootElement.GetProperty("notification_type").GetString().Should().Be("jeeb.settlement_paid");
        body.RootElement.GetProperty("payload").GetProperty("settlementId").GetString()
            .Should().Be(BatchId.ToString("D"));
    }

    [Theory]
    [InlineData("settlement.recorded")]
    [InlineData("settlement.disputed")]
    [InlineData("settlement.resolved")]
    public async Task NonPaidOwnerEventsAreValidatedAndAcknowledgedWithoutNotification(
        string eventType)
    {
        var handler = new CaptureHandler();
        var controller = Controller(handler);
        AddOwnerHeaders(controller);

        var result = await controller.Dispatch(CodCallback(eventType), CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        JsonSerializer.Serialize(accepted.Value).Should().Contain("\"dispatched\":0");
        handler.Request.Should().BeNull();
    }

    [Fact]
    public async Task UnknownEventTypeIsRejectedBeforeNotificationDial()
    {
        var handler = new CaptureHandler();
        var controller = Controller(handler);
        AddOwnerHeaders(controller);
        var callback = CodCallback("settlement.recorded") with { EventType = "settlement.unknown" };

        var result = await controller.Dispatch(callback, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        handler.Request.Should().BeNull();
    }

    [Fact]
    public void ExactSnakeCaseUpgEnvelopeBindsToCallbackContract()
    {
        var callback = JsonSerializer.Deserialize<SettlementEventCallbackV1>($$"""
            {
              "event_id":"{{EventId:D}}",
              "event_type":"settlement.recorded",
              "occurred_at":"2026-08-07T10:00:00Z",
              "aggregate":{"type":"cod_settlement","id":"{{SettlementId:D}}","version":2},
              "provider_id":"provider-42",
              "delivery_id":"delivery-42",
              "batch_id":null,
              "money":{"gross_amount":"100.00","commission_amount":"15.50","net_amount":"84.50","currency":"USD"},
              "status":"pending",
              "previous_status":"intent",
              "commission_rate":"0.155",
              "snapshot_sequence":2,
              "reason":null,
              "payment_reference":null,
              "period":{"start":null,"end":null},
              "actor_id":"system:orchestrator"
            }
            """);

        callback.Should().NotBeNull();
        callback!.EventId.Should().Be(EventId);
        callback.Aggregate!.Id.Should().Be(SettlementId);
        callback.DeliveryId.Should().Be("delivery-42");
        callback.Money!.GrossAmount!.Value.GetString().Should().Be("100.00");
        callback.SnapshotSequence.Should().Be(2);
    }

    [Fact]
    public async Task MismatchedOwnerDedupeHeaderIsRejectedBeforeNotificationDial()
    {
        var handler = new CaptureHandler();
        var controller = Controller(handler);
        controller.Request.Headers["X-Event-Id"] = EventId.ToString("D");
        controller.Request.Headers["Idempotency-Key"] = "different-event";

        var result = await controller.Dispatch(Callback(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        handler.Request.Should().BeNull();
    }

    [Fact]
    public async Task NonLoopbackPeerIsRejectedBeforeNotificationDial()
    {
        var handler = new CaptureHandler();
        var controller = Controller(handler);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.168.2.44");

        var result = await controller.Dispatch(Callback(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        handler.Request.Should().BeNull();
    }

    [Fact]
    public async Task ConfiguredPrivateOwnerNetworkIsAcceptedWithoutServiceAuthentication()
    {
        var handler = new CaptureHandler();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminCallbacks:TrustedNetworks:0"] = "192.168.2.0/24",
        }).Build();
        var controller = Controller(handler, configuration);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.168.2.44");
        AddOwnerHeaders(controller);

        var result = await controller.Dispatch(Callback(), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        handler.Request.Should().NotBeNull();
    }

    [Fact]
    public async Task ConfiguredPublicNetworkIsIgnored()
    {
        var handler = new CaptureHandler();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminCallbacks:TrustedNetworks:0"] = "0.0.0.0/0",
        }).Build();
        var controller = Controller(handler, configuration);
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.44");

        var result = await controller.Dispatch(Callback(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        handler.Request.Should().BeNull();
    }

    private static SettlementEventCallbacksController Controller(
        CaptureHandler handler, IConfiguration? configuration = null)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://notification.test/") };
        var controller = new SettlementEventCallbacksController(
            new SingleClientFactory(client), NullLogger<SettlementEventCallbacksController>.Instance,
            configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        return controller;
    }

    private static SettlementEventCallbackV1 Callback()
    {
        return new SettlementEventCallbackV1(
            EventId,
            "settlement.paid",
            DateTimeOffset.Parse("2026-08-07T10:00:00Z"),
            new SettlementEventAggregateV1("settlement_batch", BatchId, 3),
            "provider-42",
            null,
            BatchId,
            Money(null, null, "84.50"),
            "paid",
            "pending",
            null,
            null,
            null,
            "bank-reference-42",
            new SettlementEventPeriodV1("2026-08-01", "2026-08-07"),
            "finance-42");
    }

    private static SettlementEventCallbackV1 CodCallback(string eventType)
    {
        var (status, previous, reason, rate, sequence) = eventType switch
        {
            "settlement.recorded" => ("pending", "intent", (string?)null, (JsonElement?)Decimal("0.155"), (int?)2),
            "settlement.disputed" => ("disputed", "pending", "cash mismatch", null, null),
            "settlement.resolved" => ("resolved", "disputed", "bank receipt verified", null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(eventType)),
        };
        return new SettlementEventCallbackV1(
            EventId,
            eventType,
            DateTimeOffset.Parse("2026-08-07T10:00:00Z"),
            new SettlementEventAggregateV1("cod_settlement", SettlementId, 2),
            "provider-42",
            "delivery-42",
            null,
            Money("100.00", "15.50", "84.50"),
            status,
            previous,
            rate,
            sequence,
            reason,
            null,
            null,
            "system:orchestrator");
    }

    private static SettlementEventMoneyV1 Money(
        string? gross, string? commission, string? net) =>
        new(
            gross is null ? null : Decimal(gross),
            commission is null ? null : Decimal(commission),
            net is null ? null : Decimal(net),
            "USD");

    private static JsonElement Decimal(string value)
    {
        using var document = JsonDocument.Parse($"\"{value}\"");
        return document.RootElement.Clone();
    }

    private static void AddOwnerHeaders(SettlementEventCallbacksController controller)
    {
        controller.Request.Headers["X-Event-Id"] = EventId.ToString("D");
        controller.Request.Headers["Idempotency-Key"] = EventId.ToString("D");
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                Request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
