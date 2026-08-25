using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
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
        result.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task CheckAsync_AtBoundary_AvailableEqualsRequired_IsAllowed()
    {
        var guard = NewGuard(new FakeWalletClient { Balance = 5.0 }, "fail-closed");

        var result = await guard.CheckAsync(Guid.NewGuid(), requiredFee: 5.0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_MultiCurrency_SumsOnlyTheFeeCurrencyGroup()
    {
        var holderId = Guid.NewGuid();
        var fake = new DominantCurrencyWalletClient(holderId);
        var guard = NewGuard(fake, "fail-closed");

        // Fee currency 2 totals 3.0; the lone currency-1 wallet (100.0) must NOT blend in.
        var result = await guard.CheckAsync(holderId, requiredFee: 3.0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Available.Should().Be(3.0m);
        result.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task CheckAsync_PinsToFeeCurrency_EvenWhenAnotherCurrencyHasMoreWallets()
    {
        // Currency 1 carries more wallets; the pin follows the fee currency, not the count.
        var holderId = Guid.NewGuid();
        var guard = NewGuard(
            new TypedWalletClient(holderId, ("jeeb", 2, 50.0), ("jeeb", 1, 0.0), ("bonus", 1, 0.0)),
            "fail-closed");

        var result = await guard.CheckAsync(holderId, requiredFee: 5.0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Available.Should().Be(50.0m);
        result.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task CheckAsync_ReturnsConfiguredCurrencyCode()
    {
        var guard = NewGuard(new FakeWalletClient { Balance = 5.0 }, "fail-closed");

        var result = await guard.CheckAsync(Guid.NewGuid(), requiredFee: 5.0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task CheckAsync_NoWalletInFeeCurrency_ReturnsZeroInFeeCurrency()
    {
        // 100.0 sits on currency 1 — an honest zero in the fee currency, not an outage.
        var guard = NewGuard(new FakeWalletClient { Balance = 100.0, CurrencyId = 1 }, "fail-closed");

        var result = await guard.CheckAsync(Guid.NewGuid(), requiredFee: 1.0m, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.Available.Should().Be(0m);
        result.Currency.Should().Be("USD");
        result.DegradedByUpstreamFailure.Should().BeFalse();
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

    // ----- R-M1 (G-01): the guard compares against SPENDABLE balance only -----

    [Theory]
    [InlineData("cod_earnings")]
    [InlineData("cod_commission")]
    [InlineData("COD_Insurance")]
    public async Task CheckAsync_Ignores_NonSpendable_Cod_Types(string codType)
    {
        var holderId = Guid.NewGuid();
        var guard = NewGuard(new TypedWalletClient(holderId, (codType, 2, 5_000.0)), "fail-closed");

        var result = await guard.CheckAsync(holderId, requiredFee: 5.0m, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.Available.Should().Be(0m);
        result.DegradedByUpstreamFailure.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("codex")]
    [InlineData("topup")]
    public async Task CheckAsync_Still_Counts_Every_Non_Cod_Type(string? spendableType)
    {
        // Control case: were the pin a blanket exclusion, this would fail closed and block
        // every legitimate offer — untyped wallets are what live holders carry today.
        var holderId = Guid.NewGuid();
        var guard = NewGuard(new TypedWalletClient(holderId, (spendableType, 2, 5.0)), "fail-closed");

        var result = await guard.CheckAsync(holderId, requiredFee: 5.0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Available.Should().Be(5.0m);
    }

    [Fact]
    public async Task CheckAsync_Cod_Types_On_The_Fee_Currency_Are_Still_Excluded()
    {
        // Two fat cod_* legs sit on the fee currency itself: the pin narrows the currency,
        // the spendable-type filter is what still keeps them out of the compare.
        var holderId = Guid.NewGuid();
        var guard = NewGuard(
            new TypedWalletClient(holderId,
                (null, 2, 5.0), ("cod_earnings", 2, 900.0), ("cod_commission", 2, 900.0)),
            "fail-closed");

        var result = await guard.CheckAsync(holderId, requiredFee: 5.0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Available.Should().Be(5.0m);
    }

    private static WalletSufficiencyGuard NewGuard(
        SwServiceWalletClient wallet, string failMode, int feeCurrencyId = 2, string feeCurrencyCode = "USD")
        => new(wallet, Options.Create(new WalletGuardOptions { FailMode = failMode }),
            Options.Create(new CommissionCollectionOptions
            {
                CurrencyId = feeCurrencyId, CurrencyCode = feeCurrencyCode,
            }),
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
    public async Task Guard_ErrorPayload_NamesCurrency_NotNull()
    {
        // The jeeber must be told WHICH currency fell short — never a null label.
        await using var factory = NewFactory(new FakeWalletClient { Balance = 1.0 });
        var (_, requestId) = await SeedRequestAsync(factory);
        var jeeberId = Guid.NewGuid().ToString();

        var resp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = 100m, etaMinutes = 30, note = (string?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
        body["currency"]!.Value<string>().Should().Be("USD");
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

    // c2-1 (E3) — a non-GUID caller/winner is a HARD 403: structural, NOT FailMode-governed,
    // since an id that can never be balance-checked has no desirable skip configuration.

    [Fact]
    public async Task Submit_Returns403_WhenCallerIdIsNotGuid()
    {
        await using var factory = NewFactory(new FakeWalletClient { Balance = 10.0 });
        var (_, requestId) = await SeedRequestAsync(factory);

        var resp = await JeeberClient(factory, "not-a-guid").PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = 100m, etaMinutes = 30, note = (string?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
        body["type"]!.Value<string>().Should().Be("https://jeeb.dev/errors/wallet-holder-unresolved");

        var offers = await factory.Services.GetRequiredService<FakePendingOffersStore>()
            .ListForRequestAsync(requestId, CancellationToken.None);
        offers.Should().BeEmpty("the deny fires BEFORE the mint — an unguarded offer must not exist");
    }

    [Fact]
    public async Task Submit_Returns201_WhenCallerIdIsGuid()
    {
        // Regression pin: the hard deny must not swallow the healthy path.
        await using var factory = NewFactory(new FakeWalletClient { Balance = 10.0 });
        var (_, requestId) = await SeedRequestAsync(factory);

        var resp = await JeeberClient(factory, Guid.NewGuid().ToString()).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = 100m, etaMinutes = 30, note = (string?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Edit_Returns403_WhenActorIdNotGuid_OnRaise()
    {
        var wallet = new FakeWalletClient { Balance = 1_000.0 }; // balance is never the question here
        var offerService = new RecordingOfferServiceClient();
        await using var factory = NewFactory(wallet, offerService: offerService);

        var (_, requestId) = await SeedRequestAsync(factory);
        offerService.JeeberFeed.Add(new JeeberFeedOffer
        {
            OfferId = "offer-403", RequestId = requestId, Status = "pending", FeeCents = 100_00,
        });
        SeedRoutingIndex(factory, "offer-403", requestId);

        var resp = await JeeberClient(factory, "not-a-guid").PutAsJsonAsync(
            "/v1/offers/offer-403", new { fee = 500m }); // a confirmed RAISE

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
        body["type"]!.Value<string>().Should().Be("https://jeeb.dev/errors/wallet-holder-unresolved");
        offerService.EditCalled.Should().BeFalse("the raise must never reach offer-service unguarded");
    }

    [Fact]
    public async Task Accept_Returns403_WhenWinningJeeberIdBlank()
    {
        var offerService = new RecordingOfferServiceClient();
        await using var factory = NewFactory(new FakeWalletClient { Balance = 10.0 }, offerService: offerService);

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var offers = factory.Services.GetRequiredService<FakePendingOffersStore>();
        var offer = offers.EnqueueForTest(Guid.NewGuid().ToString(), requestId);
        SeedRoutingIndex(factory, offer.Id, requestId); // winner recorded as null

        var resp = await ClientActor(factory, clientId).PostAsync(
            $"/v1/offers/{offer.Id}/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
        body["type"]!.Value<string>().Should().Be("https://jeeb.dev/errors/wallet-holder-unresolved");
        offerService.AcceptWithStatusCalled.Should().BeFalse();

        var reread = (await offers.ListForRequestAsync(requestId, CancellationToken.None))
            .Single(o => o.Id == offer.Id);
        reread.Status.Should().Be(PendingOfferStatus.Pending, "an unresolvable winner must not withdraw the bid");
    }

    [Fact]
    public async Task Accept_Returns403_WhenWinningJeeberIdNotGuid()
    {
        var offerService = new RecordingOfferServiceClient();
        await using var factory = NewFactory(new FakeWalletClient { Balance = 10.0 }, offerService: offerService);

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var offers = factory.Services.GetRequiredService<FakePendingOffersStore>();
        var offer = offers.EnqueueForTest("not-a-guid", requestId);
        SeedRoutingIndex(factory, offer.Id, requestId, "not-a-guid");

        var resp = await ClientActor(factory, clientId).PostAsync(
            $"/v1/offers/{offer.Id}/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
        body["type"]!.Value<string>().Should().Be("https://jeeb.dev/errors/wallet-holder-unresolved");
        offerService.AcceptWithStatusCalled.Should().BeFalse("a non-GUID winner must never be forwarded");

        var reread = (await offers.ListForRequestAsync(requestId, CancellationToken.None))
            .Single(o => o.Id == offer.Id);
        reread.Status.Should().Be(PendingOfferStatus.Pending);
    }

    // c2-2 (E4) — a DEGRADED fee lookup routes through the ONE FailMode knob, exactly like a
    // wallet-service outage; a genuine 2xx that simply carries no fee stays a benign skip.

    [Fact]
    public async Task Edit_Returns503_WhenCurrentFeeLookupDegraded_FailClosed()
    {
        var offerService = new RecordingOfferServiceClient { JeeberFeedDegraded = true };
        await using var factory = NewFactory(new FakeWalletClient { Balance = 1_000.0 }, offerService: offerService);

        var (_, requestId) = await SeedRequestAsync(factory);
        offerService.JeeberFeed.Add(new JeeberFeedOffer
        {
            OfferId = "offer-degraded", RequestId = requestId, Status = "pending", FeeCents = 100_00,
        });
        SeedRoutingIndex(factory, "offer-degraded", requestId);

        var resp = await JeeberClient(factory, Guid.NewGuid().ToString()).PutAsJsonAsync(
            "/v1/offers/offer-degraded", new { fee = 500m });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
        body["type"]!.Value<string>().Should().Be("https://jeeb.dev/errors/offer-fee-unresolvable");
        offerService.EditCalled.Should().BeFalse("a lookup that could not run must not admit the raise");
    }

    [Fact]
    public async Task Edit_Proceeds_WhenCurrentFeeLookupDegraded_FailOpen()
    {
        var offerService = new RecordingOfferServiceClient { JeeberFeedDegraded = true };
        await using var factory = NewFactory(
            new FakeWalletClient { Balance = 0 }, failMode: "fail-open", offerService: offerService);

        var (_, requestId) = await SeedRequestAsync(factory);
        offerService.JeeberFeed.Add(new JeeberFeedOffer
        {
            OfferId = "offer-failopen", RequestId = requestId, Status = "pending", FeeCents = 100_00,
        });
        SeedRoutingIndex(factory, "offer-failopen", requestId);

        var resp = await JeeberClient(factory, Guid.NewGuid().ToString()).PutAsJsonAsync(
            "/v1/offers/offer-failopen", new { fee = 500m });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        offerService.EditCalled.Should().BeTrue("fail-open proceeds unchecked, with a WARN");
    }

    [Fact]
    public async Task Accept_Returns503_WhenFeeLookupDegraded_FailClosed_AndDoesNotWithdraw()
    {
        var leg = await RunAcceptWithDegradedDependencyAsync(
            DegradedDependency.OfferFeeRead, failMode: "fail-closed");

        leg.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
        leg.ProblemType.Should().Be("https://jeeb.dev/errors/offer-fee-unresolvable");
        leg.Forwarded.Should().BeFalse();
        leg.OfferStatus.Should().Be(PendingOfferStatus.Pending,
            "insufficiency was never confirmed — a degrade must not withdraw the offer");
    }

    [Fact]
    public async Task Accept_Proceeds_WhenFeeLookupDegraded_FailOpen()
    {
        var leg = await RunAcceptWithDegradedDependencyAsync(
            DegradedDependency.OfferFeeRead, failMode: "fail-open");

        leg.Forwarded.Should().BeTrue("fail-open proceeds unchecked, with a WARN");
        leg.Status.Should().NotBe(HttpStatusCode.ServiceUnavailable);
        leg.OfferStatus.Should().NotBe(PendingOfferStatus.Withdrawn);
    }

    [Fact]
    public async Task Accept_Proceeds_WhenOfferGenuinelyAbsentFrom2xxRead_BenignSkip()
    {
        // CONTROL: a healthy 2xx read that simply does not carry the offer (a normal
        // withdraw) leaves the fee unresolvable but NOT degraded — it must still forward.
        var offerService = new RecordingOfferServiceClient();
        await using var factory = NewFactory(new FakeWalletClient { Balance = 0 }, offerService: offerService);

        var (clientId, requestId) = await SeedRequestAsync(factory);
        SeedRoutingIndex(factory, "offer-gone", requestId, Guid.NewGuid().ToString());

        var resp = await ClientActor(factory, clientId).PostAsync(
            "/v1/offers/offer-gone/accept", content: null);

        resp.StatusCode.Should().NotBe(HttpStatusCode.ServiceUnavailable,
            "a benign absent offer must never be over-corrected into a fee-unresolvable 503");
        offerService.AcceptWithStatusCalled.Should().BeTrue("nothing to enforce — the accept proceeds");
    }

    // c2-3 — the ONE FailMode knob, pinned symmetric across both faults.

    [Fact]
    public async Task Accept_FailClosed_DeniesSymmetrically_ForWalletOutageAndForFeeBlip()
    {
        var walletOutage = await RunAcceptWithDegradedDependencyAsync(
            DegradedDependency.WalletService, failMode: "fail-closed");
        var feeBlip = await RunAcceptWithDegradedDependencyAsync(
            DegradedDependency.OfferFeeRead, failMode: "fail-closed");

        // Same 503 deny + same absence of side effects; only the Type names the upstream.
        walletOutage.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
        walletOutage.ProblemType.Should().Be("https://jeeb.dev/errors/wallet-service-unavailable");
        feeBlip.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
        feeBlip.ProblemType.Should().Be("https://jeeb.dev/errors/offer-fee-unresolvable");

        walletOutage.Forwarded.Should().BeFalse();
        feeBlip.Forwarded.Should().BeFalse();
        walletOutage.OfferStatus.Should().Be(PendingOfferStatus.Pending);
        feeBlip.OfferStatus.Should().Be(PendingOfferStatus.Pending);
    }

    [Fact]
    public async Task Accept_FailOpen_ProceedsSymmetrically_ForWalletOutageAndForFeeBlip()
    {
        var walletOutage = await RunAcceptWithDegradedDependencyAsync(
            DegradedDependency.WalletService, failMode: "fail-open");
        var feeBlip = await RunAcceptWithDegradedDependencyAsync(
            DegradedDependency.OfferFeeRead, failMode: "fail-open");

        // The knob flips BOTH branches together — neither fault denies under fail-open.
        walletOutage.Forwarded.Should().BeTrue();
        feeBlip.Forwarded.Should().BeTrue();
        walletOutage.Status.Should().NotBe(HttpStatusCode.ServiceUnavailable);
        feeBlip.Status.Should().NotBe(HttpStatusCode.ServiceUnavailable);
        walletOutage.OfferStatus.Should().NotBe(PendingOfferStatus.Withdrawn);
        feeBlip.OfferStatus.Should().NotBe(PendingOfferStatus.Withdrawn);
    }

    [Fact]
    public void Config_Default_FailMode_IsFailClosed()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindRepoRoot(), "src", "JeebGateway", "appsettings.json"))
            .Build();

        var options = new WalletGuardOptions();
        config.GetSection(WalletGuardOptions.SectionName).Bind(options);

        options.FailMode.Should().Be("fail-closed");
        options.IsFailOpen.Should().BeFalse("the SHIPPED default must never silently flip to fail-open");
    }

    // -----------------------------------------------------------------
    // c1 (G1) — AGGREGATE exposure, the per-jeeber live cap, STRICT enumeration.
    // Layer A admission (Holds:Enabled=false, NewFactory's default): the balance is
    // measured against the jeeber's WHOLE live offer set, never one isolated bid.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Submit_Returns402_WhenAggregateExposureExceedsBalance_AcrossTwoRequests()
    {
        // 10.0 covers exactly ONE $100 offer's 10%; the second bid must see the first's exposure.
        await using var factory = NewFactory(new FakeWalletClient { Balance = 10.0 });
        var (_, requestA) = await SeedRequestAsync(factory);
        var (_, requestB) = await SeedRequestAsync(factory);
        var jeeber = JeeberClient(factory, Guid.NewGuid().ToString());

        var first = await jeeber.PostAsJsonAsync($"/requests/{requestA}/offers", OfferBody(100m));
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await jeeber.PostAsJsonAsync($"/requests/{requestB}/offers", OfferBody(100m));

        second.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        var body = JObject.Parse(await second.Content.ReadAsStringAsync());
        body["type"]!.Value<string>().Should().Be("https://jeeb.dev/errors/insufficient-wallet-balance");
        body["outstanding"]!.Value<decimal>().Should().Be(10m);
        body["thisOffer"]!.Value<decimal>().Should().Be(10m);
        body["needed"]!.Value<decimal>().Should().Be(20m, "needed is the AGGREGATE: this offer plus every live one");
        body["available"]!.Value<decimal>().Should().Be(10m);
        body["currency"]!.Value<string>().Should().Be("USD");
    }

    [Fact]
    public async Task Submit_Returns201_WhenAggregateWithinBalance()
    {
        // Control: 20.0 covers BOTH $100 offers' 10% — aggregation must not over-block.
        await using var factory = NewFactory(new FakeWalletClient { Balance = 20.0 });
        var (_, requestA) = await SeedRequestAsync(factory);
        var (_, requestB) = await SeedRequestAsync(factory);
        var jeeber = JeeberClient(factory, Guid.NewGuid().ToString());

        var first = await jeeber.PostAsJsonAsync($"/requests/{requestA}/offers", OfferBody(100m));
        var second = await jeeber.PostAsJsonAsync($"/requests/{requestB}/offers", OfferBody(100m));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created, "the aggregate lands exactly on the balance");
    }

    [Fact]
    public async Task Submit_Returns409_WhenLiveOfferCapReached()
    {
        // Balance is never the question here — the cap denies on multiplicity alone.
        await using var factory = NewFactory(
            new FakeWalletClient { Balance = 1_000.0 }, maxLiveOffersPerJeeber: 2);
        var (_, requestA) = await SeedRequestAsync(factory);
        var (_, requestB) = await SeedRequestAsync(factory);
        var (_, requestC) = await SeedRequestAsync(factory);
        var jeeber = JeeberClient(factory, Guid.NewGuid().ToString());

        (await jeeber.PostAsJsonAsync($"/requests/{requestA}/offers", OfferBody(100m)))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await jeeber.PostAsJsonAsync($"/requests/{requestB}/offers", OfferBody(100m)))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var third = await jeeber.PostAsJsonAsync($"/requests/{requestC}/offers", OfferBody(100m));

        third.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JObject.Parse(await third.Content.ReadAsStringAsync());
        body["type"]!.Value<string>().Should().Be("https://jeeb.dev/errors/offer-live-limit-reached");
        body["limit"]!.Value<int>().Should().Be(2);
        body["live"]!.Value<int>().Should().Be(2);

        var offers = await factory.Services.GetRequiredService<FakePendingOffersStore>()
            .ListForRequestAsync(requestC, CancellationToken.None);
        offers.Should().BeEmpty("the cap denies BEFORE the mint");
    }

    [Fact]
    public async Task Submit_CapExcludesTerminalOffers()
    {
        // Two WITHDRAWN offers plus one live one, cap 2: only the live one may count.
        await using var factory = NewFactory(
            new FakeWalletClient { Balance = 1_000.0 }, maxLiveOffersPerJeeber: 2);
        var (_, requestA) = await SeedRequestAsync(factory);
        var (_, requestB) = await SeedRequestAsync(factory);
        var (_, requestC) = await SeedRequestAsync(factory);
        var (_, requestD) = await SeedRequestAsync(factory);
        var jeeber = JeeberClient(factory, Guid.NewGuid().ToString());

        var offerA = await SubmitOfferIdAsync(jeeber, requestA);
        var offerB = await SubmitOfferIdAsync(jeeber, requestB);
        (await jeeber.DeleteAsync($"/requests/{requestA}/offers/{offerA}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await jeeber.DeleteAsync($"/requests/{requestB}/offers/{offerB}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await jeeber.PostAsJsonAsync($"/requests/{requestC}/offers", OfferBody(100m)))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var fourth = await jeeber.PostAsJsonAsync($"/requests/{requestD}/offers", OfferBody(100m));

        fourth.StatusCode.Should().Be(HttpStatusCode.Created,
            "withdrawn offers are terminal — a cap that counted them would lock the jeeber out");
    }

    [Fact]
    public async Task Submit_Returns503_OfferExposureUnresolvable_WhenEnumerationDegraded()
    {
        // OD-C1-3 STRICT: an unreadable offer set is never read as "no exposure".
        await using var factory = NewFactory(new FakeWalletClient { Balance = 1_000.0 });
        var (_, requestId) = await SeedRequestAsync(factory);
        var offers = factory.Services.GetRequiredService<FakePendingOffersStore>();
        offers.ForceListForJeeberDegraded = true;

        var resp = await JeeberClient(factory, Guid.NewGuid().ToString()).PostAsJsonAsync(
            $"/requests/{requestId}/offers", OfferBody(100m));

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
        body["type"]!.Value<string>().Should().Be("https://jeeb.dev/errors/offer-exposure-unresolvable");

        offers.ForceListForJeeberDegraded = false; // the verification re-read must be undegraded
        (await offers.ListForRequestAsync(requestId, CancellationToken.None))
            .Should().BeEmpty("the bid is BLOCKED — nothing minted, nothing held");
    }

    [Fact]
    public async Task Edit_Returns402_WhenRaisedFeePlusOutstandingExceedsBalance()
    {
        // The raise alone (15.0) fits in 20.0; the raise PLUS the sibling's 10.0 does not.
        var offerService = new RecordingOfferServiceClient();
        await using var factory = NewFactory(new FakeWalletClient { Balance = 20.0 }, offerService: offerService);

        var (_, requestA) = await SeedRequestAsync(factory);
        var (_, requestB) = await SeedRequestAsync(factory);
        offerService.JeeberFeed.Add(new JeeberFeedOffer
        {
            OfferId = "offer-edit-agg", RequestId = requestA, Status = "pending", FeeCents = 100_00,
        });
        offerService.JeeberFeed.Add(new JeeberFeedOffer
        {
            OfferId = "offer-edit-sibling", RequestId = requestB, Status = "pending", FeeCents = 100_00,
        });
        SeedRoutingIndex(factory, "offer-edit-agg", requestA);

        var resp = await JeeberClient(factory, Guid.NewGuid().ToString()).PutAsJsonAsync(
            "/v1/offers/offer-edit-agg", new { fee = 150m });

        resp.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
        body["type"]!.Value<string>().Should().Be("https://jeeb.dev/errors/insufficient-wallet-balance");
        body["thisOffer"]!.Value<decimal>().Should().Be(15m, "thisOffer is the NEW fee's commission");
        body["outstanding"]!.Value<decimal>().Should().Be(10m);
        body["needed"]!.Value<decimal>().Should().Be(25m);
        body["available"]!.Value<decimal>().Should().Be(20m);
        body["currency"]!.Value<string>().Should().Be("USD");
        offerService.EditCalled.Should().BeFalse("the raise must not reach offer-service unbacked");
    }

    [Fact]
    public async Task Edit_ExcludesEditedOfferFromOutstanding()
    {
        // The edited offer's OLD commission must not be added to its NEW one: raising the
        // only live offer to $200 needs exactly 20.0 — double-counting would need 30.0.
        var offerService = new RecordingOfferServiceClient();
        await using var factory = NewFactory(new FakeWalletClient { Balance = 20.0 }, offerService: offerService);

        var (_, requestId) = await SeedRequestAsync(factory);
        offerService.JeeberFeed.Add(new JeeberFeedOffer
        {
            OfferId = "offer-edit-solo", RequestId = requestId, Status = "pending", FeeCents = 100_00,
        });
        SeedRoutingIndex(factory, "offer-edit-solo", requestId);

        var resp = await JeeberClient(factory, Guid.NewGuid().ToString()).PutAsJsonAsync(
            "/v1/offers/offer-edit-solo", new { fee = 200m });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        offerService.EditCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Accept_Returns409_WhenAggregateExposureExceedsBalance()
    {
        // Both bids were affordable at submit (20.0); the balance then drops to cover only one.
        var wallet = new FakeWalletClient { Balance = 20.0 };
        var offerService = new RecordingOfferServiceClient();
        await using var factory = NewFactory(wallet, offerService: offerService);

        var (clientId, requestA) = await SeedRequestAsync(factory);
        var (_, requestB) = await SeedRequestAsync(factory);
        var jeeber = JeeberClient(factory, Guid.NewGuid().ToString());

        var offerA = await SubmitOfferIdAsync(jeeber, requestA);
        await SubmitOfferIdAsync(jeeber, requestB);

        wallet.Balance = 10.0; // the winner's own 10% still fits — the aggregate does not

        var acceptResp = await ClientActor(factory, clientId).PostAsync(
            $"/v1/offers/{offerA}/accept", content: null);

        acceptResp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JObject.Parse(await acceptResp.Content.ReadAsStringAsync());
        body["type"]!.Value<string>().Should().Be("https://jeeb.dev/errors/offer-jeeber-insufficient-balance");
        // E7 is DE-LEAKED (CONTRACT §7): the jeeber's figures never reach the client.
        ShouldNotCarry(body, "needed", "available", "currency", "outstanding", "thisOffer");
        offerService.AcceptWithStatusCalled.Should().BeFalse();

        var offer = (await factory.Services.GetRequiredService<FakePendingOffersStore>()
                .ListForRequestAsync(requestA, CancellationToken.None))
            .Single(o => o.Id == offerA);
        offer.Status.Should().Be(PendingOfferStatus.Withdrawn, "the unaffordable winner is auto-withdrawn");
    }

    [Fact]
    public async Task Accept_Returns503_OfferExposureUnresolvable_WhenEnumerationDegraded_AndDoesNotWithdraw()
    {
        var wallet = new FakeWalletClient { Balance = 10.0 };
        var offerService = new RecordingOfferServiceClient();
        await using var factory = NewFactory(wallet, offerService: offerService);

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var offerId = await SubmitOfferIdAsync(JeeberClient(factory, Guid.NewGuid().ToString()), requestId);

        // Only the JEEBER-scoped read degrades: the fee read stays healthy, so this is E5, not E4.
        var offers = factory.Services.GetRequiredService<FakePendingOffersStore>();
        offers.ForceListForJeeberDegraded = true;

        var acceptResp = await ClientActor(factory, clientId).PostAsync(
            $"/v1/offers/{offerId}/accept", content: null);

        acceptResp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = JObject.Parse(await acceptResp.Content.ReadAsStringAsync());
        body["type"]!.Value<string>().Should().Be("https://jeeb.dev/errors/offer-exposure-unresolvable");
        offerService.AcceptWithStatusCalled.Should().BeFalse();

        offers.ForceListForJeeberDegraded = false;
        var offer = (await offers.ListForRequestAsync(requestId, CancellationToken.None))
            .Single(o => o.Id == offerId);
        offer.Status.Should().Be(PendingOfferStatus.Pending,
            "insufficiency was never confirmed — a degrade must not withdraw the offer");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>Holds are OFF by default here: this class pins the LAYER A aggregate admission,
    /// which is the contract whenever <c>Holds:Enabled=false</c> (the rollback mode).</summary>
    private static WebApplicationFactory<Program> NewFactory(
        FakeWalletClient wallet, string failMode = "fail-closed", RecordingOfferServiceClient? offerService = null,
        bool holds = false, int? maxLiveOffersPerJeeber = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    { "WalletGuard:FailMode", failMode },
                    { "FeatureFlags:UseUpstream:Offer", "true" },
                    { "Holds:Enabled", holds ? "true" : "false" },
                };
                if (maxLiveOffersPerJeeber is int cap)
                {
                    settings["Offers:MaxLiveOffersPerJeeber"] = cap.ToString(CultureInfo.InvariantCulture);
                }

                cfg.AddInMemoryCollection(settings);
            });
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
    /// at submit — seed it directly since these tests fabricate the offerId. The optional
    /// jeeberId is the accept path's winner (null = the pre-c2-1 blank-winner shape).</summary>
    private static void SeedRoutingIndex(
        WebApplicationFactory<Program> factory, string offerId, string requestId, string? jeeberId = null)
    {
        var index = factory.Services.GetRequiredService<IOfferRequestIndex>();
        index.Record(offerId, requestId, jeeberId);
    }

    /// <summary>The submit body every c1 test posts — one quoted fee, fixed ETA, no note.</summary>
    private static object OfferBody(decimal fee)
        => new { fee, etaMinutes = 30, note = (string?)null };

    /// <summary>Submits a real offer over HTTP and returns its minted id, so a test's live
    /// exposure is built the same way production builds it (index recorded, ledger written).</summary>
    private static async Task<string> SubmitOfferIdAsync(HttpClient jeeber, string requestId, decimal fee = 100m)
    {
        var resp = await jeeber.PostAsJsonAsync($"/requests/{requestId}/offers", OfferBody(fee));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<OfferDto>())!.Id;
    }

    /// <summary>De-leak pin: the key is absent, or present-and-JSON-null — never a figure.</summary>
    private static void ShouldNotCarry(JObject body, params string[] keys)
    {
        foreach (var key in keys)
        {
            var token = body[key];
            (token is null || token.Type == JTokenType.Null).Should().BeTrue(
                "the client-facing body must not carry '{0}' (value was {1})", key, token);
        }
    }

    /// <summary>Locates the repo root from THIS source file so a test can read the SHIPPED
    /// appsettings.json (not the copy staged into the test bin folder).</summary>
    private static string FindRepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new FileInfo(thisFile).Directory!;
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "JeebGateway")))
            dir = dir.Parent!;

        (dir is not null).Should().BeTrue("could not locate repo root from the test source file path");
        return dir!.FullName;
    }

    /// <summary>Which single dependency degrades on an accept leg. Both faults mean "the
    /// balance check could not run", so the ONE FailMode knob must treat them identically.</summary>
    private enum DegradedDependency
    {
        WalletService,
        OfferFeeRead,
    }

    private sealed record AcceptLeg(
        HttpStatusCode Status, string? ProblemType, bool Forwarded, string? OfferStatus);

    /// <summary>Submits a real offer, degrades exactly ONE dependency, then accepts — so the
    /// two fault legs are compared through byte-identical arrange/act code.</summary>
    private static async Task<AcceptLeg> RunAcceptWithDegradedDependencyAsync(
        DegradedDependency dependency, string failMode)
    {
        var wallet = new FakeWalletClient { Balance = 10.0 }; // sufficient at submit
        var offerService = new RecordingOfferServiceClient();
        await using var factory = NewFactory(wallet, failMode, offerService);

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var jeeberId = Guid.NewGuid().ToString();

        var submitResp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = 100m, etaMinutes = 30, note = (string?)null });
        submitResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var offerId = (await submitResp.Content.ReadFromJsonAsync<OfferDto>())!.Id;

        var offers = factory.Services.GetRequiredService<FakePendingOffersStore>();
        if (dependency == DegradedDependency.WalletService) wallet.Unreachable = true;
        else offers.ForceListDegraded = true;

        var acceptResp = await ClientActor(factory, clientId).PostAsync(
            $"/v1/offers/{offerId}/accept", content: null);

        var problemType = acceptResp.IsSuccessStatusCode
            ? null
            : JObject.Parse(await acceptResp.Content.ReadAsStringAsync())["type"]?.Value<string>();

        // The toggle only degrades the DISCRIMINATED read; this ledger re-read is undegraded.
        var offer = (await offers.ListForRequestAsync(requestId, CancellationToken.None))
            .Single(o => o.Id == offerId);
        return new AcceptLeg(
            acceptResp.StatusCode, problemType, offerService.AcceptWithStatusCalled, offer.Status);
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

    /// <summary>Two active wallets on fee currency 2 (sums to 3.0) plus one lone wallet
    /// on currency 1 (100.0) that must never blend into the compare.</summary>
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
                    new() { WalletId = Guid.NewGuid(), HolderId = _holderId, CurrencyID = 2, Amount = 2.0, IsActive = true },
                    new() { WalletId = Guid.NewGuid(), HolderId = _holderId, CurrencyID = 2, Amount = 1.0, IsActive = true },
                    new() { WalletId = Guid.NewGuid(), HolderId = _holderId, CurrencyID = 1, Amount = 100.0, IsActive = true },
                },
            });

        public override Task<JeebGateway.service.ServiceWallet.GetHolderWallets> WalletsAsync(Guid holderId)
            => WalletsAsync(holderId, CancellationToken.None);
    }

    /// <summary>R-M1: a holder whose wallets carry explicit (type, currency, amount) rows.</summary>
    private sealed class TypedWalletClient : SwServiceWalletClient
    {
        private readonly Guid _holderId;
        private readonly (string? Type, int Currency, double Amount)[] _rows;

        public TypedWalletClient(Guid holderId, params (string? Type, int Currency, double Amount)[] rows)
            : base("http://localhost", new HttpClient())
        {
            _holderId = holderId;
            _rows = rows;
        }

        public override Task<JeebGateway.service.ServiceWallet.GetHolderWallets> WalletsAsync(Guid holderId, CancellationToken ct)
            => Task.FromResult(new JeebGateway.service.ServiceWallet.GetHolderWallets
            {
                WalletHolder = new JeebGateway.service.ServiceWallet.WalletHolder { HolderId = _holderId, IsActive = true },
                Wallets = _rows.Select(r => new JeebGateway.service.ServiceWallet.Wallet
                {
                    WalletId = Guid.NewGuid(), HolderId = _holderId,
                    CurrencyID = r.Currency, Amount = r.Amount, IsActive = true, Type = r.Type,
                }).ToList(),
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

        /// <summary>c2-2 test seam — an offer-service non-2xx blip, distinct from
        /// "the offer is genuinely absent from a healthy 2xx read".</summary>
        public bool JeeberFeedDegraded { get; set; }

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

        /// <summary>Ok-by-default (the seeded feed) so the existing 402/skip edit tests stay
        /// green; only <see cref="JeeberFeedDegraded"/> yields the degraded sentinel.</summary>
        public Task<OfferReadResult<JeeberFeedOffer>> TryListOffersForJeeberAsync(
            string jeeberId, string? status, CancellationToken ct)
            => Task.FromResult(JeeberFeedDegraded
                ? new OfferReadResult<JeeberFeedOffer>(true, Array.Empty<JeeberFeedOffer>())
                : new OfferReadResult<JeeberFeedOffer>(false, JeeberFeed.ToList()));

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
