using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// D15 (Phase V run 3) — the at-door handover code must NOT be satisfiable by the
/// shared login one-time-password service, whose mock mode accepts a fixed 1234.
/// </summary>
public class D15DoorOtpSharedVerifierBypassTests
{
    private const double PickupLat = 33.5138;
    private const double PickupLng = 36.2765;
    private const string TenantApplicationId = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46";

    // The literal the login-OTP mock (Mock__IsMock=true, Mock__FixedCode) mints for
    // EVERY phone. It is the defect, never a valid door code.
    private const string LoginMockFixedCode = "1234";

    private const string Owner  = "client-owner-d15";
    // c2-1: the accept guard resolves the winner to a wallet holder, so this id is a GUID.
    private const string Jeeber = "5e1a9c74-2b38-4d06-8f95-c3a7e04b1d62";

    // ---- flag-OFF leg (gateway owns the attempt counter) ---------------------

    [Fact]
    public async Task FlagOff_LoginMockFixedCode_IsRejected_WhileRealCodeStillCompletes()
    {
        var otp      = new LoginMockOtpClient();
        var delivery = new RecordingDeliveryClient();
        using var factory = NewFactory(otp, delivery, deliveryUpstream: false);

        var (requestId, realCode) = await SeedAtDoorDeliveryAsync(factory);

        // 1) THE DEFECT: 1234 is the login mock's universal code. The door must
        //    reject it and BURN an attempt, exactly like any other wrong code.
        var bypass = await VerifyAsync(factory, requestId, LoginMockFixedCode);
        bypass.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "1234 is the shared login-OTP mock code; it must never open a door");
        (await BodyAsync(bypass)).Should().Contain("2 attempt(s) remaining");

        // 2) DISCRIMINATING CONTROL A: another wrong code decrements again, so the
        //    endpoint is really evaluating digits and counting, not blanket-denying.
        var wrong = await VerifyAsync(factory, requestId, "0000");
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await BodyAsync(wrong)).Should().Contain("1 attempt(s) remaining",
            "the attempt counter must move — a fixed 401 for everything is not a fix");

        // 3) DISCRIMINATING CONTROL B: the REAL per-delivery code still completes the
        //    handover in this same run, so the rejection above is selective.
        var good = await VerifyAsync(factory, requestId, realCode);
        good.StatusCode.Should().Be(HttpStatusCode.OK,
            "the code the customer actually sees must still complete the delivery");
        (await ReadBoolAsync(good, "verified")).Should().BeTrue();
        delivery.CanonicalTransitionCalls.Should().Contain(c =>
            c.DeliveryId == requestId && c.To == CanonicalDeliveryStatus.Done);

        // 4) The door path must not consult the shared login verifier AT ALL —
        //    separation, not merely a different answer from it.
        otp.ValidateCalls.Should().BeEmpty(
            "the door OTP must not share a verifier with the login OTP");
    }

    // ---- flag-ON leg (delivery-service owns the attempt counter) -------------
    // This is the LIVE shape: run 3's 401 carried `attemptsRemaining`, which only
    // the upstream leg emits.

    [Fact]
    public async Task FlagOn_LoginMockFixedCode_IsRejected_WhileRealCodeStillCompletes()
    {
        var otp      = new LoginMockOtpClient();
        var delivery = new RecordingDeliveryClient { HandoverEnabled = true };
        using var factory = NewFactory(otp, delivery, deliveryUpstream: true);

        var (requestId, realCode) = await SeedAtDoorDeliveryAsync(factory);

        // 1) THE DEFECT: 1234 must reach delivery-service as success=false.
        var bypass = await VerifyAsync(factory, requestId, LoginMockFixedCode);
        bypass.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "1234 is the shared login-OTP mock code; it must never open a door");
        delivery.HandoverVerifyCalls.Should().ContainSingle();
        delivery.HandoverVerifyCalls[0].Success.Should().BeFalse();
        (await BodyAsync(bypass)).Should().Contain("2 attempt(s) remaining");

        // 2) DISCRIMINATING CONTROL A: the durable counter still moves on a wrong code.
        var wrong = await VerifyAsync(factory, requestId, "0000");
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await BodyAsync(wrong)).Should().Contain("1 attempt(s) remaining");

        // 3) DISCRIMINATING CONTROL B: the REAL code still completes, same run.
        var good = await VerifyAsync(factory, requestId, realCode);
        good.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadBoolAsync(good, "verified")).Should().BeTrue();
        delivery.HandoverVerifyCalls.Should().HaveCount(3);
        delivery.HandoverVerifyCalls[2].Success.Should().BeTrue();

        // 4) Separation: the shared login verifier was never called.
        otp.ValidateCalls.Should().BeEmpty(
            "the door OTP must not share a verifier with the login OTP");
    }

    // ---- the mock is genuinely "on" in these tests ---------------------------

    [Fact]
    public async Task LoginOtpMock_IsStillOn_ForLogin_SoTheDoorTestsPinBypassFreedomUnderMockMode()
    {
        var otp = new LoginMockOtpClient();

        // Sanity on the double itself: it behaves like OTPMockService with
        // FixedCode=1234 — 1234 validates for ANY phone, anything else throws.
        await otp.ValidateOTPAsync(
            new ValidateOTPRequestModel
            {
                PhoneNumber   = "+9613123456",
                Otp           = LoginMockFixedCode,
                ApplicationId = TenantApplicationId
            },
            CancellationToken.None);

        var wrong = async () => await otp.ValidateOTPAsync(
            new ValidateOTPRequestModel
            {
                PhoneNumber   = "+9613123456",
                Otp           = "0000",
                ApplicationId = TenantApplicationId
            },
            CancellationToken.None);

        await wrong.Should().ThrowAsync<ApiException>(
            "the double must be able to answer differently, or the door tests are vacuous");
        otp.ValidateCalls.Should().HaveCount(2);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    /// <summary>Seeds an accepted delivery parked at AtDoor and returns its REAL
    /// gateway-minted handover code (never the login mock's fixed code).</summary>
    private static async Task<(string RequestId, string RealCode)> SeedAtDoorDeliveryAsync(
        WebApplicationFactory<Program> factory)
    {
        // The code is cryptographically random, so on the ~1-in-10,000 draw where it
        // equals 1234 the case cannot discriminate — reseed instead of asserting.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var offerId = $"offer-d15-{Guid.NewGuid():N}";
            var requestId = await SeedRequestAsync(factory, Owner, "+9613123456");
            factory.Services.GetRequiredService<IOfferRequestIndex>()
                .Record(offerId, requestId, Jeeber);
            SetAcceptedOffer(factory, offerId);

            var acceptResp = await Actor(factory, Owner, "customer")
                .PostAsync($"/v1/offers/{offerId}/accept", content: null);
            acceptResp.StatusCode.Should().Be(HttpStatusCode.OK);

            var code = await ReadStringAsync(acceptResp, "handoverCode");
            code.Should().NotBeNullOrEmpty();
            if (code == LoginMockFixedCode)
            {
                continue;
            }

            var store = factory.Services.GetRequiredService<IRequestsStore>();
            (await store.SetStatusAsync(requestId, RequestStatus.AtDoor, CancellationToken.None))
                .Should().BeTrue();
            return (requestId, code!);
        }

        throw new InvalidOperationException("Could not mint a handover code distinct from 1234.");
    }

    private static Task<HttpResponseMessage> VerifyAsync(
        WebApplicationFactory<Program> factory, string requestId, string code)
        => Actor(factory, Jeeber, "driver")
            .PostAsJsonAsync($"/v1/deliveries/{requestId}/otp/verify", new { code });

    private static WebApplicationFactory<Program> NewFactory(
        IServiceOTPClient otp, IDeliveryServiceClient delivery, bool deliveryUpstream)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("FeatureFlags:UseUpstream:Offer", "true");
                builder.UseSetting("FeatureFlags:UseUpstream:Delivery", deliveryUpstream ? "true" : "false");
                builder.UseSetting("Auth:Otp:ApplicationId", TenantApplicationId);
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton<IOfferServiceClient>(AcceptingOfferClient.Instance);
                    services.RemoveAll<IDeliveryServiceClient>();
                    services.AddSingleton(delivery);
                    services.RemoveAll<IServiceOTPClient>();
                    services.AddSingleton(otp);
                });
            });

    private static void SetAcceptedOffer(WebApplicationFactory<Program> factory, string offerId)
        => ((AcceptingOfferClient)factory.Services.GetRequiredService<IOfferServiceClient>())
            .NextOfferId = offerId;

    private static async Task<string> SeedRequestAsync(
        WebApplicationFactory<Program> factory, string clientId, string recipientPhone)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId        = clientId,
            Description     = "Pick up the package",
            TierId          = "flash",
            PickupLocation  = new GeoPoint { Lat = PickupLat, Lng = PickupLng },
            DropoffLocation = new GeoPoint { Lat = PickupLat + 0.01, Lng = PickupLng + 0.01 },
            RecipientPhone  = recipientPhone,
        }, CancellationToken.None);
        return created.Id;
    }

    private static HttpClient Actor(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    private static Task<string> BodyAsync(HttpResponseMessage resp)
        => resp.Content.ReadAsStringAsync();

    private static async Task<string?> ReadStringAsync(HttpResponseMessage resp, string prop)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    private static async Task<bool> ReadBoolAsync(HttpResponseMessage resp, string prop)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty(prop, out var el)
               && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
               && el.GetBoolean();
    }

    // ---------------------------------------------------------------------
    // fakes
    // ---------------------------------------------------------------------

    /// <summary>Stand-in for the shared one-time-password service running in MOCK mode
    /// (Mock__IsMock=true, FixedCode=1234): 1234 validates for every phone.</summary>
    private sealed class LoginMockOtpClient : IServiceOTPClient
    {
        public List<string> ValidateCalls { get; } = new();
        public List<string> SendCalls { get; } = new();

        public Task SendOTPAsync(SendOTPRequestUserID? body)
            => SendOTPAsync(body, CancellationToken.None);

        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken)
        {
            SendCalls.Add(body?.PhoneNumber ?? string.Empty);
            return Task.CompletedTask;
        }

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body)
            => ValidateOTPAsync(body, CancellationToken.None);

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken)
        {
            ValidateCalls.Add(body?.Otp ?? string.Empty);
            if (body?.Otp == LoginMockFixedCode)
            {
                return Task.CompletedTask;
            }

            throw new ApiException("Invalid mock OTP", 400, "{}", null!, null);
        }

        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Offer-service double whose accept always wins for the offer id most
    /// recently seeded, so the gateway mints the in-app handover code.</summary>
    private sealed class AcceptingOfferClient : IOfferServiceClient
    {
        public static readonly AcceptingOfferClient Instance = new();

        public string NextOfferId { get; set; } = string.Empty;

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId  = offerId,
                    JeeberId         = Jeeber,
                    RejectedOfferIds = Array.Empty<string>(),
                },
            });

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

    /// <summary>delivery-service double. When <c>HandoverEnabled</c> it emulates the
    /// durable attempt counter the flag-ON leg delegates to.</summary>
    private sealed class RecordingDeliveryClient : IDeliveryServiceClient
    {
        private int _attempts;

        public bool HandoverEnabled { get; init; }
        public List<(string DeliveryId, string To)> CanonicalTransitionCalls { get; } = new();
        public List<(string DeliveryId, bool Success)> HandoverVerifyCalls { get; } = new();
        public ConcurrentQueue<CreateDeliveryRowUpstream> CreateCalls { get; } = new();

        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(
            string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct)
        {
            HandoverVerifyCalls.Add((deliveryId, success));
            if (!HandoverEnabled)
            {
                throw new NotSupportedException();
            }

            if (success)
            {
                return Task.FromResult(new DeliveryHandoverVerifyResult
                {
                    DeliveryId = deliveryId,
                    Verified   = true,
                    Status     = "Done",
                });
            }

            _attempts++;
            throw new DeliveryHandoverException(401, "invalid_code", attemptsRemaining: 3 - _attempts);
        }

        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct)
        {
            CreateCalls.Enqueue(body);
            return Task.FromResult(new DeliveryRowUpstream { Id = body.Id, TenantId = body.TenantId, Status = "Ordered" });
        }

        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(
            string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct)
        {
            CanonicalTransitionCalls.Add((deliveryId, to));
            return Task.FromResult(new DeliveryTransitionUpstream
            {
                DeliveryId     = deliveryId,
                Status         = to,
                TransitionId   = Guid.NewGuid().ToString(),
                TransitionedAt = DateTimeOffset.UtcNow
            });
        }

        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct)
            => Task.FromResult<DeliveryReadUpstream?>(null);

        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(
            string deliveryId, string? codeHash, CancellationToken ct)
            => Task.FromResult(new DeliveryHandoverIssueResult { DeliveryId = deliveryId, Issued = true });

        public Task<IReadOnlyList<AvailableProviderUpstream>> ListAvailableProvidersAsync(
            double? lat, double? lng, double? radiusKm,
            IReadOnlyCollection<string>? roles, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AvailableProviderUpstream>>(Array.Empty<AvailableProviderUpstream>());

        public Task<IReadOnlyList<JeeberAvailabilityUpstream>> ListKnownProvidersAsync(
            DateTimeOffset since, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JeeberAvailabilityUpstream>>(Array.Empty<JeeberAvailabilityUpstream>());

        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<JeebGateway.Tiers.DeliveryTierDto>> ListTiersAsync(CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(
            string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(
            JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
