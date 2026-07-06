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
///   <item><b>JEBV4-148 IPendingOffersStore</b> → <see cref="StoreDurabilityGuard.KnownInMemoryBacklog"/>
///   (durable target is offer-service via the UpstreamPendingOffersStore BFF, but promotion to
///   Critical is owner-gated on the UseUpstream:Offer flag + offer-service route completion; logged
///   loudly, not silent).</item>
/// </list>
/// </summary>
public class StoreDurabilityGuardGapStoresTests
{
    private static bool InCritical(Type iface) =>
        StoreDurabilityGuard.Critical.Any(c => c.Iface == iface);

    // ── JEBV4-124 — settlement enqueue is Critical (durable) ───────────────

    [Fact]
    public void SettlementEnqueue_Is_Critical_With_Postgres_DurableTarget()
    {
        var entry = StoreDurabilityGuard.Critical
            .FirstOrDefault(c => c.Iface == typeof(JeebGateway.Financials.ISettlementEnqueueStore));

        entry.Iface.Should().Be(typeof(JeebGateway.Financials.ISettlementEnqueueStore),
            "the money-adjacent settlement-enqueue intent must be fail-closed guarded");
        entry.DurableImpls.Should().ContainSingle()
            .Which.Should().Be(typeof(JeebGateway.Financials.PostgresSettlementEnqueueStore));
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

    // ── JEBV4-148 — pending offers is on the known-in-memory backlog ───────

    [Fact]
    public void PendingOffers_Is_On_The_InMemory_Backlog_Not_Critical()
    {
        StoreDurabilityGuard.KnownInMemoryBacklog.Should()
            .Contain(typeof(JeebGateway.Availability.IPendingOffersStore),
                "the pending-offers ledger is a known in-memory gap, logged loudly (promotion to Critical is owner-gated on UseUpstream:Offer + offer-service routes)");

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
                .Concat(StoreDurabilityGuard.IntentionalInMemory));

        classified.Should().Contain(typeof(JeebGateway.Financials.ISettlementEnqueueStore));
        classified.Should().Contain(typeof(JeebGateway.Tracking.ILocationStore));
        classified.Should().Contain(typeof(JeebGateway.Availability.IPendingOffersStore));
    }

    // ── Guard invariants still hold with the three additions ───────────────

    [Fact]
    public void No_Store_Is_Classified_In_More_Than_One_Bucket()
    {
        var critical = StoreDurabilityGuard.Critical.Select(c => c.Iface).ToList();
        var backlog = StoreDurabilityGuard.KnownInMemoryBacklog.ToList();
        var intentional = StoreDurabilityGuard.IntentionalInMemory.ToList();

        critical.Should().NotIntersectWith(backlog, "a durable store is not a backlog gap");
        critical.Should().NotIntersectWith(intentional, "a durable store is not a rebuildable cache");
        backlog.Should().NotIntersectWith(intentional, "a backlog gap is not an intentional cache");
    }
}
