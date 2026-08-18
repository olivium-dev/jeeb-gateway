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

/// <summary>Successor to WalletSettlementLedgerClientTests. W2-R11 moved the settlement ledger out
/// of the gateway, so the wire behaviour pinned here is settlement-service's, not wallet-service's.</summary>
public sealed class SettlementServiceClientWireTests
{
    private const string ServiceToken = "settlement-service-scope-token-00000001";
    private const string DeliveryId = "delivery-7";
    private const string HolderId = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "22222222-2222-2222-2222-222222222222";
    private static readonly Guid SettlementId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset SettledAt = DateTimeOffset.Parse("2026-08-10T01:02:03Z");

    [Fact]
    public async Task Posts_One_Settle_Carrying_The_Service_Token_And_No_Gateway_Money_Math()
    {
        var handler = new SettlementHandler();
        var client = NewClient(handler);

        var result = await client.SettleAsync(Command(), CancellationToken.None);

        result.Created.Should().BeTrue();
        result.HolderExcluded.Should().BeFalse();
        result.Row!.Id.Should().Be(SettlementId.ToString());
        result.Row.GoodsCost.Should().Be(123.4567m);
        result.Row.Commission.Should().Be(12.3456m);
        result.Row.SettledAt.Should().Be(SettledAt);
        handler.SettleCalls.Should().Be(1, "one settle command is exactly one upstream POST");

        handler.Requests.Should().OnlyContain(request =>
            request.Authorization == "Bearer " + ServiceToken,
            "settlement-service is reached with the SERVICE-scope bearer, unlike the old private-overlay wallet hop");

        using var body = JsonDocument.Parse(handler.SettleBodies.Single());
        var root = body.RootElement;
        root.GetProperty("deliveryId").GetString().Should().Be(DeliveryId);
        root.GetProperty("holderId").GetString().Should().Be(HolderId);
        root.GetProperty("clientId").GetString().Should().Be(ClientId);
        root.GetProperty("tierId").GetString().Should().Be("express");
        root.GetProperty("currency").GetString().Should().Be(SettlementService.CurrencyUsd);
        root.GetProperty("paymentMethod").GetString().Should().Be(SettlementService.PaymentMethodCash);
        root.GetProperty("grossAmount").GetDecimal().Should().Be(123.4567m,
            "the collected cash goes upstream unrounded");

        // The old client split goods/commission/insurance into three explicit wallet legs; upstream
        // now owns the arithmetic, so a commission/fee field appearing here is a boundary regression.
        root.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(new[]
        {
            "deliveryId", "holderId", "clientId", "tierId", "grossAmount",
            "currency", "paymentMethod", "settledAt",
        });
    }

    [Fact]
    public async Task Pending_Intent_Omits_Gross_Amount_So_Upstream_Stores_Null_Not_Zero()
    {
        var handler = new SettlementHandler();
        var client = NewClient(handler);

        await client.SettleAsync(Command(gross: null) with { SettledAt = null }, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.SettleBodies.Single());
        body.RootElement.TryGetProperty("grossAmount", out _).Should().BeFalse(
            "an amount-less intent must store money as NULL upstream, never as a real 0.00");
        body.RootElement.TryGetProperty("settledAt", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Ambiguous_Settle_Failure_Throws_Typed_And_The_Replay_Reuses_The_Same_Key()
    {
        var handler = new SettlementHandler(failFirstSettle: true);
        var client = NewClient(handler);

        var thrown = await client.Invoking(value => value.SettleAsync(Command(), CancellationToken.None))
            .Should().ThrowAsync<SettlementServiceUnavailableException>();
        thrown.Which.Member.Should().Be("SettleAsync", "an ambiguous failure is never a confident success");
        var replay = await client.SettleAsync(Command(), CancellationToken.None);

        replay.Row!.Id.Should().Be(SettlementId.ToString());
        replay.Created.Should().BeFalse("a replayed settle did not create a second money row");
        handler.SettleCalls.Should().Be(2);

        // The retry is keyed on the same delivery id, so upstream dedupes instead of double-creating.
        handler.SettleDeliveryIds().Should().Equal(DeliveryId, DeliveryId);
    }

    [Fact]
    public async Task Rejects_A_Non_Guid_Holder_Before_Any_Money_Post()
    {
        var handler = new SettlementHandler();
        var client = NewClient(handler);

        var result = await client.SettleAsync(
            Command() with { HolderId = "jeeber-not-a-guid" }, CancellationToken.None);

        result.HolderExcluded.Should().BeTrue();
        result.Created.Should().BeFalse();
        result.Row.Should().BeNull("an excluded holder must not yield a fabricated settlement row");
        handler.Requests.Should().BeEmpty("the refusal happens before initiation, not after a money POST");
    }

    [Fact]
    public async Task A_Conflicting_Settle_Returns_The_Stored_Money_Instead_Of_Overwriting_It()
    {
        var handler = new SettlementHandler(conflictOnSettle: true);
        var client = NewClient(handler);

        var result = await client.SettleAsync(Command(gross: 999.99m), CancellationToken.None);

        result.Created.Should().BeFalse();
        result.Row!.GoodsCost.Should().Be(123.4567m,
            "the stored amount stands: a conflicting settle is never allowed to rewrite money");
        handler.Requests.Select(request => request.Path).Should().Equal(
            "/settlements", "/settlements/by-delivery/delivery-7");
    }

    [Fact]
    public async Task An_Unconfigured_Service_Token_Fails_Closed_Before_The_Network_Call()
    {
        var handler = new SettlementHandler();
        var client = NewClient(handler, token: null);

        var act = () => client.SettleAsync(Command(), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<SettlementServiceUnavailableException>();
        thrown.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("SERVICE credential is not configured or is invalid");
        handler.Requests.Should().BeEmpty(
            "a missing credential must fail before an unauthenticated money request reaches the owner");
    }

    [Fact]
    public async Task A_Mounted_Service_Token_Rotates_Without_A_Gateway_Restart()
    {
        var directory = Directory.CreateTempSubdirectory("settlement-service-token");
        try
        {
            var path = Path.Combine(directory.FullName, "token");
            var before = new string('a', 40);
            var after = new string('b', 40);
            await File.WriteAllTextAsync(path, before);
            var handler = new SettlementHandler();
            var client = NewClient(handler, token: null, tokenFile: path);

            await client.GetByDeliveryAsync(DeliveryId, CancellationToken.None);
            await File.WriteAllTextAsync(path, after + "\n");
            await client.GetByDeliveryAsync(DeliveryId, CancellationToken.None);

            handler.Requests.Select(request => request.Authorization)
                .Should().Equal("Bearer " + before, "Bearer " + after);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task A_Missing_Mounted_Service_Token_Fails_Closed_Before_The_Network_Call()
    {
        var handler = new SettlementHandler();
        var client = NewClient(
            handler,
            token: null,
            tokenFile: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        var act = () => client.GetByDeliveryAsync(DeliveryId, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<SettlementServiceUnavailableException>();
        thrown.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("credential file is missing or outside the allowed size");
        handler.Requests.Should().BeEmpty();
    }

    private static ISettlementServiceClient NewClient(
        HttpMessageHandler inner,
        string? token = ServiceToken,
        string? tokenFile = null)
    {
        var authenticated = new SettlementServiceTokenHandler(
            new StaticOptionsMonitor<SettlementServiceOptions>(
                new SettlementServiceOptions
                {
                    BaseUrl = "http://settlement.test/",
                    ApiToken = token,
                    ApiTokenFile = tokenFile,
                }))
        {
            InnerHandler = inner,
        };
        var http = new HttpClient(authenticated) { BaseAddress = new Uri("http://settlement.test/") };
        return new SettlementServiceClient(http, NullLogger<SettlementServiceClient>.Instance);
    }

    private static SettlementSettleCommand Command(decimal? gross = 123.4567m) => new(
        DeliveryId: DeliveryId,
        HolderId: HolderId,
        ClientId: ClientId,
        TierId: "express",
        GrossAmount: gross,
        PaymentMethod: SettlementService.PaymentMethodCash,
        SettledAt: SettledAt);

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class SettlementHandler : HttpMessageHandler
    {
        private readonly bool _failFirstSettle;
        private readonly bool _conflictOnSettle;
        private int _settleCalls;

        public SettlementHandler(bool failFirstSettle = false, bool conflictOnSettle = false)
        {
            _failFirstSettle = failFirstSettle;
            _conflictOnSettle = conflictOnSettle;
        }

        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();
        public ConcurrentQueue<string> SettleBodies { get; } = new();
        public int SettleCalls => _settleCalls;

        public IReadOnlyList<string> SettleDeliveryIds() => SettleBodies.Select(body =>
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("deliveryId").GetString()!;
        }).ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue(new CapturedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString()));

            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/settlements")
            {
                SettleBodies.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (_conflictOnSettle) return new HttpResponseMessage(HttpStatusCode.Conflict);

                var call = Interlocked.Increment(ref _settleCalls);
                if (_failFirstSettle && call == 1)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

                // Upstream is idempotent on delivery id: a repeat is a flagged replay, not a new row.
                var response = Json(Row(), call == 1 ? HttpStatusCode.Created : HttpStatusCode.OK);
                if (call > 1) response.Headers.Add("Idempotency-Replayed", "true");
                return response;
            }

            if (request.Method == HttpMethod.Get && path == $"/settlements/by-delivery/{DeliveryId}")
                return Json(Row(), HttpStatusCode.OK);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string Row() => $$"""
            {
              "settlementId": "{{SettlementId:D}}",
              "deliveryId": "{{DeliveryId}}",
              "holderId": "{{HolderId}}",
              "clientId": "{{ClientId}}",
              "tierId": "express",
              "state": "settled",
              "currency": "USD",
              "paymentMethod": "cash",
              "grossAmount": 123.4567,
              "commissionRate": 0.10,
              "commissionAmount": 12.3456,
              "netAmount": 111.1111,
              "settledAt": "2026-08-10T01:02:03+00:00",
              "createdAt": "2026-08-10T01:02:03+00:00"
            }
            """;

        private static HttpResponseMessage Json(string value, HttpStatusCode status) => new(status)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? Authorization);
}
