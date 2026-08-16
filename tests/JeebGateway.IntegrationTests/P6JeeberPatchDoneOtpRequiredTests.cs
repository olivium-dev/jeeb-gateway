using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Conversations;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// P6/G1 (batch b01-20260725) — the illegal <c>AtDoor → Done</c> edge is refused LOCALLY,
/// with a TYPED <c>otp_required</c> 422, before the gateway dials delivery-service.
///
/// <para>THE BUG: <c>AtDoor → Done</c> is not an edge. The frozen SM
/// (<see cref="DeliverySm"/> edges 10/11 + the escalate alias) gives <c>AtDoor</c> exactly
/// three exits, all of them OTP/escalation triggers — the only door to <c>Done</c> is
/// <c>otp_verified</c>, fired by <c>POST /v1/deliveries/{id}/otp/verify</c>. On 2026-07-25
/// the jeeber app nonetheless PATCHed <c>{to:"Done"}</c> five times and got five 1 ms
/// generic <c>transition_not_allowed</c> 422s back from upstream; the same delivery
/// completed 67 s later through the OTP verify endpoint.</para>
///
/// <para>G1 answers that PATCH at the gateway with a reason token the client can MATCH ON
/// (<c>otp_required</c>) instead of forwarding a provably-doomed transition. The guard is
/// scoped to <c>partySource == "jeeber"</c>: the customer completion leg and the admin
/// <c>admin_resolve</c> leg are untouched (GW-2 / GW-3), and the forward ladder is
/// unaffected (GW-4).</para>
///
/// <para>Harness mirrors <c>DeliveryCompleteChatAutoCloseTests</c> (same factory shape and
/// seed helpers) with the delivery client swapped for a RECORDING fake, so "the upstream
/// was never dialled" is an assertion and not a hope.</para>
/// </summary>
public class P6JeeberPatchDoneOtpRequiredTests
{
    private const string RecipientPhone = "+9613123456";
    private const string TenantApplicationId = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46";
    private const decimal AcceptedFee = 2_000_000m;

    // ----------------------------------------------------------------------
    // GW-1 — the keystone: refused locally, typed, zero upstream round-trips.
    // ----------------------------------------------------------------------

    /// <summary>
    /// GW-1: a jeeber PATCHing <c>{to:"Done"}</c> on a row at <c>AtDoor</c> gets the typed
    /// 422 <c>otp_required</c> problem+json — and delivery-service is NEVER called.
    /// </summary>
    [Fact]
    public async Task GW1_Jeeber_Patch_To_Done_Is_Refused_Locally_With_Typed_OtpRequired()
    {
        var upstream = new RecordingDeliveryClient { TransitionTo = CanonicalDeliveryStatus.Done };
        await using var factory = UpstreamFactory(upstream);
        var (deliveryId, jeeberId) = await SeedAtDoorAsync(factory);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var patch = await jeeber.PatchAsync(
            $"/v1/deliveries/{deliveryId}/status",
            JsonContent.Create(new { to = "Done" }));

        patch.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity,
            "AtDoor→Done is not an edge — only otp_verified opens that door");
        patch.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var body = JsonDocument.Parse(await patch.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.GetProperty("type").GetString().Should().Be("https://jeeb.dev/errors/otp-required");
        root.GetProperty("status").GetInt32().Should().Be(422);
        // THE CONTRACT the mobile client matches on (P6/S2b matches the TOKEN, not prose).
        root.GetProperty("reason").GetString().Should().Be("otp_required");
        root.GetProperty("from").GetString().Should().Be("AtDoor");
        root.GetProperty("to").GetString().Should().Be("Done");
        root.GetProperty("trigger").GetString().Should().Be("otp_verified");

        // The "no upstream round-trip" half of G1: the doomed transition never left here.
        upstream.TransitionCalls.Should().BeEmpty(
            "the gateway must fail fast on a provably-illegal edge, not forward it");
    }

    /// <summary>
    /// GW-1 (alias arm): the same refusal for the legacy <c>{status:"delivered"}</c> body
    /// shape, which <see cref="CanonicalDeliveryVocab.TryResolveTarget"/> also resolves to
    /// <c>Done</c>. The guard keys off the RESOLVED canonical target, not the wire spelling.
    /// </summary>
    [Fact]
    public async Task GW1_Jeeber_Patch_LegacyDeliveredAlias_Is_Also_Refused_Locally()
    {
        var upstream = new RecordingDeliveryClient { TransitionTo = CanonicalDeliveryStatus.Done };
        await using var factory = UpstreamFactory(upstream);
        var (deliveryId, jeeberId) = await SeedAtDoorAsync(factory);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var patch = await jeeber.PatchAsync(
            $"/v1/deliveries/{deliveryId}/status",
            JsonContent.Create(new { status = "delivered" }));

        patch.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var body = JsonDocument.Parse(await patch.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("reason").GetString().Should().Be("otp_required");
        upstream.TransitionCalls.Should().BeEmpty();
    }

    // ----------------------------------------------------------------------
    // GW-2 — the customer completion leg is untouched.
    // ----------------------------------------------------------------------

    /// <summary>
    /// GW-2: the customer "I received it" PATCH → <c>Done</c> still FORWARDS with
    /// <c>partySource == "client"</c> and still returns 200. This is the regression guard
    /// for the G1 scope — the guard must see the party source, not just the target.
    /// (The conversation-auto-close half of this leg stays covered by
    /// <c>DeliveryCompleteChatAutoCloseTests.Customer_Patch_To_Done_Closes_Conversation</c>,
    /// which is deliberately left unedited.)
    /// </summary>
    [Fact]
    public async Task GW2_Customer_Patch_To_Done_Still_Forwards_As_Client()
    {
        var upstream = new RecordingDeliveryClient { TransitionTo = CanonicalDeliveryStatus.Done };
        await using var factory = UpstreamFactory(upstream);
        var (deliveryId, _) = await SeedAtDoorAsync(factory);

        var customer = ClientFor(factory, "p6-client-" + Guid.NewGuid(), "customer");
        var patch = await customer.PatchAsync(
            $"/v1/deliveries/{deliveryId}/status",
            JsonContent.Create(new { to = "Done" }));

        patch.StatusCode.Should().Be(HttpStatusCode.OK, "the customer completion leg is untouched by G1");
        upstream.TransitionCalls.Should().HaveCount(1);
        upstream.TransitionCalls[0].To.Should().Be("Done");
        upstream.TransitionCalls[0].PartySource.Should().Be(CanonicalDeliveryVocab.SourceClient);
    }

    // ----------------------------------------------------------------------
    // GW-3 — admin_resolve still forwards.
    // ----------------------------------------------------------------------

    /// <summary>
    /// GW-3: an admin resolving an escalated row (<c>FailedNeedsEscalation → Done</c>, SM
    /// edge 12) is NOT intercepted — it forwards with <c>partySource == "admin"</c>.
    /// The actor carries a participant role too (driver,admin) so it clears the
    /// class-level <c>delivery.participate</c> capability, exactly as
    /// <c>DeliveryCanonicalVocabTests.PatchStatus_AdminResolve_ForwardsAdminPartySource</c>
    /// models it.
    /// </summary>
    [Fact]
    public async Task GW3_AdminResolve_From_Escalated_Row_Still_Forwards()
    {
        var upstream = new RecordingDeliveryClient { TransitionTo = CanonicalDeliveryStatus.Done };
        await using var factory = UpstreamFactory(upstream);
        var (deliveryId, _) = await SeedAtDoorAsync(factory);

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        (await store.SetStatusAsync(deliveryId, RequestStatus.Disputed, default)).Should().BeTrue();

        var admin = ClientFor(factory, "p6-admin-" + Guid.NewGuid(), "driver,admin");
        var patch = await admin.PatchAsync(
            $"/v1/deliveries/{deliveryId}/status",
            JsonContent.Create(new { trigger = "admin_resolve", to = "Done" }));

        patch.StatusCode.Should().Be(HttpStatusCode.OK, "admin_resolve is SM edge 12 and G1 must not touch it");
        upstream.TransitionCalls.Should().HaveCount(1);
        upstream.TransitionCalls[0].To.Should().Be("Done");
        upstream.TransitionCalls[0].PartySource.Should().Be(CanonicalDeliveryVocab.SourceAdmin);
    }

    // ----------------------------------------------------------------------
    // GW-4 — the forward ladder is unaffected for legal edges.
    // ----------------------------------------------------------------------

    /// <summary>
    /// GW-4: the jeeber's legal forward ladder (Ordered → Picked → InTransit → AtDoor) is
    /// forwarded verbatim, 200 each. G1 narrows exactly one target, not the path to it.
    /// </summary>
    [Theory]
    [InlineData("Picked")]
    [InlineData("InTransit")]
    [InlineData("AtDoor")]
    public async Task GW4_Jeeber_Forward_Ladder_Is_Still_Forwarded(string canonicalTo)
    {
        var upstream = new RecordingDeliveryClient { EchoRequestedTarget = true };
        await using var factory = UpstreamFactory(upstream);
        var (deliveryId, jeeberId) = await SeedAtDoorAsync(factory);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var patch = await jeeber.PatchAsync(
            $"/v1/deliveries/{deliveryId}/status",
            JsonContent.Create(new { to = canonicalTo }));

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        upstream.TransitionCalls.Should().HaveCount(1);
        upstream.TransitionCalls[0].To.Should().Be(canonicalTo);
        upstream.TransitionCalls[0].PartySource.Should().Be(CanonicalDeliveryVocab.SourceJeeber);
    }

    // ----------------------------------------------------------------------
    // GW-5 — the frozen edge set did not move.
    // ----------------------------------------------------------------------

    /// <summary>
    /// GW-5 (local mirror; the authoritative gate is
    /// <c>DeliverySmParityTests.Gateway_Table_Matches_Frozen_ADR002_Edge_Set</c>, which is
    /// deliberately NOT edited by P6): S8 added a REASON STRING, not an edge. If this count
    /// moves, S8 touched the frozen table and that is a defect.
    /// </summary>
    [Fact]
    public void GW5_Frozen_Edge_Count_Is_Unchanged_By_The_New_Reason_Constant()
    {
        DeliverySm.AllValidTransitions().Should().HaveCount(14);
        DeliverySm.ReasonOtpRequired.Should().Be("otp_required");
        DeliverySm.ReasonOtpRequired.Should().NotBe(DeliverySm.ReasonTransitionNotAllowed);
        // The reason token is NOT a trigger — it must never leak into the trigger lexicon.
        DeliveryTrigger.IsKnown(DeliverySm.ReasonOtpRequired).Should().BeFalse();
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private WebApplicationFactory<Program> UpstreamFactory(IDeliveryServiceClient delivery)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Delivery", "true");
            builder.UseSetting("Auth:Otp:ApplicationId", TenantApplicationId);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton(delivery);
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new NoOpOtpClient());
                services.RemoveAll<IConversationProvisioner>();
                services.AddSingleton<IConversationProvisioner>(new NoOpConversationProvisioner());
            });
        });

    private static async Task<(string deliveryId, string jeeberId)> SeedAtDoorAsync(
        WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"p6-client-{Guid.NewGuid()}";
        var jeeberId = $"p6-jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the parcel",
            RecipientPhone = RecipientPhone
        }, default);
        (await store.TryAcceptByJeeberAsync(created.Id, jeeberId, int.MaxValue, DateTimeOffset.UtcNow, default))
            .Should().NotBeNull();
        (await store.TrySetAcceptedFeeAsync(created.Id, AcceptedFee, default)).Should().BeTrue();
        (await store.SetStatusAsync(created.Id, RequestStatus.AtDoor, default)).Should().BeTrue();
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
    /// Delivery-service double that RECORDS every canonical transition hop, so a test can
    /// assert the gateway did — or provably did not — dial upstream.
    /// </summary>
    private sealed class RecordingDeliveryClient : IDeliveryServiceClient
    {
    // OA-21 (51a2677) added the provider-audience reads to IDeliveryServiceClient. This double's
    // subject is elsewhere; an empty audience is the neutral answer, not a simulated fault.
    public Task<IReadOnlyList<JeebGateway.Services.Clients.AvailableProviderUpstream>> ListAvailableProvidersAsync(
        double? lat, double? lng, double? radiusKm,
        IReadOnlyCollection<string>? roles, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JeebGateway.Services.Clients.AvailableProviderUpstream>>(
            System.Array.Empty<JeebGateway.Services.Clients.AvailableProviderUpstream>());

    public Task<IReadOnlyList<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>> ListKnownProvidersAsync(
        System.DateTimeOffset since, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>>(
            System.Array.Empty<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>());

        public List<(string DeliveryId, string To, string PartySource, string ActorId, string ActorRole)> TransitionCalls { get; }
            = new();

        /// <summary>Fixed status echoed back on a forwarded transition.</summary>
        public string? TransitionTo { get; init; }

        /// <summary>When true, the fake echoes whatever target the gateway forwarded.</summary>
        public bool EchoRequestedTarget { get; init; }

        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(
            string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct)
        {
            TransitionCalls.Add((deliveryId, to, partySource, actorId, actorRole));
            return Task.FromResult(new DeliveryTransitionUpstream
            {
                DeliveryId = deliveryId,
                Status = EchoRequestedTarget ? to : (TransitionTo ?? to),
                TransitionedAt = DateTimeOffset.UtcNow
            });
        }

        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct)
            => Task.FromResult<DeliveryReadUpstream?>(new DeliveryReadUpstream
            {
                DeliveryId = deliveryId,
                Status = CanonicalDeliveryStatus.AtDoor,
                CreatedAt = DateTimeOffset.UtcNow
            });

        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(
            string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Keeps the completion leg's chat auto-close off the wire in tests.</summary>
    private sealed class NoOpConversationProvisioner : IConversationProvisioner
    {
        public Task<string?> CreateBroadcastingConversationAsync(
            string requestId, string clientId, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task CloseConversationAsync(string? conversationId, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>Keeps the OTP service off the wire; P6/G1 never reaches an OTP hop.</summary>
    private sealed class NoOpOtpClient : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
