using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Requests.OtpHandover;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S09 A7 / E9 / BR-OTP-6 (JEB-55) — idempotent re-verify after success.
///
/// Scenario contract (scenario-S09 row A7, N11 prose, CP-H): a DUPLICATE
/// <c>POST /deliveries/{id}/otp/verify</c> after the delivery has already been
/// driven to <c>Done</c> must SHORT-CIRCUIT on already-`done` and return the
/// REPLAYED <c>200 { verified:true, status:"Done" }</c> — with NO second
/// settlement, NO OTP re-validation, and NO state-machine re-transition.
///
/// Root cause this guards: delivery-service's handover FSM enforces the at_door
/// gate (status != AtDoor → 409 not_at_door) BEFORE the success/runDone path, so
/// a second verify on an already-`Done` delivery is collapsed into the SAME
/// <c>409 { reason:"not_at_door" }</c> as a genuinely never-at-door delivery. The
/// gateway cannot disambiguate from the 409 alone, so before the fix it mapped the
/// A7 replay to a client <c>409</c> (the single remaining S09 red). The gateway now
/// does ONE canonical state read on a 409 and, when the delivery is terminal
/// <c>Done</c>, returns the idempotent 200 replay.
///
/// These tests run on the UPSTREAM compose path
/// (<c>FeatureFlags:UseUpstream:Delivery=true</c>) with the delivery + OTP NSwag
/// clients swapped for in-process fakes, so <c>VerifyOtpViaDeliveryServiceAsync</c>
/// and <c>MapHandoverException</c> are exercised without a live upstream.
/// </summary>
public class S09HandoverIdempotentReverifyTests
{
    private const string RecipientPhone = "+962799123456";
    private const string TenantApplicationId = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46";
    private const string ValidCode = "1234";

    /// <summary>
    /// A7 (positive): a second verify on an ALREADY-`Done` delivery returns the
    /// idempotent 200 replay <c>{ verified:true, status:"Done" }</c>, NOT a 409.
    /// The replay path must NOT re-validate the OTP and must NOT re-run the SM
    /// transition (BR-OTP-6: exactly-once settlement).
    /// </summary>
    [Fact]
    public async Task Second_Verify_After_Done_Returns_Idempotent_200_Replay_Not_409()
    {
        var delivery = new ConfigurableDeliveryClient
        {
            // Mirrors delivery-service: a verify on a delivery that is no longer
            // AtDoor (because it is already Done) is a 409 not_at_door.
            VerifyOutcome = _ => throw new DeliveryHandoverException(
                (int)HttpStatusCode.Conflict, "not_at_door"),
            // The canonical state read proves the delivery is terminal Done.
            CanonicalStatus = CanonicalDeliveryStatus.Done
        };
        var otp = new RecordingOtpClient();
        await using var factory = UpstreamFactory(delivery, otp);

        var (deliveryId, jeeberId) = await SeedAtDoorAsync(factory);
        var jeeber = ClientFor(factory, jeeberId, role: "driver");

        var resp = await VerifyOtp(jeeber, deliveryId, ValidCode);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "a duplicate verify after Done must replay the prior terminal success (A7), not 409");
        var body = await resp.Content.ReadFromJsonAsync<OtpHandoverVerificationResponse>();
        body!.Verified.Should().BeTrue();
        body.Status.Should().Be(CanonicalDeliveryStatus.Done,
            "the replay echoes the terminal Done state");
        body.DeliveryId.Should().Be(deliveryId);

        // BR-OTP-6 / exactly-once: the replay reads the canonical state but must NOT
        // re-validate the OTP code against one-time-password (OTP-used-once law) and
        // must NOT re-run the AtDoor→Done verify hop (no second settlement).
        delivery.VerifyCalls.Should().Be(1, "the verify hop is attempted exactly once");
        delivery.CanonicalReads.Should().Be(1, "exactly one canonical state read disambiguates the 409");
    }

    /// <summary>
    /// A7 (negative): a 409 from a delivery that is NOT terminal (genuine
    /// not_at_door — e.g. the courier never arrived) must still surface as the
    /// existing client <c>409 not-at-door</c>. The idempotent replay must not
    /// swallow a real not-at-door conflict.
    /// </summary>
    [Fact]
    public async Task Verify_NotAtDoor_When_Delivery_Not_Done_Still_Returns_409()
    {
        var delivery = new ConfigurableDeliveryClient
        {
            VerifyOutcome = _ => throw new DeliveryHandoverException(
                (int)HttpStatusCode.Conflict, "not_at_door"),
            // The courier is still in transit — NOT terminal Done.
            CanonicalStatus = CanonicalDeliveryStatus.InTransit
        };
        var otp = new RecordingOtpClient();
        await using var factory = UpstreamFactory(delivery, otp);

        var (deliveryId, jeeberId) = await SeedAtDoorAsync(factory);
        var jeeber = ClientFor(factory, jeeberId, role: "driver");

        var resp = await VerifyOtp(jeeber, deliveryId, ValidCode);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a genuine not_at_door 409 on a non-Done delivery must NOT be replayed as 200");
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/not-at-door");
        delivery.CanonicalReads.Should().Be(1, "the gateway probes the state once before mapping the 409");
    }

    /// <summary>
    /// Guard the first-verify happy path is untouched by the replay change: a
    /// genuine success still returns 200 with status Done and never triggers the
    /// canonical-read probe (the probe is 409-only).
    /// </summary>
    [Fact]
    public async Task First_Successful_Verify_Returns_200_Without_Replay_Probe()
    {
        var delivery = new ConfigurableDeliveryClient
        {
            VerifyOutcome = success => new DeliveryHandoverVerifyResult
            {
                DeliveryId = "ignored-overwritten-below",
                Verified = true,
                Status = CanonicalDeliveryStatus.Done
            }
        };
        var otp = new RecordingOtpClient();
        await using var factory = UpstreamFactory(delivery, otp);

        var (deliveryId, jeeberId) = await SeedAtDoorAsync(factory);
        var jeeber = ClientFor(factory, jeeberId, role: "driver");

        var resp = await VerifyOtp(jeeber, deliveryId, ValidCode);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OtpHandoverVerificationResponse>();
        body!.Verified.Should().BeTrue();
        body.Status.Should().Be(CanonicalDeliveryStatus.Done);
        delivery.CanonicalReads.Should().Be(0,
            "a successful first verify never reaches the 409 replay probe");
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private WebApplicationFactory<Program> UpstreamFactory(
        ConfigurableDeliveryClient delivery, RecordingOtpClient otp)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Drive the upstream compose path (VerifyOtpViaDeliveryServiceAsync).
            builder.UseSetting("FeatureFlags:UseUpstream:Delivery", "true");
            builder.UseSetting("Auth:Otp:ApplicationId", TenantApplicationId);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(delivery);
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(otp);
            });
        });

    /// <summary>
    /// Seeds an in-memory delivery row carrying a recipient phone, bound to a
    /// jeeber and landed on AtDoor — the precondition the verify controller reads
    /// from <c>IRequestsStore</c> before composing the upstream verify hop.
    /// </summary>
    private static async Task<(string deliveryId, string jeeberId)> SeedAtDoorAsync(
        WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"s09-a7-client-{Guid.NewGuid()}";
        var jeeberId = $"s09-a7-jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the parcel",
            RecipientPhone = RecipientPhone
        }, default);

        var accepted = await store.TryAcceptByJeeberAsync(
            created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default);
        accepted.Should().NotBeNull();
        (await store.SetStatusAsync(created.Id, RequestStatus.AtDoor, default))
            .Should().BeTrue("setup: move row to at_door");

        return (created.Id, jeeberId);
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    private static Task<HttpResponseMessage> VerifyOtp(HttpClient http, string deliveryId, string code)
        => http.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code });

    /// <summary>
    /// Configurable <see cref="IDeliveryServiceClient"/> double for the handover
    /// verify path. Only the verify hop + the canonical state read carry behaviour;
    /// every other method is loud (NotSupportedException) so an accidental call
    /// surfaces rather than silently passing.
    /// </summary>
    private sealed class ConfigurableDeliveryClient : IDeliveryServiceClient
    {
        // S03: jeeber available-requests feed is not exercised by these handover tests.
        public Task<JeeberAvailableRequestsResult> GetAvailableRequestsAsync(string jeeberId, CancellationToken ct)
            => Task.FromResult(new JeeberAvailableRequestsResult());

        /// <summary>Verify-hop outcome: return a result (200) or throw a <see cref="DeliveryHandoverException"/>.</summary>
        public Func<bool, DeliveryHandoverVerifyResult> VerifyOutcome { get; init; }
            = _ => throw new DeliveryHandoverException((int)HttpStatusCode.Conflict, "not_at_door");

        /// <summary>Canonical SM status returned by <see cref="GetCanonicalDeliveryAsync"/>; null ⇒ 404 (null read).</summary>
        public string? CanonicalStatus { get; init; }

        public int VerifyCalls { get; private set; }
        public int CanonicalReads { get; private set; }

        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(
            string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct)
        {
            VerifyCalls++;
            var result = VerifyOutcome(success);
            // Echo the actual delivery id so the success response binds correctly.
            return Task.FromResult(new DeliveryHandoverVerifyResult
            {
                DeliveryId = deliveryId,
                Verified = result.Verified,
                Status = result.Status
            });
        }

        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct)
        {
            CanonicalReads++;
            if (CanonicalStatus is null)
            {
                return Task.FromResult<DeliveryReadUpstream?>(null);
            }
            return Task.FromResult<DeliveryReadUpstream?>(new DeliveryReadUpstream
            {
                DeliveryId = deliveryId,
                Status = CanonicalStatus,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        // ---- loud no-ops -----------------------------------------------------
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>In-process <see cref="IServiceOTPClient"/> that records dispatches and validates as success.</summary>
    private sealed class RecordingOtpClient : IServiceOTPClient
    {
        public List<(string PhoneNumber, string ApplicationId)> ValidateOtpCalls { get; } = new();

        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => ValidateOTPAsync(body, CancellationToken.None);

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken)
        {
            ValidateOtpCalls.Add((body?.PhoneNumber ?? "", body?.ApplicationId ?? ""));
            return Task.CompletedTask; // 2xx ⇒ success=true in the controller
        }

        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
