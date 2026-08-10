using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Financials;
using JeebGateway.IntegrationTests.Fakes;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Xunit;
using SwServiceWalletClient = JeebGateway.service.ServiceWallet.ServiceWalletClient;

namespace JeebGateway.IntegrationTests;

/// <summary>F1 — the three offer wallet guards (submit/accept/edit) plus the shared
/// <see cref="WalletSufficiencyGuard"/> primitive (fail modes, breaker, multi-currency).</summary>
public class WalletGuardOfferTests
{
    // -----------------------------------------------------------------
    // Pure unit tests — WalletSufficiencyGuard, no HTTP.
    // -----------------------------------------------------------------

    [Fact]
    public async Task CheckAsync_Insufficient_ReturnsNotAllowed_WithNeededAvailableCurrency()
    {
        var guard = NewGuard(new FakeWalletClient { Balance = 1.0 }, "fail-closed");

        var result = await guard.CheckAsync(Guid.NewGuid(), requiredFee: 5.0m, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.Required.Should().Be(5.0m);
        result.Available.Should().Be(1.0m);
        result.Currency.Should().BeNull(); // no ISO mapping exists — honest, not fabricated.
    }

    [Fact]
    public async Task CheckAsync_AtBoundary_AvailableEqualsRequired_IsAllowed()
    {
        var guard = NewGuard(new FakeWalletClient { Balance = 5.0 }, "fail-closed");

        var result = await guard.CheckAsync(Guid.NewGuid(), requiredFee: 5.0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_MultiCurrency_SumsOnlyTheDominantGroup()
    {
        var holderId = Guid.NewGuid();
        var fake = new DominantCurrencyWalletClient(holderId);
        var guard = NewGuard(fake, "fail-closed");

        // Dominant group (2 wallets, currency 1) totals 3.0; the lone currency-2 wallet
        // (100.0) must NOT be blended in.
        var result = await guard.CheckAsync(holderId, requiredFee: 3.0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Available.Should().Be(3.0m);
    }

    [Fact]
    public async Task CheckAsync_WalletServiceUnreachable_FailClosed_IsNotAllowed()
    {
        var guard = NewGuard(new FakeWalletClient { Unreachable = true }, "fail-closed");

        var result = await guard.CheckAsync(Guid.NewGuid(), requiredFee: 1.0m, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DegradedByUpstreamFailure.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_WalletServiceUnreachable_FailOpen_IsAllowed()
    {
        var guard = NewGuard(new FakeWalletClient { Unreachable = true }, "fail-open");

        var result = await guard.CheckAsync(Guid.NewGuid(), requiredFee: 1.0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.DegradedByUpstreamFailure.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_CircuitBreakerOpen_FailOpen_IsAllowed()
    {
        // Correction 8 — the breaker-open exception, not just WalletApiException/timeout.
        var guard = NewGuard(new BreakerOpenWalletClient(), "fail-open");

        var result = await guard.CheckAsync(Guid.NewGuid(), requiredFee: 1.0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.DegradedByUpstreamFailure.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_CircuitBreakerOpen_FailClosed_IsNotAllowed()
    {
        var guard = NewGuard(new BreakerOpenWalletClient(), "fail-closed");

        var result = await guard.CheckAsync(Guid.NewGuid(), requiredFee: 1.0m, CancellationToken.None);

        result.Allowed.Should().BeFalse();
    }

    private static WalletSufficiencyGuard NewGuard(SwServiceWalletClient wallet, string failMode)
        => new(wallet, Options.Create(new WalletGuardOptions { FailMode = failMode }),
            NullLogger<WalletSufficiencyGuard>.Instance);

    // -----------------------------------------------------------------
    // Guard 1 — POST /requests/{id}/offers (submit).
    // -----------------------------------------------------------------

    [Fact]
    public async Task Submit_Returns402_WhenAvailableBalanceBelowCommission()
    {
        // fee=100 → required commission = 10.0; wallet only has 1.0.
        await using var factory = NewFactory(new FakeWalletClient { Balance = 1.0 });
        var (_, requestId) = await SeedRequestAsync(factory);
        var jeeberId = Guid.NewGuid().ToString();

        var resp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = 100m, etaMinutes = 30, note = (string?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);

        // Correction 5 — raw JSON, top-level (not nested under an "errors" object).
        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
        body["needed"]!.Value<decimal>().Should().Be(10.0m);
        body["available"]!.Value<decimal>().Should().Be(1.0m);
        body["type"]!.Value<string>().Should().Be("https://jeeb.dev/errors/insufficient-wallet-balance");
    }

    [Fact]
    public async Task Submit_Returns201_AtBoundary_AvailableEqualsRequired()
    {
        // fee=100 → required = 10.0; wallet has EXACTLY 10.0.
        await using var factory = NewFactory(new FakeWalletClient { Balance = 10.0 });
        var (_, requestId) = await SeedRequestAsync(factory);
        var jeeberId = Guid.NewGuid().ToString();

        var resp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = 100m, etaMinutes = 30, note = (string?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Submit_FailsOpen_WhenWalletServiceUnavailable_AndFailModeIsFailOpen()
    {
        await using var factory = NewFactory(new FakeWalletClient { Unreachable = true }, failMode: "fail-open");
        var (_, requestId) = await SeedRequestAsync(factory);
        var jeeberId = Guid.NewGuid().ToString();

        var resp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = 100m, etaMinutes = 30, note = (string?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Submit_FailsClosed_WhenWalletServiceUnavailable_AndFailModeIsFailClosed_Default()
    {
        await using var factory = NewFactory(new FakeWalletClient { Unreachable = true }, failMode: "fail-closed");
        var (_, requestId) = await SeedRequestAsync(factory);
        var jeeberId = Guid.NewGuid().ToString();

        var resp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = 100m, etaMinutes = 30, note = (string?)null });

        // An outage is a distinct 503, never a fabricated "insufficient balance" 402.
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
        body["type"]!.Value<string>().Should().Be("https://jeeb.dev/errors/wallet-service-unavailable");
    }

    [Fact]
    public void RequiredCommission_RoundsAwayFromZero_MatchingCommissionCalculator()
    {
        // fee=100.25 → 10.025; banker's rounding would give 10.02, settlement charges 10.03.
        WalletGuardContract.RequiredCommission(100.25m).Should().Be(10.03m);
        WalletGuardContract.RequiredCommission(100.25m)
            .Should().Be(CommissionCalculator.Calculate(100.25m, CommissionTier.Standard).Commission);
    }

    // -----------------------------------------------------------------
    // Guard 2 — POST /v1/offers/{id}/accept.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Accept_Returns409InsufficientBalance_WhenJeeberBalanceDroppedSinceSubmit()
    {
        var wallet = new FakeWalletClient { Balance = 10.0 }; // sufficient at submit
        var offerService = new RecordingOfferServiceClient();
        await using var factory = NewFactory(wallet, offerService: offerService);

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var jeeberId = Guid.NewGuid().ToString();

        var submitResp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = 100m, etaMinutes = 30, note = (string?)null });
        submitResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var offerId = (await submitResp.Content.ReadFromJsonAsync<OfferDto>())!.Id;

        wallet.Balance = 1.0; // balance drops before the client accepts

        var acceptResp = await ClientActor(factory, clientId).PostAsync(
            $"/v1/offers/{offerId}/accept", content: null);

        acceptResp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JObject.Parse(await acceptResp.Content.ReadAsStringAsync());
        body["type"]!.Value<string>().Should().Be("https://jeeb.dev/errors/offer-jeeber-insufficient-balance");
        offerService.AcceptWithStatusCalled.Should().BeFalse("guard 2 must short-circuit before forwarding upstream");
    }

    [Fact]
    public async Task Accept_AutoWithdrawsStaleOffer_OnInsufficientBalance()
    {
        var wallet = new FakeWalletClient { Balance = 10.0 };
        var offerService = new RecordingOfferServiceClient();
        await using var factory = NewFactory(wallet, offerService: offerService);

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var jeeberId = Guid.NewGuid().ToString();

        var submitResp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = 100m, etaMinutes = 30, note = (string?)null });
        var offerId = (await submitResp.Content.ReadFromJsonAsync<OfferDto>())!.Id;

        wallet.Balance = 1.0;
        await ClientActor(factory, clientId).PostAsync($"/v1/offers/{offerId}/accept", content: null);

        var offers = factory.Services.GetRequiredService<FakePendingOffersStore>();
        var offer = (await offers.ListForRequestAsync(requestId, CancellationToken.None))
            .Single(o => o.Id == offerId);
        offer.Status.Should().Be(PendingOfferStatus.Withdrawn);
    }

    [Fact]
    public async Task Accept_Returns503_AndDoesNotWithdraw_WhenWalletUnreachable_FailClosed()
    {
        var wallet = new FakeWalletClient { Balance = 10.0 };
        var offerService = new RecordingOfferServiceClient();
        await using var factory = NewFactory(wallet, offerService: offerService);

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var jeeberId = Guid.NewGuid().ToString();

        var submitResp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = 100m, etaMinutes = 30, note = (string?)null });
        var offerId = (await submitResp.Content.ReadFromJsonAsync<OfferDto>())!.Id;

        wallet.Unreachable = true; // outage, NOT insufficiency

        var acceptResp = await ClientActor(factory, clientId).PostAsync(
            $"/v1/offers/{offerId}/accept", content: null);

        acceptResp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        offerService.AcceptWithStatusCalled.Should().BeFalse();
        var offer = (await factory.Services.GetRequiredService<FakePendingOffersStore>()
                .ListForRequestAsync(requestId, CancellationToken.None))
            .Single(o => o.Id == offerId);
        offer.Status.Should().Be(PendingOfferStatus.Pending, "an outage must never withdraw the offer");
    }

    [Fact]
    public async Task Accept_Replay_AfterUpstreamSuccess_DoesNotWithdrawAcceptedOffer()
    {
        // Correction 7 — a retry hitting guard 2 after the offer already accepted must
        // swallow the withdraw's NotPending outcome, never crash, and stay 409.
        var wallet = new FakeWalletClient { Balance = 10.0 };
        var offerService = new RecordingOfferServiceClient();
        await using var factory = NewFactory(wallet, offerService: offerService);

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var jeeberId = Guid.NewGuid().ToString();

        var submitResp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = 100m, etaMinutes = 30, note = (string?)null });
        var offerId = (await submitResp.Content.ReadFromJsonAsync<OfferDto>())!.Id;

        // Simulate "already accepted upstream" directly on the store, bypassing the
        // full accept orchestration (irrelevant to this guard's replay behaviour).
        var offers = factory.Services.GetRequiredService<FakePendingOffersStore>();
        (await offers.AcceptAsync(offerId, DateTimeOffset.UtcNow, CancellationToken.None))
            .Should().BeTrue();

        wallet.Balance = 1.0; // now insufficient on the replay

        var acceptResp = await ClientActor(factory, clientId).PostAsync(
            $"/v1/offers/{offerId}/accept", content: null);

        // The guard still 409s the replay (residual, documented risk) — but the
        // withdraw side effect must not surface a failure on top of it.
        acceptResp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var reread = (await offers.ListForRequestAsync(requestId, CancellationToken.None))
            .Single(o => o.Id == offerId);
        reread.Status.Should().Be(PendingOfferStatus.Accepted, "NotPending must be swallowed, not overwrite the accepted state");
    }

    // -----------------------------------------------------------------
    // Guard 3 — PUT /v1/offers/{offerId} (edit).
    // -----------------------------------------------------------------

    [Fact]
    public async Task Edit_Returns402_WhenRaisedFeeExceedsBalance()
    {
        var wallet = new FakeWalletClient { Balance = 1.0 };
        var offerService = new RecordingOfferServiceClient();
        await using var factory = NewFactory(wallet, offerService: offerService);

        var (_, requestId) = await SeedRequestAsync(factory);
        var jeeberId = Guid.NewGuid().ToString();
        offerService.JeeberFeed.Add(new JeeberFeedOffer
        {
            OfferId = "offer-1", RequestId = requestId, Status = "pending", FeeCents = 100_00,
        });
        SeedRoutingIndex(factory, "offer-1", requestId);

        var resp = await JeeberClient(factory, jeeberId).PutAsJsonAsync(
            "/v1/offers/offer-1", new { fee = 500m });

        resp.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        offerService.EditCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Edit_Skips_WhenFeeLoweredOrUnchanged()
    {
        var wallet = new FakeWalletClient { Balance = 0 }; // would fail any raised-fee check
        var offerService = new RecordingOfferServiceClient();
        await using var factory = NewFactory(wallet, offerService: offerService);

        var (_, requestId) = await SeedRequestAsync(factory);
        var jeeberId = Guid.NewGuid().ToString();
        offerService.JeeberFeed.Add(new JeeberFeedOffer
        {
            OfferId = "offer-2", RequestId = requestId, Status = "pending", FeeCents = 100_00,
        });
        SeedRoutingIndex(factory, "offer-2", requestId);

        var resp = await JeeberClient(factory, jeeberId).PutAsJsonAsync(
            "/v1/offers/offer-2", new { fee = 100m }); // unchanged, not raised

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        offerService.EditCalled.Should().BeTrue();
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(
        FakeWalletClient wallet, string failMode = "fail-closed", RecordingOfferServiceClient? offerService = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "WalletGuard:FailMode", failMode },
                    { "FeatureFlags:UseUpstream:Offer", "true" },
                }));
            builder.ConfigureTestServices(services =>
            {
                FakeOfferStoreWebApplicationFactory.UseFakeOfferStore(services);
                services.RemoveAll<SwServiceWalletClient>();
                services.AddScoped<SwServiceWalletClient>(_ => wallet);
                if (offerService is not null)
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton<IOfferServiceClient>(offerService);
                }
            });
        });

    private static async Task<(string ClientId, string RequestId)> SeedRequestAsync(WebApplicationFactory<Program> factory)
    {
        var clientId = $"client-{Guid.NewGuid()}";
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            // D2: the offer/feed range guard needs a resolvable tier + pickup point.
            TierId = Fakes.InRangeGeoFixture.TierId,
            PickupLocation = new GeoPoint
            {
                Lat = Fakes.InRangeGeoFixture.Lat,
                Lng = Fakes.InRangeGeoFixture.Lng,
            },
            ClientId = clientId,
            Description = "Pick up a package",
        }, CancellationToken.None);
        return (clientId, created.Id);
    }

    /// <summary>Guard 3's edit route resolves requestId from the routing index, learned
    /// at submit — seed it directly since these tests fabricate the offerId.</summary>
    private static void SeedRoutingIndex(WebApplicationFactory<Program> factory, string offerId, string requestId)
    {
        var index = factory.Services.GetRequiredService<IOfferRequestIndex>();
        index.Record(offerId, requestId, null);
    }

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string jeeberId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return c;
    }

    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string clientId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", clientId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "client");
        return c;
    }

    /// <summary>Two active wallets on currency 1 (dominant, sums to 3.0) plus one lone
    /// wallet on currency 2 (100.0) that must never blend into the compare.</summary>
    private sealed class DominantCurrencyWalletClient : SwServiceWalletClient
    {
        private readonly Guid _holderId;
        public DominantCurrencyWalletClient(Guid holderId) : base("http://localhost", new HttpClient())
            => _holderId = holderId;

        public override Task<JeebGateway.service.ServiceWallet.GetHolderWallets> WalletsAsync(Guid holderId, CancellationToken ct)
            => Task.FromResult(new JeebGateway.service.ServiceWallet.GetHolderWallets
            {
                WalletHolder = new JeebGateway.service.ServiceWallet.WalletHolder { HolderId = _holderId, IsActive = true },
                Wallets = new List<JeebGateway.service.ServiceWallet.Wallet>
                {
                    new() { WalletId = Guid.NewGuid(), HolderId = _holderId, CurrencyID = 1, Amount = 2.0, IsActive = true },
                    new() { WalletId = Guid.NewGuid(), HolderId = _holderId, CurrencyID = 1, Amount = 1.0, IsActive = true },
                    new() { WalletId = Guid.NewGuid(), HolderId = _holderId, CurrencyID = 2, Amount = 100.0, IsActive = true },
                },
            });

        public override Task<JeebGateway.service.ServiceWallet.GetHolderWallets> WalletsAsync(Guid holderId)
            => WalletsAsync(holderId, CancellationToken.None);
    }

    private sealed class BreakerOpenWalletClient : SwServiceWalletClient
    {
        public BreakerOpenWalletClient() : base("http://localhost", new HttpClient())
        {
        }

        public override Task<JeebGateway.service.ServiceWallet.GetHolderWallets> WalletsAsync(Guid holderId, CancellationToken ct)
            => throw new Polly.CircuitBreaker.BrokenCircuitException("simulated breaker open");

        public override Task<JeebGateway.service.ServiceWallet.GetHolderWallets> WalletsAsync(Guid holderId)
            => WalletsAsync(holderId, CancellationToken.None);
    }

    /// <summary>Records whether AcceptWithStatusAsync/EditAsync were invoked, so guard 2/3
    /// tests can assert the short-circuit fires before the upstream forward.</summary>
    private sealed class RecordingOfferServiceClient : IOfferServiceClient
    {
        public bool AcceptWithStatusCalled { get; private set; }
        public bool EditCalled { get; private set; }
        public List<JeeberFeedOffer> JeeberFeed { get; } = new();

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
        {
            AcceptWithStatusCalled = true;
            return Task.FromResult(new OfferAcceptResult { Status = OfferAcceptStatus.Accepted });
        }

        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId, long? feeCents, int? etaMinutes,
            string? note, int? maxEdits, CancellationToken ct)
        {
            EditCalled = true;
            return Task.FromResult(new OfferMutationResult { Status = OfferMutationStatus.Ok });
        }

        public Task<IReadOnlyList<JeeberFeedOffer>> ListOffersForJeeberAsync(
            string jeeberId, string? status, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JeeberFeedOffer>>(JeeberFeed);

        public Task<OfferAcceptWire> AcceptAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<RequestMirrorResult> MirrorRequestAsync(
            string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
