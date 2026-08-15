using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Financials;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

public sealed class WalletSettlementLedgerClientTests
{
    private static readonly Guid HolderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid HolderWalletId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SystemWalletId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid HeaderId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task Posts_One_Explicit_MultiLeg_Wallet_Transaction_Without_Auth_Headers()
    {
        var handler = new WalletHandler();
        var client = NewClient(handler);

        var result = await client.PostLedgerEntryAsync(Request(), CancellationToken.None);

        result.LedgerEntryId.Should().Be(HeaderId.ToString("D"));
        result.PostedAt.Should().Be(DateTimeOffset.Parse("2026-08-10T01:02:03Z"));
        handler.ExecuteCalls.Should().Be(1);
        handler.InitiateKeys.Should().Equal("settlement:settlement-42");
        handler.Requests.Should().OnlyContain(request =>
            request.Authorization == null && request.ServiceAuth == null,
            "wallet-service is protected by the private overlay, not service-auth headers");

        using var body = JsonDocument.Parse(handler.InitiateBodies.Single());
        var root = body.RootElement;
        root.GetProperty("serviceName").GetString().Should().Be("jeeb-gateway");
        root.GetProperty("tag").GetString().Should().Be("cod-settlement");
        root.GetProperty("externalReference").GetString().Should().Be("delivery-7");
        root.GetProperty("applyConfiguredFees").GetBoolean().Should().BeFalse();
        var legs = root.GetProperty("transactions").EnumerateArray().ToArray();
        legs.Should().HaveCount(3);
        AssertLeg(legs[0], SystemWalletId, HolderWalletId, 123.4567m, isFee: false);
        AssertLeg(legs[1], HolderWalletId, SystemWalletId, 12.3456m, isFee: true);
        AssertLeg(legs[2], HolderWalletId, SystemWalletId, 1.1111m, isFee: true);
    }

    [Fact]
    public async Task Ambiguous_Execute_Failure_Replays_The_Same_Header_And_Key()
    {
        var handler = new WalletHandler(failFirstExecute: true);
        var client = NewClient(handler);

        await client.Invoking(value => value.PostLedgerEntryAsync(Request(), CancellationToken.None))
            .Should().ThrowAsync<WalletSettlementUnavailableException>();
        var replay = await client.PostLedgerEntryAsync(Request(), CancellationToken.None);

        replay.LedgerEntryId.Should().Be(HeaderId.ToString("D"));
        handler.InitiateKeys.Should().Equal(
            "settlement:settlement-42", "settlement:settlement-42");
        handler.ExecuteHeaders.Should().Equal(HeaderId, HeaderId);
    }

    [Fact]
    public async Task Rejects_Ambiguous_Or_Missing_Currency_Wallets_Before_Initiation()
    {
        var handler = new WalletHandler(duplicateHolderWallet: true);
        var client = NewClient(handler);

        await client.Invoking(value => value.PostLedgerEntryAsync(Request(), CancellationToken.None))
            .Should().ThrowAsync<WalletSettlementUnavailableException>()
            .WithMessage("*exactly one active Jeeber 'USD' wallet*");
        handler.InitiateBodies.Should().BeEmpty();
    }

    [Fact]
    public async Task Shadow_Failure_Cannot_Replace_A_Successful_Wallet_Result()
    {
        var primary = NewClient(new WalletHandler());
        var comparator = new ShadowComparingSettlementLedgerClient(
            primary,
            new ThrowingShadow(),
            NullLogger<ShadowComparingSettlementLedgerClient>.Instance);

        var result = await comparator.PostLedgerEntryAsync(Request(), CancellationToken.None);

        result.LedgerEntryId.Should().Be(HeaderId.ToString("D"));
    }

    private static WalletSettlementLedgerClient NewClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://wallet.test/") };
        return new WalletSettlementLedgerClient(
            new FixedHttpClientFactory(http),
            NullLogger<WalletSettlementLedgerClient>.Instance);
    }

    private static LedgerEntryRequest Request() => new()
    {
        DeliveryId = "delivery-7",
        JeeberId = HolderId.ToString("D"),
        ClientId = "client-8",
        EntryType = "cash_settlement",
        GoodsCost = 123.4567m,
        Commission = 12.3456m,
        Insurance = 1.1111m,
        Total = 13.4567m,
        Currency = "USD",
        PaymentMethod = "cash",
        IdempotencyKey = "settlement-42",
    };

    private static void AssertLeg(
        JsonElement leg,
        Guid source,
        Guid destination,
        decimal amount,
        bool isFee)
    {
        leg.GetProperty("sourceWalletId").GetGuid().Should().Be(source);
        leg.GetProperty("destinationWalletId").GetGuid().Should().Be(destination);
        leg.GetProperty("amount").GetDecimal().Should().Be(amount);
        leg.GetProperty("isAdditionalFees").GetBoolean().Should().Be(isFee);
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            name.Should().Be(WalletSettlementLedgerClient.HttpClientName);
            return client;
        }
    }

    private sealed class ThrowingShadow : ISettlementLedgerShadowReader
    {
        public Task<LegacySettlementLedgerEntry?> ReadAsync(string idempotencyKey, CancellationToken ct) =>
            throw new InvalidOperationException("legacy database unavailable");
    }

    private sealed class WalletHandler : HttpMessageHandler
    {
        private readonly bool _failFirstExecute;
        private readonly bool _duplicateHolderWallet;
        private int _executeCalls;

        public WalletHandler(bool failFirstExecute = false, bool duplicateHolderWallet = false)
        {
            _failFirstExecute = failFirstExecute;
            _duplicateHolderWallet = duplicateHolderWallet;
        }

        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();
        public ConcurrentQueue<string> InitiateKeys { get; } = new();
        public ConcurrentQueue<string> InitiateBodies { get; } = new();
        public ConcurrentQueue<Guid> ExecuteHeaders { get; } = new();
        public int ExecuteCalls => _executeCalls;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue(new CapturedRequest(
                request.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Service-Auth", out var serviceAuth)
                    ? serviceAuth.Single()
                    : null));

            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/Fees/currencies")
                return Json("[{\"id\":2,\"code\":\"USD\",\"rate\":1}]");
            if (request.Method == HttpMethod.Get && path == $"/Wallet/holder/{HolderId:D}/wallets")
            {
                var duplicate = _duplicateHolderWallet
                    ? $",{{\"walletId\":\"{Guid.Parse("55555555-5555-5555-5555-555555555555"):D}\",\"currencyID\":2,\"isActive\":true}}"
                    : string.Empty;
                return Json($"{{\"wallets\":[{{\"walletId\":\"{HolderWalletId:D}\",\"currencyID\":2,\"isActive\":true}}{duplicate}]}}");
            }
            if (request.Method == HttpMethod.Get && path == "/system-wallet")
                return Json($"{{\"wallets\":[{{\"walletId\":\"{SystemWalletId:D}\",\"currencyID\":2,\"isActive\":true}}]}}");
            if (request.Method == HttpMethod.Post && path == "/Transaction/initiate")
            {
                InitiateKeys.Enqueue(request.Headers.GetValues("Idempotency-Key").Single());
                InitiateBodies.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json($"{{\"transactionHeader\":{{\"txId\":\"{HeaderId:D}\",\"createdAt\":\"2026-08-10T01:02:03Z\"}},\"transactionDetails\":[]}}");
            }
            if (request.Method == HttpMethod.Post
                && path == $"/Transaction/{HeaderId:D}/execute")
            {
                ExecuteHeaders.Enqueue(HeaderId);
                var call = Interlocked.Increment(ref _executeCalls);
                if (_failFirstExecute && call == 1)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Path,
        string? Authorization,
        string? ServiceAuth);
}
