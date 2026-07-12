using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
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
/// E22 / I3 (JEBV4-241, cross-ref JEBV4-217; Q-036) — a COMPLETED delivery auto-closes
/// its chat conversation, and it does so through the <b>consumed</b> chat-service's own
/// API (round-3 SETTLED, Lane-I consumption path), never a gateway store / Firestore
/// write.
///
/// <para>MECHANISM: the gateway hooks the single completion convergence point
/// (<c>DeliveriesController.CreditJeeberOnCompletionAsync</c>, reached by BOTH the OTP
/// verify → Done leg and the customer PATCH → Done leg) and calls
/// <see cref="IConversationProvisioner.CloseConversationAsync"/>, backed by the consumed
/// chat-service's existing channel-deactivate verb
/// (<c>PATCH /api/channels/{id}/deactivate</c>). Driving the S08 conversation aggregate
/// to a NEW <c>closed</c> phase is deliberately NOT taken (that phase does not exist in
/// chat-service and would need the owner-approved extension protocol) — the ONE writer is
/// the deactivate verb. These tests assert the transition is composed exactly once with
/// the delivery row's <c>ConversationId</c>, and that a chat blip degrades-don't-fail.</para>
///
/// <para>Same in-process harness as <c>JeeberEarningsOnCompleteTests</c>: the delivery +
/// OTP NSwag clients are swapped for fakes and <see cref="IConversationProvisioner"/> is
/// swapped for a recording fake, so no live upstream is needed.</para>
/// </summary>
public class DeliveryCompleteChatAutoCloseTests
{
    private const string RecipientPhone = "+9613123456";
    private const string TenantApplicationId = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46";
    private const string ValidCode = "1234";
    private const decimal AcceptedFee = 2_000_000m;

    /// <summary>
    /// KEYSTONE: an OTP verify that completes the handover (→ Done) auto-closes the
    /// delivery's conversation via the consumed chat-service — exactly once, with the
    /// row's ConversationId.
    /// </summary>
    [Fact]
    public async Task Otp_Verify_Completion_Closes_Conversation_ViaConsumedChatService()
    {
        var chat = new RecordingConversationProvisioner();
        await using var factory = UpstreamFactory(SuccessfulVerifyClient(), chat);
        var conversationId = "conv-close-" + Guid.NewGuid();
        var (deliveryId, jeeberId) = await SeedAtDoorWithFeeAsync(factory, AcceptedFee, conversationId);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var verify = await jeeber.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code = ValidCode });

        verify.StatusCode.Should().Be(HttpStatusCode.OK, "the handover completes on the upstream path");

        // The conversation was driven to closed exactly once, keyed by the row's
        // ConversationId — through the consumed chat-service (not a gateway store).
        chat.CloseCallCount.Should().Be(1);
        chat.LastClosedConversationId.Should().Be(conversationId);
    }

    /// <summary>
    /// The customer PATCH → Done completion leg reaches the SAME convergence point and
    /// closes the conversation too (proves the ONE-writer hook covers both legs).
    /// </summary>
    [Fact]
    public async Task Customer_Patch_To_Done_Closes_Conversation()
    {
        var chat = new RecordingConversationProvisioner();
        await using var factory = UpstreamFactory(DoneTransitionClient(), chat);
        var conversationId = "conv-patch-" + Guid.NewGuid();
        var (deliveryId, _) = await SeedAtDoorWithFeeAsync(factory, AcceptedFee, conversationId);

        var client = ClientFor(factory, "close-client-" + Guid.NewGuid(), "customer");
        var patch = await client.PatchAsync(
            $"/v1/deliveries/{deliveryId}/status",
            JsonContent.Create(new { to = "Done" }));

        patch.StatusCode.Should().Be(HttpStatusCode.OK, "the customer PATCH drives the delivery to Done");

        chat.CloseCallCount.Should().Be(1);
        chat.LastClosedConversationId.Should().Be(conversationId);
    }

    /// <summary>
    /// DEGRADE-DON'T-FAIL: a chat-service blip on the close must NEVER turn a committed,
    /// settled completion into a 5xx — the verify still returns 200.
    /// </summary>
    [Fact]
    public async Task Completion_WhenChatCloseThrows_StillReturns200()
    {
        var chat = new ThrowingCloseConversationProvisioner();
        await using var factory = UpstreamFactory(SuccessfulVerifyClient(), chat);
        var (deliveryId, jeeberId) = await SeedAtDoorWithFeeAsync(factory, AcceptedFee, "conv-blip-" + Guid.NewGuid());

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var verify = await jeeber.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code = ValidCode });

        verify.StatusCode.Should().Be(HttpStatusCode.OK,
            "a chat close blip is best-effort and must not fail the committed completion");
    }

    /// <summary>
    /// When the delivery has NO conversation id (chat was down at create), the close is a
    /// no-op forward of null — the completion still succeeds and no channel is targeted.
    /// </summary>
    [Fact]
    public async Task Completion_WhenNoConversationId_ForwardsNull_NoTarget()
    {
        var chat = new RecordingConversationProvisioner();
        await using var factory = UpstreamFactory(SuccessfulVerifyClient(), chat);
        var (deliveryId, jeeberId) = await SeedAtDoorWithFeeAsync(factory, AcceptedFee, conversationId: null);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var verify = await jeeber.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code = ValidCode });

        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        // The hook still fires once (single convergence point) but forwards a null
        // conversation id — the real provisioner no-ops it; no channel is closed.
        chat.CloseCallCount.Should().Be(1);
        chat.LastClosedConversationId.Should().BeNull();
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private static ConfigurableDeliveryClient SuccessfulVerifyClient() => new()
    {
        VerifyOutcome = _ => new DeliveryHandoverVerifyResult
        {
            DeliveryId = "overwritten",
            Verified = true,
            Status = CanonicalDeliveryStatus.Done
        }
    };

    private static ConfigurableDeliveryClient DoneTransitionClient() => new()
    {
        TransitionTo = CanonicalDeliveryStatus.Done
    };

    private WebApplicationFactory<Program> UpstreamFactory(
        ConfigurableDeliveryClient delivery, IConversationProvisioner chat)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Delivery", "true");
            builder.UseSetting("Auth:Otp:ApplicationId", TenantApplicationId);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(delivery);
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new RecordingOtpClient());
                services.RemoveAll<IConversationProvisioner>();
                services.AddSingleton(chat);
            });
        });

    private static async Task<(string deliveryId, string jeeberId)> SeedAtDoorWithFeeAsync(
        WebApplicationFactory<Program> factory, decimal fee, string? conversationId)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"close-client-{Guid.NewGuid()}";
        var jeeberId = $"close-jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the parcel",
            RecipientPhone = RecipientPhone
        }, default);
        (await store.TryAcceptByJeeberAsync(created.Id, jeeberId, int.MaxValue, DateTimeOffset.UtcNow, default))
            .Should().NotBeNull();
        (await store.TrySetAcceptedFeeAsync(created.Id, fee, default)).Should().BeTrue();
        (await store.SetStatusAsync(created.Id, RequestStatus.AtDoor, default)).Should().BeTrue();
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            created.ConversationId = conversationId;
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

    /// <summary>Records the delivery-complete conversation-close call for assertions.</summary>
    private sealed class RecordingConversationProvisioner : IConversationProvisioner
    {
        public int CloseCallCount { get; private set; }
        public string? LastClosedConversationId { get; private set; }

        public Task<string?> CreateBroadcastingConversationAsync(
            string requestId, string clientId, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<string?> AdvanceToAcceptedAsync(
            string? conversationId, string winningJeeberId,
            IReadOnlyList<string> losingMemberIds, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task CloseConversationAsync(string? conversationId, CancellationToken ct)
        {
            CloseCallCount++;
            LastClosedConversationId = conversationId;
            return Task.CompletedTask;
        }
    }

    /// <summary>Simulates a chat-service blip during the delivery-complete close.</summary>
    private sealed class ThrowingCloseConversationProvisioner : IConversationProvisioner
    {
        public Task<string?> CreateBroadcastingConversationAsync(
            string requestId, string clientId, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<string?> AdvanceToAcceptedAsync(
            string? conversationId, string winningJeeberId,
            IReadOnlyList<string> losingMemberIds, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task CloseConversationAsync(string? conversationId, CancellationToken ct)
            => throw new HttpRequestException("chat-service unavailable");
    }

    /// <summary>
    /// Delivery-service double: the verify hop and the canonical transition hop each
    /// return a configurable terminal status; everything else is loud. GetCanonical
    /// returns Done so the read-through/enrich paths behave.
    /// </summary>
    private sealed class ConfigurableDeliveryClient : IDeliveryServiceClient
    {
        public Func<bool, DeliveryHandoverVerifyResult> VerifyOutcome { get; init; }
            = _ => throw new DeliveryHandoverException((int)HttpStatusCode.Conflict, "not_at_door");

        public string? TransitionTo { get; init; }

        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(
            string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct)
        {
            var r = VerifyOutcome(success);
            return Task.FromResult(new DeliveryHandoverVerifyResult
            {
                DeliveryId = deliveryId,
                Verified = r.Verified,
                Status = r.Status
            });
        }

        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(
            string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct)
        {
            if (TransitionTo is null)
            {
                throw new NotSupportedException();
            }
            return Task.FromResult(new DeliveryTransitionUpstream
            {
                DeliveryId = deliveryId,
                Status = TransitionTo,
                TransitionedAt = DateTimeOffset.UtcNow
            });
        }

        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct)
            => Task.FromResult<DeliveryReadUpstream?>(new DeliveryReadUpstream
            {
                DeliveryId = deliveryId,
                Status = CanonicalDeliveryStatus.Done,
                CreatedAt = DateTimeOffset.UtcNow
            });

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

    private sealed class RecordingOtpClient : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
