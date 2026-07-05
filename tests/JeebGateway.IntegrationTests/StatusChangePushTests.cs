using System.Net;
using System.Net.Http.Json;
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
/// fix/status-change-push (AUDIT-B FINDING #1). Same "stranded on the dead in-memory
/// branch" class as the settlement bug: the counterparty <see cref="NotificationTrigger.StatusChange"/>
/// push (courier picked up / on the way / arrived / delivery completed) was only wired
/// into the retired flag-OFF in-memory VerifyOtp branch. On live
/// (<c>FeatureFlags:UseUpstream:Delivery=true</c>) every transition returned 200 with NO
/// push, so customer &amp; jeeber got no delivery-status pushes at all.
///
/// THE FIX wires the existing <c>NotifyOtherPartyAsync</c> counterparty push into BOTH
/// live paths:
/// <list type="bullet">
///   <item><c>PatchStatusViaDeliveryServiceAsync</c> — the real PATCH /status path the
///     app drives for Picked/InTransit/AtDoor/Delivered.</item>
///   <item><c>VerifyOtpViaDeliveryServiceAsync</c> — the flag-ON handover verify → Done.</item>
/// </list>
/// Both are STRICTLY best-effort: a push-composer throw must never turn a committed
/// transition/handover into a 5xx.
///
/// These tests drive the UPSTREAM compose path (Delivery=true) with the delivery + OTP
/// NSwag clients and the push composer swapped for in-process fakes (same harness family
/// as <c>JeeberEarningsOnCompleteTests</c>), so no live Go/Elixir upstream is needed.
/// </summary>
public class StatusChangePushTests
{
    private const string RecipientPhone = "+962799123456";
    private const string TenantApplicationId = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46";
    private const string ValidCode = "1234";

    /// <summary>
    /// KEYSTONE (PATCH live path): a flag-ON PATCH /status transition that commits
    /// upstream fans a <see cref="NotificationTrigger.StatusChange"/> push to BOTH the
    /// client and the jeeber (the counterparties). Before the fix this path emitted
    /// nothing.
    /// </summary>
    [Fact]
    public async Task PatchStatus_Transition_On_Live_Emits_StatusChange_Push_To_Counterparty()
    {
        var push = new CapturingPushService();
        var delivery = new ConfigurableDeliveryClient
        {
            TransitionOutcome = to => new DeliveryTransitionUpstream { DeliveryId = "overwritten", Status = to }
        };
        await using var factory = UpstreamFactory(delivery, push);
        var (deliveryId, clientId, jeeberId) = await SeedPickedUpWithJeeberAsync(factory);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var patch = await jeeber.PatchAsJsonAsync(
            $"/deliveries/{deliveryId}/status", new { to = CanonicalDeliveryStatus.InTransit });

        patch.StatusCode.Should().Be(HttpStatusCode.OK, "the canonical transition committed upstream");

        var statusPushes = push.Sent
            .Where(r => r.Trigger == NotificationTrigger.StatusChange
                        && r.Data is { } d && d.TryGetValue("deliveryId", out var id) && id == deliveryId)
            .ToList();

        statusPushes.Should().NotBeEmpty("the committed transition must fan a StatusChange push (the regression)");
        statusPushes.Select(r => r.UserId).Should().Contain(clientId, "the client is a counterparty");
        statusPushes.Select(r => r.UserId).Should().Contain(jeeberId, "the jeeber is a counterparty");
        statusPushes.Should().OnlyContain(r => r.Data!["status"] == CanonicalDeliveryStatus.InTransit,
            "the push carries the fresh upstream target status");
    }

    /// <summary>
    /// KEYSTONE (handover live path): the /otp/verify → Done completion on the flag-ON
    /// upstream path fans the completion <see cref="NotificationTrigger.StatusChange"/>
    /// push to the counterparty. Before the fix the flag-ON compose path returned before
    /// the (in-memory-only) push, so nobody got the "delivery completed" notification.
    /// </summary>
    [Fact]
    public async Task OtpVerify_Completion_On_Live_Emits_Completion_StatusChange_Push()
    {
        var push = new CapturingPushService();
        var delivery = new ConfigurableDeliveryClient
        {
            VerifyOutcome = _ => new DeliveryHandoverVerifyResult
            {
                DeliveryId = "overwritten",
                Verified = true,
                Status = CanonicalDeliveryStatus.Done
            }
        };
        await using var factory = UpstreamFactory(delivery, push);
        var (deliveryId, clientId, jeeberId) = await SeedAtDoorWithJeeberAsync(factory);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var verify = await jeeber.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code = ValidCode });

        verify.StatusCode.Should().Be(HttpStatusCode.OK, "the handover completes on the upstream path");

        var completionPushes = push.Sent
            .Where(r => r.Trigger == NotificationTrigger.StatusChange
                        && r.Data is { } d && d.TryGetValue("deliveryId", out var id) && id == deliveryId)
            .ToList();

        completionPushes.Should().NotBeEmpty("handover completion must fan a StatusChange push");
        completionPushes.Select(r => r.UserId).Should().Contain(new[] { clientId, jeeberId });
        completionPushes.Should().OnlyContain(r => r.Data!["status"] == CanonicalDeliveryStatus.Done,
            "the completion push carries the Done terminal status");
    }

    /// <summary>
    /// BEST-EFFORT GUARD: a push composer that THROWS on every send must NOT turn the
    /// committed transition into a 5xx — the transition already committed upstream and
    /// the 200 is authoritative. The push is fire-and-forget observability, never a gate.
    /// </summary>
    [Fact]
    public async Task PatchStatus_Push_Composer_Throw_Does_Not_Fail_The_Transition()
    {
        var push = new CapturingPushService { ThrowOnSend = true };
        var delivery = new ConfigurableDeliveryClient
        {
            TransitionOutcome = to => new DeliveryTransitionUpstream { DeliveryId = "overwritten", Status = to }
        };
        await using var factory = UpstreamFactory(delivery, push);
        var (deliveryId, _, jeeberId) = await SeedPickedUpWithJeeberAsync(factory);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var patch = await jeeber.PatchAsJsonAsync(
            $"/deliveries/{deliveryId}/status", new { to = CanonicalDeliveryStatus.InTransit });

        patch.StatusCode.Should().Be(HttpStatusCode.OK,
            "a push-composer fault is swallowed best-effort; the committed transition stays a 200");
        push.SendAttempts.Should().BeGreaterThan(0, "the push WAS attempted (and threw), proving the guard caught it");
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private WebApplicationFactory<Program> UpstreamFactory(
        ConfigurableDeliveryClient delivery, CapturingPushService push)
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
                services.RemoveAll<IPushNotificationService>();
                services.AddSingleton<IPushNotificationService>(push);
            });
        });

    private static async Task<(string deliveryId, string clientId, string jeeberId)> SeedPickedUpWithJeeberAsync(
        WebApplicationFactory<Program> factory)
        => await SeedWithJeeberAsync(factory, RequestStatus.PickedUp);

    private static async Task<(string deliveryId, string clientId, string jeeberId)> SeedAtDoorWithJeeberAsync(
        WebApplicationFactory<Program> factory)
        => await SeedWithJeeberAsync(factory, RequestStatus.AtDoor);

    private static async Task<(string deliveryId, string clientId, string jeeberId)> SeedWithJeeberAsync(
        WebApplicationFactory<Program> factory, string status)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"push-client-{Guid.NewGuid()}";
        var jeeberId = $"push-jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the parcel",
            RecipientPhone = RecipientPhone
        }, default);
        (await store.TryAcceptByJeeberAsync(created.Id, jeeberId, int.MaxValue, DateTimeOffset.UtcNow, default))
            .Should().NotBeNull();
        (await store.SetStatusAsync(created.Id, status, default)).Should().BeTrue();
        return (created.Id, clientId, jeeberId);
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    /// <summary>Captures every push handed to the composer; can be told to throw to prove best-effort.</summary>
    private sealed class CapturingPushService : IPushNotificationService
    {
        private readonly List<PushNotificationRequest> _sent = new();
        public IReadOnlyList<PushNotificationRequest> Sent => _sent;
        public int SendAttempts { get; private set; }
        public bool ThrowOnSend { get; init; }

        public Task<PushDeliveryResult> SendAsync(PushNotificationRequest request, CancellationToken ct)
        {
            SendAttempts++;
            if (ThrowOnSend)
            {
                throw new InvalidOperationException("simulated push composer failure");
            }

            _sent.Add(request);
            return Task.FromResult(new PushDeliveryResult(
                request.UserId, request.Trigger, PushDeliveryOutcome.Delivered, 1));
        }
    }

    /// <summary>Delivery-service double: the transition + verify hops return configurable results; all else is loud.</summary>
    private sealed class ConfigurableDeliveryClient : IDeliveryServiceClient
    {
        public Func<string, DeliveryTransitionUpstream> TransitionOutcome { get; init; }
            = to => throw new DeliveryTransitionException((int)HttpStatusCode.UnprocessableEntity, "transition_not_allowed", null, to, null);

        public Func<bool, DeliveryHandoverVerifyResult> VerifyOutcome { get; init; }
            = _ => throw new DeliveryHandoverException((int)HttpStatusCode.Conflict, "not_at_door");

        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(
            string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct)
        {
            var r = TransitionOutcome(to);
            return Task.FromResult(new DeliveryTransitionUpstream { DeliveryId = deliveryId, Status = r.Status });
        }

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
