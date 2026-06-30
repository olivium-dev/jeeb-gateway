using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Requests.OtpHandover;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Sprint-003 Gate-C FIX 1 — terminal <c>delivered</c> projection on the FLAG-ON
/// (upstream) OTP-verify success path.
///
/// The canonical AtDoor→Done transition + settlement commit in delivery-service; the
/// gateway must then mirror the terminal flip onto its local request read-model so the
/// client-facing <c>GET /v1/requests/{id}</c> reads <c>delivered</c> (not the last
/// PATCH-status value, e.g. AtDoor). Before the fix the flag-OFF path projected (it
/// always ran <c>_store.SetStatusAsync(.., Delivered)</c>) but the flag-ON success path
/// returned <c>Done</c>/verified and OMITTED that projection — and production runs
/// flag-on. <c>deliveryId == requestId</c>, so the projection lands on the right request
/// row. It is degrade-don't-fail: a committed, verified handover is never turned into a
/// 5xx by the best-effort local write.
///
/// Harness mirrors <see cref="S09HandoverIdempotentReverifyTests"/>: the upstream compose
/// path (<c>FeatureFlags:UseUpstream:Delivery=true</c>) with the delivery + OTP NSwag
/// clients swapped for in-process fakes.
/// </summary>
public class S03FlagOnTerminalProjectionTests
{
    private const string RecipientPhone = "+962799123456";
    private const string TenantApplicationId = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46";
    private const string ValidCode = "1234";

    /// <summary>
    /// Happy path: a successful flag-on verify (delivery-service returns Done) projects
    /// the terminal <c>delivered</c> state onto the gateway request read-model.
    /// </summary>
    [Fact]
    public async Task FlagOn_Verify_Success_Projects_Delivered_To_Request_ReadModel()
    {
        var delivery = new ConfigurableDeliveryClient
        {
            VerifyOutcome = _ => new DeliveryHandoverVerifyResult
            {
                DeliveryId = "overwritten-by-double",
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

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var row = await store.GetAsync(deliveryId, default);
        row.Should().NotBeNull();
        row!.Status.Should().Be(RequestStatus.Delivered,
            "the flag-on verify success path must project the terminal delivered state onto the request read-model");
    }

    /// <summary>
    /// Negative path: when delivery-service REJECTS the verify (a non-200 from the
    /// handover contract on a non-terminal row), the gateway returns an error and must
    /// NOT project <c>delivered</c> — the row stays <c>AtDoor</c>. Guards that the
    /// projection is gated on the committed-success branch only.
    /// </summary>
    [Fact]
    public async Task FlagOn_Verify_Rejected_Does_Not_Project_Delivered()
    {
        var delivery = new ConfigurableDeliveryClient
        {
            VerifyOutcome = _ => throw new DeliveryHandoverException(
                (int)HttpStatusCode.Conflict, "not_at_door"),
            CanonicalStatus = CanonicalDeliveryStatus.InTransit
        };
        var otp = new RecordingOtpClient();
        await using var factory = UpstreamFactory(delivery, otp);

        var (deliveryId, jeeberId) = await SeedAtDoorAsync(factory);
        var jeeber = ClientFor(factory, jeeberId, role: "driver");

        var resp = await VerifyOtp(jeeber, deliveryId, ValidCode);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a rejected verify must surface the upstream conflict, not a success");

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var row = await store.GetAsync(deliveryId, default);
        row!.Status.Should().Be(RequestStatus.AtDoor,
            "a rejected verify must NOT project delivered — the row stays AtDoor");
    }

    // ----------------------------------------------------------------------
    // Helpers (mirror S09HandoverIdempotentReverifyTests)
    // ----------------------------------------------------------------------

    private WebApplicationFactory<Program> UpstreamFactory(
        ConfigurableDeliveryClient delivery, RecordingOtpClient otp)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
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

    private static async Task<(string deliveryId, string jeeberId)> SeedAtDoorAsync(
        WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"s03-proj-client-{Guid.NewGuid()}";
        var jeeberId = $"s03-proj-jeeber-{Guid.NewGuid()}";

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

    private sealed class ConfigurableDeliveryClient : IDeliveryServiceClient
    {
        public Func<bool, DeliveryHandoverVerifyResult> VerifyOutcome { get; init; }
            = _ => throw new DeliveryHandoverException((int)HttpStatusCode.Conflict, "not_at_door");

        public string? CanonicalStatus { get; init; }

        public int VerifyCalls { get; private set; }
        public int CanonicalReads { get; private set; }

        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(
            string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct)
        {
            VerifyCalls++;
            var result = VerifyOutcome(success);
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
