using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Push;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-300 — the accept→delivery assignment-mirror race, leg (b).
///
/// On live (<c>FeatureFlags:UseUpstream:Delivery=true</c>) a PATCH /deliveries/{id}/status
/// fired within seconds of accept can arrive at delivery-service BEFORE the accept-time
/// assignment mirror has made the row carry <c>jeeber_id</c>. delivery-service's authorise()
/// then 403s wrong_party even for the LEGITIMATELY-assigned party. The fix
/// (<c>PatchStatusViaDeliveryServiceAsync</c>): on an upstream 403, re-run the IDEMPOTENT
/// assignment mirror from the gateway's local ledger row (which the accept saga stamped with
/// the winning jeeber) and retry the transition ONCE.
///
/// These tests drive the real PATCH path with the delivery client swapped for an in-process
/// double that models the race — 403 while the row is unassigned, allow once the mirror upsert
/// carries jeeber_id — so no live Go upstream is needed. Same harness family as
/// <see cref="StatusChangePushTests"/>.
/// </summary>
public class AssignmentMirrorRaceTests
{
    private const double PickupLat = 33.5138;
    private const double PickupLng = 36.2765;

    /// <summary>
    /// KEYSTONE: a legitimately-assigned jeeber whose transition 403s because the row is not
    /// yet assigned upstream gets a 200 — the gateway re-mirrors the assignment from its local
    /// ledger row and retries the transition once. Proves the race self-heals in-request.
    /// </summary>
    [Fact]
    public async Task PatchStatus_403_Then_ReMirror_And_Retry_Succeeds()
    {
        var delivery = new RaceyDeliveryClient(); // unassigned upstream → first transition 403s
        await using var factory = UpstreamFactory(delivery);
        var (deliveryId, jeeberId) = await SeedAcceptedAsync(factory, assignLocally: true);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var patch = await jeeber.PatchAsJsonAsync(
            $"/deliveries/{deliveryId}/status", new { to = CanonicalDeliveryStatus.InTransit });

        patch.StatusCode.Should().Be(HttpStatusCode.OK,
            "the 403 was the assignment-mirror race; the re-mirror + single retry resolves it");
        delivery.TransitionAttempts.Should().Be(2, "exactly one retry after the re-mirror (bounded)");
        delivery.AssignmentUpserts.Should().ContainSingle()
            .Which.JeeberId.Should().Be(jeeberId, "the re-mirror carried the winning jeeber from the local ledger");
    }

    /// <summary>
    /// A GENUINE wrong-party (the gateway holds NO local assignment for this delivery) is
    /// surfaced as 403 unchanged — there is nothing to re-mirror, so the original verdict wins
    /// and the transition is attempted exactly once (no retry, no upsert).
    /// </summary>
    [Fact]
    public async Task PatchStatus_403_With_No_Local_Assignment_Surfaces_403_Unchanged()
    {
        var delivery = new RaceyDeliveryClient();
        await using var factory = UpstreamFactory(delivery);
        // Seed a row with tier/pickup but NO jeeber assigned locally → repair has nothing to do.
        var (deliveryId, _) = await SeedAcceptedAsync(factory, assignLocally: false);

        var stranger = ClientFor(factory, $"stranger-jeeber-{Guid.NewGuid()}", "driver");
        var patch = await stranger.PatchAsJsonAsync(
            $"/deliveries/{deliveryId}/status", new { to = CanonicalDeliveryStatus.InTransit });

        patch.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "with no local assignment there is nothing to re-mirror; the genuine 403 is surfaced");
        delivery.TransitionAttempts.Should().Be(1, "no retry when the re-mirror cannot help");
        delivery.AssignmentUpserts.Should().BeEmpty("no assignment was re-POSTed");
    }

    /// <summary>
    /// BOUNDEDNESS: even when the assignment IS re-mirrored, if the upstream STILL 403s (a
    /// real wrong-party that survives the re-mirror), the gateway surfaces the 403 after
    /// exactly ONE retry — it never loops. Proves the retry budget is 1, not unbounded.
    /// </summary>
    [Fact]
    public async Task PatchStatus_403_Persists_After_ReMirror_Surfaces_403_After_Single_Retry()
    {
        var delivery = new RaceyDeliveryClient { AlwaysWrongParty = true };
        await using var factory = UpstreamFactory(delivery);
        var (deliveryId, jeeberId) = await SeedAcceptedAsync(factory, assignLocally: true);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var patch = await jeeber.PatchAsJsonAsync(
            $"/deliveries/{deliveryId}/status", new { to = CanonicalDeliveryStatus.InTransit });

        patch.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a 403 that survives the re-mirror is a genuine verdict, surfaced verbatim");
        delivery.TransitionAttempts.Should().Be(2, "the retry is bounded to exactly one, never a loop");
        delivery.AssignmentUpserts.Should().ContainSingle("the re-mirror ran once before the single retry");
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private static WebApplicationFactory<Program> UpstreamFactory(RaceyDeliveryClient delivery)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Delivery", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(delivery);
                services.RemoveAll<IPushNotificationService>();
                services.AddSingleton<IPushNotificationService>(new NoopPushService());
            });
        });

    private static async Task<(string deliveryId, string jeeberId)> SeedAcceptedAsync(
        WebApplicationFactory<Program> factory, bool assignLocally)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var jeeberId = $"win-jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = $"client-{Guid.NewGuid()}",
            Description = "Pick up the parcel",
            TierId = "flash",
            PickupLocation = new GeoPoint { Lat = PickupLat, Lng = PickupLng },
            DropoffLocation = new GeoPoint { Lat = PickupLat + 0.01, Lng = PickupLng + 0.01 },
        }, CancellationToken.None);

        if (assignLocally)
        {
            (await store.TryAcceptByJeeberAsync(created.Id, jeeberId, int.MaxValue, DateTimeOffset.UtcNow, CancellationToken.None))
                .Should().NotBeNull("the accept saga stamps the winning jeeber onto the local ledger row");
            (await store.SetStatusAsync(created.Id, RequestStatus.PickedUp, CancellationToken.None)).Should().BeTrue();
        }

        return (created.Id, jeeberId);
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    /// <summary>
    /// Delivery-service double modelling the assignment race: the canonical transition 403s
    /// wrong_party while the row is unassigned, then allows once a create-row upsert carrying
    /// <c>jeeber_id</c> flips it to assigned (late-assignment, never steals). Optionally 403s
    /// forever (<see cref="AlwaysWrongParty"/>) to model a genuine wrong-party. Every other
    /// member is loud — this path must not call them.
    /// </summary>
    private sealed class RaceyDeliveryClient : IDeliveryServiceClient
    {
        private bool _assigned;

        /// <summary>When true the transition 403s even after the row carries jeeber_id.</summary>
        public bool AlwaysWrongParty { get; init; }

        public int TransitionAttempts { get; private set; }
        public List<CreateDeliveryRowUpstream> AssignmentUpserts { get; } = new();

        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(
            string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct)
        {
            TransitionAttempts++;
            if (!_assigned || AlwaysWrongParty)
            {
                throw new DeliveryTransitionException(
                    (int)HttpStatusCode.Forbidden, "wrong_party", from: "Ordered", to: to, trigger: partySource);
            }
            return Task.FromResult(new DeliveryTransitionUpstream { DeliveryId = deliveryId, Status = to });
        }

        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(body.JeeberId))
            {
                AssignmentUpserts.Add(body);
                _assigned = true; // idempotent late-assignment: the row now carries jeeber_id
            }
            return Task.FromResult(new DeliveryRowUpstream { Id = body.Id, TenantId = body.TenantId, Status = "Ordered" });
        }

        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct)
            => Task.FromResult<DeliveryReadUpstream?>(new DeliveryReadUpstream
            {
                DeliveryId = deliveryId,
                Status = "Ordered",
                CreatedAt = DateTimeOffset.UtcNow
            });

        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoopPushService : IPushNotificationService
    {
        public Task<PushDeliveryResult> SendAsync(PushNotificationRequest request, CancellationToken ct)
            => Task.FromResult(new PushDeliveryResult(request.UserId, request.Trigger, PushDeliveryOutcome.Delivered, 1));
    }
}
