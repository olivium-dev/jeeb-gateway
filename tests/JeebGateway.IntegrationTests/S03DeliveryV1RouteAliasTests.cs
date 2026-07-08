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
/// Sprint-003 Gate-C FIX 2 — additive <c>/v1</c> route aliases for the three delivery
/// action endpoints (PATCH status, GET otp, POST otp/verify).
///
/// The frozen contract §5.4 and the mobile app call the <c>/v1</c>-prefixed forms, but
/// only <c>GET /v1/deliveries/{id}</c> had the alias; the action endpoints were
/// relative-only, so their <c>/v1</c> forms 404'd — a real device following the contract
/// would 404 on the deliver step. Each fix adds a second, byte-compatible route template
/// (exactly like the GAP-A offer-create alias d388672). These tests assert ROUTE PARITY:
/// the <c>/v1</c>-prefixed form and the relative form resolve to the same action and
/// return the identical outcome — a missing alias would make ONLY the <c>/v1</c> form
/// 404 while the relative form succeeds.
///
/// Harness mirrors <see cref="S09HandoverIdempotentReverifyTests"/>.
/// </summary>
public class S03DeliveryV1RouteAliasTests
{
    private const string RecipientPhone = "+9613123456";
    private const string TenantApplicationId = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46";
    private const string ValidCode = "1234";

    /// <summary>
    /// The <c>/v1</c>-prefixed OTP-verify form resolves to the same action and returns the
    /// same success as the relative form (route parity).
    /// </summary>
    [Fact]
    public async Task V1_OtpVerify_Alias_Resolves_Like_Relative_Form()
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

        var (relId, relJeeber) = await SeedAtDoorAsync(factory);
        var (v1Id, v1Jeeber) = await SeedAtDoorAsync(factory);

        var relResp = await ClientFor(factory, relJeeber, "driver")
            .PostAsJsonAsync($"/deliveries/{relId}/otp/verify", new { code = ValidCode });
        var v1Resp = await ClientFor(factory, v1Jeeber, "driver")
            .PostAsJsonAsync($"/v1/deliveries/{v1Id}/otp/verify", new { code = ValidCode });

        v1Resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the /v1-prefixed otp/verify form must resolve (200), not 404 on the route");
        v1Resp.StatusCode.Should().Be(relResp.StatusCode, "route parity: /v1 form == relative form");
    }

    /// <summary>
    /// The <c>/v1</c>-prefixed OTP-trigger (GET otp) form resolves like the relative form.
    /// Run on the in-memory (flag-off) trigger path so a seeded AtDoor row + the recording
    /// OTP client yields a deterministic 200 on both forms.
    /// </summary>
    [Fact]
    public async Task V1_OtpTrigger_Alias_Resolves_Like_Relative_Form()
    {
        var delivery = new ConfigurableDeliveryClient();   // loud — flag-off path never calls it
        var otp = new RecordingOtpClient();
        await using var factory = InMemoryFactory(delivery, otp);

        var (relId, relJeeber) = await SeedAtDoorAsync(factory);
        var (v1Id, v1Jeeber) = await SeedAtDoorAsync(factory);

        var relResp = await ClientFor(factory, relJeeber, "driver").GetAsync($"/deliveries/{relId}/otp");
        var v1Resp = await ClientFor(factory, v1Jeeber, "driver").GetAsync($"/v1/deliveries/{v1Id}/otp");

        relResp.StatusCode.Should().Be(HttpStatusCode.OK, "precondition: the relative trigger form succeeds in-memory");
        v1Resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the /v1-prefixed otp trigger form must resolve (200), not 404 on the route");
    }

    /// <summary>
    /// The <c>/v1</c>-prefixed PATCH-status form resolves like the relative form. An empty
    /// body is rejected with 400 by the action BEFORE it touches the delivery client — so a
    /// 400 (not 404) from BOTH forms proves the route resolves.
    /// </summary>
    [Fact]
    public async Task V1_PatchStatus_Alias_Resolves_Like_Relative_Form()
    {
        var delivery = new ConfigurableDeliveryClient();   // loud — not reached on the 400 branch
        var otp = new RecordingOtpClient();
        await using var factory = UpstreamFactory(delivery, otp);

        var (deliveryId, jeeberId) = await SeedAtDoorAsync(factory);
        var jeeber = ClientFor(factory, jeeberId, role: "driver");

        var relResp = await jeeber.PatchAsJsonAsync($"/deliveries/{deliveryId}/status", new { });
        var v1Resp = await jeeber.PatchAsJsonAsync($"/v1/deliveries/{deliveryId}/status", new { });

        relResp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "precondition: empty body is a 400 on the relative form");
        v1Resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the /v1-prefixed status form must resolve to the action (400 on empty body), not 404 on the route");
    }

    // ----------------------------------------------------------------------
    // Helpers (mirror S09HandoverIdempotentReverifyTests)
    // ----------------------------------------------------------------------

    private WebApplicationFactory<Program> UpstreamFactory(
        ConfigurableDeliveryClient delivery, RecordingOtpClient otp)
        => BuildFactory(delivery, otp, upstreamDelivery: true);

    private WebApplicationFactory<Program> InMemoryFactory(
        ConfigurableDeliveryClient delivery, RecordingOtpClient otp)
        => BuildFactory(delivery, otp, upstreamDelivery: false);

    private WebApplicationFactory<Program> BuildFactory(
        ConfigurableDeliveryClient delivery, RecordingOtpClient otp, bool upstreamDelivery)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Delivery", upstreamDelivery ? "true" : "false");
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
        var clientId = $"s03-alias-client-{Guid.NewGuid()}";
        var jeeberId = $"s03-alias-jeeber-{Guid.NewGuid()}";

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

    private sealed class ConfigurableDeliveryClient : IDeliveryServiceClient
    {
        public Func<bool, DeliveryHandoverVerifyResult> VerifyOutcome { get; init; }
            = _ => throw new DeliveryHandoverException((int)HttpStatusCode.Conflict, "not_at_door");

        public string? CanonicalStatus { get; init; }

        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(
            string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct)
        {
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
