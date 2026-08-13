using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using FluentAssertions;
using JeebGateway.Migration;
using JeebGateway.Requests;
using JeebGateway.Requests.OtpHandover;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    private static async Task<Seed> SeedAsync(WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = $"client-{Guid.NewGuid()}",
            Description = "Pick up the package"
        }, default);

        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        var accepted = await store.TryAcceptByJeeberAsync(
            created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default);
        accepted.Should().NotBeNull();

        (await store.SetStatusAsync(created.Id, RequestStatus.HeadingOff, default)).Should().BeTrue();
        return new Seed(created.Id, created.ClientId, jeeberId, accepted!.DeliveryOtp);
    }

    private static Task<HttpResponseMessage> VerifyOtp(HttpClient http, string deliveryId, string code) =>
        http.PostAsJsonAsync($"/deliveries/{deliveryId}/verify-otp", new { otpCode = code });
}
