using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Push;
using JeebGateway.Requests;
using JeebGateway.Requests.OtpHandover;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-013 / JEEB-31 integration tests for the
/// PATCH /deliveries/{id}/status endpoint.
///
/// Each test seeds a unique delivery to keep the shared in-memory store
/// from bleeding state across cases. Pushes go through the real
/// <see cref="IPushNotificationService"/> so the in-memory FCM/APNs
/// transports record the outbound fan-out — letting us assert that the
/// "other party" receives a status-change notification on every commit.
/// </summary>
public class DeliveriesEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DeliveriesEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    // -------- GET /deliveries/{id} single-read (S15/S09/S13) ------------------
    //
    // The single-read is the one genuinely-missing gateway route in the
    // delivery-lifecycle keystone. It reads the gateway SM mirror via
    // IRequestsStore.GetAsync and must return 200+body for a known row, 404
    // (NOT 500 — the S13 E5 fix) for an unknown id, and 401 with no identity.

    [Fact]
    public async Task GetById_KnownDelivery_Returns200WithBody()
    {
        var seed = await SeedAsync(initialStatus: RequestStatus.Accepted);
        var http = AuthClient(seed.JeeberId);

        var resp = await http.GetAsync($"/deliveries/{seed.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await resp.Content.ReadFromJsonAsync<DeliveryDto>();
        dto!.Id.Should().Be(seed.Id);
        dto.ClientId.Should().Be(seed.ClientId);
        dto.Status.Should().Be(RequestStatus.Accepted);
        dto.JeeberId.Should().Be(seed.JeeberId);
    }

    [Fact]
    public async Task GetById_UnknownDelivery_Returns404_NotFound_NotServerError()
    {
        var http = AuthClient("jeeber-404");

        // S13 E5: an unknown id MUST surface as a clean 404, never a 500.
        var resp = await http.GetAsync($"/deliveries/unknown-{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ((int)resp.StatusCode).Should().BeLessThan(500, "unknown id is a 404, never a server error (S13 E5)");
    }

    [Fact]
    public async Task GetById_NoIdentity_Returns401()
    {
        var seed = await SeedAsync();
        var anon = _factory.CreateClient();

        var resp = await anon.GetAsync($"/deliveries/{seed.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -------- PATCH /deliveries/{id}/status (canonical forward) ---------------
    //
    // JEB-1479 cut-over: the legacy in-gateway linear state-machine PATCH tests
    // (full snake_case chain, skip/backward/same-state 400s, GPS-activation,
    // PATCH-body OTP gate, per-transition push fan-out) were REMOVED with the
    // retired local-store transition path. PATCH /deliveries/{id}/status now
    // unconditionally forwards to delivery-service's canonical transition
    // contract; that forwarding behaviour (200 canonical status, typed 422 on an
    // illegal edge, 404, 400 on an unresolvable target, 401) is covered in full
    // by DeliveryCanonicalVocabTests against a recordable in-process upstream
    // fake. The legacy V2 mobile route itself stays alive (deprecated alias, no
    // 404) — see DeliveryCanonicalVocabTests.PatchStatus_* .

    // -------- T-BE-019 External OTP handover endpoints (JEB-55) --------------
    //
    // PR JEB-628 review pinned these specific assertions:
    //   B1 — status guard is `at_door`, not `heading_off`
    //   B2 — wrong code returns HTTP 401, not 400
    //   B3 — verified path delegates the status flip to IDeliveryServiceClient,
    //        not _store.SetStatusAsync directly
    //   B4 — attempt counter lives in IDistributedCache (cross-replica)
    //   B5 — no log line ever contains the submitted code (AC5)
    //   B6 — recipient phone comes from delivery.RecipientPhone, NOT a placeholder
    //   B7 — real IAdminEscalationStore row on the third wrong code
    //   B8 — these tests run with mocked IServiceOTPClient + IDeliveryServiceClient
    //   B9 — no .BeOneOf(...) assertion theatre
    //
    // Each test gets its own factory so the captured log + fake clients are
    // isolated; the shared _factory in the rest of this fixture is reused
    // for the legacy PATCH /status tests above.

    private WebApplicationFactory<Program> ExternalOtpFactory(
        FakeServiceOtpClient otpClient,
        FakeDeliveryServiceClient deliveryClient,
        CapturingLoggerProvider logCapture,
        bool deliveryUpstream = false)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            // T-BE-019 kill-switch: when on, the controller composes the
            // downstream delivery-service path; when off (default), the legacy
            // in-memory handover stays. Default false keeps existing tests green.
            builder.UseSetting(
                "FeatureFlags:UseUpstream:Delivery",
                deliveryUpstream ? "true" : "false");

            builder.ConfigureServices(services =>
            {
                // Replace the NSwag-generated OTP client with a fake we control
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(otpClient);

                // Replace the upstream delivery-service client with a fake so
                // we can assert StatusTransitionAsync is called on the verified
                // path (B3 / AC2).
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(deliveryClient);

                // Capture log records so we can assert the AC5 invariant:
                // the submitted code must NEVER appear in any structured log.
                services.AddLogging(b => b.AddProvider(logCapture));
            });
        });
    }

    [Fact]
    public async Task TriggerOtp_WithValidDelivery_AtDoor_DispatchesToRecipientPhone()
    {
        var otp        = new FakeServiceOtpClient();
        var delivery   = new FakeDeliveryServiceClient();
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture);

        var seed = await SeedAsync(factory, RequestStatus.AtDoor, recipientPhone: "+962799123456");
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await http.GetAsync($"/deliveries/{seed.Id}/otp");

        // AC1 happy-path: 200 OK
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OtpTriggerResponseDto>();
        body!.DeliveryId.Should().Be(seed.Id);
        body.Triggered.Should().BeTrue();

        // B6: the OTP service was hit with the row's RecipientPhone, not a placeholder
        otp.SendOtpCalls.Should().ContainSingle();
        otp.SendOtpCalls[0].PhoneNumber.Should().Be("+962799123456");
        otp.SendOtpCalls[0].ApplicationId.Should().Be($"delivery_handover_{seed.Id}");
    }

    [Fact]
    public async Task TriggerOtp_WithoutRecipientPhone_Returns400_PhoneMissing()
    {
        var otp        = new FakeServiceOtpClient();
        var delivery   = new FakeDeliveryServiceClient();
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture);

        // Seed at_door but WITHOUT a recipient phone — the endpoint must reject
        // rather than ship an OTP to a hardcoded placeholder (B6).
        var seed = await SeedAsync(factory, RequestStatus.AtDoor, recipientPhone: null);
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await http.GetAsync($"/deliveries/{seed.Id}/otp");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/recipient-phone-missing");
        otp.SendOtpCalls.Should().BeEmpty("no OTP should be sent without a recipient phone");
    }

    [Fact]
    public async Task TriggerOtp_WithWrongStatus_HeadingOff_Returns400()
    {
        var otp        = new FakeServiceOtpClient();
        var delivery   = new FakeDeliveryServiceClient();
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture);

        // B1: en-route is NOT the handover step — must be `at_door`.
        var seed = await SeedAsync(factory, RequestStatus.HeadingOff, recipientPhone: "+962700111222");
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await http.GetAsync($"/deliveries/{seed.Id}/otp");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/invalid-otp-trigger-state");
        problem.Title.Should().Contain("at_door");
        otp.SendOtpCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task TriggerOtp_WithUnknownDelivery_Returns404()
    {
        var otp        = new FakeServiceOtpClient();
        var delivery   = new FakeDeliveryServiceClient();
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture);

        var http = AuthClient(factory, "jeeber-404");
        var resp = await http.GetAsync($"/deliveries/unknown-{Guid.NewGuid()}/otp");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TriggerOtp_WithoutAuth_Returns401()
    {
        var otp        = new FakeServiceOtpClient();
        var delivery   = new FakeDeliveryServiceClient();
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture);

        var seed = await SeedAsync(factory, RequestStatus.AtDoor, recipientPhone: "+962700333444");

        var resp = await factory.CreateClient().GetAsync($"/deliveries/{seed.Id}/otp");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task VerifyOtp_WithCorrectCode_TransitionsViaUpstreamAndReturns200()
    {
        // AC1 happy path: correct code → status flips through delivery-service
        // (AC2), and AC6 log line appears.
        var otp        = new FakeServiceOtpClient();
        var delivery   = new FakeDeliveryServiceClient();
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture);

        var seed = await SeedAsync(factory, RequestStatus.AtDoor, recipientPhone: "+962700555666");
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await http.PostAsJsonAsync(
            $"/deliveries/{seed.Id}/otp/verify",
            new { code = "1234" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OtpVerificationResponseDto>();
        body!.Verified.Should().BeTrue();
        body.Status.Should().Be(RequestStatus.Delivered);

        // B3 / AC2: upstream delivery-service was the transition writer
        delivery.StatusTransitionCalls.Should().ContainSingle();
        delivery.StatusTransitionCalls[0].DeliveryId.Should().Be(seed.Id);
        delivery.StatusTransitionCalls[0].Status.Should().Be(RequestStatus.Delivered);

        // AC6: handover.verified event with deliveryId
        logCapture.Records.Should().Contain(r =>
            r.Message.Contains("handover.verified") && r.Message.Contains(seed.Id));

        // AC5 / B5: the submitted code must NEVER appear in any log line
        logCapture.Records.Should().NotContain(r => r.Message.Contains("1234"));
    }

    [Fact]
    public async Task VerifyOtp_WithWrongCode_Returns401_AC3()
    {
        // B2 / AC3: wrong code is HTTP 401 (Unauthorized), not 400.
        var otp        = new FakeServiceOtpClient { ValidateOutcome = FakeServiceOtpClient.OtpResult.Wrong };
        var delivery   = new FakeDeliveryServiceClient();
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture);

        var seed = await SeedAsync(factory, RequestStatus.AtDoor, recipientPhone: "+962700777888");
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await http.PostAsJsonAsync(
            $"/deliveries/{seed.Id}/otp/verify",
            new { code = "9999" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // B3: status MUST NOT have been transitioned upstream on a wrong-code
        delivery.StatusTransitionCalls.Should().BeEmpty();

        // AC5: the wrong code must NEVER appear in a log line
        logCapture.Records.Should().NotContain(r => r.Message.Contains("9999"));
    }

    [Fact]
    public async Task VerifyOtp_AfterThreeWrongCodes_Returns423WithRealEscalation_B7()
    {
        // B4: attempt counter survives across requests (and would across
        // replicas thanks to IDistributedCache).
        // B7: third wrong attempt opens a real IAdminEscalationStore row,
        // not a synthetic "ext_otp_*" string.
        var otp        = new FakeServiceOtpClient { ValidateOutcome = FakeServiceOtpClient.OtpResult.Wrong };
        var delivery   = new FakeDeliveryServiceClient();
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture);

        var seed = await SeedAsync(factory, RequestStatus.AtDoor, recipientPhone: "+962700999000");
        var http = AuthClient(factory, seed.JeeberId);

        // First two wrong codes — 401 with remaining-attempt detail
        for (var i = 0; i < 2; i++)
        {
            var r = await http.PostAsJsonAsync(
                $"/deliveries/{seed.Id}/otp/verify",
                new { code = "1111" });
            r.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                $"wrong attempt #{i + 1} should be 401 per AC3");
        }

        // Third wrong code — 423 Locked + real escalation row
        var lockResp = await http.PostAsJsonAsync(
            $"/deliveries/{seed.Id}/otp/verify",
            new { code = "1111" });
        lockResp.StatusCode.Should().Be(HttpStatusCode.Locked);

        var locked = await lockResp.Content.ReadFromJsonAsync<OtpLockedResponseDto>();
        locked!.EscalationId.Should().NotBeNullOrWhiteSpace();
        locked.EscalationId.Should().NotStartWith("ext_otp_",
            "the escalation id must reference a real IAdminEscalationStore row, not a placeholder");

        // The escalation row exists in IAdminEscalationStore (B7)
        var escalations = factory.Services.GetRequiredService<IAdminEscalationStore>();
        var row = await escalations.GetForDeliveryAsync(seed.Id, EscalationReason.OtpLocked, CancellationToken.None);
        row.Should().NotBeNull();
        row!.Id.Should().Be(locked.EscalationId);
        row.DeliveryId.Should().Be(seed.Id);
        row.OtpAttemptCount.Should().Be(3);

        // No status transition fired on any wrong path
        delivery.StatusTransitionCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyOtp_WithMissingCode_Returns400()
    {
        var otp        = new FakeServiceOtpClient();
        var delivery   = new FakeDeliveryServiceClient();
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture);

        var seed = await SeedAsync(factory, RequestStatus.AtDoor, recipientPhone: "+962700111000");
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await http.PostAsJsonAsync($"/deliveries/{seed.Id}/otp/verify", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/otp-code-required");
    }

    [Fact]
    public async Task VerifyOtp_WithWrongStatus_Returns400()
    {
        var otp        = new FakeServiceOtpClient();
        var delivery   = new FakeDeliveryServiceClient();
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture);

        var seed = await SeedAsync(factory, RequestStatus.Accepted, recipientPhone: "+962700222333");
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await http.PostAsJsonAsync(
            $"/deliveries/{seed.Id}/otp/verify",
            new { code = "1234" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/invalid-otp-verification-state");
    }

    [Fact]
    public async Task VerifyOtp_WithUnknownDelivery_Returns404()
    {
        var otp        = new FakeServiceOtpClient();
        var delivery   = new FakeDeliveryServiceClient();
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture);

        var http = AuthClient(factory, "jeeber-404");

        var resp = await http.PostAsJsonAsync(
            $"/deliveries/unknown-{Guid.NewGuid()}/otp/verify",
            new { code = "1234" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task VerifyOtp_WithoutAuth_Returns401()
    {
        var otp        = new FakeServiceOtpClient();
        var delivery   = new FakeDeliveryServiceClient();
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture);

        var seed = await SeedAsync(factory, RequestStatus.AtDoor, recipientPhone: "+962700444555");

        var resp = await factory.CreateClient().PostAsJsonAsync(
            $"/deliveries/{seed.Id}/otp/verify",
            new { code = "1234" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -------- T-BE-019 downstream compose path (FeatureFlags:UseUpstream:Delivery on) --
    //
    // Flag-on rewires the durable gate (at_door gate, attempt counter, 423-lock,
    // AtDoor→Done + settlement) to delivery-service. The gateway keeps ONLY the
    // SMS round-trip (issue) and the code-validation hop (verify), then forwards
    // a success boolean. The raw code never reaches delivery-service (AC5).

    [Fact]
    public async Task IssueOtp_FlagOn_GatesViaDeliveryServiceThenSendsSms()
    {
        // AC1 happy path: delivery-service /otp/issue is called FIRST (the
        // at_door gate), THEN one-time-password sends the SMS.
        var otp        = new FakeServiceOtpClient();
        var delivery   = new FakeDeliveryServiceClient();
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture, deliveryUpstream: true);

        var seed = await SeedAsync(factory, RequestStatus.AtDoor, recipientPhone: "+962799123456");
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await http.GetAsync($"/deliveries/{seed.Id}/otp");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Gate hit exactly once, with no code_hash (the code does not exist yet).
        delivery.IssueCalls.Should().ContainSingle();
        delivery.IssueCalls[0].DeliveryId.Should().Be(seed.Id);
        delivery.IssueCalls[0].CodeHash.Should().BeNull();

        // SMS dispatched to the row phone with the canonical applicationId.
        otp.SendOtpCalls.Should().ContainSingle();
        otp.SendOtpCalls[0].PhoneNumber.Should().Be("+962799123456");
        otp.SendOtpCalls[0].ApplicationId.Should().Be($"delivery_handover_{seed.Id}");
    }

    [Fact]
    public async Task IssueOtp_FlagOn_NotAtDoor_Propagates409_AndSkipsSms()
    {
        // delivery-service owns the gate now: 409 not_at_door short-circuits
        // BEFORE any SMS goes out.
        var otp        = new FakeServiceOtpClient();
        var delivery   = new FakeDeliveryServiceClient
        {
            IssueThrows = new DeliveryHandoverException(
                409, reason: "not_at_door")
        };
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture, deliveryUpstream: true);

        // Seed at AtDoor locally so the local guard would have passed — proving
        // the gate is now the upstream's, not the gateway's.
        var seed = await SeedAsync(factory, RequestStatus.AtDoor, recipientPhone: "+962799000111");
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await http.GetAsync($"/deliveries/{seed.Id}/otp");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/not-at-door");

        // No SMS round-trip happened — the gate rejected first.
        otp.SendOtpCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyOtp_FlagOn_CorrectCode_ForwardsSuccessTrue_Returns200_Done()
    {
        // AC2: gateway validates the code against one-time-password (success),
        // forwards { success:true } to delivery-service, gets 200 + status Done.
        // AC5/B5: the code never reaches delivery-service and never hits a log.
        var otp        = new FakeServiceOtpClient { ValidateOutcome = FakeServiceOtpClient.OtpResult.Correct };
        var delivery   = new FakeDeliveryServiceClient();
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture, deliveryUpstream: true);

        var seed = await SeedAsync(factory, RequestStatus.AtDoor, recipientPhone: "+962700555666");
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await http.PostAsJsonAsync($"/deliveries/{seed.Id}/otp/verify", new { code = "1234" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OtpVerificationResponseDto>();
        body!.Verified.Should().BeTrue();
        body.Status.Should().Be("Done");

        // delivery-service got the success boolean — never the raw code.
        delivery.VerifyCalls.Should().ContainSingle();
        delivery.VerifyCalls[0].DeliveryId.Should().Be(seed.Id);
        delivery.VerifyCalls[0].Success.Should().BeTrue();

        // AC6: handover.verified emitted with the delivery id.
        logCapture.Records.Should().Contain(r =>
            r.Message.Contains("handover.verified") && r.Message.Contains(seed.Id));

        // AC5: the code never appears in any log line.
        logCapture.Records.Should().NotContain(r => r.Message.Contains("1234"));
    }

    [Fact]
    public async Task VerifyOtp_FlagOn_WrongCode_ForwardsSuccessFalse_Maps401()
    {
        // Wrong code → one-time-password rejects → gateway forwards
        // { success:false } → delivery-service returns 401 invalid_code with
        // attempts_remaining, mapped straight through (AC3).
        var otp        = new FakeServiceOtpClient { ValidateOutcome = FakeServiceOtpClient.OtpResult.Wrong };
        var delivery   = new FakeDeliveryServiceClient
        {
            VerifyThrows = new DeliveryHandoverException(
                401, reason: "invalid_code", attemptsRemaining: 2)
        };
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture, deliveryUpstream: true);

        var seed = await SeedAsync(factory, RequestStatus.AtDoor, recipientPhone: "+962700777888");
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await http.PostAsJsonAsync($"/deliveries/{seed.Id}/otp/verify", new { code = "9999" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/otp-verification-failed");
        problem.Extensions.Should().ContainKey("attemptsRemaining");

        // delivery-service was asked to verify with success=false.
        delivery.VerifyCalls.Should().ContainSingle();
        delivery.VerifyCalls[0].Success.Should().BeFalse();

        // AC5: wrong code never logged.
        logCapture.Records.Should().NotContain(r => r.Message.Contains("9999"));
    }

    [Fact]
    public async Task VerifyOtp_FlagOn_Locked_Maps423WithEscalationId()
    {
        // delivery-service owns the durable counter; on the locking attempt it
        // returns 423 locked + escalation_id, mapped straight through (AC4).
        var otp        = new FakeServiceOtpClient { ValidateOutcome = FakeServiceOtpClient.OtpResult.Wrong };
        var delivery   = new FakeDeliveryServiceClient
        {
            VerifyThrows = new DeliveryHandoverException(
                423, reason: "locked", escalationId: "esc-abc-123")
        };
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture, deliveryUpstream: true);

        var seed = await SeedAsync(factory, RequestStatus.AtDoor, recipientPhone: "+962700999000");
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await http.PostAsJsonAsync($"/deliveries/{seed.Id}/otp/verify", new { code = "1111" });

        resp.StatusCode.Should().Be(HttpStatusCode.Locked);
        var locked = await resp.Content.ReadFromJsonAsync<OtpLockedResponseDto>();
        locked!.EscalationId.Should().Be("esc-abc-123");

        logCapture.Records.Should().NotContain(r => r.Message.Contains("1111"));
    }

    [Fact]
    public async Task VerifyOtp_FlagOn_NotAtDoor_Maps409()
    {
        // delivery-service is authoritative on the at_door gate at verify time
        // too: 409 not_at_door forwarded straight through.
        var otp        = new FakeServiceOtpClient { ValidateOutcome = FakeServiceOtpClient.OtpResult.Correct };
        var delivery   = new FakeDeliveryServiceClient
        {
            VerifyThrows = new DeliveryHandoverException(
                409, reason: "not_at_door")
        };
        var logCapture = new CapturingLoggerProvider();
        await using var factory = ExternalOtpFactory(otp, delivery, logCapture, deliveryUpstream: true);

        var seed = await SeedAsync(factory, RequestStatus.AtDoor, recipientPhone: "+962700222333");
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await http.PostAsJsonAsync($"/deliveries/{seed.Id}/otp/verify", new { code = "1234" });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/not-at-door");
    }

    // ----------------------- helpers -----------------------------------------

    private HttpClient AuthClient(string userId) => AuthClient(_factory, userId);

    private static HttpClient AuthClient(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return c;
    }

    private Task<Seed> SeedAsync(
        string? initialStatus = null,
        bool bindJeeber = true)
        => SeedAsync(_factory, initialStatus, bindJeeber, recipientPhone: null);

    private static async Task<Seed> SeedAsync(
        WebApplicationFactory<Program> factory,
        string? initialStatus = null,
        bool bindJeeber = true,
        string? recipientPhone = null)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId       = clientId,
            Description    = "Pick up the package",
            RecipientPhone = recipientPhone
        }, CancellationToken.None);

        string? otp = null;
        var current = created.Status; // 'pending' for immediate deliveries.
        if (bindJeeber)
        {
            var accepted = await store.TryAcceptByJeeberAsync(
                created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default);
            accepted.Should().NotBeNull();
            otp = accepted!.DeliveryOtp;
            current = RequestStatus.Accepted;
        }

        var landing = initialStatus
            ?? (bindJeeber ? RequestStatus.Accepted : RequestStatus.Pending);

        if (landing != current)
        {
            var ok = await store.SetStatusAsync(created.Id, landing, default);
            ok.Should().BeTrue($"setup: move seeded row to {landing}");
        }

        return new Seed(created.Id, clientId, jeeberId, otp);
    }

    private sealed record Seed(string Id, string ClientId, string JeeberId, string? Otp);

    private sealed record DeliveryDto(
        string Id,
        string ClientId,
        string Status,
        string Description,
        string? PickupAddress,
        string? DropoffAddress,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ScheduledAt,
        string? JeeberId,
        DateTimeOffset? AcceptedAt,
        bool GpsTrackingActive);

    // T-BE-019 DTOs for external OTP endpoints
    private sealed record OtpTriggerResponseDto(string DeliveryId, bool Triggered, string Message);
    private sealed record OtpVerificationResponseDto(string DeliveryId, bool Verified, string Status, string Message);
    private sealed record OtpLockedResponseDto(string EscalationId, DateTimeOffset LockedAt, string Reason);

    // ----------------------- T-BE-019 test fakes -----------------------------

    /// <summary>
    /// In-process fake for <see cref="IServiceOTPClient"/> so tests can
    /// assert what was sent upstream without making real HTTP calls and so
    /// the verify path can be deterministically driven to success or
    /// wrong-code outcomes. Replaces the brittle 3-outcome <c>BeOneOf</c>
    /// pattern called out in JEB-628 review B9.
    /// </summary>
    private sealed class FakeServiceOtpClient : IServiceOTPClient
    {
        public enum OtpResult { Correct, Wrong }
        public OtpResult ValidateOutcome { get; set; } = OtpResult.Correct;

        public List<(string PhoneNumber, string ApplicationId)> SendOtpCalls { get; } = new();
        public List<(string PhoneNumber, string Otp, string ApplicationId)> ValidateOtpCalls { get; } = new();

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
        {
            ValidateOtpCalls.Add((body?.PhoneNumber ?? "", body?.Otp ?? "", body?.ApplicationId ?? ""));
            if (ValidateOutcome == OtpResult.Correct)
            {
                return Task.CompletedTask;
            }
            // Wrong code: throw the NSwag-style ApiException with status 400 +
            // a response body that mimics what the real service might send.
            // Note: ApiException.Message embeds the response body, which is
            // why B5 / AC5 demands the controller NEVER pass `ex.Message` to
            // a logger — this fake exercises that exact code path.
            throw new ApiException(
                message:        "Invalid OTP",
                statusCode:     400,
                response:       "{\"error\":\"invalid_otp\"}",
                headers:        new Dictionary<string, IEnumerable<string>>(),
                innerException: null);
        }

        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// In-process fake for <see cref="IDeliveryServiceClient"/>. We assert
    /// against <see cref="StatusTransitionCalls"/> on the verified-OTP path
    /// (B3 / AC2) — the gateway must NOT write status to its own store on
    /// the verified path; the canonical writer is upstream.
    /// </summary>
    private sealed class FakeDeliveryServiceClient : IDeliveryServiceClient
    {
        public List<(string DeliveryId, string Status)> StatusTransitionCalls { get; } = new();

        // ---- T-BE-019 downstream compose path (flag-on) capture/config ----
        public List<(string DeliveryId, string? CodeHash)> IssueCalls { get; } = new();
        public List<(string DeliveryId, bool Success)> VerifyCalls { get; } = new();

        /// <summary>When set, IssueHandoverOtpAsync throws this instead of 200.</summary>
        public DeliveryHandoverException? IssueThrows { get; set; }

        /// <summary>When set, VerifyHandoverOtpAsync throws this instead of 200.</summary>
        public DeliveryHandoverException? VerifyThrows { get; set; }

        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct)
        {
            IssueCalls.Add((deliveryId, codeHash));
            if (IssueThrows is not null) throw IssueThrows;
            return Task.FromResult(new DeliveryHandoverIssueResult
            {
                DeliveryId = deliveryId,
                Issued     = true
            });
        }

        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, CancellationToken ct)
        {
            VerifyCalls.Add((deliveryId, success));
            if (VerifyThrows is not null) throw VerifyThrows;
            return Task.FromResult(new DeliveryHandoverVerifyResult
            {
                DeliveryId = deliveryId,
                Verified   = true,
                Status     = "Done"
            });
        }

        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct)
        {
            StatusTransitionCalls.Add((deliveryId, status));
            return Task.FromResult(new DeliveryRequestUpstream
            {
                Id        = deliveryId,
                ClientId  = "client-id",
                Status    = status,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        // ---- Canonical SM-1 transition path (flag-on) capture/config ----
        public List<(string DeliveryId, string To, string PartySource, string ActorId, string ActorRole)> CanonicalTransitionCalls { get; } = new();

        /// <summary>When set, CanonicalTransitionAsync throws this instead of 200.</summary>
        public DeliveryTransitionException? TransitionThrows { get; set; }

        /// <summary>Status returned on a 200 transition; defaults to the requested target.</summary>
        public string? TransitionReturnsStatus { get; set; }

        /// <summary>When set, GetCanonicalDeliveryAsync returns this (null ⇒ 404).</summary>
        public DeliveryReadUpstream? CanonicalReadReturns { get; set; }
        public bool CanonicalReadReturnsNull { get; set; }
        public List<string> CanonicalReadCalls { get; } = new();

        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(
            string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct)
        {
            CanonicalTransitionCalls.Add((deliveryId, to, partySource, actorId, actorRole));
            if (TransitionThrows is not null) throw TransitionThrows;
            return Task.FromResult(new DeliveryTransitionUpstream
            {
                DeliveryId     = deliveryId,
                Status         = TransitionReturnsStatus ?? to,
                TransitionId   = Guid.NewGuid().ToString(),
                TransitionedAt = DateTimeOffset.UtcNow
            });
        }

        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct)
        {
            CanonicalReadCalls.Add(deliveryId);
            if (CanonicalReadReturnsNull) return Task.FromResult<DeliveryReadUpstream?>(null);
            return Task.FromResult<DeliveryReadUpstream?>(CanonicalReadReturns ?? new DeliveryReadUpstream
            {
                DeliveryId = deliveryId,
                ClientId   = "client-id",
                Status     = "Ordered",
                CreatedAt  = DateTimeOffset.UtcNow
            });
        }

        // The remaining methods are not exercised by the OTP tests; throw
        // explicitly so an accidental call is loud.
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<JeebGateway.Tiers.DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>
    /// Captures every formatted log message so AC5 assertions ("the
    /// submitted code never appears in logs") can be enforced. The
    /// existing PR review (B5) flagged that the NSwag <c>ApiException</c>
    /// embeds the upstream response body in <c>Message</c>; this provider
    /// makes the assertion mechanical.
    /// </summary>
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
