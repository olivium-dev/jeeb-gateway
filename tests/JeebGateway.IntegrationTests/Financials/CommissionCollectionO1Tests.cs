using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Controllers;
using JeebGateway.Financials;
using JeebGateway.IntegrationTests.Fakes;
using JeebGateway.Requests;
using JeebGateway.Requests.Cancellation;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using SwServiceWalletClient = JeebGateway.service.ServiceWallet.ServiceWalletClient;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// O1 (owner ruling 2026-08-16, AMENDED the same day) — the money model.
///
/// <para>The owner's rungs: the offer price is free-form; at offer time the wallet is CHECKED
/// against the fee; that wallet is a fee account and COD may never flow through it; and
/// <i>"the wallet only drain when he make an offer and it is accepted"</i> — the debit fires at
/// ACCEPT. The amendment reversed the implementer's charge-at-completion ruling in ADR-0011.</para>
///
/// <para>Keystone: <see cref="Accepting_An_Offer_Drains_The_Fee_From_The_Winners_Wallet"/>. Remove
/// the collector call from <c>JeebOffersController.BuildAcceptedResponseAsync</c> and it goes red.</para>
/// </summary>
public class CommissionCollectionO1Tests
{
    private static readonly Guid Jeeber = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private static readonly Guid FeeWallet = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid SystemWallet = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private const string CodWalletType = "cod_float";

    // ─────────────────────────────────────────────────────────────────────────
    // Rung 1 + 2 — the owner's own worked example: a free-form $113.70 offer.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rung1_The_Offer_Price_Is_Free_Form_And_Has_No_Upper_Cap()
    {
        // "jeeber can charge as much has he wants" — only a $1 floor exists, never a ceiling.
        RequestOffersController.MinimumFee.Should().Be(1m);

        foreach (var fee in new[] { 1m, 113.70m, 9_999_999m })
        {
            WalletGuardContract.RequiredCommission(fee)
                .Should().BeGreaterThan(0m, "every free-form price still carries the flat fee");
        }
    }

    [Fact]
    public async Task Rung2_The_Owners_11370_Example_Is_Checked_Against_The_Wallet_At_Offer_Time()
    {
        // Q-001 (owner, 2026-07-07): flat 10%. 113.70 -> 11.37, rounded away from zero.
        var required = WalletGuardContract.RequiredCommission(113.70m);
        required.Should().Be(11.37m);

        (await NewGuard(11.37).CheckAsync(Jeeber, required, default)).Allowed
            .Should().BeTrue("the boundary is inclusive");

        // Control: the same guard returns the OTHER answer one cent lower, so the Allowed above
        // is a real decision and not a constant true.
        (await NewGuard(11.36).CheckAsync(Jeeber, required, default)).Allowed.Should().BeFalse();
    }

    [Fact]
    public void What_Is_Checked_At_Offer_Time_Is_Exactly_What_Is_Charged_At_Accept()
    {
        // One expression, both sites — the guard cannot pass an amount the debit then exceeds,
        // and both agree with what settlement-service books later (Q-001).
        foreach (var fee in new[] { 1m, 3m, 100.25m, 113.70m })
        {
            WalletGuardContract.RequiredCommission(fee)
                .Should().Be(CommissionCalculator.Calculate(fee, CommissionTier.Standard).Commission);
        }

        // 3.00 * 0.10 = 0.30 — the one commission ever observed live.
        WalletGuardContract.RequiredCommission(3.00m).Should().Be(0.30m);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rung 3 — the fee wallet is a FEE account. COD never pays the fee.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rung3_A_Cod_Wallet_Is_Never_The_Source_Of_A_Fee_Debit()
    {
        var handler = new StubWalletHandler();
        handler.HolderWallets = Wallets((Guid.NewGuid(), 1, CodWalletType, true));
        var client = NewDebitClient(handler);

        (await client.ResolveFeeWalletAsync(Jeeber, default))
            .Should().BeNull("a COD float leg may never fund the platform fee");

        // Control: the SAME holder read returns a wallet id the moment a non-COD leg exists,
        // so the null above is a rejection and not an always-null read.
        handler.HolderWallets = Wallets(
            (Guid.NewGuid(), 1, CodWalletType, true), (FeeWallet, 1, "jeeb", true));
        (await client.ResolveFeeWalletAsync(Jeeber, default)).Should().Be(FeeWallet);
    }

    [Fact]
    public async Task Rung3_Inactive_And_Wrong_Currency_Wallets_Are_Not_Debitable()
    {
        var handler = new StubWalletHandler
        {
            HolderWallets = Wallets(
                (Guid.NewGuid(), 1, "jeeb", false),   // deactivated
                (Guid.NewGuid(), 2, "jeeb", true)),   // another currency
        };
        var client = NewDebitClient(handler);

        (await client.ResolveFeeWalletAsync(Jeeber, default)).Should().BeNull();

        handler.HolderWallets = Wallets((FeeWallet, 1, "jeeb", true));
        (await client.ResolveFeeWalletAsync(Jeeber, default)).Should().Be(FeeWallet);
    }

    [Fact]
    public async Task A_Holder_With_Only_Cod_Wallets_Yields_NoFeeWallet_And_Debits_Nothing()
    {
        var wallet = new FakeDebitClient { FeeWallet = null, SystemWallet = SystemWallet };

        var result = await NewCollector(wallet, enabled: true).CollectOnAcceptAsync(Accept(113.70m), default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.NoFeeWallet);
        wallet.Initiated.Should().BeEmpty("no fee wallet, no debit — never a fallback source");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The owner gate — merged OFF, and never silently inert.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_By_Default_Reads_Nothing_And_Debits_Nothing()
    {
        new CommissionCollectionOptions().Enabled
            .Should().BeFalse("merging O1 must not start moving money");

        var wallet = new FakeDebitClient { FeeWallet = FeeWallet, SystemWallet = SystemWallet };

        var result = await NewCollector(wallet, enabled: false).CollectOnAcceptAsync(Accept(113.70m), default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.Disabled);
        result.Amount.Should().Be(11.37m, "the owed amount is still computed, so it can be counted");
        wallet.FeeWalletReads.Should().Be(0);
        wallet.Initiated.Should().BeEmpty();
    }

    [Fact]
    public async Task Enabled_Debits_Ten_Percent_From_The_Fee_Wallet_Into_The_Platform_Wallet()
    {
        // Control for the test above: same collector, same command, flag flipped.
        var wallet = new FakeDebitClient { FeeWallet = FeeWallet, SystemWallet = SystemWallet };

        var result = await NewCollector(wallet, enabled: true).CollectOnAcceptAsync(Accept(113.70m), default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.Collected);
        var leg = wallet.Initiated.Should().ContainSingle().Which;
        leg.Source.Should().Be(FeeWallet);
        leg.Destination.Should().Be(SystemWallet);
        leg.Amount.Should().Be(11.37m);
        wallet.Executed.Should().ContainSingle().And.Contain(leg.TransactionId);
        wallet.Aborted.Should().BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Exactly-once at accept, with zero gateway state.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_Idempotency_Key_Is_Scoped_To_The_ACCEPT_Event_Not_A_Settlement()
    {
        var wallet = new FakeDebitClient { FeeWallet = FeeWallet, SystemWallet = SystemWallet };
        var command = Accept(50m);

        await NewCollector(wallet, enabled: true).CollectOnAcceptAsync(command, default);

        wallet.Initiated.Single().IdempotencyKey.Should().Be($"accept:{command.RequestId}");
        WalletCommissionCollector.IdempotencyKeyFor(command.RequestId)
            .Should().Be($"accept:{command.RequestId}");
    }

    [Fact]
    public async Task A_Replayed_Accept_Charges_Exactly_Once()
    {
        // A headerless accept retry re-runs the whole post-commit block, so the ONLY thing standing
        // between a jeeber and a double charge is the idempotency key. This double models
        // wallet-service's documented replay: same key + same body returns the ORIGINAL header.
        var wallet = new FakeDebitClient
        {
            FeeWallet = FeeWallet, SystemWallet = SystemWallet, HonourIdempotency = true,
        };
        var collector = NewCollector(wallet, enabled: true);
        var command = Accept(113.70m);

        var first = await collector.CollectOnAcceptAsync(command, default);
        var second = await collector.CollectOnAcceptAsync(command, default);

        first.Outcome.Should().Be(CommissionCollectionOutcome.Collected);
        second.Outcome.Should().Be(CommissionCollectionOutcome.Collected);
        second.TransactionId.Should().Be(first.TransactionId, "the replay returns the original header");
        wallet.DistinctTransactionsCreated.Should().Be(1, "one accept, one debit");
        wallet.Executed.Distinct().Should().ContainSingle("execute is idempotent on the transaction id");

        // Control: without the key the same double creates a SECOND transaction, which is exactly
        // the double charge this test exists to exclude.
        var unkeyed = new FakeDebitClient
        {
            FeeWallet = FeeWallet, SystemWallet = SystemWallet, HonourIdempotency = false,
        };
        var loose = NewCollector(unkeyed, enabled: true);
        await loose.CollectOnAcceptAsync(command, default);
        await loose.CollectOnAcceptAsync(command, default);
        unkeyed.DistinctTransactionsCreated.Should().Be(2);
    }

    [Fact]
    public async Task A_Second_Accept_Carrying_A_DIFFERENT_Amount_Is_Refused_Never_Charged_Twice()
    {
        var wallet = new FakeDebitClient
        {
            FeeWallet = FeeWallet,
            SystemWallet = SystemWallet,
            InitiateFault = () => new WalletCommissionDebitException(
                "key already used with a different body", HttpStatusCode.Conflict,
                WalletCommissionDebitException.IdempotencyConflictType),
        };

        var result = await NewCollector(wallet, enabled: true).CollectOnAcceptAsync(Accept(200m), default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.IdempotencyConflict);
        wallet.Executed.Should().BeEmpty();
        wallet.Aborted.Should().BeEmpty("nothing was created, so there is nothing to release");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Money-safety on failure. An accepted auction NEVER unwinds.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Insufficient_Balance_Is_Read_Off_Wallet_Services_Own_Problem_Type()
    {
        var wallet = new FakeDebitClient
        {
            FeeWallet = FeeWallet,
            SystemWallet = SystemWallet,
            InitiateFault = () => new WalletCommissionDebitException(
                "Insufficient balance", HttpStatusCode.Conflict,
                WalletCommissionDebitException.InsufficientBalanceType),
        };

        var result = await NewCollector(wallet, enabled: true).CollectOnAcceptAsync(Accept(113.70m), default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.InsufficientFunds);

        // Control: the SAME 409 with a different problem type classifies differently, so the
        // outcome above comes from the type and is not guessed from the status code.
        var conflicting = new FakeDebitClient
        {
            FeeWallet = FeeWallet,
            SystemWallet = SystemWallet,
            InitiateFault = () => new WalletCommissionDebitException(
                "conflict", HttpStatusCode.Conflict,
                WalletCommissionDebitException.IdempotencyConflictType),
        };
        (await NewCollector(conflicting, enabled: true).CollectOnAcceptAsync(Accept(113.70m), default))
            .Outcome.Should().Be(CommissionCollectionOutcome.IdempotencyConflict);
    }

    [Fact]
    public async Task Insufficient_Balance_At_Execute_Releases_The_Hold()
    {
        var wallet = new FakeDebitClient
        {
            FeeWallet = FeeWallet,
            SystemWallet = SystemWallet,
            ExecuteFault = () => new WalletCommissionDebitException(
                "Insufficient balance", HttpStatusCode.Conflict,
                WalletCommissionDebitException.InsufficientBalanceType),
        };

        var result = await NewCollector(wallet, enabled: true).CollectOnAcceptAsync(Accept(113.70m), default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.InsufficientFunds);
        wallet.Aborted.Should().ContainSingle("a deterministic refusal moved no money; release the hold");
    }

    [Fact]
    public async Task An_Ambiguous_Execute_Is_Never_Aborted()
    {
        var wallet = new FakeDebitClient
        {
            FeeWallet = FeeWallet,
            SystemWallet = SystemWallet,
            // No status code == transport/timeout == the move MAY have committed.
            ExecuteFault = () => new WalletCommissionDebitException("timeout", null),
        };

        var result = await NewCollector(wallet, enabled: true).CollectOnAcceptAsync(Accept(113.70m), default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.Uncertain);
        wallet.Aborted.Should().BeEmpty("aborting a possibly-committed move is the double-move bug");
    }

    [Fact]
    public async Task The_Collector_Never_Throws_Even_When_Wallet_Service_Throws_Something_Unexpected()
    {
        var wallet = new FakeDebitClient
        {
            FeeWallet = FeeWallet,
            SystemWallet = SystemWallet,
            InitiateFault = () => new InvalidOperationException("boom"),
        };

        var act = async () => await NewCollector(wallet, enabled: true)
            .CollectOnAcceptAsync(Accept(113.70m), default);

        (await act.Should().NotThrowAsync()).Which.Outcome
            .Should().Be(CommissionCollectionOutcome.Failed);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The settle-time LINK — a read and a stamp, never a second money move.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Settling_Links_The_Row_To_The_Accept_Time_Debit_Without_Moving_Money()
    {
        var txId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var row = SettledRow(commission: 11.37m);
        var wallet = new FakeDebitClient
        {
            FeeWallet = FeeWallet,
            SystemWallet = SystemWallet,
            ByExternalReference = { [$"delivery:{row.DeliveryId}"] = txId },
        };
        var settlements = new FakeSettlementServiceClient();
        settlements.Rows[row.DeliveryId] = row;

        await NewCollector(wallet, enabled: true, settlements).LinkSettlementAsync(row, default);

        settlements.Rows[row.DeliveryId].WalletTxId
            .Should().Be(WalletCommissionCollector.ExternalRefPrefix + txId.ToString("D"));
        wallet.Initiated.Should().BeEmpty("linking is a read plus a stamp");
        wallet.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Settlement_With_No_Accept_Time_Debit_Is_Left_Unstamped_And_Counted()
    {
        // This is the per-row measure of OA-30's finding — a delivery that settled while its fee
        // was never collected. Before, that had to be excavated by hand across 275 wallet holders.
        var row = SettledRow(commission: 11.37m);
        var wallet = new FakeDebitClient { FeeWallet = FeeWallet, SystemWallet = SystemWallet };
        var settlements = new FakeSettlementServiceClient();
        settlements.Rows[row.DeliveryId] = row;

        await NewCollector(wallet, enabled: true, settlements).LinkSettlementAsync(row, default);

        settlements.Rows[row.DeliveryId].WalletTxId.Should().BeNull();

        // Control: the identical call stamps the moment a debit carries the reference.
        wallet.ByExternalReference[$"delivery:{row.DeliveryId}"] = Guid.NewGuid();
        await NewCollector(wallet, enabled: true, settlements).LinkSettlementAsync(row, default);
        settlements.Rows[row.DeliveryId].WalletTxId.Should().NotBeNull();
    }

    [Fact]
    public async Task A_Link_Failure_Never_Breaks_The_Settle()
    {
        var row = SettledRow(commission: 5m);
        var wallet = new FakeDebitClient
        {
            ByExternalReference = { [$"delivery:{row.DeliveryId}"] = Guid.NewGuid() },
        };

        var act = async () => await NewCollector(wallet, enabled: true, FakeSettlementServiceClient.Unreachable())
            .LinkSettlementAsync(row, default);

        await act.Should().NotThrowAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The wire contract with wallet-service.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Initiate_Sends_The_Idempotency_Header_The_External_Reference_And_Suppresses_Wallet_Fees()
    {
        var handler = new StubWalletHandler();
        var client = NewDebitClient(handler);

        await client.InitiateAsync(
            FeeWallet, SystemWallet, 11.37m, "platform-fee", "note", "accept:req-1", "delivery:req-1", default);

        var call = handler.Calls.Single(c => c.Path.EndsWith("Transaction/initiate", StringComparison.Ordinal));
        call.IdempotencyKey.Should().Be("accept:req-1");

        using var body = JsonDocument.Parse(call.Body!);
        body.RootElement.GetProperty("externalReference").GetString().Should().Be("delivery:req-1");
        body.RootElement.GetProperty("applyConfiguredFees").GetBoolean()
            .Should().BeFalse("the caller supplies the complete entry; wallet must not append a second fee");
        var leg = body.RootElement.GetProperty("transactions").EnumerateArray().Single();
        leg.GetProperty("sourceWalletId").GetGuid().Should().Be(FeeWallet);
        leg.GetProperty("destinationWalletId").GetGuid().Should().Be(SystemWallet);
        leg.GetProperty("amount").GetDecimal().Should().Be(11.37m);
    }

    [Fact]
    public async Task A_ProblemDetails_Body_Classifies_The_Refusal_Instead_Of_The_Status_Code()
    {
        var insufficient = NewDebitClient(new StubWalletHandler
        {
            Status = HttpStatusCode.Conflict,
            Body = $$"""{"type":"{{WalletCommissionDebitException.InsufficientBalanceType}}"}""",
        });
        var act = async () => await insufficient.ExecuteAsync(Guid.NewGuid(), default);
        (await act.Should().ThrowAsync<WalletCommissionDebitException>())
            .Which.IsInsufficientBalance.Should().BeTrue();

        // Control: the same 409 with a different type is NOT read as insufficient balance.
        var other = NewDebitClient(new StubWalletHandler
        {
            Status = HttpStatusCode.Conflict,
            Body = $$"""{"type":"{{WalletCommissionDebitException.IdempotencyConflictType}}"}""",
        });
        var act2 = async () => await other.ExecuteAsync(Guid.NewGuid(), default);
        var ex = (await act2.Should().ThrowAsync<WalletCommissionDebitException>()).Which;
        ex.IsInsufficientBalance.Should().BeFalse();
        ex.IsIdempotencyConflict.Should().BeTrue();
    }

    [Fact]
    public async Task A_Transport_Fault_Surfaces_As_An_AMBIGUOUS_Debit_Exception()
    {
        var client = NewDebitClient(new StubWalletHandler { Throw = true });

        var act = async () => await client.ExecuteAsync(Guid.NewGuid(), default);

        var ex = (await act.Should().ThrowAsync<WalletCommissionDebitException>()).Which;
        ex.StatusCode.Should().BeNull();
        ex.IsDeterministicRejection.Should().BeFalse("no status code means the move may have committed");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cancellation — NO refund is implemented, and that is now measurable.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_An_Accepted_Delivery_Refunds_Nothing_And_Records_The_Retention()
    {
        var requests = new InMemoryRequestsStore(TimeProvider.System);
        var created = await requests.CreateAsync(
            new CreateRequestInput { ClientId = "client-1", Description = "parcel" }, default);
        await requests.TryAcceptByJeeberAsync(
            created.Id, Jeeber.ToString(), int.MaxValue, DateTimeOffset.UtcNow, default);
        await requests.TrySetAcceptedFeeAsync(created.Id, 113.70m, default);

        var wallet = new FakeDebitClient { FeeWallet = FeeWallet, SystemWallet = SystemWallet };
        var service = new CancellationService(
            requests, new InMemoryJeeberRestrictionStore(), TimeProvider.System,
            Options.Create(new CancellationPolicyOptions()));

        var result = await service.CancelAsync(
            created.Id, Jeeber.ToString(), callerIsClient: false, callerIsJeeber: true,
            reason: "changed my mind", default);

        result.Outcome.Should().Be(CancellationOutcome.CancelledByJeeber);

        // The load-bearing assertion: cancelling touches wallet-service not at all. There is no
        // refund path, by design — the owner has not ruled on refunds and none was invented.
        wallet.Initiated.Should().BeEmpty();
        wallet.Executed.Should().BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // KEYSTONE — accepting an offer actually drains the wallet.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Accepting_An_Offer_Drains_The_Fee_From_The_Winners_Wallet()
    {
        var collector = new RecordingCollector();
        await using var factory = NewFactory(new FakeWalletClient { Balance = 500.0 }, collector);

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var jeeberId = Jeeber.ToString();

        var submit = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = 113.70m, etaMinutes = 30, note = (string?)null });
        submit.StatusCode.Should().Be(HttpStatusCode.Created);
        var offerId = (await submit.Content.ReadFromJsonAsync<OfferDto>())!.Id;

        var accept = await ClientActor(factory, clientId).PostAsync($"/v1/offers/{offerId}/accept", null);
        accept.StatusCode.Should().Be(HttpStatusCode.OK);

        var charged = collector.Collected.Should().ContainSingle().Which;
        charged.RequestId.Should().Be(requestId);
        charged.JeeberId.Should().Be(jeeberId);
        charged.AcceptedFee.Should().Be(113.70m, "the free-form price the jeeber set");
    }

    [Fact]
    public async Task An_Uncollectable_Fee_Never_Turns_A_Closed_Auction_Into_A_5xx()
    {
        // The accept saga has already committed a winner; nothing downstream may unwind it.
        var collector = new RecordingCollector { Fault = () => new InvalidOperationException("wallet down") };
        await using var factory = NewFactory(new FakeWalletClient { Balance = 500.0 }, collector);

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var submit = await JeeberClient(factory, Jeeber.ToString()).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = 113.70m, etaMinutes = 30, note = (string?)null });
        var offerId = (await submit.Content.ReadFromJsonAsync<OfferDto>())!.Id;

        var accept = await ClientActor(factory, clientId).PostAsync($"/v1/offers/{offerId}/accept", null);

        accept.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static CommissionCollectionCommand Accept(decimal fee)
        => new(Guid.NewGuid().ToString(), Jeeber.ToString(), fee);

    private static WalletSufficiencyGuard NewGuard(double balance)
        => new(new FakeWalletClient { Balance = balance },
            Options.Create(new WalletGuardOptions { FailMode = "fail-closed" }),
            NullLogger<WalletSufficiencyGuard>.Instance);

    private static WalletCommissionCollector NewCollector(
        IWalletCommissionDebitClient wallet, bool enabled, ISettlementServiceClient? settlements = null)
        => new(wallet, settlements ?? new FakeSettlementServiceClient(),
            Options.Create(new CommissionCollectionOptions { Enabled = enabled }),
            NullLogger<WalletCommissionCollector>.Instance);

    private static WalletCommissionDebitClient NewDebitClient(StubWalletHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://wallet.invalid/") }, currencyId: 1);

    private static Settlement SettledRow(decimal commission) => new()
    {
        Id = Guid.NewGuid().ToString(),
        DeliveryId = Guid.NewGuid().ToString(),
        ClientId = Guid.NewGuid().ToString(),
        JeeberId = Jeeber.ToString(),
        TierId = "standard",
        GoodsCost = commission * 10m,
        CommissionTier = CommissionTier.Standard,
        CommissionRate = CommissionCalculator.FlatRate,
        Commission = commission,
        Insurance = 0m,
        Total = commission,
        MinimumFeeApplied = false,
        Currency = SettlementService.CurrencyUsd,
        PaymentMethod = SettlementService.PaymentMethodCash,
        State = SettlementState.Settled,
        SettledAt = DateTimeOffset.UtcNow,
    };

    private static string Wallets(params (Guid Id, int Currency, string Type, bool Active)[] wallets)
        => JsonSerializer.Serialize(new
        {
            wallets = wallets.Select(w => new
            {
                walletId = w.Id,
                currencyID = w.Currency,
                type = w.Type,
                isActive = w.Active,
            }),
        });

    private static WebApplicationFactory<Program> NewFactory(
        FakeWalletClient wallet, ICommissionCollector collector)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "WalletGuard:FailMode", "fail-closed" },
                    { "FeatureFlags:UseUpstream:Offer", "true" },
                }));
            builder.ConfigureTestServices(services =>
            {
                FakeOfferStoreWebApplicationFactory.UseFakeOfferStore(services);
                services.RemoveAll<SwServiceWalletClient>();
                services.AddScoped<SwServiceWalletClient>(_ => wallet);
                services.RemoveAll<IOfferServiceClient>();
                services.AddSingleton<IOfferServiceClient>(new AcceptingOfferServiceClient());
                services.RemoveAll<ICommissionCollector>();
                services.AddSingleton(collector);
            });
        });

    private static async Task<(string ClientId, string RequestId)> SeedRequestAsync(
        WebApplicationFactory<Program> factory)
    {
        var clientId = $"client-{Guid.NewGuid()}";
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            TierId = InRangeGeoFixture.TierId,
            PickupLocation = new GeoPoint { Lat = InRangeGeoFixture.Lat, Lng = InRangeGeoFixture.Lng },
            ClientId = clientId,
            Description = "Pick up a package",
        }, CancellationToken.None);
        return (clientId, created.Id);
    }

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string jeeberId)
        => Actor(factory, jeeberId, "driver");

    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string clientId)
        => Actor(factory, clientId, "client");

    private static HttpClient Actor(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", role);
        return client;
    }

    // ── doubles ───────────────────────────────────────────────────────────────

    private sealed record InitiatedLeg(
        Guid Source, Guid Destination, decimal Amount, string IdempotencyKey,
        string ExternalReference, Guid TransactionId);

    /// <summary>
    /// Models the parts of wallet-service's documented contract this design leans on: initiate is
    /// deduped by <c>Idempotency-Key</c> and execute is idempotent on the transaction id.
    /// </summary>
    private sealed class FakeDebitClient : IWalletCommissionDebitClient
    {
        private readonly Dictionary<string, Guid> _byKey = new(StringComparer.Ordinal);

        public Guid? FeeWallet { get; set; }
        public Guid? SystemWallet { get; set; }
        public bool HonourIdempotency { get; set; }
        public Func<Exception>? InitiateFault { get; set; }
        public Func<Exception>? ExecuteFault { get; set; }
        public Dictionary<string, Guid> ByExternalReference { get; } = new(StringComparer.Ordinal);

        public int FeeWalletReads { get; private set; }
        public int DistinctTransactionsCreated { get; private set; }
        public List<InitiatedLeg> Initiated { get; } = new();
        public List<Guid> Executed { get; } = new();
        public List<Guid> Aborted { get; } = new();

        public Task<Guid?> ResolveFeeWalletAsync(Guid holderId, CancellationToken ct)
        {
            FeeWalletReads++;
            return Task.FromResult(FeeWallet);
        }

        public Task<Guid?> ResolveSystemWalletAsync(CancellationToken ct) => Task.FromResult(SystemWallet);

        public Task<Guid?> FindByExternalReferenceAsync(string externalReference, CancellationToken ct)
            => Task.FromResult(ByExternalReference.TryGetValue(externalReference, out var id) ? id : (Guid?)null);

        public Task<Guid> InitiateAsync(
            Guid sourceWalletId, Guid destinationWalletId, decimal amount,
            string tag, string notes, string idempotencyKey, string externalReference, CancellationToken ct)
        {
            if (InitiateFault is not null) throw InitiateFault();

            if (HonourIdempotency && _byKey.TryGetValue(idempotencyKey, out var replayed))
            {
                Initiated.Add(new InitiatedLeg(
                    sourceWalletId, destinationWalletId, amount, idempotencyKey, externalReference, replayed));
                return Task.FromResult(replayed);
            }

            var txId = Guid.NewGuid();
            DistinctTransactionsCreated++;
            if (HonourIdempotency) _byKey[idempotencyKey] = txId;
            ByExternalReference[externalReference] = txId;
            Initiated.Add(new InitiatedLeg(
                sourceWalletId, destinationWalletId, amount, idempotencyKey, externalReference, txId));
            return Task.FromResult(txId);
        }

        public Task ExecuteAsync(Guid transactionId, CancellationToken ct)
        {
            if (ExecuteFault is not null) throw ExecuteFault();
            Executed.Add(transactionId);
            return Task.CompletedTask;
        }

        public Task AbortAsync(Guid transactionId, CancellationToken ct)
        {
            Aborted.Add(transactionId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCollector : ICommissionCollector
    {
        public ConcurrentBag<CommissionCollectionCommand> Collected { get; } = new();
        public Func<Exception>? Fault { get; set; }

        public Task<CommissionCollectionResult> CollectOnAcceptAsync(
            CommissionCollectionCommand command, CancellationToken ct)
        {
            if (Fault is not null) throw Fault();
            Collected.Add(command);
            return Task.FromResult(new CommissionCollectionResult(
                CommissionCollectionOutcome.Collected, command.AcceptedFee, Guid.NewGuid()));
        }

        public Task LinkSettlementAsync(Settlement settlement, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class AcceptingOfferServiceClient : IOfferServiceClient
    {
        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(new OfferAcceptResult { Status = OfferAcceptStatus.Accepted });

        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId, long? feeCents, int? etaMinutes,
            string? note, int? maxEdits, CancellationToken ct)
            => Task.FromResult(new OfferMutationResult { Status = OfferMutationStatus.Ok });

        public Task<IReadOnlyList<JeeberFeedOffer>> ListOffersForJeeberAsync(
            string jeeberId, string? status, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JeeberFeedOffer>>(Array.Empty<JeeberFeedOffer>());

        public Task<OfferAcceptWire> AcceptAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<RequestMirrorResult> MirrorRequestAsync(
            string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException();

        // Unused by the accept path; loud so an unexpected hop fails the test rather than passing.
        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes,
            string? note, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed record StubCall(string Path, string? Body, string? IdempotencyKey);

    /// <summary>Minimal wallet-service double at the HTTP boundary: it records what was sent so the
    /// header, the external reference and the leg shape are pinned against the real serializer.</summary>
    private sealed class StubWalletHandler : HttpMessageHandler
    {
        public string HolderWallets { get; set; } = "{\"wallets\":[]}";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "nope";
        public bool Throw { get; set; }
        public List<StubCall> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw) throw new HttpRequestException("connection refused");

            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            request.Headers.TryGetValues(WalletCommissionDebitClient.IdempotencyHeader, out var keys);
            Calls.Add(new StubCall(path, body, keys?.FirstOrDefault()));

            if (Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(Status)
                {
                    Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/problem+json"),
                };
            }

            var payload = path.Contains("/wallets", StringComparison.Ordinal)
                          || path.EndsWith("system-wallet", StringComparison.Ordinal)
                ? HolderWallets
                : "{\"transactionHeader\":{\"txId\":\"33333333-3333-4333-8333-333333333333\"}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
