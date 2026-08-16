using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Financials;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// Successor to WalletSettlementLedgerClientTests. W2-R11 deleted the gateway's own wallet ledger
/// primitive; settlement-service owns the ledger, so the settle wire is pinned at its new owner.
/// </summary>
public sealed class SettlementServiceSettleWireTests
{
    private const string ServiceToken = "service-scope-token";
    private static readonly Guid HolderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SettlementId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset SettledAt = DateTimeOffset.Parse("2026-08-10T01:02:03Z");

    [Fact]
    public async Task Posts_One_Delivery_Keyed_Settle_Command_Carrying_The_Service_Bearer()
    {
        var upstream = new SettlementHandler();
        var client = NewClient(upstream);

        var result = await client.SettleAsync(Command(), CancellationToken.None);

        result.Created.Should().BeTrue();
        result.HolderExcluded.Should().BeFalse();
        result.Row!.Id.Should().Be(SettlementId.ToString());
        result.Row.DeliveryId.Should().Be("delivery-7");
        result.Row.GoodsCost.Should().Be(123.4567m);
        result.Row.Commission.Should().Be(12.3456m);
        result.Row.SettledAt.Should().Be(SettledAt);

        var only = upstream.Requests.Single();
        only.Method.Should().Be(HttpMethod.Post);
        only.Path.Should().Be("/settlements");
        only.Authorization.Should().Be("Bearer " + ServiceToken,
            "settlement-service is token-protected; an unauthenticated settle 401s and is swallowed");

        using var body = JsonDocument.Parse(upstream.Bodies.Single());
        var root = body.RootElement;
        root.GetProperty("deliveryId").GetString().Should().Be("delivery-7");
        root.GetProperty("holderId").GetString().Should().Be(HolderId.ToString("D"));
        root.GetProperty("clientId").GetString().Should().Be("client-8");
        root.GetProperty("tierId").GetString().Should().Be("tier-3");
        root.GetProperty("grossAmount").GetDecimal().Should().Be(123.4567m);
        root.GetProperty("currency").GetString().Should().Be(SettlementService.CurrencyUsd);
        root.GetProperty("paymentMethod").GetString().Should().Be(SettlementService.PaymentMethodCash);

        // The gateway sends the gross only. Ledger legs and the fee arithmetic moved upstream at
        // W2-R11; re-growing them here would put the gateway back on the ledger primitive.
        root.TryGetProperty("transactions", out _).Should().BeFalse();
        root.TryGetProperty("commission", out _).Should().BeFalse();
        root.TryGetProperty("insurance", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Ambiguous_Settle_Failure_Re_Drives_The_Same_Delivery_Key_And_Mints_No_Second_Row()
    {
        var upstream = new SettlementHandler(failFirstSettle: true);
        var client = NewClient(upstream);

        await client.Invoking(value => value.SettleAsync(Command(), CancellationToken.None))
            .Should().ThrowAsync<SettlementServiceUnavailableException>();
        var replay = await client.SettleAsync(Command(), CancellationToken.None);

        replay.Row!.Id.Should().Be(SettlementId.ToString());
        replay.Created.Should().BeFalse("a replayed settle did not change the row");
        // The delivery id IS the durable settle key: a re-drive must reuse it, never mint a new one.
        upstream.SettleDeliveryIds.Should().Equal(new[] { "delivery-7", "delivery-7" });
        upstream.Requests.Should().HaveCount(2,
            "a money POST is never retried in transport; the completion legs re-drive it");
    }

    [Fact]
    public async Task A_Holder_The_Gateway_Cannot_Resolve_Is_Excluded_Before_Any_Settle_Is_Posted()
    {
        var upstream = new SettlementHandler();
        var client = NewClient(upstream);

        var result = await client.SettleAsync(
            Command() with { HolderId = "not-a-guid" }, CancellationToken.None);

        result.HolderExcluded.Should().BeTrue();
        result.Created.Should().BeFalse();
        result.Row.Should().BeNull();
        upstream.Requests.Should().BeEmpty(
            "an unresolvable money destination must fail closed, never reach the settle POST");
    }

    private static SettlementServiceClient NewClient(HttpMessageHandler upstream)
    {
        var authenticated = new SettlementServiceTokenHandler(
            new FixedOptionsMonitor(new SettlementServiceOptions { ApiToken = ServiceToken }))
        {
            InnerHandler = upstream,
        };
        var http = new HttpClient(authenticated) { BaseAddress = new Uri("http://settlement.test/") };
        return new SettlementServiceClient(http, NullLogger<SettlementServiceClient>.Instance);
    }

    private static SettlementSettleCommand Command() => new(
        DeliveryId: "delivery-7",
        HolderId: HolderId.ToString("D"),
        ClientId: "client-8",
        TierId: "tier-3",
        GrossAmount: 123.4567m,
        PaymentMethod: SettlementService.PaymentMethodCash,
        SettledAt: SettledAt);

    private sealed class FixedOptionsMonitor(SettlementServiceOptions value)
        : IOptionsMonitor<SettlementServiceOptions>
    {
        public SettlementServiceOptions CurrentValue => value;

        public SettlementServiceOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<SettlementServiceOptions, string?> listener) => null;
    }

    private sealed class SettlementHandler(bool failFirstSettle = false) : HttpMessageHandler
    {
        private int _settleCalls;

        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();
        public ConcurrentQueue<string> Bodies { get; } = new();
        public ConcurrentQueue<string> SettleDeliveryIds { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Enqueue(new CapturedRequest(
                request.Method, path, request.Headers.Authorization?.ToString()));

            if (request.Method != HttpMethod.Post || path != "/settlements")
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Enqueue(body);
            using (var parsed = JsonDocument.Parse(body))
                SettleDeliveryIds.Enqueue(parsed.RootElement.GetProperty("deliveryId").GetString()!);

            var call = Interlocked.Increment(ref _settleCalls);
            if (failFirstSettle && call == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("upstream unavailable"),
                };
            }

            var response = new HttpResponseMessage(call == 1 ? HttpStatusCode.Created : HttpStatusCode.OK)
            {
                Content = new StringContent(Row(), Encoding.UTF8, "application/json"),
            };
            if (call > 1)
                response.Headers.TryAddWithoutValidation("Idempotency-Replayed", "true");
            return response;
        }

        private static string Row() =>
            $$"""
            {
              "settlementId": "{{SettlementId:D}}",
              "deliveryId": "delivery-7",
              "holderId": "{{HolderId:D}}",
              "clientId": "client-8",
              "tierId": "tier-3",
              "state": "settled",
              "currency": "USD",
              "paymentMethod": "cash",
              "grossAmount": 123.4567,
              "commissionRate": 0.10,
              "commissionAmount": 12.3456,
              "settledAt": "2026-08-10T01:02:03+00:00",
              "createdAt": "2026-08-10T01:02:03+00:00"
            }
            """;
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? Authorization);
}
