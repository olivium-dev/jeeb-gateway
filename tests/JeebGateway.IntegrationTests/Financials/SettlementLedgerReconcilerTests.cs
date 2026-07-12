using FluentAssertions;
using JeebGateway.Financials;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// JEBV4-47 (M3/R7): unit coverage for the settlement -> UPG ledger reconciler that
/// replays ledger posts swallowed at settle time (ledger_entry_id NULL). Fast
/// in-memory store + a scriptable ledger client double — no Postgres/Docker.
/// </summary>
public sealed class SettlementLedgerReconcilerTests
{
    private static SettlementLedgerReconciler Build(
        ISettlementStore store, ISettlementLedgerClient ledger)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(ledger)
            .BuildServiceProvider();
        return new SettlementLedgerReconciler(
            services, TimeProvider.System,
            Options.Create(new SettlementLedgerReconcilerOptions { PageSize = 100 }),
            NullLogger<SettlementLedgerReconciler>.Instance);
    }

    [Fact]
    public async Task Reconciler_replays_an_unposted_settlement_and_stamps_ledger_entry()
    {
        var store = new InMemorySettlementStore();
        await store.TryInsertAsync(MakeSettled("del-1", "jeeber-1"), CancellationToken.None);
        var ledger = new ScriptedLedgerClient(); // always succeeds

        var reconciled = await Build(store, ledger).SweepOnceAsync(CancellationToken.None);

        reconciled.Should().Be(1);
        var row = await store.GetByDeliveryAsync("del-1", CancellationToken.None);
        row!.LedgerEntryId.Should().NotBeNullOrEmpty("the reconciler stamps the ledger id on success");
        ledger.Calls.Should().ContainSingle();
        ledger.Calls[0].IdempotencyKey.Should().Be(row.Id, "the settlement id is the idempotency key");
    }

    [Fact]
    public async Task Reconciler_heals_within_one_tick_after_upg_recovers()
    {
        // AC#4: UPG down at settle -> row persisted, ledger null -> UPG back ->
        // reconciler heals on the next tick.
        var store = new InMemorySettlementStore();
        await store.TryInsertAsync(MakeSettled("del-2", "jeeber-2"), CancellationToken.None);
        var ledger = new ScriptedLedgerClient { FailuresBeforeSuccess = 1 }; // UPG still down first tick

        var reconciler = Build(store, ledger);

        // Tick 1: UPG down -> nothing reconciled, row still unposted.
        (await reconciler.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        (await store.GetByDeliveryAsync("del-2", CancellationToken.None))!
            .LedgerEntryId.Should().BeNullOrEmpty();

        // Tick 2: UPG back -> healed.
        (await reconciler.SweepOnceAsync(CancellationToken.None)).Should().Be(1);
        (await store.GetByDeliveryAsync("del-2", CancellationToken.None))!
            .LedgerEntryId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Reconciler_is_idempotent_and_never_double_posts()
    {
        var store = new InMemorySettlementStore();
        await store.TryInsertAsync(MakeSettled("del-3", "jeeber-3"), CancellationToken.None);
        var ledger = new ScriptedLedgerClient();
        var reconciler = Build(store, ledger);

        (await reconciler.SweepOnceAsync(CancellationToken.None)).Should().Be(1);
        // Second sweep: the row is now posted (ledger_entry_id set) so it is no longer
        // an unposted candidate — no second ledger post.
        (await reconciler.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        ledger.Calls.Should().ContainSingle("a stamped row must never be re-posted");
    }

    [Fact]
    public async Task Reconciler_ignores_pending_settlement_placeholders()
    {
        // A pending_settlement placeholder has no ledger post by design and must NOT
        // be reconciled (it is not a completed settlement).
        var store = new InMemorySettlementStore();
        await store.TryInsertAsync(
            MakeSettled("del-4", "jeeber-4", state: SettlementState.PendingSettlement),
            CancellationToken.None);
        var ledger = new ScriptedLedgerClient();

        var reconciled = await Build(store, ledger).SweepOnceAsync(CancellationToken.None);

        reconciled.Should().Be(0);
        ledger.Calls.Should().BeEmpty("a pending_settlement placeholder is never a reconcile candidate");
    }

    [Fact]
    public async Task Reconciler_isolates_a_still_failing_row_and_reconciles_the_rest()
    {
        // Per-row isolation: one row whose UPG post keeps failing must not wedge the
        // sweep — the healthy rows still reconcile.
        var store = new InMemorySettlementStore();
        await store.TryInsertAsync(MakeSettled("del-bad", "jeeber-5"), CancellationToken.None);
        await store.TryInsertAsync(MakeSettled("del-ok", "jeeber-6"), CancellationToken.None);
        var badRow = await store.GetByDeliveryAsync("del-bad", CancellationToken.None);
        var ledger = new ScriptedLedgerClient { AlwaysFailForKey = badRow!.Id };

        var reconciled = await Build(store, ledger).SweepOnceAsync(CancellationToken.None);

        reconciled.Should().Be(1, "the healthy row reconciles even though the bad row keeps failing");
        (await store.GetByDeliveryAsync("del-bad", CancellationToken.None))!.LedgerEntryId.Should().BeNullOrEmpty();
        (await store.GetByDeliveryAsync("del-ok", CancellationToken.None))!.LedgerEntryId.Should().NotBeNullOrEmpty();
    }

    // ── doubles ────────────────────────────────────────────────────────────────

    private sealed class ScriptedLedgerClient : ISettlementLedgerClient
    {
        public int FailuresBeforeSuccess { get; set; }
        public string? AlwaysFailForKey { get; set; }
        public List<LedgerEntryRequest> Calls { get; } = new();
        private int _seen;

        public Task<LedgerEntryResponse> PostLedgerEntryAsync(LedgerEntryRequest request, CancellationToken ct)
        {
            if (AlwaysFailForKey is not null && request.IdempotencyKey == AlwaysFailForKey)
                throw new InvalidOperationException("simulated UPG ledger outage for this row");
            if (_seen++ < FailuresBeforeSuccess)
                throw new InvalidOperationException("simulated UPG ledger outage");
            Calls.Add(request);
            return Task.FromResult(new LedgerEntryResponse
            {
                LedgerEntryId = $"ledger-{request.IdempotencyKey}",
                PostedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    private static Settlement MakeSettled(string deliveryId, string jeeberId, string? state = null)
    {
        var breakdown = CommissionCalculator.Calculate(100_000m, CommissionTier.Standard);
        return new Settlement
        {
            Id = Guid.NewGuid().ToString(),
            DeliveryId = deliveryId,
            JeeberId = jeeberId,
            ClientId = "client-1",
            TierId = "same-day",
            GoodsCost = breakdown.GoodsCost,
            CommissionTier = CommissionTier.Standard,
            CommissionRate = breakdown.CommissionRate,
            Commission = breakdown.Commission,
            Insurance = breakdown.Insurance,
            Total = breakdown.Total,
            MinimumFeeApplied = breakdown.MinimumFeeApplied,
            Currency = "USD",
            PaymentMethod = "cash",
            State = state ?? SettlementState.Settled,
            CodState = CodSettlementState.Recorded,
            SettledAt = DateTimeOffset.UtcNow,
            LedgerEntryId = null, // the unposted condition
        };
    }
}
