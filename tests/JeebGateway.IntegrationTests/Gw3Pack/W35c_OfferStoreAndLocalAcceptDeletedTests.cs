using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Infrastructure;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Gw3Pack;

/// <summary>
/// GW3 / W3.5(c) — RUNTIME half of "delete the in-memory offer store and the local
/// accept path".
///
/// The batch's own claim is a behavioural one — "the mapping is UNCONDITIONAL" and
/// "there is no second accept path" — and neither is provable by grepping for a
/// deleted identifier. Each assertion here boots the real host:
///
///   C9a/C9b  IPendingOffersStore resolves to the thin-BFF store with the offer
///            flag OFF and ON. Before GW3, OFF selected a different concrete type,
///            so C9a is the one that fails if the branch is restored.
///   C10      the gateway assembly ships exactly ONE implementation.
///   C11      POST /v1/offers/{id}/accept forwards to offer-service with the flag
///            OFF. Before GW3, flag-OFF ran a ~95-line local auction close that
///            never touched IOfferServiceClient at all. This is the decisive
///            discriminator between "deleted" and "still there but unused today".
///   C12      the fixture double lives in the TEST assembly.
///   C13      GW1's sealed StoreDurabilityGuard.Critical is untouched, and the
///            offer ledger is still on the known-in-memory backlog — GW3 changed
///            the SHAPE of that gap, it did not close it.
///
/// NOT PROVEN HERE: that MSI's live gateway answers accept, that offer-service
/// exists, or that a real jeeber's bid reaches a real customer. Those are `service`
/// / `device` evidence (GATE.md §3) and belong to V-2.
/// </summary>
public class W35c_OfferStoreAndLocalAcceptDeletedTests
{
    // ---------------------------------------------------------------------
    // C9 — the registration is unconditional, observed as behaviour.
    // ---------------------------------------------------------------------
    [Theory]
    [InlineData("false")]   // C9a — the leg that reds if the flag-off branch returns
    [InlineData("true")]    // C9b — the deployed posture
    public void C9_PendingOffersStore_IsAlwaysTheUpstreamStore(string offerFlag)
    {
        using var factory = HostWithOfferFlag(offerFlag);
        using var scope = factory.Services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IPendingOffersStore>();

        store.Should().BeOfType<UpstreamPendingOffersStore>(
            $"FeatureFlags:UseUpstream:Offer={offerFlag} must not select a store — GW3 made the "
            + "IPendingOffersStore mapping unconditional and deleted the second implementation");
    }

    // ---------------------------------------------------------------------
    // C10 — assembly census. A grep for the deleted class name cannot see the
    // same store re-added under another name; this can.
    // ---------------------------------------------------------------------
    [Fact]
    public void C10_GatewayAssemblyShipsExactlyOnePendingOffersStore()
    {
        var impls = typeof(Program).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IPendingOffersStore).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        impls.Should().BeEquivalentTo(
            new[] { nameof(UpstreamPendingOffersStore) },
            "the gateway holds no offer state after GW3; offer-service is the ledger of record");
    }

    // ---------------------------------------------------------------------
    // C11 — the accept path is unconditional. THE decisive test for W3.5(c).
    // ---------------------------------------------------------------------
    [Fact]
    public async Task C11_Accept_ForwardsToOfferService_EvenWithTheOfferFlagOff()
    {
        var upstream = new RecordingOfferServiceClient
        {
            Result = new OfferAcceptResult { Status = OfferAcceptStatus.Conflict },
        };

        using var factory = HostWithOfferFlag("false", services =>
        {
            services.RemoveAll<IOfferServiceClient>();
            services.AddSingleton<IOfferServiceClient>(upstream);
        });

        var offerId = $"offer-{Guid.NewGuid()}";
        var requestId = $"req-{Guid.NewGuid()}";
        var clientId = $"client-{Guid.NewGuid()}";
        // Same fast-path seeding RequestOffersController.Submit does at submit time.
        factory.Services.GetRequiredService<IOfferRequestIndex>().Record(offerId, requestId);

        var customer = factory.CreateClient();
        customer.DefaultRequestHeaders.Add("X-User-Id", clientId);
        customer.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var resp = await customer.PostAsync($"/v1/offers/{offerId}/accept", content: null);

        // The discriminator: the OLD flag-off branch — the deleted local accept helper,
        // deliberately NOT named here so the pack's own symbol grep stays honest — looked
        // the offer up in the gateway's own store, found nothing, and returned 404 having
        // never constructed an upstream call. CallCount==0 there; ==1 here.
        upstream.CallCount.Should().Be(1,
            "with the offer flag OFF the accept must still forward to the offer-service saga; "
            + "a non-zero count is only reachable if the local in-memory accept is gone");
        upstream.LastOfferId.Should().Be(offerId);
        upstream.LastRequestId.Should().Be(requestId);
        upstream.LastActingUserId.Should().Be(clientId);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "the upstream status is surfaced verbatim, not replaced by a local verdict");
    }

    // ---------------------------------------------------------------------
    // C12 — the store MOVED; it was not merely renamed inside the gateway.
    // ---------------------------------------------------------------------
    [Fact]
    public void C12_TheOfferFixtureDoubleLivesInTheTestAssembly()
    {
        var fake = typeof(Fakes.FakePendingOffersStore);

        fake.Assembly.Should().BeSameAs(GetType().Assembly,
            "the fixture double belongs to the fixture");
        fake.Assembly.Should().NotBeSameAs(typeof(Program).Assembly,
            "shipping an EnqueueForTest seam in production source is what W3.5(c) removed");
        typeof(IPendingOffersStore).IsAssignableFrom(fake).Should().BeTrue(
            "POS control: the moved type is still a real IPendingOffersStore, so C10's "
            + "census would have found it had it stayed in the gateway");
    }

    // ---------------------------------------------------------------------
    // C13 — cross-batch regression guard. GW1 sealed Critical at 33
    // (OWNER-DECISIONS.md 2026-07-31). GW3 must not move it, and must not quietly
    // drop the offer ledger off the backlog just because the in-memory store went
    // away — the remaining implementation still throws on 5 of its 9 members.
    // ---------------------------------------------------------------------
    [Fact]
    public void C13_StoreDurabilityGuard_IsUnchangedByGw3()
    {
        StoreDurabilityGuard.Critical.Should().HaveCount(33,
            "GW1's sealed predicate (SEALED-PREDICATES.md, owner ruling 2026-07-31)");

        StoreDurabilityGuard.KnownInMemoryBacklog.Should().Contain(typeof(IPendingOffersStore),
            "GW3 changed the shape of the offer-durability gap (no more restart-drops-bids) "
            + "but did not close it: the only surviving implementation throws NotSupportedException "
            + "on GetAsync / AcceptAsync / AcceptWithSupersedeAsync / TryEditAsync / WithdrawForJeeberAsync");

        StoreDurabilityGuard.Critical.Select(c => c.Iface)
            .Should().NotContain(typeof(IPendingOffersStore),
                "promotion is an owner decision on JEBV4-148, not a side effect of a delete");
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static WebApplicationFactory<Program> HostWithOfferFlag(
        string offerFlag, Action<IServiceCollection>? extra = null)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FeatureFlags:UseUpstream:Offer", offerFlag },
                        { "FeatureFlags:UseUpstream:Delivery", "false" },
                    }));
                if (extra is not null)
                {
                    builder.ConfigureTestServices(extra);
                }
            });

    /// <summary>Records the accept forward. Every other member throws, so a test that
    /// accidentally exercises a different route fails loudly instead of passing.</summary>
    private sealed class RecordingOfferServiceClient : IOfferServiceClient
    {
        public required OfferAcceptResult Result { get; init; }
        public int CallCount { get; private set; }
        public string? LastActingUserId { get; private set; }
        public string? LastRequestId { get; private set; }
        public string? LastOfferId { get; private set; }
        public string? LastIdempotencyKey { get; private set; }

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
        {
            CallCount++;
            LastActingUserId = actingUserId;
            LastRequestId = requestId;
            LastOfferId = offerId;
            LastIdempotencyKey = idempotencyKey;
            return Task.FromResult(Result);
        }

        public Task<OfferAcceptWire> AcceptAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException("GW3 C11 asserts the status-preserving accept only.");

        public Task<RequestMirrorResult> MirrorRequestAsync(
            string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException("not on the accept path");

        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct)
            => throw new NotSupportedException("not on the accept path");

        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException("not on the accept path");

        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId, long? feeCents, int? etaMinutes,
            string? note, int? maxEdits, CancellationToken ct)
            => throw new NotSupportedException("not on the accept path");

        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException("not on the accept path");
    }
}
