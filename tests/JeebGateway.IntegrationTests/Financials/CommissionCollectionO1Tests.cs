using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Financials;
using JeebGateway.IntegrationTests.Fakes;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// O1 (owner ruling 2026-08-16) — the money model.
///
/// <para>The owner's three rungs: the offer price is free-form; at offer time the wallet is CHECKED
/// against the fee; that wallet is a fee account and COD may never flow through it. Rungs 1 and 2
/// already shipped (<see cref="WalletSufficiencyGuard"/>); this suite pins them so they cannot
/// regress, and adds the missing fourth thing — the fee is actually TAKEN.</para>
///
/// <para>Before this change the commission was booked by settlement-service and never collected by
/// anyone: 81 Done deliveries, zero fee entries in the wallet ledger. The keystone here is
/// <see cref="Fresh_Settle_Collects_The_Booked_Commission"/> — remove the collector call from
/// <c>SettlementService.SettleUpstreamAsync</c> and it goes red.</para>
/// </summary>
public class CommissionCollectionO1Tests
{
    private static readonly Guid Jeeber = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private static readonly Guid FeeWallet = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid SystemWallet = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

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

        var enough = NewGuard(11.37);
        (await enough.CheckAsync(Jeeber, required, default)).Allowed
            .Should().BeTrue("the boundary is inclusive");

        // Control: the same guard returns the OTHER answer one cent lower, so the Allowed above
        // is a real decision and not a constant true.
        var short1Cent = NewGuard(11.36);
        (await short1Cent.CheckAsync(Jeeber, required, default)).Allowed.Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rung 3 — the fee wallet is a FEE account. COD never pays the fee.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rung3_A_Cod_Wallet_Is_Never_The_Source_Of_A_Fee_Debit()
    {
        var handler = new StubWalletHandler();
        handler.HolderWallets = Wallets(
            (Guid.Parse("cccccccc-0000-4000-8000-000000000003"), 1, "cod_float", true));
        var client = NewDebitClient(handler);

        (await client.ResolveFeeWalletAsync(Jeeber, default))
            .Should().BeNull("a COD float leg may never fund the platform fee");

        // Control: the SAME holder read returns a wallet id the moment a non-COD leg exists,
        // so the null above is a rejection and not an always-null read.
        handler.HolderWallets = Wallets(
            (Guid.Parse("cccccccc-0000-4000-8000-000000000003"), 1, "cod_float", true),
            (FeeWallet, 1, "jeeb", true));
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
        var collector = NewCollector(wallet, new FakeSettlementServiceClient(), enabled: true);

        var result = await collector.CollectAsync(SettledRow(commission: 11.37m), default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.NoFeeWallet);
        wallet.Initiated.Should().BeEmpty("no wallet, no debit — never a fallback source");
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
        var collector = NewCollector(wallet, new FakeSettlementServiceClient(), enabled: false);

        var result = await collector.CollectAsync(SettledRow(commission: 11.37m), default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.Disabled);
        wallet.FeeWalletReads.Should().Be(0);
        wallet.Initiated.Should().BeEmpty();
    }

    [Fact]
    public async Task Enabled_Debits_Exactly_The_Booked_Commission_From_Fee_Wallet_To_Platform_Wallet()
    {
        // Control for the test above: the identical collector, same row, flag flipped.
        var wallet = new FakeDebitClient { FeeWallet = FeeWallet, SystemWallet = SystemWallet };
        var settlements = new FakeSettlementServiceClient();
        var row = SettledRow(commission: 11.37m);
        settlements.Rows[row.DeliveryId] = row;

        var result = await NewCollector(wallet, settlements, enabled: true).CollectAsync(row, default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.Collected);
        wallet.Initiated.Should().ContainSingle();
        var leg = wallet.Initiated.Single();
        leg.Source.Should().Be(FeeWallet);
        leg.Destination.Should().Be(SystemWallet);
        leg.Amount.Should().Be(11.37m, "the collected fee is the booked fee, never recomputed");
        wallet.Executed.Should().ContainSingle().And.Contain(leg.TransactionId);
        wallet.Aborted.Should().BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Exactly-once, with zero gateway state.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_Idempotency_Key_Is_Derived_From_The_Settlement_Id()
    {
        var wallet = new FakeDebitClient { FeeWallet = FeeWallet, SystemWallet = SystemWallet };
        var settlements = new FakeSettlementServiceClient();
        var row = SettledRow(commission: 5m);
        settlements.Rows[row.DeliveryId] = row;

        await NewCollector(wallet, settlements, enabled: true).CollectAsync(row, default);

        wallet.Initiated.Single().IdempotencyKey.Should().Be($"settlement:{row.Id}");
        WalletCommissionCollector.IdempotencyKeyFor(row.Id).Should().Be($"settlement:{row.Id}");
    }

    [Fact]
    public async Task A_Collected_Fee_Stamps_The_Wallet_Transaction_Onto_The_Settlement_Row()
    {
        var wallet = new FakeDebitClient { FeeWallet = FeeWallet, SystemWallet = SystemWallet };
        var settlements = new FakeSettlementServiceClient();
        var row = SettledRow(commission: 5m);
        settlements.Rows[row.DeliveryId] = row;

        var result = await NewCollector(wallet, settlements, enabled: true).CollectAsync(row, default);

        settlements.Rows[row.DeliveryId].WalletTxId
            .Should().Be(WalletCommissionCollector.ExternalRefPrefix + result.TransactionId.ToString("D"));
    }

    [Fact]
    public async Task An_Already_Stamped_Settlement_Is_Never_Debited_A_Second_Time()
    {
        var wallet = new FakeDebitClient { FeeWallet = FeeWallet, SystemWallet = SystemWallet };
        var row = SettledRow(commission: 5m);
        row.WalletTxId = "wallet-tx:already-taken";

        var result = await NewCollector(wallet, new FakeSettlementServiceClient(), enabled: true)
            .CollectAsync(row, default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.AlreadyCollected);
        wallet.Initiated.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Pending_Intent_With_No_Booked_Commission_Is_Not_Collectable()
    {
        var wallet = new FakeDebitClient { FeeWallet = FeeWallet, SystemWallet = SystemWallet };

        var result = await NewCollector(wallet, new FakeSettlementServiceClient(), enabled: true)
            .CollectAsync(SettledRow(commission: 0m), default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.NotCollectable);
        wallet.Initiated.Should().BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Money-safety on failure. A settled delivery NEVER unwinds.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Insufficient_Balance_Releases_The_Hold_And_Leaves_The_Delivery_Settled()
    {
        var wallet = new FakeDebitClient
        {
            FeeWallet = FeeWallet,
            SystemWallet = SystemWallet,
            ExecuteFault = () => new WalletCommissionDebitException(
                "Insufficient balance", HttpStatusCode.Conflict),
        };
        var settlements = new FakeSettlementServiceClient();
        var row = SettledRow(commission: 11.37m);
        settlements.Rows[row.DeliveryId] = row;

        var result = await NewCollector(wallet, settlements, enabled: true).CollectAsync(row, default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.InsufficientFunds);
        wallet.Aborted.Should().ContainSingle("a deterministic refusal moved no money; release the hold");
        settlements.Rows[row.DeliveryId].State.Should().Be(SettlementState.Settled);
        settlements.Rows[row.DeliveryId].WalletTxId.Should().BeNull("an uncollected fee is not stamped");
    }

    [Fact]
    public async Task An_Ambiguous_Execute_Is_Never_Aborted_And_Never_Stamped()
    {
        var wallet = new FakeDebitClient
        {
            FeeWallet = FeeWallet,
            SystemWallet = SystemWallet,
            // No status code == transport/timeout == the move MAY have committed.
            ExecuteFault = () => new WalletCommissionDebitException("timeout", null),
        };
        var settlements = new FakeSettlementServiceClient();
        var row = SettledRow(commission: 11.37m);
        settlements.Rows[row.DeliveryId] = row;

        var result = await NewCollector(wallet, settlements, enabled: true).CollectAsync(row, default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.Uncertain);
        wallet.Aborted.Should().BeEmpty("aborting a possibly-committed move is the double-move bug");
        settlements.Rows[row.DeliveryId].WalletTxId.Should().BeNull();
    }

    [Fact]
    public async Task A_Failed_Initiate_Moves_No_Money_And_Aborts_Nothing()
    {
        var wallet = new FakeDebitClient
        {
            FeeWallet = FeeWallet,
            SystemWallet = SystemWallet,
            InitiateFault = () => new WalletCommissionDebitException("rejected", HttpStatusCode.BadRequest),
        };

        var result = await NewCollector(wallet, new FakeSettlementServiceClient(), enabled: true)
            .CollectAsync(SettledRow(commission: 5m), default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.Failed);
        wallet.Executed.Should().BeEmpty();
        wallet.Aborted.Should().BeEmpty();
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

        var act = async () => await NewCollector(wallet, new FakeSettlementServiceClient(), enabled: true)
            .CollectAsync(SettledRow(commission: 5m), default);

        (await act.Should().NotThrowAsync()).Which.Outcome
            .Should().Be(CommissionCollectionOutcome.Failed);
    }

    [Fact]
    public async Task A_Stamp_Failure_Does_Not_Unwind_A_Collected_Fee()
    {
        var wallet = new FakeDebitClient { FeeWallet = FeeWallet, SystemWallet = SystemWallet };
        var settlements = FakeSettlementServiceClient.Unreachable();

        var result = await NewCollector(wallet, settlements, enabled: true)
            .CollectAsync(SettledRow(commission: 5m), default);

        result.Outcome.Should().Be(CommissionCollectionOutcome.Collected);
        wallet.Executed.Should().ContainSingle("the money already moved; a stamp fault is reconcilable");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The wire contract with wallet-service.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Initiate_Sends_The_Idempotency_Header_And_Suppresses_Wallet_Services_Own_Fee_Leg()
    {
        var handler = new StubWalletHandler();
        var client = NewDebitClient(handler);

        await client.InitiateAsync(FeeWallet, SystemWallet, 11.37m, "platform-fee", "note", "settlement:abc", default);

        var call = handler.Calls.Single(c => c.Path.EndsWith("Transaction/initiate", StringComparison.Ordinal));
        call.IdempotencyKey.Should().Be("settlement:abc");

        using var body = JsonDocument.Parse(call.Body!);
        body.RootElement.GetProperty("applyConfiguredFees").GetBoolean()
            .Should().BeFalse("the caller supplies the complete entry; wallet must not append a second fee");
        var leg = body.RootElement.GetProperty("transactions").EnumerateArray().Single();
        leg.GetProperty("sourceWalletId").GetGuid().Should().Be(FeeWallet);
        leg.GetProperty("destinationWalletId").GetGuid().Should().Be(SystemWallet);
        leg.GetProperty("amount").GetDecimal().Should().Be(11.37m);
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

    [Fact]
    public async Task A_4xx_Surfaces_As_A_DETERMINISTIC_Rejection()
    {
        var client = NewDebitClient(new StubWalletHandler { Status = HttpStatusCode.Conflict });

        var act = async () => await client.ExecuteAsync(Guid.NewGuid(), default);

        (await act.Should().ThrowAsync<WalletCommissionDebitException>())
            .Which.IsDeterministicRejection.Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // KEYSTONE — the settle path actually calls the collector.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fresh_Settle_Collects_The_Booked_Commission()
    {
        const string deliveryId = "11111111-1111-4111-8111-111111111111";
        const string clientId = "44444444-4444-4444-8444-444444444444";

        var settlements = new FakeSettlementServiceClient();
        var collector = new RecordingCollector();
        var requests = await SeedDoneDeliveryAsync(deliveryId, clientId, cod: 113.70m);

        var service = new SettlementService(
            settlements, requests, LiveRowAnswers, new EarningsCacheInvalidator(),
            collector, NullLogger<SettlementService>.Instance);

        (await service.SettleOnCompletionAsync(deliveryId, default)).Outcome
            .Should().Be(SettlementOutcome.Settled);

        collector.Collected.Should().ContainSingle("the fee is taken at the settle that booked it");
        collector.Collected.Single().Total.Should().Be(11.37m, "113.70 * 10% (Q-001)");
    }

    [Fact]
    public async Task A_Replayed_Settle_Does_Not_Collect_The_Fee_Twice()
    {
        const string deliveryId = "22222222-2222-4222-8222-222222222222";
        const string clientId = "44444444-4444-4444-8444-444444444444";

        var settlements = new FakeSettlementServiceClient();
        var collector = new RecordingCollector();
        var requests = await SeedDoneDeliveryAsync(deliveryId, clientId, cod: 113.70m);

        var service = new SettlementService(
            settlements, requests, LiveRowAnswers, new EarningsCacheInvalidator(),
            collector, NullLogger<SettlementService>.Instance);

        await service.SettleOnCompletionAsync(deliveryId, default);
        (await service.SettleOnCompletionAsync(deliveryId, default)).Outcome
            .Should().Be(SettlementOutcome.AlreadySettled);

        collector.Collected.Should().ContainSingle("both completion legs converge on one collection");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The live in-memory row is already canonical-Done, so the read-through never fires;
    /// a null client makes an unexpected canonical read fail loudly instead of passing quietly.</summary>
    private static readonly IDeliveryServiceClient LiveRowAnswers = null!;

    private static WalletSufficiencyGuard NewGuard(double balance)
        => new(new Fakes.FakeWalletClient { Balance = balance },
            Options.Create(new WalletGuardOptions { FailMode = "fail-closed" }),
            NullLogger<WalletSufficiencyGuard>.Instance);

    private static WalletCommissionCollector NewCollector(
        IWalletCommissionDebitClient wallet, ISettlementServiceClient settlements, bool enabled)
        => new(wallet, settlements,
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

    private static async Task<IRequestsStore> SeedDoneDeliveryAsync(
        string deliveryId, string clientId, decimal cod)
    {
        var requests = new InMemoryRequestsStore(TimeProvider.System);
        var created = await requests.CreateAsync(
            new CreateRequestInput { Id = deliveryId, ClientId = clientId, Description = "parcel" }, default);
        await requests.TryAcceptByJeeberAsync(
            created.Id, Jeeber.ToString(), int.MaxValue, DateTimeOffset.UtcNow, default);
        await requests.TrySetAcceptedFeeAsync(created.Id, cod, default);
        // `delivered` folds to the canonical Done via DeliveryStatusAlias — the real completion token.
        await requests.SetStatusAsync(created.Id, RequestStatus.Delivered, default);
        return requests;
    }

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

    // ── doubles ───────────────────────────────────────────────────────────────

    private sealed record InitiatedLeg(
        Guid Source, Guid Destination, decimal Amount, string IdempotencyKey, Guid TransactionId);

    private sealed class FakeDebitClient : IWalletCommissionDebitClient
    {
        public Guid? FeeWallet { get; set; }
        public Guid? SystemWallet { get; set; }
        public Func<Exception>? InitiateFault { get; set; }
        public Func<Exception>? ExecuteFault { get; set; }

        public int FeeWalletReads { get; private set; }
        public List<InitiatedLeg> Initiated { get; } = new();
        public List<Guid> Executed { get; } = new();
        public List<Guid> Aborted { get; } = new();

        public Task<Guid?> ResolveFeeWalletAsync(Guid holderId, CancellationToken ct)
        {
            FeeWalletReads++;
            return Task.FromResult(FeeWallet);
        }

        public Task<Guid?> ResolveSystemWalletAsync(CancellationToken ct) => Task.FromResult(SystemWallet);

        public Task<Guid> InitiateAsync(
            Guid sourceWalletId, Guid destinationWalletId, decimal amount,
            string tag, string notes, string idempotencyKey, CancellationToken ct)
        {
            if (InitiateFault is not null) throw InitiateFault();
            var txId = Guid.NewGuid();
            Initiated.Add(new InitiatedLeg(sourceWalletId, destinationWalletId, amount, idempotencyKey, txId));
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
        public ConcurrentBag<Settlement> Collected { get; } = new();

        public Task<CommissionCollectionResult> CollectAsync(Settlement settlement, CancellationToken ct)
        {
            Collected.Add(settlement);
            return Task.FromResult(new CommissionCollectionResult(
                CommissionCollectionOutcome.Collected, settlement.Total, Guid.NewGuid()));
        }
    }

    private sealed record StubCall(string Path, string? Body, string? IdempotencyKey);

    /// <summary>Minimal wallet-service double at the HTTP boundary: it records what was sent so the
    /// idempotency header and the leg shape are pinned against the real serializer.</summary>
    private sealed class StubWalletHandler : HttpMessageHandler
    {
        public string HolderWallets { get; set; } = "{\"wallets\":[]}";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
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

            if (Status != HttpStatusCode.OK) return new HttpResponseMessage(Status) { Content = new StringContent("nope") };

            var payload = path.Contains("/wallets", StringComparison.Ordinal) || path.EndsWith("system-wallet", StringComparison.Ordinal)
                ? HolderWallets
                : "{\"transactionHeader\":{\"txId\":\"33333333-3333-4333-8333-333333333333\"}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

}
