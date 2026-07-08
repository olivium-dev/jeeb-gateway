using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S09 keystone regression guard (T-BE-019 / JEB-55).
///
/// The recipient phone is plumbed end-to-end through:
///   CreateRequestBody (JSON/form input DTO)
///     → CreateRequestInput
///     → IRequestsStore.CreateAsync
///     → DeliveryRequest.RecipientPhone
///     → GET /deliveries/{id}/otp (at-door handover OTP).
///
/// The downstream half of that chain (CreateRequestInput → store →
/// DeliveryRequest) already existed and is exercised by
/// <see cref="DeliveriesEndpointTests"/>. But that fixture seeds the phone by
/// calling <c>store.CreateAsync(new CreateRequestInput{ RecipientPhone = ... })</c>
/// DIRECTLY — it bypasses the public <c>POST /requests</c> create path, so it
/// could not catch the real S09 bug: <see cref="CreateRequestBody"/> had no
/// <c>RecipientPhone</c> property, so the controller never set
/// <c>CreateRequestInput.RecipientPhone</c>, so EVERY gateway-created delivery
/// had <c>recipient_phone = null</c> and the at-door OTP trigger returned 400
/// <c>recipient-phone-missing</c> for every delivery.
///
/// These tests drive the phone through the PUBLIC HTTP create body so the
/// missing DTO link is covered. They run on the default (flag-off) in-memory
/// handover path; only <see cref="IServiceOTPClient"/> is faked so no real SMS
/// round-trip occurs.
/// </summary>
public class S09RecipientPhonePlumbingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string RecipientPhone = "+9613123456";

    // JEB-1516: the configured Jeeb tenant application GUID the gateway forwards
    // to the shared one-time-password service. In production this is injected via
    // Auth__Otp__ApplicationId; the factory below binds it for the OTP tests.
    private const string TenantApplicationId = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46";

    private readonly WebApplicationFactory<Program> _factory;

    public S09RecipientPhonePlumbingTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Happy path: a recipientPhone supplied on the JSON create body round-trips
    /// onto the read DTO. Before the fix the property did not exist on
    /// <see cref="CreateRequestBody"/>, so the value was silently dropped and the
    /// read-back returned null.
    /// </summary>
    [Fact]
    public async Task Create_With_RecipientPhone_RoundTrips_On_Read()
    {
        var clientId = $"s09-rp-roundtrip-{Guid.NewGuid()}";
        var http = ClientFor(clientId, role: "customer");

        var create = await http.PostAsJsonAsync("/requests", CreatePayload(RecipientPhone));
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await create.Content.ReadFromJsonAsync<CreatedRequestDto>();
        created!.Id.Should().NotBeNullOrWhiteSpace();
        // The create response already surfaces the persisted phone.
        created.RecipientPhone.Should().Be(RecipientPhone);

        // Owner-scoped read-back through the public GET confirms persistence.
        var read = await http.GetAsync($"/requests/{created.Id}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        var readBack = await read.Content.ReadFromJsonAsync<CreatedRequestDto>();
        readBack!.RecipientPhone.Should().Be(RecipientPhone,
            "recipientPhone must thread CreateRequestBody → store → read DTO");
    }

    /// <summary>
    /// Backward-compat: omitting recipientPhone keeps today's behaviour — the
    /// row persists with a null phone (the field is additive and optional).
    /// </summary>
    [Fact]
    public async Task Create_Without_RecipientPhone_Persists_Null()
    {
        var clientId = $"s09-rp-absent-{Guid.NewGuid()}";
        var http = ClientFor(clientId, role: "customer");

        var create = await http.PostAsJsonAsync("/requests", CreatePayload(recipientPhone: null));
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await create.Content.ReadFromJsonAsync<CreatedRequestDto>();
        created!.RecipientPhone.Should().BeNull("omitting the field must remain valid and yield null");
    }

    /// <summary>
    /// Full keystone path: create WITH recipientPhone via the public JSON body,
    /// advance the row to <c>at_door</c>, then trigger the handover OTP. The OTP
    /// endpoint must return 200 Triggered=true and dispatch to the EXACT phone
    /// supplied at creation — proving the value survived the whole chain.
    /// Before the fix this returned 400 recipient-phone-missing.
    /// </summary>
    [Fact]
    public async Task Create_With_RecipientPhone_Then_AtDoor_Otp_Returns200_Triggered()
    {
        var otp = new RecordingOtpClient();
        await using var factory = FactoryWithFakeOtp(otp);

        var clientId = $"s09-rp-otp-{Guid.NewGuid()}";
        var jeeberId = $"s09-rp-jeeber-{Guid.NewGuid()}";

        // 1) Create through the PUBLIC create path carrying the recipient phone.
        var customer = ClientFor(factory, clientId, role: "customer");
        var create = await customer.PostAsJsonAsync("/requests", CreatePayload(RecipientPhone));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<CreatedRequestDto>();
        var deliveryId = created!.Id;

        // 2) Bind a Jeeber and advance to at_door via the store (the gateway has
        //    no public accept→at_door endpoint; the SM lives in delivery-service).
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var accepted = await store.TryAcceptByJeeberAsync(
            deliveryId, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default);
        accepted.Should().NotBeNull("accept must succeed so the row can reach at_door");
        (await store.SetStatusAsync(deliveryId, RequestStatus.AtDoor, default))
            .Should().BeTrue("setup: move row to at_door");

        // 3) Trigger the at-door handover OTP as the bound Jeeber.
        var jeeber = ClientFor(factory, jeeberId, role: "driver");
        var resp = await jeeber.GetAsync($"/deliveries/{deliveryId}/otp");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the OTP trigger must succeed now that recipientPhone is plumbed from the create body");
        var body = await resp.Content.ReadFromJsonAsync<OtpTriggerDto>();
        body!.Triggered.Should().BeTrue();
        body.DeliveryId.Should().Be(deliveryId);

        // The phone dispatched to one-time-password is the one supplied at create.
        otp.SendOtpCalls.Should().ContainSingle();
        otp.SendOtpCalls[0].PhoneNumber.Should().Be(RecipientPhone);
        // JEB-1516: the upstream applicationId MUST be the configured tenant GUID,
        // NOT the non-GUID delivery_handover_{id} label (which made the shared
        // service's new Guid(applicationId) throw 400 → gateway 502). The factory
        // binds Auth:Otp:ApplicationId to TenantApplicationId below.
        otp.SendOtpCalls[0].ApplicationId.Should().Be(TenantApplicationId,
            "the handover OTP must forward the configured tenant GUID so the upstream Guid.Parse succeeds");
        Guid.TryParse(otp.SendOtpCalls[0].ApplicationId, out _).Should().BeTrue(
            "a non-GUID applicationId is exactly the JEB-1516 bug that yielded a 502");
    }

    /// <summary>
    /// JEB-1516 regression guard. The legacy code built the upstream
    /// <c>applicationId</c> as <c>delivery_handover_{deliveryId}</c>. That label
    /// is NEVER a GUID — the <c>delivery_handover_</c> prefix alone guarantees it
    /// fails <c>new Guid(applicationId)</c> regardless of the delivery id's own
    /// format — so the shared one-time-password service threw 400 → the gateway
    /// surfaced 502, breaking S09 H6/A5. This test pins the bug shape (the legacy
    /// label is not a GUID) and asserts the gateway now forwards the configured
    /// tenant GUID instead, which parses cleanly upstream.
    /// </summary>
    [Fact]
    public async Task AtDoor_Otp_Coerces_NonGuid_DeliveryId_To_Configured_Tenant_Guid()
    {
        var otp = new RecordingOtpClient();
        await using var factory = FactoryWithFakeOtp(otp);

        var clientId = $"s09-jeb1516-{Guid.NewGuid()}";
        var jeeberId = $"s09-jeb1516-jeeber-{Guid.NewGuid()}";

        var customer = ClientFor(factory, clientId, role: "customer");
        var create = await customer.PostAsJsonAsync("/requests", CreatePayload(RecipientPhone));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var deliveryId = (await create.Content.ReadFromJsonAsync<CreatedRequestDto>())!.Id;

        // Precondition that pins the bug shape: the LEGACY applicationId label
        // (delivery_handover_{id}) is not a GUID — the prefix alone makes the
        // upstream new Guid(applicationId) throw, whatever the delivery id format.
        var legacyApplicationId = $"delivery_handover_{deliveryId}";
        Guid.TryParse(legacyApplicationId, out _).Should().BeFalse(
            "the legacy delivery_handover_{id} label could never parse as a GUID — the source of the JEB-1516 502");

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        (await store.TryAcceptByJeeberAsync(
            deliveryId, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default))
            .Should().NotBeNull();
        (await store.SetStatusAsync(deliveryId, RequestStatus.AtDoor, default)).Should().BeTrue();

        var jeeber = ClientFor(factory, jeeberId, role: "driver");
        var resp = await jeeber.GetAsync($"/deliveries/{deliveryId}/otp");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        otp.SendOtpCalls.Should().ContainSingle();
        // The applicationId forwarded upstream is a well-formed GUID — never the
        // delivery_handover_{id} label.
        otp.SendOtpCalls[0].ApplicationId.Should().Be(TenantApplicationId);
        otp.SendOtpCalls[0].ApplicationId.Should().NotStartWith("delivery_handover_");
        Guid.TryParse(otp.SendOtpCalls[0].ApplicationId, out _).Should().BeTrue(
            "the coerced applicationId must satisfy the upstream new Guid(applicationId)");
    }

    /// <summary>
    /// Negative path: a delivery created WITHOUT a recipientPhone still rejects
    /// the at-door OTP with 400 recipient-phone-missing — the documented guard
    /// that prevents shipping an OTP to a hardcoded placeholder. No SMS is sent.
    /// </summary>
    [Fact]
    public async Task Create_Without_RecipientPhone_Then_AtDoor_Otp_Returns400_PhoneMissing()
    {
        var otp = new RecordingOtpClient();
        await using var factory = FactoryWithFakeOtp(otp);

        var clientId = $"s09-rp-otp-missing-{Guid.NewGuid()}";
        var jeeberId = $"s09-rp-jeeber-missing-{Guid.NewGuid()}";

        var customer = ClientFor(factory, clientId, role: "customer");
        var create = await customer.PostAsJsonAsync("/requests", CreatePayload(recipientPhone: null));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var deliveryId = (await create.Content.ReadFromJsonAsync<CreatedRequestDto>())!.Id;

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        (await store.TryAcceptByJeeberAsync(
            deliveryId, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default))
            .Should().NotBeNull();
        (await store.SetStatusAsync(deliveryId, RequestStatus.AtDoor, default)).Should().BeTrue();

        var jeeber = ClientFor(factory, jeeberId, role: "driver");
        var resp = await jeeber.GetAsync($"/deliveries/{deliveryId}/otp");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/recipient-phone-missing");
        otp.SendOtpCalls.Should().BeEmpty("no OTP may be dispatched without a recipient phone");
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    /// <summary>
    /// Minimum valid create body (T-backend-007 requires tier + structured
    /// pickup/dropoff). recipientPhone is the field under test.
    /// </summary>
    private static object CreatePayload(string? recipientPhone) => new
    {
        description = "Pick up the parcel",
        tierId = "flash",
        pickupLocation = new { lat = 24.7136, lng = 46.6753 },
        dropoffLocation = new { lat = 24.6309, lng = 46.7194 },
        pickupAddress = "Pickup point",
        dropoffAddress = "Drop-off point",
        recipientPhone
    };

    private HttpClient ClientFor(string userId, string role) => ClientFor(_factory, userId, role);

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    /// <summary>
    /// A factory on the default (flag-off) in-memory handover path with the
    /// NSwag OTP client swapped for an in-process recorder so the OTP trigger
    /// completes without a real SMS round-trip.
    /// </summary>
    private WebApplicationFactory<Program> FactoryWithFakeOtp(RecordingOtpClient otp)
        => _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Delivery", "false");
            // JEB-1516: bind the tenant GUID the gateway forwards as applicationId.
            builder.UseSetting("Auth:Otp:ApplicationId", TenantApplicationId);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(otp);
            });
        });

    private sealed record CreatedRequestDto(
        string Id,
        string ClientId,
        string Status,
        string? RecipientPhone);

    private sealed record OtpTriggerDto(string DeliveryId, bool Triggered, string Message);

    /// <summary>In-process <see cref="IServiceOTPClient"/> that records dispatches.</summary>
    private sealed class RecordingOtpClient : IServiceOTPClient
    {
        public List<(string PhoneNumber, string ApplicationId)> SendOtpCalls { get; } = new();

        public Task SendOTPAsync(SendOTPRequestUserID? body)
            => SendOTPAsync(body, CancellationToken.None);

        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken)
        {
            SendOtpCalls.Add((body?.PhoneNumber ?? "", body?.ApplicationId ?? ""));
            return Task.CompletedTask;
        }

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body)
            => ValidateOTPAsync(body, CancellationToken.None);

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
