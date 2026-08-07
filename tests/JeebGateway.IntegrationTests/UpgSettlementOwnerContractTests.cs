using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Financials;
using JeebGateway.Financials.Cod;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class UpgSettlementOwnerContractTests
{
    [Fact]
    public async Task PendingSnapshotSurvivesGatewayRestartAndFinalizesWithoutAuthorizationHeaders()
    {
        var handler = new OwnerHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://upg.internal/") };
        var factory = new SingleClientFactory(http);
        var owner = new HttpCodSettlementLedger(
            factory, NullLogger<HttpCodSettlementLedger>.Instance);
        var firstGateway = new UpgSettlementStore(owner, factory);

        var pending = Row(SettlementState.PendingSettlement);
        var (intent, inserted) = await firstGateway.TryInsertAsync(pending, CancellationToken.None);

        inserted.Should().BeTrue();
        intent.State.Should().Be(SettlementState.PendingSettlement);
        intent.GoodsCost.Should().Be(120.50m);

        // A fresh adapter instance models a gateway restart. The authoritative
        // amount is recovered from UPG, never gateway memory.
        var restartedGateway = new UpgSettlementStore(owner, factory);
        var recovered = await restartedGateway.GetByDeliveryAsync("delivery-42", CancellationToken.None);
        recovered.Should().NotBeNull();
        recovered!.State.Should().Be(SettlementState.PendingSettlement);
        recovered.GoodsCost.Should().Be(120.50m);

        (await restartedGateway.ReplacePendingAsync(
            "delivery-42", Row(SettlementState.Settled), CancellationToken.None)).Should().BeTrue();

        handler.Requests.Select(request => request.Path).Should().Equal(
            "/api/v1/payments/cod/intents/delivery-42",
            "/api/v1/payments/cod/by-delivery/delivery-42",
            "/api/v1/payments/cod/by-delivery/delivery-42",
            "/api/v1/payments/cod/intents/delivery-42/finalize");
        handler.Requests.Should().OnlyContain(request => request.Authorization == null && !request.HasApiKey);
        handler.Requests.Last().IdempotencyKey.Should().Be("cod-intent:delivery-42:finalize:2");
        handler.Requests.Should().OnlyContain(request =>
            request.Body == null || !request.Body.Contains("gateway_settlement_id", StringComparison.Ordinal));

        using var intentBody = JsonDocument.Parse(handler.Requests[0].Body!);
        intentBody.RootElement.GetProperty("snapshotSequence").GetInt64().Should().Be(1);
        intentBody.RootElement.GetProperty("grossAmount").GetString().Should().Be("120.50");
        using var finalizeBody = JsonDocument.Parse(handler.Requests[^1].Body!);
        finalizeBody.RootElement.GetProperty("expectedVersion").GetInt32().Should().Be(1);
        finalizeBody.RootElement.GetProperty("snapshotSequence").GetInt64().Should().Be(2);
    }

    [Fact]
    public async Task ZeroValuePendingSnapshotIsNotPersistedByOwner()
    {
        var handler = new OwnerHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://upg.internal/") };
        var factory = new SingleClientFactory(http);
        var store = new UpgSettlementStore(
            new HttpCodSettlementLedger(factory, NullLogger<HttpCodSettlementLedger>.Instance),
            factory);

        var (row, inserted) = await store.TryInsertAsync(
            Row(SettlementState.PendingSettlement, 0m), CancellationToken.None);

        inserted.Should().BeFalse();
        row.GoodsCost.Should().Be(0m);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task OwnerListForwardsEveryStatusFilterAndConsumesAllCursorPages()
    {
        var handler = new PagingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://upg.internal/") };
        var factory = new SingleClientFactory(http);
        var store = new UpgSettlementStore(
            new HttpCodSettlementLedger(factory, NullLogger<HttpCodSettlementLedger>.Instance),
            factory);

        var rows = await store.ListByJeeberAsync(
            "provider/42",
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-08T00:00:00Z"),
            CancellationToken.None,
            new[] { "recorded", "paid" });

        rows.Select(row => row.Id).Should().BeEquivalentTo(
            "recorded-1", "recorded-2", "paid-1", "paid-2");
        handler.Requests.Should().HaveCount(4);
        handler.Requests.Should().Contain(query => query.Contains("providerId=provider%2F42", StringComparison.Ordinal));
        handler.Requests.Should().OnlyContain(query => query.Contains("sort=createdAt:asc", StringComparison.Ordinal)
                                                        && query.Contains("limit=100", StringComparison.Ordinal));
        handler.Requests.Count(query => query.Contains("status=pending", StringComparison.Ordinal)).Should().Be(2,
            "the legacy gateway recorded state maps to UPG's pending vocabulary");
        handler.Requests.Should().NotContain(query => query.Contains("status=recorded", StringComparison.Ordinal));
        handler.Requests.Count(query => query.Contains("status=paid", StringComparison.Ordinal)).Should().Be(2);
        handler.Requests.Count(query => query.Contains("cursor=page-2", StringComparison.Ordinal)).Should().Be(2);
        handler.SawCredential.Should().BeFalse();
    }

    [Fact]
    public async Task UnknownGatewayCodFilterFailsClosedBeforeOwnerCall()
    {
        var handler = new PagingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://upg.internal/") };
        var factory = new SingleClientFactory(http);
        var store = new UpgSettlementStore(
            new HttpCodSettlementLedger(factory, NullLogger<HttpCodSettlementLedger>.Instance),
            factory);

        var call = () => store.ListByJeeberAsync(
            "provider-42", null, null, CancellationToken.None, new[] { "recorded_elsewhere" });

        await call.Should().ThrowAsync<ArgumentOutOfRangeException>();
        handler.Requests.Should().BeEmpty();
    }

    private static Settlement Row(string state, decimal goodsCost = 120.50m) => new()
    {
        Id = "gateway-settlement-42",
        DeliveryId = "delivery-42",
        ClientId = "client-42",
        JeeberId = "provider-42",
        TierId = "tier-standard",
        GoodsCost = goodsCost,
        CommissionTier = CommissionTier.Standard,
        CommissionRate = 0.10m,
        Commission = 12.05m,
        Insurance = 0m,
        Total = 12.05m,
        MinimumFeeApplied = false,
        Currency = "USD",
        PaymentMethod = "cash",
        State = state,
        SettledAt = DateTimeOffset.Parse("2026-08-07T10:00:00Z"),
    };

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class OwnerHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(),
                request.Headers.Contains("X-Api-Key"),
                request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null,
                body));

            var isIntentCreate = request.Method == HttpMethod.Put;
            var isFinalize = request.RequestUri.AbsolutePath.EndsWith("/finalize", StringComparison.Ordinal);
            var status = isFinalize ? "pending" : "intent";
            var version = isFinalize ? 2 : 1;
            var response = JsonSerializer.Serialize(new
            {
                data = new
                {
                    id = "owner-42",
                    deliveryId = "delivery-42",
                    providerId = "provider-42",
                    grossAmount = "120.50",
                    commissionRate = "0.10",
                    commissionAmount = "12.05",
                    currency = "USD",
                    status,
                    version,
                    metadata = new Dictionary<string, string>
                    {
                        ["client_id"] = "client-42",
                        ["tier_id"] = "tier-standard",
                        ["payment_method"] = "cash",
                    },
                    createdAt = "2026-08-07T10:00:00Z",
                },
            });
            return new HttpResponseMessage(isIntentCreate ? HttpStatusCode.Created : HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class PagingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public bool SawCredential { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query;
            Requests.Add(query);
            SawCredential |= request.Headers.Authorization is not null || request.Headers.Contains("X-Api-Key");
            var status = query.Contains("status=paid", StringComparison.Ordinal) ? "paid" : "pending";
            var secondPage = query.Contains("cursor=page-2", StringComparison.Ordinal);
            var sequence = secondPage ? 2 : 1;
            var gatewayState = status == "pending" ? "recorded" : status;
            var response = JsonSerializer.Serialize(new
            {
                data = new[]
                {
                    new
                    {
                        id = $"{gatewayState}-{sequence}",
                        deliveryId = $"delivery-{gatewayState}-{sequence}",
                        providerId = "provider/42",
                        grossAmount = "100.00",
                        commissionAmount = "10.00",
                        currency = "USD",
                        status,
                        createdAt = $"2026-08-0{sequence}T10:00:00Z",
                    },
                },
                page = new { nextCursor = secondPage ? null : "page-2" },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record CapturedRequest(
        string Path,
        string? Authorization,
        bool HasApiKey,
        string? IdempotencyKey,
        string? Body);
}
