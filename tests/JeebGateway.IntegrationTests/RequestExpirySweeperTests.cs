using System.Net;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-028: request expiry and no-offer timeout.
///
/// Two windows govern an open request:
///   * 10-min "try expanding tier" prompt when the request still has zero
///     offers (status == pending).
///   * 30-min hard expiry when no offer has been accepted — the request
///     moves to <c>expired</c>, the Client is notified, and the request
///     can no longer receive new offers.
///
/// Each test gets a fresh factory (and therefore a fresh in-memory store
/// + notifier + clock) so cases don't share state.
/// </summary>
public class RequestExpirySweeperTests
{
    private const string FlashTierId = "0be308ce-01b5-5cb9-a3e8-9adb60668d9c";
    private const string ExpressTierId = "efe0629b-0b50-555c-b182-4bd41fcd6507";
    private const string StandardTierId = "2bd0d5df-db76-5d14-9e4d-741d60b2fa12";

    [Fact]
    public async Task Ten_Minutes_With_No_Offers_Sends_Try_Expanding_Tier_Prompt()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "expiry-nudge-client");

        var requestId = await CreateRequest(client, "groceries");

        // Just under the 10-min nudge window — sweeper must NOT fire yet.
        clock.Advance(TimeSpan.FromMinutes(9));
        await SweepOnce(factory);

        var notifier = (InMemoryRequestExpiryNotifier)factory.Services.GetRequiredService<IRequestExpiryNotifier>();
        notifier.Nudges.Should().BeEmpty();

        // Crossing the 10-min mark fires the nudge exactly once.
        clock.Advance(TimeSpan.FromMinutes(2));
        await SweepOnce(factory);

        notifier.Nudges.Should().ContainSingle()
            .Which.Should().Match<InMemoryRequestExpiryNotifier.NudgeRecord>(
                n => n.RequestId == requestId && n.ClientId == "expiry-nudge-client");

        // Idempotence — a follow-up sweep inside the window must not
        // re-send the prompt to the same Client.
        await SweepOnce(factory);
        notifier.Nudges.Should().HaveCount(1);
    }

    [Fact]
    public async Task Shorter_Other_Tier_Ttl_Does_Not_Nudge_Before_No_Offer_Window()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "expiry-short-tier-client");

        var tiers = factory.Services.GetRequiredService<JeebGateway.Tiers.ITiersStore>();
        await tiers.ReplaceAsync("scheduled", new DeliveryTierReplace
        {
            Name = "Scheduled",
            SlaHours = 24,
            RadiusKm = 1.0,
            RequestTtlSeconds = 5 * 60,
            CommissionRate = 0.1,
            PriceHint = "short scan"
        }, "admin", CancellationToken.None);

        var requestId = await CreateRequest(client, "Groceries on normal tier");

        clock.Advance(TimeSpan.FromMinutes(6));
        await SweepOnce(factory);

        var notifier = (InMemoryRequestExpiryNotifier)factory.Services.GetRequiredService<IRequestExpiryNotifier>();
        notifier.Nudges.Should().NotContain(n => n.RequestId == requestId);
        notifier.Expiries.Should().NotContain(e => e.RequestId == requestId);
    }

    [Fact]
    public async Task Unknown_Tier_Uses_Scheduled_TwentyFourHour_Fallback_Not_Shortest_Ttl()
    {
        var factory = NewFactory(out var clock);
        var store = factory.Services.GetRequiredService<IRequestsStore>();

        var request = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = "expiry-unknown-tier-client",
            Description = "legacy durable row",
            TierId = "missing-tier",
            PickupLocation = new GeoPoint { Lat = 24.7136, Lng = 46.6753 },
            DropoffLocation = new GeoPoint { Lat = 24.6309, Lng = 46.7194 }
        }, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(31));
        await SweepOnce(factory);

        var notifier = (InMemoryRequestExpiryNotifier)factory.Services.GetRequiredService<IRequestExpiryNotifier>();
        notifier.Expiries.Should().NotContain(e => e.RequestId == request.Id,
            "unknown tier ids fall back to the scheduled 24h TTL, not the shortest 30m tier");

        (await store.GetAsync(request.Id, CancellationToken.None))!.Status.Should().Be(RequestStatus.Pending);
    }

    [Fact]
    public async Task Thirty_Minute_Expiry_Cancels_Request_And_Notifies_Client()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "expiry-30m-client");

        var requestId = await CreateRequest(client, "Pick up flowers");

        // Sweep at 25m — still active, no expiry.
        clock.Advance(TimeSpan.FromMinutes(25));
        await SweepOnce(factory);

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var notifier = (InMemoryRequestExpiryNotifier)factory.Services.GetRequiredService<IRequestExpiryNotifier>();

        notifier.Expiries.Should().BeEmpty();

        // Past 30m — expire + notify.
        clock.Advance(TimeSpan.FromMinutes(6));
        await SweepOnce(factory);

        notifier.Expiries.Should().ContainSingle()
            .Which.Should().Match<InMemoryRequestExpiryNotifier.ExpiryRecord>(
                e => e.RequestId == requestId && e.ClientId == "expiry-30m-client");

        // The expiry frees a BR-9 active slot — a fresh request must
        // therefore be acceptable even if the Client previously sat at
        // the cap. (Sanity-check that expired truly is terminal.)
        var followUp = await client.PostAsJsonAsync("/requests", new
        {
            description = "re-request",
            tierId = "flash",
            pickupLocation = new { lat = 24.7, lng = 46.7 },
            dropoffLocation = new { lat = 24.6, lng = 46.7 }
        });
        followUp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Expired_Request_Cannot_Receive_New_Offers()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "expiry-no-new-offers-client");

        var requestId = await CreateRequest(client, "Grab a parcel");

        clock.Advance(TimeSpan.FromMinutes(31));
        await SweepOnce(factory);

        var store = factory.Services.GetRequiredService<IRequestsStore>();

        // Once expired, the offer-acceptance state transitions are blocked.
        // A late-arriving "matched" or "accepted" must fail so the request
        // cannot be silently re-opened to new bids by a downstream race.
        (await store.SetStatusAsync(requestId, RequestStatus.Matched, CancellationToken.None))
            .Should().BeFalse("an expired request is terminal");
        (await store.SetStatusAsync(requestId, RequestStatus.Accepted, CancellationToken.None))
            .Should().BeFalse("an expired request must not accept new offers");
    }

    [Fact]
    public async Task Request_Already_Accepted_Is_Not_Expired_By_Sweeper()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "expiry-accepted-client");

        var requestId = await CreateRequest(client, "Already accepted");

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        // Simulate the offer-service moving the request out of pre-acceptance
        // before the 30-min mark.
        (await store.SetStatusAsync(requestId, RequestStatus.Accepted, CancellationToken.None))
            .Should().BeTrue();

        clock.Advance(TimeSpan.FromMinutes(45));
        await SweepOnce(factory);

        var notifier = (InMemoryRequestExpiryNotifier)factory.Services.GetRequiredService<IRequestExpiryNotifier>();
        notifier.Expiries.Should().BeEmpty("an already-accepted request must not be expired");
        notifier.Nudges.Should().BeEmpty("the nudge fires only on still-pending requests");
    }

    [Fact]
    public async Task Expiry_Suppresses_Concurrent_Nudge_For_Same_Request()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "expiry-suppress-nudge-client");

        var requestId = await CreateRequest(client, "Late sweeper run");

        // Single sweep happens AFTER both windows have elapsed (e.g. the
        // sweeper was paused). The 30-min expiry must take precedence; the
        // Client should receive the harsher "expired" push and NOT also the
        // "try expanding tier" prompt for the same request.
        clock.Advance(TimeSpan.FromMinutes(35));
        await SweepOnce(factory);

        var notifier = (InMemoryRequestExpiryNotifier)factory.Services.GetRequiredService<IRequestExpiryNotifier>();
        notifier.Expiries.Should().ContainSingle(e => e.RequestId == requestId);
        notifier.Nudges.Should().NotContain(n => n.RequestId == requestId);
    }

    [Fact]
    public async Task Thirty_Minute_Expiry_Closes_Live_Offers_On_The_Request()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "expiry-closes-offers-client");

        var requestId = await CreateRequest(client, "Deliver a box");

        // Seed a live (pending) bid on the request, as a jeeber would have submitted.
        var offers = (JeebGateway.Availability.InMemoryPendingOffersStore)
            factory.Services.GetRequiredService<JeebGateway.Availability.IPendingOffersStore>();
        var seeded = offers.EnqueueForTest(jeeberId: "jeeber-1", requestId: requestId);
        seeded.Status.Should().Be(JeebGateway.Availability.PendingOfferStatus.Pending);

        // Past the 30-min hard window — the request expires and its live bids close.
        clock.Advance(TimeSpan.FromMinutes(31));
        await SweepOnce(factory);

        var afterSweep = await offers.ListForRequestAsync(requestId, CancellationToken.None);
        afterSweep.Should().ContainSingle()
            .Which.Status.Should().Be(
                JeebGateway.Availability.PendingOfferStatus.Superseded,
                "an expired request's live bids are closed (not-selected) so no stale pending offer lingers");
    }

    [Fact]
    public async Task Sweeper_Below_Expiry_Window_Leaves_Live_Offers_Pending()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "expiry-keeps-offers-client");

        var requestId = await CreateRequest(client, "Still open");

        var offers = (JeebGateway.Availability.InMemoryPendingOffersStore)
            factory.Services.GetRequiredService<JeebGateway.Availability.IPendingOffersStore>();
        offers.EnqueueForTest(jeeberId: "jeeber-2", requestId: requestId);

        // Below the 30-min window — the request is still open, its bid stays live.
        clock.Advance(TimeSpan.FromMinutes(25));
        await SweepOnce(factory);

        var afterSweep = await offers.ListForRequestAsync(requestId, CancellationToken.None);
        afterSweep.Should().ContainSingle()
            .Which.Status.Should().Be(
                JeebGateway.Availability.PendingOfferStatus.Pending,
                "a request that has not expired must not have its live bids closed");
    }

    [Theory]
    [InlineData(FlashTierId, 1800)]
    [InlineData(ExpressTierId, 7200)]
    [InlineData(StandardTierId, 86400)]
    public async Task Upstream_Tier_Uuid_Uses_Real_PerTier_Expiry_Window(
        string tierId,
        int ttlSeconds)
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));
        var logger = new RecordingLogger<RequestExpirySweeper>();
        using var services = BuildSweeperServices(clock);
        var store = services.GetRequiredService<IRequestsStore>();
        var sweeper = new RequestExpirySweeper(
            services,
            clock,
            Options.Create(new RequestExpiryOptions()),
            new DeliveryServiceClient(new HttpClient(new UpstreamTiersHandler())
            {
                BaseAddress = new Uri("http://upstream-delivery.test/"),
            }),
            new StaticFlagsMonitor(new UpstreamFeatureFlags { Delivery = true }),
            logger);
        var request = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = $"expiry-{tierId}",
            Description = "live upstream tier",
            TierId = tierId,
            PickupLocation = new GeoPoint { Lat = 24.7136, Lng = 46.6753 },
            DropoffLocation = new GeoPoint { Lat = 24.6309, Lng = 46.7194 },
        }, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(ttlSeconds - 1));
        await sweeper.SweepOnceAsync(CancellationToken.None);

        (await store.GetAsync(request.Id, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Pending);

        clock.Advance(TimeSpan.FromSeconds(2));
        await sweeper.SweepOnceAsync(CancellationToken.None);

        (await store.GetAsync(request.Id, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Expired);
        logger.Messages.Should().NotContain(message =>
            message.Contains("unknown tier", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// With the upstream catalog live, a request still stamped with a LEGACY
    /// SLUG tier ("flash" → canonicalised to "urgent", 30m in the local
    /// catalog) must keep its real window. Loading only the upstream UUID
    /// catalog would leave the slug unresolvable and silently hand it the 24h
    /// safe fallback — the same defect being fixed here, just inverted.
    /// </summary>
    [Fact]
    public async Task Legacy_Slug_Tier_Still_Resolves_When_Upstream_Catalog_Is_Live()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));
        var logger = new RecordingLogger<RequestExpirySweeper>();
        using var services = BuildSweeperServices(clock);
        var store = services.GetRequiredService<IRequestsStore>();
        var sweeper = new RequestExpirySweeper(
            services,
            clock,
            Options.Create(new RequestExpiryOptions()),
            new DeliveryServiceClient(new HttpClient(new UpstreamTiersHandler())
            {
                BaseAddress = new Uri("http://upstream-delivery.test/"),
            }),
            new StaticFlagsMonitor(new UpstreamFeatureFlags { Delivery = true }),
            logger);
        var request = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = "expiry-legacy-slug",
            Description = "legacy slug tier",
            TierId = "flash",
            PickupLocation = new GeoPoint { Lat = 24.7136, Lng = 46.6753 },
            DropoffLocation = new GeoPoint { Lat = 24.6309, Lng = 46.7194 },
        }, CancellationToken.None);

        // Well past the 30m flash window, but far short of the 24h fallback:
        // only a correctly resolved slug expires here.
        clock.Advance(TimeSpan.FromMinutes(31));
        await sweeper.SweepOnceAsync(CancellationToken.None);

        (await store.GetAsync(request.Id, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Expired);
        logger.Messages.Should().NotContain(message =>
            message.Contains("unknown tier", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(out FakeClock clock)
    {
        var theClock = new FakeClock(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));
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

    private static ServiceProvider BuildSweeperServices(FakeClock clock)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<InMemoryRequestsStore>();
        services.AddSingleton<IRequestsStore>(sp => sp.GetRequiredService<InMemoryRequestsStore>());
        services.AddSingleton<InMemoryRequestExpiryNotifier>();
        services.AddSingleton<IRequestExpiryNotifier>(sp =>
            sp.GetRequiredService<InMemoryRequestExpiryNotifier>());
        services.AddSingleton<JeebGateway.Availability.InMemoryPendingOffersStore>();
        services.AddSingleton<JeebGateway.Availability.IPendingOffersStore>(sp =>
            sp.GetRequiredService<JeebGateway.Availability.InMemoryPendingOffersStore>());
        services.AddSingleton<JeebGateway.Tiers.ITiersStore, InMemoryTiersStore>();
        return services.BuildServiceProvider();
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    private static async Task<string> CreateRequest(HttpClient client, string description)
    {
        // T-backend-007 added tier + locations as required fields. The
        // sweeper tests don't care about those values — a single canned
        // pickup/dropoff pair is enough to land a row in the store.
        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description,
            tierId = "flash",
            pickupLocation = new { lat = 24.7136, lng = 46.6753 },
            dropoffLocation = new { lat = 24.6309, lng = 46.7194 }
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<RequestDto>();
        return dto!.Id;
    }

    private static Task SweepOnce(WebApplicationFactory<Program> factory)
    {
        var sweeper = factory.Services
            .GetServices<IHostedService>()
            .OfType<RequestExpirySweeper>()
            .Single();
        return sweeper.SweepOnceAsync(default);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class UpstreamTiersHandler : HttpMessageHandler
    {
        private const string CatalogJson = """
            [
              {
                "id":"0be308ce-01b5-5cb9-a3e8-9adb60668d9c","name":"flash",
                "slaHours":1,"radiusKm":3.0,"ttl_seconds":1800,"ttl_minutes":30,
                "commissionRate":0.10,"priceHint":"flash",
                "createdAt":"2026-07-21T00:00:00Z","updatedAt":"2026-07-21T00:00:00Z"
              },
              {
                "id":"efe0629b-0b50-555c-b182-4bd41fcd6507","name":"express",
                "slaHours":2,"radiusKm":10.0,"ttl_seconds":7200,"ttl_minutes":120,
                "commissionRate":0.10,"priceHint":"express",
                "createdAt":"2026-07-21T00:00:00Z","updatedAt":"2026-07-21T00:00:00Z"
              },
              {
                "id":"2bd0d5df-db76-5d14-9e4d-741d60b2fa12","name":"standard",
                "slaHours":24,"radiusKm":25.0,"ttl_seconds":86400,"ttl_minutes":1440,
                "commissionRate":0.10,"priceHint":"standard",
                "createdAt":"2026-07-21T00:00:00Z","updatedAt":"2026-07-21T00:00:00Z"
              }
            ]
            """;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CatalogJson, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }

    private sealed class StaticFlagsMonitor : IOptionsMonitor<UpstreamFeatureFlags>
    {
        public StaticFlagsMonitor(UpstreamFeatureFlags value) => CurrentValue = value;

        public UpstreamFeatureFlags CurrentValue { get; }

        public UpstreamFeatureFlags Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<UpstreamFeatureFlags, string?> listener) => null;
    }

    private sealed record RequestDto(
        string Id,
        string ClientId,
        string Status,
        string Description,
        string? PickupAddress,
        string? DropoffAddress,
        DateTimeOffset CreatedAt);
}
