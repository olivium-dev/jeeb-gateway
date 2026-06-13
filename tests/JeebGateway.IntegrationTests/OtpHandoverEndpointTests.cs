using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Requests.OtpHandover;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-015 / JEEB-33 integration tests for the OTP handover flow.
///
/// Coverage:
/// <list type="bullet">
///   <item>Correct OTP transitions the row to <c>delivered</c>.</item>
///   <item>Wrong OTP decrements the attempt budget but keeps the row open.</item>
///   <item>3rd wrong OTP locks the row and opens an admin escalation entry
///     with reason <see cref="EscalationReason.OtpLocked"/>.</item>
///   <item>Locked-out rows return 423 on every subsequent attempt without
///     creating a duplicate escalation.</item>
///   <item>Marking the Client unreachable starts the 15-min timer and the
///     <c>OtpHandoverSweeper</c> opens an escalation with reason
///     <see cref="EscalationReason.ClientUnreachable"/> once the window
///     elapses.</item>
/// </list>
///
/// Each test uses an isolated <see cref="WebApplicationFactory{TEntryPoint}"/>
/// so the in-memory stores (requests, escalations) start empty.
/// </summary>
public class OtpHandoverEndpointTests
{
    // -------- Verify-OTP: success path -----------------------------------------

    [Fact]
    public async Task Correct_Otp_Transitions_To_Delivered()
    {
        await using var factory = NewFactory(out _);
        var seed = await SeedAsync(factory, initialStatus: RequestStatus.HeadingOff);
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await VerifyOtp(http, seed.Id, seed.Otp!);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<OtpVerificationResponse>();
        body!.Verified.Should().BeTrue();
        body.Delivery.Status.Should().Be(RequestStatus.Delivered);

        // Persisted on the row.
        var row = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(seed.Id, default);
        row!.Status.Should().Be(RequestStatus.Delivered);
        row.OtpLockedAt.Should().BeNull("success does not lock the row");
        row.OtpEscalationId.Should().BeNull("success does not escalate");

        // No escalation rows were created.
        var escalations = await factory.Services.GetRequiredService<IAdminEscalationStore>()
            .ListAsync(default);
        escalations.Should().BeEmpty();
    }

    // -------- Verify-OTP: wrong OTP decrements without locking -----------------

    [Fact]
    public async Task Wrong_Otp_Decrements_Remaining_Attempts()
    {
        await using var factory = NewFactory(out _);
        var seed = await SeedAsync(factory, initialStatus: RequestStatus.HeadingOff);
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await VerifyOtp(http, seed.Id, "000000");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/otp-mismatch");
        problem.Detail.Should().Contain("2 attempt(s) remaining");

        var row = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(seed.Id, default);
        row!.Status.Should().Be(RequestStatus.HeadingOff, "row stays open after a single wrong attempt");
        row.OtpAttemptCount.Should().Be(1);
        row.OtpLockedAt.Should().BeNull();
    }

    // -------- Verify-OTP: 3 strikes lockout + escalation -----------------------

    [Fact]
    public async Task Three_Wrong_Otps_Lock_Row_And_Create_Escalation()
    {
        await using var factory = NewFactory(out _);
        var seed = await SeedAsync(factory, initialStatus: RequestStatus.HeadingOff);
        var http = AuthClient(factory, seed.JeeberId);

        // First two wrong attempts come back as 400 with decrementing budget.
        (await VerifyOtp(http, seed.Id, "111111")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await VerifyOtp(http, seed.Id, "222222")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Third attempt hits the lockout boundary.
        var third = await VerifyOtp(http, seed.Id, "333333");
        third.StatusCode.Should().Be(HttpStatusCode.Locked);

        var locked = await third.Content.ReadFromJsonAsync<OtpLockedResponse>();
        locked!.Reason.Should().Be(EscalationReason.OtpLocked);
        locked.EscalationId.Should().NotBeNullOrEmpty();

        // Row state reflects the lockout and carries the escalation id.
        var row = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(seed.Id, default);
        row!.OtpAttemptCount.Should().Be(3);
        row.OtpLockedAt.Should().NotBeNull();
        row.OtpEscalationId.Should().Be(locked.EscalationId);

        // Exactly one escalation row was opened, with the OtpLocked reason.
        var escalation = await factory.Services.GetRequiredService<IAdminEscalationStore>()
            .GetForDeliveryAsync(seed.Id, EscalationReason.OtpLocked, default);
        escalation.Should().NotBeNull();
        escalation!.Status.Should().Be(EscalationStatus.Pending);
        escalation.OtpAttemptCount.Should().Be(3);
        escalation.JeeberId.Should().Be(seed.JeeberId);
        escalation.ClientId.Should().Be(seed.ClientId);

        var all = await factory.Services.GetRequiredService<IAdminEscalationStore>()
            .ListAsync(default);
        all.Should().HaveCount(1, "only one escalation should be created from the 3-strike lockout");
    }

    // -------- Verify-OTP: subsequent attempts after lockout ---------------------

    [Fact]
    public async Task Attempts_After_Lockout_Return_423_Without_Duplicating_Escalation()
    {
        await using var factory = NewFactory(out _);
        var seed = await SeedAsync(factory, initialStatus: RequestStatus.HeadingOff);
        var http = AuthClient(factory, seed.JeeberId);

        // Burn the budget.
        await VerifyOtp(http, seed.Id, "111111");
        await VerifyOtp(http, seed.Id, "222222");
        await VerifyOtp(http, seed.Id, "333333");

        // Two further attempts — both should return 423 Locked without a
        // second escalation row appearing.
        var followUp1 = await VerifyOtp(http, seed.Id, "444444");
        var followUp2 = await VerifyOtp(http, seed.Id, seed.Otp!); // even the correct code is rejected after lockout
        followUp1.StatusCode.Should().Be(HttpStatusCode.Locked);
        followUp2.StatusCode.Should().Be(HttpStatusCode.Locked);

        var all = await factory.Services.GetRequiredService<IAdminEscalationStore>()
            .ListAsync(default);
        all.Should().HaveCount(1, "the 423 path is idempotent — no new escalations on repeated calls");
    }

    // -------- Verify-OTP: malformed / state-machine guards ---------------------

    [Fact]
    public async Task Missing_OtpCode_Returns_400()
    {
        await using var factory = NewFactory(out _);
        var seed = await SeedAsync(factory, initialStatus: RequestStatus.HeadingOff);
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await http.PostAsJsonAsync(
            $"/deliveries/{seed.Id}/verify-otp",
            new { otpCode = "" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/otp-required");
    }

    [Fact]
    public async Task Verify_When_Row_Not_In_HeadingOff_Returns_400()
    {
        await using var factory = NewFactory(out _);
        // Seed sits at Accepted — OTP verification should be refused.
        var seed = await SeedAsync(factory, initialStatus: RequestStatus.Accepted);
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await VerifyOtp(http, seed.Id, seed.Otp!);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/otp-not-in-handover-state");

        var row = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(seed.Id, default);
        row!.OtpAttemptCount.Should().Be(0, "state-machine guard must not consume an attempt");
    }

    [Fact]
    public async Task Unknown_Delivery_Returns_404()
    {
        await using var factory = NewFactory(out _);
        var http = AuthClient(factory, "jeeber-404");

        var resp = await VerifyOtp(http, $"unknown-{Guid.NewGuid()}", "123456");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task No_Identity_Returns_401()
    {
        await using var factory = NewFactory(out _);
        var seed = await SeedAsync(factory, initialStatus: RequestStatus.HeadingOff);
        var anon = factory.CreateClient();

        var resp = await anon.PostAsJsonAsync(
            $"/deliveries/{seed.Id}/verify-otp",
            new { otpCode = seed.Otp });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -------- Client unreachable: 15-min timer + escalation --------------------

    [Fact]
    public async Task Mark_Unreachable_Sets_Timestamp_And_Returns_200()
    {
        await using var factory = NewFactory(out var clock);
        var seed = await SeedAsync(factory, initialStatus: RequestStatus.HeadingOff);
        var http = AuthClient(factory, seed.JeeberId);

        var resp = await http.PostAsync($"/deliveries/{seed.Id}/client-unreachable", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(seed.Id, default);
        row!.ClientUnreachableAt.Should().Be(clock.GetUtcNow());
        row.OtpEscalationId.Should().BeNull("no escalation until 15 min elapse");
    }

    [Fact]
    public async Task Unreachable_Timer_Below_15_Minutes_Does_Not_Escalate()
    {
        await using var factory = NewFactory(out var clock);
        var seed = await SeedAsync(factory, initialStatus: RequestStatus.HeadingOff);
        var http = AuthClient(factory, seed.JeeberId);

        await http.PostAsync($"/deliveries/{seed.Id}/client-unreachable", content: null);

        // Move just shy of the 15-min window.
        clock.Advance(TimeSpan.FromMinutes(14));
        await SweepOnce(factory);

        var row = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(seed.Id, default);
        row!.OtpEscalationId.Should().BeNull("sweeper must not escalate before 15 min elapse");

        var escalations = await factory.Services.GetRequiredService<IAdminEscalationStore>()
            .ListAsync(default);
        escalations.Should().BeEmpty();
    }

    [Fact]
    public async Task Unreachable_Timer_After_15_Minutes_Escalates()
    {
        await using var factory = NewFactory(out var clock);
        var seed = await SeedAsync(factory, initialStatus: RequestStatus.HeadingOff);
        var http = AuthClient(factory, seed.JeeberId);

        await http.PostAsync($"/deliveries/{seed.Id}/client-unreachable", content: null);

        // Past the 15-min window.
        clock.Advance(TimeSpan.FromMinutes(16));
        await SweepOnce(factory);

        var row = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(seed.Id, default);
        row!.OtpEscalationId.Should().NotBeNullOrEmpty("sweeper must escalate after the window elapses");

        var escalation = await factory.Services.GetRequiredService<IAdminEscalationStore>()
            .GetForDeliveryAsync(seed.Id, EscalationReason.ClientUnreachable, default);
        escalation.Should().NotBeNull();
        escalation!.Status.Should().Be(EscalationStatus.Pending);
        escalation.JeeberId.Should().Be(seed.JeeberId);
        escalation.ClientId.Should().Be(seed.ClientId);

        // Stamped escalation id matches the one in the store.
        row.OtpEscalationId.Should().Be(escalation.Id);
    }

    [Fact]
    public async Task Sweeper_Is_Idempotent_Across_Runs()
    {
        await using var factory = NewFactory(out var clock);
        var seed = await SeedAsync(factory, initialStatus: RequestStatus.HeadingOff);
        var http = AuthClient(factory, seed.JeeberId);

        await http.PostAsync($"/deliveries/{seed.Id}/client-unreachable", content: null);
        clock.Advance(TimeSpan.FromMinutes(20));

        await SweepOnce(factory);
        await SweepOnce(factory);
        await SweepOnce(factory);

        var escalations = await factory.Services.GetRequiredService<IAdminEscalationStore>()
            .ListAsync(default);
        // The sweeper creates a fresh AdminEscalation row each loop but
        // the TrySetEscalationIdAsync write-once guard ensures only the
        // first one is referenced by the delivery — and ListUnreachable
        // filters rows that already carry an escalation id, so the loop
        // body runs exactly once.
        escalations.Should().HaveCount(1, "the unreachable filter excludes already-escalated rows");
    }

    [Fact]
    public async Task Mark_Unreachable_Is_Idempotent()
    {
        await using var factory = NewFactory(out var clock);
        var seed = await SeedAsync(factory, initialStatus: RequestStatus.HeadingOff);
        var http = AuthClient(factory, seed.JeeberId);

        var resp1 = await http.PostAsync($"/deliveries/{seed.Id}/client-unreachable", content: null);
        resp1.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstStamp = (await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(seed.Id, default))!.ClientUnreachableAt!.Value;

        clock.Advance(TimeSpan.FromMinutes(5));
        var resp2 = await http.PostAsync($"/deliveries/{seed.Id}/client-unreachable", content: null);
        resp2.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(seed.Id, default);
        row!.ClientUnreachableAt.Should().Be(firstStamp,
            "the unreachable timestamp must not be reset by repeated calls — otherwise the 15-min window could be re-armed forever");
    }

    [Fact]
    public async Task Mark_Unreachable_On_Unknown_Delivery_Returns_404()
    {
        await using var factory = NewFactory(out _);
        var http = AuthClient(factory, "jeeber-404");

        var resp = await http.PostAsync(
            $"/deliveries/unknown-{Guid.NewGuid()}/client-unreachable", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------- helpers ----------------------------------

    private static WebApplicationFactory<Program> NewFactory(out FakeClock clock)
    {
        var theClock = new FakeClock(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));
        clock = theClock;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(theClock);
            });
        });
    }

    private static HttpClient AuthClient(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return c;
    }

    private static async Task<Seed> SeedAsync(
        WebApplicationFactory<Program> factory,
        string initialStatus)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the package"
        }, default);

        // Run through Accept to mint the OTP and bind the Jeeber, then
        // land the row on the test's desired starting status. SetStatus
        // only blocks terminal states, so we can move backwards from
        // Accepted to whatever the test asks for.
        var accepted = await store.TryAcceptByJeeberAsync(
            created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default);
        accepted.Should().NotBeNull();
        var otp = accepted!.DeliveryOtp;

        if (initialStatus != RequestStatus.Accepted)
        {
            var ok = await store.SetStatusAsync(created.Id, initialStatus, default);
            ok.Should().BeTrue($"setup: move seeded row to {initialStatus}");
        }

        return new Seed(created.Id, clientId, jeeberId, otp);
    }

    private static Task<HttpResponseMessage> VerifyOtp(HttpClient http, string deliveryId, string code)
    {
        return http.PostAsJsonAsync(
            $"/deliveries/{deliveryId}/verify-otp",
            new { otpCode = code });
    }

    private static Task SweepOnce(WebApplicationFactory<Program> factory)
    {
        var sweeper = factory.Services
            .GetServices<IHostedService>()
            .OfType<OtpHandoverSweeper>()
            .Single();
        return sweeper.SweepOnceAsync(default);
    }

    private sealed record Seed(string Id, string ClientId, string JeeberId, string? Otp);

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
