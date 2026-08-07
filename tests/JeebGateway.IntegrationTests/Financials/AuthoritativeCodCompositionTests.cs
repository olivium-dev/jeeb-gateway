using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Financials;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

public sealed class AuthoritativeCodCompositionTests
{
    [Fact]
    public async Task AtDoorSnapshotUsesDeliveryOwnerAndExactAcceptedOfferAmount()
    {
        var transport = new OwnerTransport();
        var store = new MemorySettlementStore();
        var service = CreateService(store, transport);

        var inserted = await service.TrySnapshotPendingCodAsync("delivery-42", CancellationToken.None);

        inserted.Should().BeTrue();
        store.Row.Should().NotBeNull();
        store.Row!.DeliveryId.Should().Be("delivery-42");
        store.Row.ClientId.Should().Be("client-42");
        store.Row.JeeberId.Should().Be("jeeber-42");
        store.Row.GoodsCost.Should().Be(42.50m);
        transport.OfferActor.Should().Be("client-42",
            "the delivery owner supplies the identity accepted by the offer owner");
    }

    [Fact]
    public async Task ExistingIntentReplayRequiresExactOwnerIdentityAmountAndAllowableState()
    {
        var transport = new OwnerTransport();
        var store = new MemorySettlementStore
        {
            Row = SettlementRow(goodsCost: 42.50m, state: SettlementState.PendingSettlement),
        };
        var service = CreateService(store, transport);

        (await service.IsAuthoritativeCodIntentAsync(
            "delivery-42", "InTransit", CancellationToken.None)).Should().BeTrue();

        store.Row = SettlementRow(goodsCost: 42.51m, state: SettlementState.PendingSettlement);
        (await service.IsAuthoritativeCodIntentAsync(
            "delivery-42", "InTransit", CancellationToken.None)).Should().BeFalse();

        store.Row = SettlementRow(goodsCost: 42.50m, state: SettlementState.Settled);
        (await service.IsAuthoritativeCodIntentAsync(
            "delivery-42", "InTransit", CancellationToken.None)).Should().BeFalse();
        (await service.IsAuthoritativeCodIntentAsync(
            "delivery-42", "AtDoor", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task OfferOwnerFailureFailsClosedWithoutWritingAnIntent()
    {
        var transport = new OwnerTransport { OfferStatus = HttpStatusCode.ServiceUnavailable };
        var store = new MemorySettlementStore();
        var service = CreateService(store, transport);

        var act = () => service.TrySnapshotPendingCodAsync("delivery-42", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        store.Row.Should().BeNull();
    }

    private static SettlementService CreateService(
        ISettlementStore store,
        OwnerTransport transport)
    {
        var delivery = new DeliveryServiceClient(new HttpClient(transport)
        {
            BaseAddress = new Uri("http://delivery.test/"),
        });
        var offers = new OfferServiceClient(new HttpClient(transport)
        {
            BaseAddress = new Uri("http://offer.test/"),
        });
        return new SettlementService(
            store, delivery, offers, new EarningsCacheInvalidator(),
            TimeProvider.System, NullLogger<SettlementService>.Instance);
    }

    private static Settlement SettlementRow(decimal goodsCost, string state) => new()
    {
        Id = "settlement-42",
        DeliveryId = "delivery-42",
        ClientId = "client-42",
        JeeberId = "jeeber-42",
        TierId = "standard",
        GoodsCost = goodsCost,
        CommissionTier = CommissionTier.Standard,
        CommissionRate = 0.10m,
        Commission = decimal.Round(goodsCost * 0.10m, 2),
        Insurance = 0m,
        Total = decimal.Round(goodsCost * 0.10m, 2),
        MinimumFeeApplied = false,
        Currency = SettlementService.CurrencyUsd,
        PaymentMethod = SettlementService.PaymentMethodCash,
        State = state,
        CodState = CodSettlementState.Recorded,
        SettledAt = DateTimeOffset.UtcNow,
    };

    private sealed class OwnerTransport : HttpMessageHandler
    {
        public HttpStatusCode OfferStatus { get; init; } = HttpStatusCode.OK;
        public string? OfferActor { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var isOffer = string.Equals(request.RequestUri!.Host, "offer.test", StringComparison.Ordinal);
            if (isOffer)
            {
                OfferActor = request.Headers.TryGetValues("x-user-id", out var values)
                    ? values.Single()
                    : null;
                return Task.FromResult(Json(OfferStatus,
                    """{"offers":[{"id":"offer-42","request_id":"delivery-42","actor_id":"jeeber-42","fee_cents":4250,"eta_minutes":15,"status":"accepted"}]}"""));
            }

            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"delivery_id":"delivery-42","client_id":"client-42","jeeber_id":"jeeber-42","status":"InTransit","tier_id":"standard","created_at":"2026-08-07T00:00:00Z"}"""));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class MemorySettlementStore : ISettlementStore
    {
        public Settlement? Row { get; set; }

        public Task<(Settlement Row, bool Inserted)> TryInsertAsync(Settlement settlement, CancellationToken ct)
        {
            if (Row is not null) return Task.FromResult((Row, false));
            Row = settlement;
            return Task.FromResult((settlement, true));
        }

        public Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct) =>
            Task.FromResult(Row?.DeliveryId == deliveryId ? Row : null);

        public Task<IReadOnlyList<Settlement>> ListByJeeberAsync(
            string jeeberId, DateTimeOffset? from, DateTimeOffset? to,
            CancellationToken ct, IReadOnlyCollection<string>? codStates = null) =>
            Task.FromResult<IReadOnlyList<Settlement>>(Row is null ? Array.Empty<Settlement>() : new[] { Row });

        public Task<Settlement?> GetByIdAsync(string settlementId, CancellationToken ct) =>
            Task.FromResult(Row?.Id == settlementId ? Row : null);

        public Task<Settlement?> MarkReceiptGeneratedAsync(
            string settlementId, DateTimeOffset at, CancellationToken ct) => Task.FromResult(Row);

        public Task<bool> ReplacePendingAsync(
            string deliveryId, Settlement settled, CancellationToken ct) => Task.FromResult(false);
    }
}
