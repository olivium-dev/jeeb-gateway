using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using FluentAssertions;
using JeebGateway.Migration;
using JeebGateway.Requests;
using JeebGateway.Requests.OtpHandover;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// gwdbx W3-02 / G-11 — the 423 lockout path must NEVER wait on delivery-service.
///
/// <para>Both proofs are deterministic constructs, not sleeps: a
/// <see cref="TaskCompletionSource"/> that is never completed, and an
/// <see cref="HttpMessageHandler"/> that never returns. If the production code
/// awaited either one, the test would hang until the CI timeout rather than
/// flake — a slow-but-passing outcome is impossible.</para>
/// </summary>
public class EscalationMirrorG11Tests
{
    private const string TenantApplicationId = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46";

    // -------- Proof 1: the 423 response does not await the mirror ---------------

    [Fact]
    public async Task Lockout_Returns_423_Even_When_The_Mirror_Never_Completes()
    {
        var mirror = new NeverCompletingEscalationMirror();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IEscalationMirror>();
                s.AddSingleton<IEscalationMirror>(mirror);
            }));

        var seed = await SeedAsync(factory);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", seed.JeeberId);
        http.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        (await VerifyOtp(http, seed.Id, "111111")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await VerifyOtp(http, seed.Id, "222222")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // If MirrorAsync were awaited this call never returns and the suite times out.
        var third = await VerifyOtp(http, seed.Id, "333333");
        third.StatusCode.Should().Be(HttpStatusCode.Locked);

        var locked = await third.Content.ReadFromJsonAsync<OtpLockedResponse>();
        locked!.Reason.Should().Be(EscalationReason.OtpLocked);
        locked.EscalationId.Should().NotBeNullOrEmpty(
            "the 423 body is built from the LOCAL escalation row, not the mirror");

        // The probe must have had data: prove the mirror was actually invoked, so a
        // silently-unwired seam cannot masquerade as "does not block".
        mirror.Calls.Should().Be(1, "the lockout path must hand exactly one row to the mirror");
        mirror.Seen.Single().Id.Should().Be(locked.EscalationId,
            "the mirrored row is the same local row the 423 body names (G-15 key)");

        // And the local store is still the authority.
        var stored = await factory.Services.GetRequiredService<IAdminEscalationStore>()
            .GetForDeliveryAsync(seed.Id, EscalationReason.OtpLocked, default);
        stored!.Id.Should().Be(locked.EscalationId);
    }

    [Fact]
    public async Task External_Otp_Lockout_Returns_423_Even_When_The_Mirror_Never_Completes()
    {
        // Second of the three mirror call sites: the durable external-OTP lockout.
        var mirror = new NeverCompletingEscalationMirror();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("FeatureFlags:UseUpstream:Delivery", "false");
            b.UseSetting("Auth:Otp:ApplicationId", TenantApplicationId);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IServiceOTPClient>();
                s.AddSingleton<IServiceOTPClient>(new AlwaysWrongOtpClient());
                s.RemoveAll<IEscalationMirror>();
                s.AddSingleton<IEscalationMirror>(mirror);
            });
        });

        var seed = await SeedAsync(factory, RequestStatus.AtDoor, recipientPhone: "+9613999000");
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", seed.JeeberId);
        http.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        for (var i = 0; i < 2; i++)
        {
            (await ExternalVerify(http, seed.Id)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // If MirrorAsync were awaited this call never returns and the suite times out.
        var locked = await ExternalVerify(http, seed.Id);
        locked.StatusCode.Should().Be(HttpStatusCode.Locked);

        var body = await locked.Content.ReadFromJsonAsync<OtpLockedResponse>();
        body!.EscalationId.Should().NotBeNullOrEmpty();

        // The probe had data: an unwired seam at this site cannot pass.
        mirror.Calls.Should().Be(1, "the external-OTP lockout must hand exactly one row to the mirror");
        mirror.Seen.Single().Id.Should().Be(body.EscalationId,
            "the mirrored row is the same local row the 423 body names (G-15 key)");
    }

    [Fact]
    public async Task Sweeper_Completes_Even_When_The_Mirror_Never_Completes()
    {
        // Third mirror call site: the client-unreachable sweeper.
        var mirror = new NeverCompletingEscalationMirror();
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(clock);
                s.RemoveAll<IEscalationMirror>();
                s.AddSingleton<IEscalationMirror>(mirror);
            }));

        var seed = await SeedAsync(factory);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", seed.JeeberId);
        http.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        (await http.PostAsync($"/deliveries/{seed.Id}/client-unreachable", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMinutes(16));

        // If the sweeper awaited MirrorAsync this never returns and the suite times out.
        var sweeper = factory.Services.GetServices<IHostedService>().OfType<OtpHandoverSweeper>().Single();
        await sweeper.SweepOnceAsync(default);

        // The probe had data: the sweep really did escalate this row.
        var row = await factory.Services.GetRequiredService<IRequestsStore>().GetAsync(seed.Id, default);
        row!.OtpEscalationId.Should().NotBeNullOrEmpty("the sweep must have escalated the due row");
        mirror.Calls.Should().Be(1, "the sweeper must hand exactly one row to the mirror");
        mirror.Seen.Single().Id.Should().Be(row.OtpEscalationId);
    }

    // -------- Proof 2: the real mirror never touches HTTP inline ----------------

    [Fact]
    public void Real_Mirror_Completes_Synchronously_While_DeliveryService_Hangs()
    {
        var mirror = new DeliveryServiceEscalationMirror(
            StaticMonitor.For(new GwdbxMigrationOptions { OtpEscalationsMode = "dual-write-local-read" }),
            NullLogger<DeliveryServiceEscalationMirror>.Instance);

        var row = NewRow();
        var task = mirror.MirrorAsync(row, CancellationToken.None);

        // Synchronous completion is the whole G-11 guarantee: the caller's stack
        // never enters an awaitable state, so the upstream cannot influence latency.
        task.IsCompletedSuccessfully.Should().BeTrue(
            "MirrorAsync must hand off and return, never await the network");

        // The probe had data: the row really was queued for the drainer.
        mirror.Reader.TryRead(out var queued).Should().BeTrue();
        queued!.Id.Should().Be(row.Id);
    }

    [Fact]
    public void Real_Mirror_Is_A_NoOp_At_The_Local_Ladder_Rung()
    {
        var mirror = new DeliveryServiceEscalationMirror(
            StaticMonitor.For(new GwdbxMigrationOptions()),
            NullLogger<DeliveryServiceEscalationMirror>.Instance);

        mirror.MirrorAsync(NewRow(), CancellationToken.None).IsCompletedSuccessfully.Should().BeTrue();
        mirror.Reader.TryRead(out _).Should().BeFalse("\"local\" must not queue anything");
    }

    [Fact]
    public async Task Drainer_Hanging_On_Http_Does_Not_Block_The_Producer()
    {
        var mode = StaticMonitor.For(new GwdbxMigrationOptions { OtpEscalationsMode = "dual-write-local-read" });
        var mirror = new DeliveryServiceEscalationMirror(mode, NullLogger<DeliveryServiceEscalationMirror>.Instance);
        var handler = new NeverRespondingHandler();
        var drainer = new EscalationMirrorDrainer(
            mirror, new SingleClientFactory(handler), NullLogger<EscalationMirrorDrainer>.Instance);

        using var cts = new CancellationTokenSource();
        await drainer.StartAsync(cts.Token);

        // The drainer parks forever inside the first POST; every further hand-off
        // must still complete synchronously.
        await mirror.MirrorAsync(NewRow(), CancellationToken.None);
        await handler.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(30));

        for (var i = 0; i < 10; i++)
        {
            mirror.MirrorAsync(NewRow(), CancellationToken.None).IsCompletedSuccessfully
                .Should().BeTrue($"hand-off {i} must not wait on the parked drainer");
        }

        handler.Requests.Should().Be(1, "the drainer is still parked in the first POST");
        cts.Cancel();
    }

    // ------------------------------- fakes -------------------------------------

    private static AdminEscalation NewRow() => new()
    {
        Id = Guid.NewGuid().ToString(),
        DeliveryId = $"d-{Guid.NewGuid()}",
        ClientId = "c-1",
        JeeberId = "p-1",
        Reason = EscalationReason.OtpLocked,
        Status = EscalationStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        OtpAttemptCount = 3,
    };

    /// <summary>Returns a task that is NEVER completed — awaiting it hangs forever.</summary>
    private sealed class NeverCompletingEscalationMirror : IEscalationMirror
    {
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<AdminEscalation> _seen = new();

        public int Calls { get; private set; }

        public IReadOnlyList<AdminEscalation> Seen
        {
            get { lock (_seen) { return _seen.ToArray(); } }
        }

        public Task MirrorAsync(AdminEscalation row, CancellationToken ct)
        {
            lock (_seen)
            {
                Calls++;
                _seen.Add(row);
            }

            return _never.Task;
        }
    }

    /// <summary>Every code is wrong, so MaxAttempts drives the durable lockout to 423.</summary>
    private sealed class AlwaysWrongOtpClient : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;

        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken ct) => Task.CompletedTask;

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body)
            => ValidateOTPAsync(body, CancellationToken.None);

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken ct)
            => throw new ApiException(
                message: "Invalid OTP",
                statusCode: 400,
                response: "{\"error\":\"invalid_otp\"}",
                headers: new Dictionary<string, IEnumerable<string>>(),
                innerException: null);

        public Task UserAsync() => Task.CompletedTask;

        public Task UserAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeClock(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _never = new();

        public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            FirstRequest.TrySetResult();
            return _never.Task;
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false) { BaseAddress = new Uri("http://delivery.invalid/") };
    }

    private static class StaticMonitor
    {
        public static IOptionsMonitor<GwdbxMigrationOptions> For(GwdbxMigrationOptions value) =>
            new Monitor(value);

        private sealed class Monitor : IOptionsMonitor<GwdbxMigrationOptions>
        {
            public Monitor(GwdbxMigrationOptions value) => CurrentValue = value;

            public GwdbxMigrationOptions CurrentValue { get; }

            public GwdbxMigrationOptions Get(string? name) => CurrentValue;

            public IDisposable? OnChange(Action<GwdbxMigrationOptions, string?> listener) => null;
        }
    }

    // ------------------------------- helpers -----------------------------------

    private sealed record Seed(string Id, string ClientId, string JeeberId, string? Otp);

    private static async Task<Seed> SeedAsync(
        WebApplicationFactory<Program> factory,
        string landingStatus = RequestStatus.HeadingOff,
        string? recipientPhone = null)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = $"client-{Guid.NewGuid()}",
            Description = "Pick up the package",
            RecipientPhone = recipientPhone
        }, default);

        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        var accepted = await store.TryAcceptByJeeberAsync(
            created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default);
        accepted.Should().NotBeNull();

        (await store.SetStatusAsync(created.Id, landingStatus, default)).Should().BeTrue();
        return new Seed(created.Id, created.ClientId, jeeberId, accepted!.DeliveryOtp);
    }

    private static Task<HttpResponseMessage> VerifyOtp(HttpClient http, string deliveryId, string code) =>
        http.PostAsJsonAsync($"/deliveries/{deliveryId}/verify-otp", new { otpCode = code });

    private static Task<HttpResponseMessage> ExternalVerify(HttpClient http, string deliveryId) =>
        http.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code = "1111" });
}
