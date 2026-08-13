using System.Net;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Migration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// gwdbx W3-04 — the availability write-through to delivery-service.
///
/// <para>Three claims: the ladder ships at <c>local</c>; at <c>local</c> the
/// write-through changes nothing about gateway availability; and a broken
/// delivery-service cannot break gateway availability (fail-open).</para>
/// </summary>
public class AvailabilityMirrorW304Tests
{
    // -------- The shipped default ----------------------------------------------

    [Fact]
    public void AvailabilityMode_Defaults_To_Local()
    {
        new GwdbxMigrationOptions().AvailabilityMode.Should().Be("local");
        new GwdbxMigrationOptions().Availability.Should().Be(GwdbxMigrationPhase.Local);
    }

    [Fact]
    public async Task Booted_Gateway_Binds_AvailabilityMode_As_Local()
    {
        await using var factory = new WebApplicationFactory<Program>();
        // Force the host to build so options really bind.
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<IOptionsMonitor<GwdbxMigrationOptions>>()
            .CurrentValue.Availability.Should().Be(GwdbxMigrationPhase.Local,
                "W3-13 owns the flip; this PR must ship inert");
    }

    // -------- local rung: the write-through is a no-op ---------------------------

    [Fact]
    public void Mirror_Is_A_NoOp_At_The_Local_Ladder_Rung()
    {
        var mirror = NewMirror(new GwdbxMigrationOptions());

        mirror.MirrorInteractionAsync("j-1", DateTimeOffset.UtcNow, default)
            .IsCompletedSuccessfully.Should().BeTrue();
        mirror.MirrorIdleOfflineAsync("j-1", default).IsCompletedSuccessfully.Should().BeTrue();

        mirror.Reader.TryRead(out _).Should().BeFalse("\"local\" must not queue anything");
    }

    [Fact]
    public void Mirror_Queues_Both_Signals_Once_Dual_Write_Is_On()
    {
        // Control for the test above: without this, "nothing queued" could just mean
        // the seam is dead rather than gated.
        var mirror = NewMirror(new GwdbxMigrationOptions { AvailabilityMode = "dual-write-local-read" });

        mirror.MirrorInteractionAsync("j-1", DateTimeOffset.UtcNow, default)
            .IsCompletedSuccessfully.Should().BeTrue("the hand-off must never await the network");
        mirror.MirrorIdleOfflineAsync("j-2", default).IsCompletedSuccessfully.Should().BeTrue();

        mirror.Reader.TryRead(out var first).Should().BeTrue();
        first!.UserId.Should().Be("j-1");
        first.IdleOffline.Should().BeFalse("an in-app read is activity, not a presence change");

        mirror.Reader.TryRead(out var second).Should().BeTrue();
        second!.UserId.Should().Be("j-2");
        second.IdleOffline.Should().BeTrue();
    }

    [Fact]
    public async Task Availability_Get_Returns_200_At_The_Local_Rung()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var http = AsJeeber(factory);

        var response = await http.GetAsync("/v1/jeebers/me/availability");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the local rung must leave the availability contract exactly as it was");
    }

    // -------- fail-open: a broken delivery-service cannot break availability ------

    [Fact]
    public async Task Availability_Get_Still_200s_When_The_Mirror_Never_Completes()
    {
        var mirror = new NeverCompletingAvailabilityMirror();
        await using var factory = WithMirror(mirror);
        var http = AsJeeber(factory);

        // If the write-through were awaited this call never returns and the suite
        // times out — a slow-but-passing outcome is impossible.
        var response = await http.GetAsync("/v1/jeebers/me/availability");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        mirror.Calls.Should().Be(1,
            "the probe must have had data: an unwired seam cannot pass this test");
    }

    [Fact]
    public async Task Availability_Get_Still_200s_When_The_Mirror_Throws_Synchronously()
    {
        // A faulted Task would prove nothing — the guard discards those. This fake
        // throws BEFORE any Task exists, so only FailOpenAvailabilityMirror saves it.
        var mirror = new SynchronouslyThrowingAvailabilityMirror();
        await using var factory = WithMirror(mirror);
        var http = AsJeeber(factory);

        (await http.GetAsync("/v1/jeebers/me/availability")).StatusCode.Should().Be(HttpStatusCode.OK);
        mirror.Calls.Should().Be(1, "the probe must have thrown once on the availability path");
    }

    [Fact]
    public async Task Sweeper_Completes_When_The_Mirror_Throws_Synchronously()
    {
        var mirror = new SynchronouslyThrowingAvailabilityMirror();
        // The production IPendingOffersStore throws on WithdrawForJeeberAsync, which the
        // sweeper's per-record catch swallows BEFORE the mirror line — swap it out so this
        // test exercises the mirror rather than that unrelated pre-existing fault.
        await using var factory = WithMirror(mirror, s =>
        {
            s.RemoveAll<IPendingOffersStore>();
            s.AddSingleton<IPendingOffersStore>(new Fakes.FakePendingOffersStore(TimeProvider.System));
        });
        _ = factory.CreateClient();

        var store = factory.Services.GetRequiredService<IAvailabilityStore>();
        await store.GoOnlineAsync("sweep-j", new GoOnlineRequest
        {
            VehicleType = VehicleType.Car,
            Zone = "z",
        }, default);
        await store.RecordInteractionAsync("sweep-j", DateTimeOffset.UtcNow.AddDays(-2), default);

        var sweeper = factory.Services.GetServices<IHostedService>().OfType<AutoOfflineSweeper>().Single();
        await sweeper.SweepOnceAsync(default);

        // The probe had data: the sweep really did flip this jeeber.
        (await store.GetAsync("sweep-j", default)).IsOnline.Should().BeFalse();
        mirror.Calls.Should().Be(1, "the sweeper must report exactly one idle flip");
    }

    [Fact]
    public async Task Real_Mirror_Hand_Off_Does_Not_Wait_On_A_Hanging_DeliveryService()
    {
        var mirror = NewMirror(new GwdbxMigrationOptions { AvailabilityMode = "dual-write-local-read" });
        var handler = new NeverRespondingHandler();
        var drainer = new AvailabilityMirrorDrainer(
            mirror, new SingleClientFactory(handler), NullLogger<AvailabilityMirrorDrainer>.Instance);

        using var cts = new CancellationTokenSource();
        await drainer.StartAsync(cts.Token);

        await mirror.MirrorInteractionAsync("j-1", DateTimeOffset.UtcNow, default);
        await handler.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(30));

        for (var i = 0; i < 10; i++)
        {
            mirror.MirrorInteractionAsync($"j-{i}", DateTimeOffset.UtcNow, default)
                .IsCompletedSuccessfully.Should().BeTrue($"hand-off {i} must not wait on the parked drainer");
        }

        handler.Requests.Should().Be(1, "the drainer is still parked in the first POST");

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await drainer.StopAsync(stop.Token);
        drainer.ExecuteTask!.IsCompleted.Should().BeTrue(
            "the drain loop must observe cancellation, not stay parked in HttpClient");
    }

    // ------------------------------- helpers -----------------------------------

    private static DeliveryServiceAvailabilityMirror NewMirror(GwdbxMigrationOptions options) =>
        new(new StaticMonitor(options), NullLogger<DeliveryServiceAvailabilityMirror>.Instance);

    private static WebApplicationFactory<Program> WithMirror(
        IAvailabilityMirror mirror, Action<IServiceCollection>? extra = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IAvailabilityMirror>();
                s.AddSingleton(mirror);
                s.RemoveAll<FailOpenAvailabilityMirror>();
                s.AddSingleton(sp => new FailOpenAvailabilityMirror(
                    mirror, sp.GetRequiredService<ILogger<FailOpenAvailabilityMirror>>()));
                extra?.Invoke(s);
            }));

    private static HttpClient AsJeeber(WebApplicationFactory<Program> factory)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", $"jeeber-{Guid.NewGuid()}");
        http.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return http;
    }

    private sealed class NeverCompletingAvailabilityMirror : IAvailabilityMirror
    {
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task MirrorInteractionAsync(string userId, DateTimeOffset at, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return _never.Task;
        }

        public Task MirrorIdleOfflineAsync(string userId, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return _never.Task;
        }
    }

    private sealed class SynchronouslyThrowingAvailabilityMirror : IAvailabilityMirror
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task MirrorInteractionAsync(string userId, DateTimeOffset at, CancellationToken ct) => Throw();

        public Task MirrorIdleOfflineAsync(string userId, CancellationToken ct) => Throw();

        private Task Throw()
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("availability mirror threw synchronously");
        }
    }

    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _never = new();
        private CancellationTokenRegistration _registration;

        public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            // Cancellation-aware: a fake ignoring ct would hide a drainer that never stops.
            _registration = ct.Register(() => _never.TrySetCanceled(ct));
            FirstRequest.TrySetResult();
            return _never.Task;
        }

        protected override void Dispose(bool disposing)
        {
            _registration.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false) { BaseAddress = new Uri("http://delivery.invalid/") };
    }

    private sealed class StaticMonitor : IOptionsMonitor<GwdbxMigrationOptions>
    {
        public StaticMonitor(GwdbxMigrationOptions value) => CurrentValue = value;

        public GwdbxMigrationOptions CurrentValue { get; }

        public GwdbxMigrationOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<GwdbxMigrationOptions, string?> listener) => null;
    }
}
