using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using JeebGateway.Infrastructure;
using Xunit;

namespace JeebGateway.IntegrationTests.Infrastructure;

/// <summary>
/// AUDIT-A durability guard-gap closure (JEBV4-124 / 143 / 148 — umbrella JEBV4-122).
///
/// <para>Three gateway stores were previously ABSENT from <see cref="StoreDurabilityGuard"/>
/// entirely — so a prod-like gateway could boot green (200 + healthy /health/ready) while
/// serving them from process memory. These tests pin the correct classification of each so a
/// mis-deploy fails closed / is logged loudly instead of silently losing data:</para>
///
/// <list type="bullet">
///   <item><b>JEBV4-124 ISettlementEnqueueStore</b> → <see cref="StoreDurabilityGuard.Critical"/>
///   (MONEY-ADJACENT; durable target PostgresSettlementEnqueueStore + migration 0034).</item>
///   <item><b>JEBV4-143 ILocationStore</b> → <see cref="StoreDurabilityGuard.IntentionalInMemory"/>
///   (derived, rebuildable hot-path GPS cache; authoritative last-known location is the already-
///   Critical PostgresAvailabilityStore — the IGeoIndex/JEBV4-156 precedent).</item>
///   <item><b>JEBV4-148 IPendingOffersStore</b> → <see cref="StoreDurabilityGuard.UpstreamContractIncomplete"/>
///   (RECLASSIFIED 2026-08-01; was KnownInMemoryBacklog). It holds no gateway state at all — its
///   sole implementation is the thin-BFF UpstreamPendingOffersStore — but its contract is
///   incomplete (5 members throw NotSupportedException pending offer-service routes). Logged
///   loudly, not silent; promotion to Critical stays owner-gated.</item>
/// </list>
///
/// <para><b>Why the reclassification matters.</b> While IPendingOffersStore sat on
/// KnownInMemoryBacklog — a list whose own doc read "stores of record still awaiting a Postgres
/// target" — the guard was lying about its own subject: an auditor asking "what in-memory state
/// does the gateway hold?" got a false positive on a store holding none. The gap is real; the
/// category was wrong. These tests pin the correct bucket AND, in
/// <see cref="Guard_Still_Reds_For_A_Genuine_InMemory_StoreOfRecord"/>, prove the move did not
/// blunt the guard's teeth for a store that genuinely IS in-memory.</para>
/// </summary>
public class StoreDurabilityGuardGapStoresTests
{
    private static bool InCritical(Type iface) =>
        StoreDurabilityGuard.Critical.Any(c => c.Iface == iface);

    // ── JEBV4-124 — settlement enqueue is Critical (durable) ───────────────

    // INVERTED at gwdbx W2-R02: migration 0052 dropped settlement_enqueue, so the durable target
    // this entry demanded no longer exists and the entry left the roster with it.
    [Fact]
    public void SettlementEnqueue_Left_Critical_When_Its_Table_Was_Dropped()
    {
        InCritical(typeof(JeebGateway.Financials.ISettlementEnqueueStore)).Should()
            .BeFalse("demanding PostgresSettlementEnqueueStore would refuse every prod-like boot");

        StoreDurabilityGuard.KnownInMemoryBacklog.Should()
            .NotContain(typeof(JeebGateway.Financials.ISettlementEnqueueStore),
                "the prod-like registration is the Null store, not an in-memory store of record");
        StoreDurabilityGuard.IntentionalInMemory.Should()
            .NotContain(typeof(JeebGateway.Financials.ISettlementEnqueueStore),
                "laundering a money store onto a log-only list would make the guard lie");
    }

    // ── JEBV4-143 — location store is IntentionalInMemory (rebuildable cache) ──

    [Fact]
    public void LocationStore_Is_IntentionalInMemory_Not_Critical_Not_Backlog()
    {
        StoreDurabilityGuard.IntentionalInMemory.Should()
            .Contain(typeof(JeebGateway.Tracking.ILocationStore),
                "the latest-GPS-fix hot path is a derived, rebuildable cache whose truth lives in the durable jeeber_availability table");

        InCritical(typeof(JeebGateway.Tracking.ILocationStore)).Should()
            .BeFalse("a rebuildable cache must not fail the boot gate");
        StoreDurabilityGuard.KnownInMemoryBacklog.Should()
            .NotContain(typeof(JeebGateway.Tracking.ILocationStore),
                "it is intentional-in-memory (no migration pending), not a backlog gap");
    }

    // ── JEBV4-148 — pending offers is an INCOMPLETE UPSTREAM CONTRACT, not in-memory ──

    [Fact]
    public void PendingOffers_Is_UpstreamContractIncomplete_Not_InMemoryBacklog_Not_Critical()
    {
        StoreDurabilityGuard.UpstreamContractIncomplete.Should()
            .Contain(typeof(JeebGateway.Availability.IPendingOffersStore),
                "its sole implementation is the stateless thin-BFF UpstreamPendingOffersStore whose contract is incomplete (members throw NotSupportedException pending offer-service routes)");

        StoreDurabilityGuard.KnownInMemoryBacklog.Should()
            .NotContain(typeof(JeebGateway.Availability.IPendingOffersStore),
                "it holds NO state in process memory, so a list meaning 'still in-memory, lost on restart' must not name it — that is the checker lying about its own subject");

        InCritical(typeof(JeebGateway.Availability.IPendingOffersStore)).Should()
            .BeFalse("it is not yet promoted to the fail-closed set — that is an owner decision (JEBV4-148)");
    }

    // ── All three are now KNOWN to the guard (the gap is closed) ───────────

    [Fact]
    public void All_Three_GuardGap_Stores_Are_Now_Classified_By_The_Guard()
    {
        var classified = new HashSet<Type>(
            StoreDurabilityGuard.Critical.Select(c => c.Iface)
                .Concat(StoreDurabilityGuard.KnownInMemoryBacklog)
                .Concat(StoreDurabilityGuard.UpstreamContractIncomplete)
                .Concat(StoreDurabilityGuard.IntentionalInMemory));

        // W2-R02: ISettlementEnqueueStore is deliberately no longer classified — its table is gone
        // and its prod-like registration is the Null store (SettlementStoreRetiredW2R02Tests.A1/A2).
        classified.Should().NotContain(typeof(JeebGateway.Financials.ISettlementEnqueueStore));
        classified.Should().Contain(typeof(JeebGateway.Tracking.ILocationStore));
        classified.Should().Contain(typeof(JeebGateway.Availability.IPendingOffersStore));
    }

    // ── Guard invariants still hold with the three additions ───────────────

    [Fact]
    public void No_Store_Is_Classified_In_More_Than_One_Bucket()
    {
        var critical = StoreDurabilityGuard.Critical.Select(c => c.Iface).ToList();
        var backlog = StoreDurabilityGuard.KnownInMemoryBacklog.ToList();
        var upstreamIncomplete = StoreDurabilityGuard.UpstreamContractIncomplete.ToList();
        var intentional = StoreDurabilityGuard.IntentionalInMemory.ToList();

        critical.Should().NotIntersectWith(backlog, "a durable store is not a backlog gap");
        critical.Should().NotIntersectWith(upstreamIncomplete, "a durable store is not an incomplete upstream contract");
        critical.Should().NotIntersectWith(intentional, "a durable store is not a rebuildable cache");
        backlog.Should().NotIntersectWith(upstreamIncomplete, "an in-memory store of record is not a stateless adapter");
        backlog.Should().NotIntersectWith(intentional, "a backlog gap is not an intentional cache");
        upstreamIncomplete.Should().NotIntersectWith(intentional, "an incomplete upstream contract is not a rebuildable cache");
    }

    // ── NEGATIVE CONTROL (executed, not described) ─────────────────────────
    //
    // Moving IPendingOffersStore between two LOGGING lists must not blunt the guard's
    // teeth. This proves, by execution, that a store which genuinely IS an in-memory
    // store of record still produces a violation — and that the all-durable baseline is
    // silent, so the assertion is not passing vacuously.

    [Fact]
    public void Guard_Still_Reds_For_A_Genuine_InMemory_StoreOfRecord()
    {
        var durable = StoreDurabilityGuard.Critical
            .ToDictionary(c => c.Iface, c => c.DurableImpls[0]);

        // POSITIVE CONTROL — every critical interface on its approved durable impl.
        // A checker that can never be silent proves nothing when it fires.
        StoreDurabilityGuard
            .Evaluate(t => durable.TryGetValue(t, out var ok) ? ok : null)
            .Should().BeEmpty("all-durable is the green baseline the negative control is measured against");

        // NEGATIVE CONTROL — swap exactly ONE critical store for a real in-memory
        // store of record (money state) and require the guard to red.
        // RE-TARGETED at W2-R02: ISettlementStore left the roster with its table, so the negative
        // control now uses the refresh-token store, which is still Critical.
        var mutated = new Dictionary<Type, Type>(durable)
        {
            [typeof(JeebGateway.Tokens.IRefreshTokenStore)] =
                typeof(JeebGateway.Tokens.InMemoryRefreshTokenStore),
        };

        var violations = StoreDurabilityGuard
            .Evaluate(t => mutated.TryGetValue(t, out var impl) ? impl : null);

        violations.Should().ContainSingle(
            "exactly one critical store was mutated, so exactly one violation must be reported");
        violations[0].Should().Contain(nameof(JeebGateway.Tokens.IRefreshTokenStore))
            .And.Contain(nameof(JeebGateway.Tokens.InMemoryRefreshTokenStore),
                "the violation must name both the contract and the in-memory impl that breached it");

        // And the reclassified store is still absent from the fail-closed set, so this
        // PR changed logging categories only — never the gate's behaviour.
        InCritical(typeof(JeebGateway.Availability.IPendingOffersStore)).Should().BeFalse();
    }
}
