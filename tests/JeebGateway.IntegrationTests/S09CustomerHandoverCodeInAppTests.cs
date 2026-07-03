using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Requests.OtpHandover;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// OWNER-ESCALATED P0 (Jeeb G4 follow-up) — kill the OTP "half-and-half": the
/// CUSTOMER must be able to read the delivery handover code IN-APP.
///
/// Revised AC5 (owner decision): the raw 4-digit code is returned once, auth-scoped,
/// ONLY to the authenticated client (owner) of the delivery via
/// <see cref="OtpTriggerResponse.Code"/>; it must still never be logged. The gateway
/// mints the code, persists ONLY its SHA-256 hash, STILL dispatches the SMS
/// (belt-and-suspenders), and on verify matches the submitted code against the stored
/// hash FIRST — falling through to the one-time-password path so the SMS-minted code
/// keeps working.
///
/// Coverage (both the legacy in-memory path AND the flag-on delivery-service path):
/// <list type="bullet">
///   <item>(a) the delivery's own client gets a 4-digit <c>code</c> at at_door;</item>
///   <item>(b) a jeeber caller gets NO <c>code</c> key (body byte-unchanged);</item>
///   <item>(c) verify succeeds end-to-end with the gateway-minted code (no
///     one-time-password round-trip);</item>
///   <item>(d) verify still succeeds via the one-time-password path when no hash
///     was minted;</item>
///   <item>(e) a wrong code still 401s and locks after 3 attempts;</item>
///   <item>(f) no captured log line contains the minted code.</item>
/// </list>
///
/// The code is never printed in a test name or assertion message (it is random per
/// run); leak assertions compare on a boolean so a failure never echoes the code.
/// </summary>
public class S09CustomerHandoverCodeInAppTests
{
    private const string RecipientPhone = "+962799123456";
    private const string TenantApplicationId = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46";

    // ---------------------------------------------------------------------------
    // (a) + (b) TRIGGER — customer gets an in-app 4-digit code; jeeber gets none
    // ---------------------------------------------------------------------------

    [Fact] // (a) legacy path
    public async Task Legacy_Customer_Trigger_Returns_FourDigit_Code_At_AtDoor()
    {
        var otp = new RecordingOtpClient();
        await using var factory = LegacyFactory(new HandoverCodeDeliveryClient(), otp);
        var (deliveryId, clientId, _) = await SeedAtDoorAsync(factory);

        var resp = await TriggerOtp(CustomerClient(factory, clientId), deliveryId);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OtpTriggerResponse>();
        body!.Triggered.Should().BeTrue();
        body.Code.Should().NotBeNull().And.MatchRegex("^[0-9]{4}$",
            "the delivery's own client reads the 4-digit handover code in-app (revised AC5)");
        otp.SendCalls.Should().Be(1, "the SMS is STILL dispatched (belt-and-suspenders)");
    }

    [Fact] // (b) legacy path
    public async Task Legacy_Jeeber_Trigger_Omits_Code_Field_Entirely()
    {
        var otp = new RecordingOtpClient();
        await using var factory = LegacyFactory(new HandoverCodeDeliveryClient(), otp);
        var (deliveryId, _, jeeberId) = await SeedAtDoorAsync(factory);

        var resp = await TriggerOtp(JeeberClient(factory, jeeberId), deliveryId);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertNoCodeKeyAsync(resp, "a jeeber caller must get today's body exactly — no code key");
        otp.SendCalls.Should().Be(1);
    }

    [Fact] // (a) flag-on path — also asserts the SHA-256 hash is forwarded as code_hash
    public async Task Upstream_Customer_Trigger_Returns_Code_And_Forwards_CodeHash()
    {
        var delivery = new HandoverCodeDeliveryClient();
        var otp = new RecordingOtpClient();
        await using var factory = UpstreamFactory(delivery, otp);
        var (deliveryId, clientId, _) = await SeedAtDoorAsync(factory);

        var resp = await TriggerOtp(CustomerClient(factory, clientId), deliveryId);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OtpTriggerResponse>();
        body!.Code.Should().NotBeNull().And.MatchRegex("^[0-9]{4}$");
        delivery.IssueCalls.Should().Be(1, "the durable at_door gate is still called first");
        delivery.LastIssueCodeHash.Should().NotBeNullOrEmpty(
            "the minted code's SHA-256 hash is forwarded to delivery-service as code_hash");
        delivery.LastIssueCodeHash.Should().MatchRegex("^[0-9A-F]{64}$", "code_hash is a SHA-256 hex digest");
        otp.SendCalls.Should().Be(1);
    }

    [Fact] // (b) flag-on path — jeeber gets no code AND code_hash stays null
    public async Task Upstream_Jeeber_Trigger_Omits_Code_And_Forwards_Null_CodeHash()
    {
        var delivery = new HandoverCodeDeliveryClient();
        var otp = new RecordingOtpClient();
        await using var factory = UpstreamFactory(delivery, otp);
        var (deliveryId, _, jeeberId) = await SeedAtDoorAsync(factory);

        var resp = await TriggerOtp(JeeberClient(factory, jeeberId), deliveryId);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertNoCodeKeyAsync(resp, "a jeeber caller must get today's body exactly — no code key");
        delivery.LastIssueCodeHash.Should().BeNull("no code is minted for a non-client caller");
    }

    // ---------------------------------------------------------------------------
    // (c) VERIFY succeeds with the gateway-minted in-app code (no OTP round-trip)
    // ---------------------------------------------------------------------------

    [Fact] // (c) flag-on path
    public async Task Upstream_Verify_With_InApp_Code_Succeeds_Without_OtpRoundTrip()
    {
        var delivery = new HandoverCodeDeliveryClient
        {
            VerifyOutcome = _ => new DeliveryHandoverVerifyResult
            {
                DeliveryId = "overwritten",
                Verified = true,
                Status = CanonicalDeliveryStatus.Done
            }
        };
        var otp = new RecordingOtpClient();
        await using var factory = UpstreamFactory(delivery, otp);
        var (deliveryId, clientId, jeeberId) = await SeedAtDoorAsync(factory);

        // Customer reads the code in-app.
        var code = await TriggerAndReadCodeAsync(CustomerClient(factory, clientId), deliveryId);

        // Jeeber verifies with that in-app code.
        var resp = await VerifyOtp(JeeberClient(factory, jeeberId), deliveryId, code);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OtpHandoverVerificationResponse>();
        body!.Verified.Should().BeTrue();
        body.Status.Should().Be(CanonicalDeliveryStatus.Done);
        otp.ValidateCalls.Should().Be(0,
            "a gateway-minted-code match short-circuits the one-time-password validation");
        delivery.VerifyCalls.Should().Be(1);
        delivery.LastVerifySuccess.Should().BeTrue(
            "the gateway forwards success=true to delivery-service after the in-app code matched");
    }

    [Fact] // (c) legacy path
    public async Task Legacy_Verify_With_InApp_Code_Succeeds_And_Transitions_Delivered()
    {
        var delivery = new HandoverCodeDeliveryClient();
        var otp = new RecordingOtpClient();
        await using var factory = LegacyFactory(delivery, otp);
        var (deliveryId, clientId, jeeberId) = await SeedAtDoorAsync(factory);

        var code = await TriggerAndReadCodeAsync(CustomerClient(factory, clientId), deliveryId);

        var resp = await VerifyOtp(JeeberClient(factory, jeeberId), deliveryId, code);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OtpHandoverVerificationResponse>();
        body!.Verified.Should().BeTrue();
        body.Status.Should().Be(RequestStatus.Delivered);
        otp.ValidateCalls.Should().Be(0, "the in-app code match short-circuits one-time-password validation");
        delivery.StatusTransitionCalls.Should().Be(1, "the legacy path hands the transition to delivery-service");

        var row = await factory.Services.GetRequiredService<IRequestsStore>().GetAsync(deliveryId, default);
        row!.Status.Should().Be(RequestStatus.Delivered);
    }

    // ---------------------------------------------------------------------------
    // (d) VERIFY still works via the one-time-password path when no hash was minted
    // ---------------------------------------------------------------------------

    [Fact] // (d) flag-on path — the SMS-minted code (no gateway hash) keeps working
    public async Task Upstream_Verify_FallsThrough_To_Otp_When_No_Code_Minted()
    {
        var delivery = new HandoverCodeDeliveryClient
        {
            VerifyOutcome = _ => new DeliveryHandoverVerifyResult
            {
                DeliveryId = "overwritten",
                Verified = true,
                Status = CanonicalDeliveryStatus.Done
            }
        };
        var otp = new RecordingOtpClient(); // ValidateOTP returns 2xx ⇒ success
        await using var factory = UpstreamFactory(delivery, otp);
        var (deliveryId, _, jeeberId) = await SeedAtDoorAsync(factory);

        // No customer trigger ⇒ no gateway-minted hash. Jeeber verifies the SMS code.
        var resp = await VerifyOtp(JeeberClient(factory, jeeberId), deliveryId, "5678");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        otp.ValidateCalls.Should().Be(1,
            "with no gateway hash the verify falls through to one-time-password validation");
        delivery.LastVerifySuccess.Should().BeTrue();
    }

    [Fact] // (d) legacy path
    public async Task Legacy_Verify_FallsThrough_To_Otp_When_No_Code_Minted()
    {
        var delivery = new HandoverCodeDeliveryClient();
        var otp = new RecordingOtpClient();
        await using var factory = LegacyFactory(delivery, otp);
        var (deliveryId, _, jeeberId) = await SeedAtDoorAsync(factory);

        var resp = await VerifyOtp(JeeberClient(factory, jeeberId), deliveryId, "5678");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        otp.ValidateCalls.Should().Be(1, "no gateway hash ⇒ fall through to one-time-password");
        delivery.StatusTransitionCalls.Should().Be(1);
    }

    // ---------------------------------------------------------------------------
    // (e) A wrong code still 401s and locks after 3 — even when a hash was minted
    // ---------------------------------------------------------------------------

    [Fact] // (e) legacy path — the gateway owns the attempt counter + 423 lock
    public async Task Legacy_Wrong_Code_401s_Then_Locks_After_Three_Even_With_Minted_Hash()
    {
        var delivery = new HandoverCodeDeliveryClient();
        // ValidateOTP throws ⇒ wrong code on the fall-through path.
        var otp = new RecordingOtpClient
        {
            ValidateThrows = new ApiException("unauthorized", (int)HttpStatusCode.Unauthorized, null, EmptyHeaders, null)
        };
        await using var factory = LegacyFactory(delivery, otp);
        var (deliveryId, clientId, jeeberId) = await SeedAtDoorAsync(factory);

        // Customer mints a hash — proving a wrong code still falls through and locks.
        await TriggerAndReadCodeAsync(CustomerClient(factory, clientId), deliveryId);
        var jeeber = JeeberClient(factory, jeeberId);

        (await VerifyOtp(jeeber, deliveryId, "0001")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await VerifyOtp(jeeber, deliveryId, "0002")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var third = await VerifyOtp(jeeber, deliveryId, "0003");
        third.StatusCode.Should().Be(HttpStatusCode.Locked);

        var locked = await third.Content.ReadFromJsonAsync<OtpLockedResponse>();
        locked!.EscalationId.Should().NotBeNullOrEmpty();
        otp.ValidateCalls.Should().Be(3, "each wrong code falls through to one-time-password (hash mismatch)");

        var escalation = await factory.Services.GetRequiredService<IAdminEscalationStore>()
            .GetForDeliveryAsync(deliveryId, EscalationReason.OtpLocked, default);
        escalation.Should().NotBeNull("the 3rd failure opens a real admin escalation");
    }

    [Fact] // (e) flag-on path — delivery-service owns the lock; the gateway maps 401/423 through
    public async Task Upstream_Wrong_Code_401s_Then_Maps_423_Locked_From_DeliveryService()
    {
        var attempt = 0;
        var delivery = new HandoverCodeDeliveryClient
        {
            // Mirror delivery-service: 401 invalid_code (decrementing), then 423 locked.
            VerifyOutcome = _ =>
            {
                attempt++;
                if (attempt >= 3)
                {
                    throw new DeliveryHandoverException(
                        (int)HttpStatusCode.Locked, "locked", escalationId: "esc_upstream_1",
                        lockedAt: DateTimeOffset.UtcNow);
                }
                throw new DeliveryHandoverException(
                    (int)HttpStatusCode.Unauthorized, "invalid_code", attemptsRemaining: 3 - attempt);
            }
        };
        var otp = new RecordingOtpClient
        {
            ValidateThrows = new ApiException("unauthorized", (int)HttpStatusCode.Unauthorized, null, EmptyHeaders, null)
        };
        await using var factory = UpstreamFactory(delivery, otp);
        var (deliveryId, _, jeeberId) = await SeedAtDoorAsync(factory);
        var jeeber = JeeberClient(factory, jeeberId);

        (await VerifyOtp(jeeber, deliveryId, "0001")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await VerifyOtp(jeeber, deliveryId, "0002")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var third = await VerifyOtp(jeeber, deliveryId, "0003");
        third.StatusCode.Should().Be(HttpStatusCode.Locked);

        var locked = await third.Content.ReadFromJsonAsync<OtpLockedResponse>();
        locked!.EscalationId.Should().Be("esc_upstream_1");
        otp.ValidateCalls.Should().Be(3, "no gateway hash ⇒ each attempt falls through to one-time-password");
    }

    // ---------------------------------------------------------------------------
    // (f) No captured log line contains the raw minted code (revised AC5 logging rule)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Minted_Code_Is_Never_Written_To_Logs()
    {
        var sink = new ConcurrentQueue<string>();
        var delivery = new HandoverCodeDeliveryClient
        {
            VerifyOutcome = _ => new DeliveryHandoverVerifyResult
            {
                DeliveryId = "overwritten",
                Verified = true,
                Status = CanonicalDeliveryStatus.Done
            }
        };
        var otp = new RecordingOtpClient();
        await using var factory = UpstreamFactory(delivery, otp, sink);
        var (deliveryId, clientId, jeeberId) = await SeedAtDoorAsync(factory);

        // Full customer-reads-in-app → jeeber-verifies flow (the code passes through
        // both the trigger and the verify controllers).
        var code = await TriggerAndReadCodeAsync(CustomerClient(factory, clientId), deliveryId);
        (await VerifyOtp(JeeberClient(factory, jeeberId), deliveryId, code)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // Assert on a boolean so a failure never echoes the raw code. Word-boundary
        // match avoids coincidental substring hits inside hex trace/correlation ids.
        var pattern = $"\\b{Regex.Escape(code)}\\b";
        var leaked = sink.ToArray().Any(line => Regex.IsMatch(line, pattern));
        leaked.Should().BeFalse("no captured log line may contain the raw minted handover code (revised AC5)");
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, IEnumerable<string>> EmptyHeaders =
        new Dictionary<string, IEnumerable<string>>();

    private static WebApplicationFactory<Program> UpstreamFactory(
        HandoverCodeDeliveryClient delivery, RecordingOtpClient otp, ConcurrentQueue<string>? logSink = null)
        => BuildFactory(delivery, otp, deliveryFlag: true, logSink);

    private static WebApplicationFactory<Program> LegacyFactory(
        HandoverCodeDeliveryClient delivery, RecordingOtpClient otp, ConcurrentQueue<string>? logSink = null)
        => BuildFactory(delivery, otp, deliveryFlag: false, logSink);

    private static WebApplicationFactory<Program> BuildFactory(
        HandoverCodeDeliveryClient delivery, RecordingOtpClient otp, bool deliveryFlag, ConcurrentQueue<string>? logSink)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Delivery", deliveryFlag ? "true" : "false");
            builder.UseSetting("Auth:Otp:ApplicationId", TenantApplicationId);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(delivery);
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(otp);
            });
            if (logSink is not null)
            {
                builder.UseSetting("Logging:LogLevel:Default", "Trace");
                builder.ConfigureLogging(lb => lb.AddProvider(new CapturingLoggerProvider(logSink)));
            }
        });

    /// <summary>Seeds an AtDoor delivery with a recipient phone; returns its ids.</summary>
    private static async Task<(string deliveryId, string clientId, string jeeberId)> SeedAtDoorAsync(
        WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"g4-client-{Guid.NewGuid()}";
        var jeeberId = $"g4-jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the parcel",
            RecipientPhone = RecipientPhone
        }, default);

        (await store.TryAcceptByJeeberAsync(
            created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default))
            .Should().NotBeNull();
        (await store.SetStatusAsync(created.Id, RequestStatus.AtDoor, default))
            .Should().BeTrue("setup: move row to at_door");

        return (created.Id, clientId, jeeberId);
    }

    private static HttpClient CustomerClient(WebApplicationFactory<Program> factory, string clientId)
        => ClientFor(factory, clientId, role: "customer");

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string jeeberId)
        => ClientFor(factory, jeeberId, role: "driver");

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    private static Task<HttpResponseMessage> TriggerOtp(HttpClient http, string deliveryId)
        => http.GetAsync($"/deliveries/{deliveryId}/otp");

    private static Task<HttpResponseMessage> VerifyOtp(HttpClient http, string deliveryId, string code)
        => http.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code });

    private static async Task<string> TriggerAndReadCodeAsync(HttpClient http, string deliveryId)
    {
        var resp = await TriggerOtp(http, deliveryId);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OtpTriggerResponse>();
        body!.Code.Should().NotBeNull().And.MatchRegex("^[0-9]{4}$");
        return body.Code!;
    }

    private static async Task AssertNoCodeKeyAsync(HttpResponseMessage resp, string because)
    {
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.TryGetProperty("code", out _).Should().BeFalse(because);
    }

    /// <summary>
    /// <see cref="IDeliveryServiceClient"/> double for the handover-code flow: records
    /// the issue code_hash, runs a configurable verify outcome, and answers the legacy
    /// status transition + canonical read. Every unused method is loud so an accidental
    /// call surfaces rather than silently passing.
    /// </summary>
    private sealed class HandoverCodeDeliveryClient : IDeliveryServiceClient
    {
        public int IssueCalls { get; private set; }
        public string? LastIssueCodeHash { get; private set; }
        public int VerifyCalls { get; private set; }
        public bool? LastVerifySuccess { get; private set; }
        public int StatusTransitionCalls { get; private set; }
        public string? CanonicalStatus { get; init; }

        /// <summary>Verify-hop outcome: return a result (200) or throw a <see cref="DeliveryHandoverException"/>.</summary>
        public Func<bool, DeliveryHandoverVerifyResult> VerifyOutcome { get; init; }
            = _ => new DeliveryHandoverVerifyResult { DeliveryId = "overwritten", Verified = true, Status = "Done" };

        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct)
        {
            IssueCalls++;
            LastIssueCodeHash = codeHash;
            return Task.FromResult(new DeliveryHandoverIssueResult { DeliveryId = deliveryId, Issued = true });
        }

        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(
            string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct)
        {
            VerifyCalls++;
            LastVerifySuccess = success;
            var result = VerifyOutcome(success); // may throw DeliveryHandoverException
            return Task.FromResult(new DeliveryHandoverVerifyResult
            {
                DeliveryId = deliveryId,
                Verified = result.Verified,
                Status = result.Status
            });
        }

        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct)
        {
            StatusTransitionCalls++;
            return Task.FromResult(new DeliveryRequestUpstream
            {
                Id = deliveryId,
                ClientId = "g4-upstream-client",
                Status = status
            });
        }

        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct)
            => Task.FromResult<DeliveryReadUpstream?>(CanonicalStatus is null
                ? null
                : new DeliveryReadUpstream { DeliveryId = deliveryId, Status = CanonicalStatus, CreatedAt = DateTimeOffset.UtcNow });

        // ---- loud no-ops -----------------------------------------------------
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>In-process <see cref="IServiceOTPClient"/>: records dispatches; validates as
    /// success unless <see cref="ValidateThrows"/> is set (wrong/expired code).</summary>
    private sealed class RecordingOtpClient : IServiceOTPClient
    {
        public int SendCalls { get; private set; }
        public int ValidateCalls { get; private set; }
        public ApiException? ValidateThrows { get; init; }

        public Task SendOTPAsync(SendOTPRequestUserID? body) => SendOTPAsync(body, CancellationToken.None);
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken)
        {
            SendCalls++;
            return Task.CompletedTask;
        }

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => ValidateOTPAsync(body, CancellationToken.None);
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken)
        {
            ValidateCalls++;
            if (ValidateThrows is not null) throw ValidateThrows;
            return Task.CompletedTask; // 2xx ⇒ success=true in the controller
        }

        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Captures every formatted log message into a sink so (f) can assert the
    /// raw minted code never reaches a log line.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _sink;
        public CapturingLoggerProvider(ConcurrentQueue<string> sink) => _sink = sink;
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_sink);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly ConcurrentQueue<string> _sink;
            public CapturingLogger(ConcurrentQueue<string> sink) => _sink = sink;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => _sink.Enqueue(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
