using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Gap G4 (run-24 CHECK C) — the in-app delivery handover code, and the CHECK A
/// description read-back. The cycle-4 mobile client reads <c>handoverCode</c> from
/// the OFFER-ACCEPT response (<c>POST /v1/offers/{offerId}/accept</c>) and persists
/// it store-first. These tests pin the contract the client depends on:
///
///  1. the OWNER's accept response carries a 4-digit <c>handoverCode</c>;
///  2. the code is OWNER-SCOPED (a non-owner jeeber never sees it) and it VERIFIES
///     at handover (verify-precedence) even when the upstream one-time-password
///     rejects every code — proving the gateway-minted code is what worked — and it
///     is NEVER written to a log line;
///  3. <c>GET /v1/requests/{id}</c> echoes the request <c>description</c> (CHECK A).
///
/// offer-service / delivery-service / one-time-password are replaced by deterministic
/// fakes; the request row is seeded via the real <see cref="IRequestsStore"/> and the
/// offerId→requestId pairing via the real <see cref="IOfferRequestIndex"/>, exactly as
/// a real submit records them.
/// </summary>
public class HandoverCodeInAppTests
{
    private const double PickupLat = 33.5138;
    private const double PickupLng = 36.2765;
    private const string TenantApplicationId = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46";

    // ----- 1) Owner's accept response carries a 4-digit handoverCode -----------

    [Fact]
    public async Task Accept_OwnerResponse_Carries_FourDigit_HandoverCode()
    {
        var offer = AcceptedOfferFake("offer-hc", "jeeber-win");
        var delivery = new FakeHandoverDeliveryClient();
        using var factory = NewFactory(offer, delivery, new FakeHandoverOtpClient());

        var requestId = await SeedRequestAsync(factory, "client-owner");
        SeedRouting(factory, "offer-hc", requestId, "jeeber-win");

        var resp = await Actor(factory, "client-owner", "customer")
            .PostAsync("/v1/offers/offer-hc/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var code = await ReadPropAsync(resp, "handoverCode");
        code.Should().NotBeNullOrEmpty("the owner must be able to read the handover code in-app");
        Regex.IsMatch(code!, "^[0-9]{4}$").Should()
            .BeTrue($"handoverCode must be a 4-digit code, was '{code}'");
    }

    // ----- 2) Owner-scoped + verifies at handover + never logged ---------------

    [Fact]
    public async Task HandoverCode_IsOwnerScoped_Verifies_AtHandover_AndNeverLogged()
    {
        var offer = AcceptedOfferFake("offer-e2e", "jeeber-win");
        var delivery = new FakeHandoverDeliveryClient();
        // one-time-password REJECTS every code: the only way verify can succeed is via
        // the gateway-minted in-app code (verify-precedence). Proves the code the
        // customer saw is what actually verifies.
        var otp = new FakeHandoverOtpClient { RejectAll = true };
        var logs = new CapturingLoggerProvider();
        using var factory = NewFactory(offer, delivery, otp, logs);

        const string owner = "client-owner";
        const string jeeber = "jeeber-win";
        var requestId = await SeedRequestAsync(factory, owner, recipientPhone: "+962799123456");
        SeedRouting(factory, "offer-e2e", requestId, jeeber);

        // (a) OWNER accepts -> reads the handover code in-app.
        var acceptResp = await Actor(factory, owner, "customer")
            .PostAsync("/v1/offers/offer-e2e/accept", content: null);
        acceptResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var handoverCode = await ReadPropAsync(acceptResp, "handoverCode");
        handoverCode.Should().NotBeNullOrEmpty();

        // Advance the delivery to the at-door handover step.
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        (await store.SetStatusAsync(requestId, RequestStatus.AtDoor, CancellationToken.None))
            .Should().BeTrue();

        // (b) OWNER-SCOPED store-miss fallback: the owner's GET /otp echoes the SAME code.
        var ownerOtp = await Actor(factory, owner, "customer").GetAsync($"/v1/deliveries/{requestId}/otp");
        ownerOtp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadPropAsync(ownerOtp, "code")).Should().Be(handoverCode);

        // (c) A NON-OWNER jeeber triggering the same OTP NEVER receives the code.
        var jeeberOtp = await Actor(factory, jeeber, "driver").GetAsync($"/v1/deliveries/{requestId}/otp");
        jeeberOtp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadPropAsync(jeeberOtp, "code")).Should().BeNull(
            "the handover code must never be exposed to the jeeber / a non-owner");

        // (d) The code VERIFIES at handover via verify-precedence (one-time-password
        //     rejects all, so a success here can ONLY come from the gateway code).
        var verifyResp = await Actor(factory, jeeber, "driver")
            .PostAsJsonAsync($"/v1/deliveries/{requestId}/otp/verify", new { code = handoverCode });
        verifyResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadBoolPropAsync(verifyResp, "verified")).Should().BeTrue();
        delivery.StatusTransitionCalls.Should().Contain(c =>
            c.DeliveryId == requestId && c.Status == RequestStatus.Delivered);

        // (e) A WRONG code still fails (falls through to one-time-password, which rejects).
        var wrongResp = await Actor(factory, jeeber, "driver")
            .PostAsJsonAsync($"/v1/deliveries/{requestId}/otp/verify", new { code = "0000" });
        wrongResp.StatusCode.Should().NotBe(HttpStatusCode.OK);

        // (f) The raw code is NEVER written to any log line (masking). Guard against a
        //     vacuous pass: the verified handover emits a `handover.verified` line, so
        //     the capture is provably non-empty — yet the code appears in NONE of it.
        logs.Records.Should().Contain(r => r.Message.Contains("handover.verified"),
            "sanity: the log capture must actually be recording controller logs");
        logs.Records.Should().NotContain(r => r.Message.Contains(handoverCode!),
            "the handover code must never appear in a log line");
    }

    // ----- 3) GET /v1/requests/{id} echoes the description (run-24 CHECK A) -----

    [Fact]
    public async Task GetRequestById_Echoes_Description()
    {
        var delivery = new FakeHandoverDeliveryClient();
        using var factory = NewFactory(AcceptedOfferFake("unused", null), delivery, new FakeHandoverOtpClient());

        const string owner = "client-desc";
        const string typed = "Two large pizzas, ring the top bell";
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = owner,
            Description = typed,
        }, CancellationToken.None);

        var resp = await Actor(factory, owner, "customer").GetAsync($"/v1/requests/{created.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadPropAsync(resp, "description")).Should().Be(typed,
            "the customer's own by-id read must echo the description they typed");
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static FakeAcceptOfferClient AcceptedOfferFake(string offerId, string? winningJeeberId)
        => new()
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = offerId,
                    JeeberId = winningJeeberId,
                    RejectedOfferIds = Array.Empty<string>(),
                },
            },
        };

    private static WebApplicationFactory<Program> NewFactory(
        IOfferServiceClient offer,
        IDeliveryServiceClient delivery,
        IServiceOTPClient otp,
        CapturingLoggerProvider? logs = null)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("FeatureFlags:UseUpstream:Offer", "true");
                builder.UseSetting("Auth:Otp:ApplicationId", TenantApplicationId);
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton(offer);
                    services.RemoveAll<IDeliveryServiceClient>();
                    services.AddSingleton(delivery);
                    services.RemoveAll<IServiceOTPClient>();
                    services.AddSingleton(otp);
                    if (logs is not null)
                        services.AddLogging(b => b.AddProvider(logs));
                });
            });

    private static async Task<string> SeedRequestAsync(
        WebApplicationFactory<Program> factory, string clientId, string? recipientPhone = null)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the package",
            TierId = "flash",
            PickupLocation = new GeoPoint { Lat = PickupLat, Lng = PickupLng },
            DropoffLocation = new GeoPoint { Lat = PickupLat + 0.01, Lng = PickupLng + 0.01 },
            RecipientPhone = recipientPhone,
        }, CancellationToken.None);
        return created.Id;
    }

    private static void SeedRouting(
        WebApplicationFactory<Program> factory, string offerId, string requestId, string jeeberId)
        => factory.Services.GetRequiredService<IOfferRequestIndex>().Record(offerId, requestId, jeeberId);

    private static HttpClient Actor(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    /// <summary>Reads a top-level string property from a JSON response, or null when absent/null.</summary>
    private static async Task<string?> ReadPropAsync(HttpResponseMessage resp, string prop)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    private static async Task<bool> ReadBoolPropAsync(HttpResponseMessage resp, string prop)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty(prop, out var el)
               && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
               && el.GetBoolean();
    }

    // ---------------------------------------------------------------------
    // fakes
    // ---------------------------------------------------------------------

    private sealed class FakeAcceptOfferClient : IOfferServiceClient
    {
        public required OfferAcceptResult Result { get; init; }

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(Result);

        public Task<OfferAcceptWire> AcceptAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<RequestMirrorResult> MirrorRequestAsync(
            string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId,
            long? feeCents, int? etaMinutes, string? note, int? maxEdits, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// delivery-service fake: records the post-accept row assignment (SyncDeliveryLeg)
    /// and the verified-handover status transition. Everything else throws so an
    /// accidental call is loud.
    /// </summary>
    private sealed class FakeHandoverDeliveryClient : IDeliveryServiceClient
    {
        public ConcurrentQueue<CreateDeliveryRowUpstream> CreateCalls { get; } = new();
        public List<(string DeliveryId, string Status)> StatusTransitionCalls { get; } = new();

        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct)
        {
            CreateCalls.Enqueue(body);
            return Task.FromResult(new DeliveryRowUpstream { Id = body.Id, TenantId = body.TenantId, Status = "Ordered" });
        }

        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct)
        {
            StatusTransitionCalls.Add((deliveryId, status));
            return Task.FromResult(new DeliveryRequestUpstream
            {
                Id = deliveryId,
                ClientId = "client-id",
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        public Task<IReadOnlyList<JeebGateway.Tiers.DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(
            string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(
            string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>
    /// one-time-password fake. SendOTP always "succeeds"; ValidateOTP throws an
    /// NSwag-style 400 for every code when <see cref="RejectAll"/> is set, so a
    /// successful verify can ONLY come from the gateway-minted in-app code.
    /// </summary>
    private sealed class FakeHandoverOtpClient : IServiceOTPClient
    {
        public bool RejectAll { get; init; }

        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => ValidateOTPAsync(body, CancellationToken.None);
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken)
        {
            if (RejectAll)
            {
                throw new ApiException(
                    message: "Invalid OTP",
                    statusCode: 400,
                    response: "{\"error\":\"invalid_otp\"}",
                    headers: new Dictionary<string, IEnumerable<string>>(),
                    innerException: null);
            }
            return Task.CompletedTask;
        }

        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Records { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _parent;
            public CapturingLogger(CapturingLoggerProvider parent) => _parent = parent;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_parent.Records)
                {
                    _parent.Records.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
