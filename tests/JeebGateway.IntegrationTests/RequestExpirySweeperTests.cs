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
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-028: legacy gateway-owned request TTL authority.
///
/// <see cref="RequestExpirySweeper"/> is now expiry-only. The no-offer nudge
/// lives in <see cref="RequestNudgeSweeper"/>.
///
/// This whole class covers the LEGACY gateway-owned TTL authority kept only
/// while <c>FeatureFlags:RequestExpiry:Source == "gateway"</c>.
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

        // Just under the 10-min nudge window — nudge sweeper must NOT fire yet.
        clock.Advance(TimeSpan.FromMinutes(9));
        await NudgeOnce(factory);

        var notifier = (InMemoryRequestExpiryNotifier)factory.Services.GetRequiredService<IRequestExpiryNotifier>();
        notifier.Nudges.Should().BeEmpty();

        // Crossing the 10-min mark fires the nudge exactly once.
        clock.Advance(TimeSpan.FromMinutes(2));
        await NudgeOnce(factory);

        notifier.Nudges.Should().ContainSingle()
            .Which.Should().Match<InMemoryRequestExpiryNotifier.NudgeRecord>(
                n => n.RequestId == requestId && n.ClientId == "expiry-nudge-client");

        // The in-memory notifier records every dispatch attempt; production deduplication
        // belongs to the expiry notifier's request-nudge:{requestId} key.
        await NudgeOnce(factory);
        notifier.Nudges.Should().HaveCount(2,
            "notifier idempotency, not the gateway nudge sweeper, deduplicates request nudges");
    }

    [Fact]
    public async Task Nudge_Suppressed_When_Request_Has_Live_Offer()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "nudge-has-offer-client");

        var requestId = await CreateRequest(client, "Deliver a parcel");

        var offers = (JeebGateway.IntegrationTests.Fakes.FakePendingOffersStore)
            factory.Services.GetRequiredService<JeebGateway.Availability.IPendingOffersStore>();
        offers.EnqueueForTest(jeeberId: "nudge-jeeber-1", requestId: requestId);

        // Past the 10-min no-offer mark, but a jeeber has already bid.
        clock.Advance(TimeSpan.FromMinutes(11));
        await NudgeOnce(factory);

        var notifier = (InMemoryRequestExpiryNotifier)factory.Services.GetRequiredService<IRequestExpiryNotifier>();
        notifier.Nudges.Should().BeEmpty(
            "FR-6.6 defines the try-expanding-tier nudge for a request with ZERO offers");
    }

    [Fact]
    public async Task Nudge_Sent_When_Only_Withdrawn_Offers_Remain()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "nudge-withdrawn-offer-client");

        var requestId = await CreateRequest(client, "Deliver a parcel");

        var offers = (JeebGateway.IntegrationTests.Fakes.FakePendingOffersStore)
            factory.Services.GetRequiredService<JeebGateway.Availability.IPendingOffersStore>();
        var seeded = offers.EnqueueForTest(jeeberId: "nudge-jeeber-2", requestId: requestId);
        var outcome = await offers.TryWithdrawAsync(
            seeded.Id, requestId, "nudge-jeeber-2", clock.GetUtcNow(), CancellationToken.None);
        outcome.Should().Be(JeebGateway.Availability.WithdrawOfferOutcome.Withdrawn);

        clock.Advance(TimeSpan.FromMinutes(11));
        await NudgeOnce(factory);

        var notifier = (InMemoryRequestExpiryNotifier)factory.Services.GetRequiredService<IRequestExpiryNotifier>();
        notifier.Nudges.Should().ContainSingle(
            "a retracted bid leaves the request offerless again, so the nudge is legitimate")
            .Which.RequestId.Should().Be(requestId);
    }

    [Fact]
    public async Task Nudge_Sent_When_Offers_Lookup_Throws()
    {
        var factory = NewFactory(out var clock, services =>
        {
            services.RemoveAll<JeebGateway.Availability.IPendingOffersStore>();
            services.AddSingleton<JeebGateway.Availability.IPendingOffersStore, ThrowingOffersStore>();
        });
        var client = ClientFor(factory, "nudge-offer-blip-client");

        var requestId = await CreateRequest(client, "Deliver a parcel");

        clock.Advance(TimeSpan.FromMinutes(11));
        await NudgeOnce(factory);

        var notifier = (InMemoryRequestExpiryNotifier)factory.Services.GetRequiredService<IRequestExpiryNotifier>();
        notifier.Nudges.Should().ContainSingle(
            "an offers-lookup blip must degrade to sending the nudge, never to silently swallowing it")
            .Which.RequestId.Should().Be(requestId);
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
        await NudgeOnce(factory);
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
        await NudgeOnce(factory);
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
        await NudgeOnce(factory);

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
        var offers = (JeebGateway.IntegrationTests.Fakes.FakePendingOffersStore)
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

        var offers = (JeebGateway.IntegrationTests.Fakes.FakePendingOffersStore)
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
        var sweeper = CreateSweeper(
            services,
            clock,
            new StaticSourceMonitor(new RequestExpirySourceOptions { Source = "gateway" }),
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
        var sweeper = CreateSweeper(
            services,
            clock,
            new StaticSourceMonitor(new RequestExpirySourceOptions { Source = "gateway" }),
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

    [Fact]
    public async Task Delivery_Service_Source_Disables_Legacy_Gateway_Expiry_Sweeper()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));
        var logger = new RecordingLogger<RequestExpirySweeper>();
        using var services = BuildSweeperServices(clock);
        var store = services.GetRequiredService<IRequestsStore>();
        var sweeper = CreateSweeper(
            services,
            clock,
            new StaticSourceMonitor(new RequestExpirySourceOptions { Source = "delivery-service" }),
            logger);
        var request = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = "expiry-delivery-service-authority",
            Description = "delivery service owns expiry",
            TierId = "flash",
            PickupLocation = new GeoPoint { Lat = 24.7136, Lng = 46.6753 },
            DropoffLocation = new GeoPoint { Lat = 24.6309, Lng = 46.7194 },
        }, CancellationToken.None);

        clock.Advance(TimeSpan.FromHours(1));
        await sweeper.SweepOnceAsync(CancellationToken.None);

        (await store.GetAsync(request.Id, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Pending);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(out FakeClock clock)
        => NewFactory(out clock, null);

    private static WebApplicationFactory<Program> NewFactory(
        out FakeClock clock,
        Action<IServiceCollection>? extraTestServices)
    {
        var theClock = new FakeClock(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));
        clock = theClock;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(theClock);
                services.AddSingleton<IDurableRequestsMirror, LocalExpiryAuthority>();
            });

            // GW3 / W3.5(c) deleted the gateway's in-memory offer store, so a bare
            // WebApplicationFactory now resolves IPendingOffersStore to the real
            // UpstreamPendingOffersStore (offer-service over HTTP). These expiry tests
            // assert on the offer ledger the sweeper closes, so the fixture must own it.
            // ConfigureTestServices (not ConfigureServices) so the swap lands after
            // Program.cs's own unconditional registration.
            builder.ConfigureTestServices(
                Fakes.FakeOfferStoreWebApplicationFactory.UseFakeOfferStore);

            if (extraTestServices is not null)
            {
                builder.ConfigureTestServices(extraTestServices);
            }
        });
    }

    private static ServiceProvider BuildSweeperServices(FakeClock clock)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<InMemoryRequestsStore>();
        services.AddSingleton<IRequestsStore>(sp => sp.GetRequiredService<InMemoryRequestsStore>());
        services.AddSingleton<IDurableRequestsMirror, LocalExpiryAuthority>();
        services.AddSingleton<InMemoryRequestExpiryNotifier>();
        services.AddSingleton<IRequestExpiryNotifier>(sp =>
            sp.GetRequiredService<InMemoryRequestExpiryNotifier>());
        services.AddSingleton<JeebGateway.IntegrationTests.Fakes.FakePendingOffersStore>();
        services.AddSingleton<JeebGateway.Availability.IPendingOffersStore>(sp =>
            sp.GetRequiredService<JeebGateway.IntegrationTests.Fakes.FakePendingOffersStore>());
        services.AddSingleton<JeebGateway.Tiers.ITiersStore, InMemoryTiersStore>();
        return services.BuildServiceProvider();
    }

    private static RequestExpirySweeper CreateSweeper(
        IServiceProvider services,
        FakeClock clock,
        StaticSourceMonitor source,
        RecordingLogger<RequestExpirySweeper> logger)
    {
        var delivery = new DeliveryServiceClient(new HttpClient(new UpstreamTiersHandler())
        {
            BaseAddress = new Uri("http://upstream-delivery.test/"),
        });
        var windows = new TierExpiryWindowResolver(
            new StaticFlagsMonitor(new UpstreamFeatureFlags { Delivery = true }),
            delivery,
            new RecordingLogger<TierExpiryWindowResolver>(logger.Messages));

        return new RequestExpirySweeper(
            services,
            clock,
            Options.Create(new RequestExpiryOptions()),
            windows,
            source,
            logger);
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

    private static Task NudgeOnce(WebApplicationFactory<Program> factory)
    {
        var sweeper = factory.Services
            .GetServices<IHostedService>()
            .OfType<RequestNudgeSweeper>()
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
        public RecordingLogger(ConcurrentQueue<string>? messages = null) =>
            Messages = messages ?? new ConcurrentQueue<string>();

        public ConcurrentQueue<string> Messages { get; }

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

    private sealed class StaticSourceMonitor : IOptionsMonitor<RequestExpirySourceOptions>
    {
        public StaticSourceMonitor(RequestExpirySourceOptions value) => CurrentValue = value;

        public RequestExpirySourceOptions CurrentValue { get; }

        public RequestExpirySourceOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<RequestExpirySourceOptions, string?> listener) => null;
    }

    /// <summary>
    /// Fast-test authority used only by this legacy HTTP/sizing suite. The real
    /// cross-replica guarantees are covered against PostgreSQL in
    /// PostgresRequestExpiryAuthorityTests.
    /// </summary>
    private sealed class LocalExpiryAuthority : IDurableRequestsMirror
    {
        private readonly InMemoryRequestsStore _rows;
        private readonly HashSet<string> _expired = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        public LocalExpiryAuthority(InMemoryRequestsStore rows) => _rows = rows;

        // GW5 / W1.6-gateway: durable reconcile candidates for the post-accept chat
        // settlement. This authority double is backed by a real InMemoryRequestsStore, so
        // it delegates rather than reporting a fake-empty page.
        public Task<IReadOnlyList<DeliveryRequest>> ListAssignedSinceAsync(
            DateTimeOffset since, int limit, CancellationToken ct)
            => _rows.ListAssignedSinceAsync(since, limit, ct);

        public Task<DeliveryRequest?> GetByConversationIdAsync(string conversationId, CancellationToken ct)
            => _rows.GetByConversationIdAsync(conversationId, ct);

        public Task UpdateConversationIdAsync(string requestId, string conversationId, CancellationToken ct)
            => _rows.SetConversationIdAsync(requestId, conversationId, ct);

        public async Task<bool> MarkExpiredAsync(
            string requestId,
            DateTimeOffset expiredAt,
            CancellationToken ct)
        {
            var row = await _rows.GetAsync(requestId, ct);
            if (row is null || !RequestStatus.IsPreAcceptance(row.Status))
            {
                return false;
            }

            lock (_lock)
            {
                return _expired.Add(requestId);
            }
        }

        public Task UpsertOnCreateAsync(DeliveryRequest row, CancellationToken ct) =>
            Task.CompletedTask;

        public Task MarkCancelledAsync(
            string requestId,
            string gwStatus,
            string? cancelledBy,
            string? cancellationReason,
            DateTimeOffset at,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task UpdateLifecycleAsync(
            string requestId,
            string? gwStatus,
            string? gwJeeberId,
            decimal? gwAcceptedFee,
            DateTimeOffset at,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(
            string clientId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DeliveryRequest>>(Array.Empty<DeliveryRequest>());

        public Task<IReadOnlyList<DeliveryRequest>> ListForJeeberAsync(
            string jeeberId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DeliveryRequest>>(Array.Empty<DeliveryRequest>());

        public Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct) =>
            Task.FromResult<DeliveryRequest?>(null);
    }

    /// <summary>
    /// Offer ledger that faults on every read — stands in for an offer-service blip so the
    /// nudge sweeper's degrade direction (still nudge) is asserted, not assumed.
    /// </summary>
    private sealed class ThrowingOffersStore : JeebGateway.Availability.IPendingOffersStore
    {
        private static InvalidOperationException Blip()
            => new("offer-service unavailable (test double)");

        public Task<IReadOnlyList<JeebGateway.Availability.PendingOffer>> ListForRequestAsync(
            string requestId, CancellationToken ct) => throw Blip();

        public Task<int> WithdrawForJeeberAsync(string jeeberId, CancellationToken ct) => throw Blip();

        public Task<JeebGateway.Availability.PendingOffer?> GetAsync(string offerId, CancellationToken ct)
            => throw Blip();

        public Task<bool> AcceptAsync(string offerId, DateTimeOffset at, CancellationToken ct)
            => throw Blip();

        public Task<JeebGateway.Availability.AcceptOfferOutcome> AcceptWithSupersedeAsync(
            string offerId, DateTimeOffset at, CancellationToken ct) => throw Blip();

        public Task<JeebGateway.Availability.EditOfferOutcome> TryEditAsync(
            string offerId, string requestId, string jeeberId, decimal? fee, int? etaMinutes,
            string? note, int maxEdits, DateTimeOffset at, CancellationToken ct) => throw Blip();

        public Task<JeebGateway.Availability.PendingOffer> TrySubmitAsync(
            string requestId, string jeeberId, decimal fee, int etaMinutes, string? note,
            int maxPerRequest, DateTimeOffset at, CancellationToken ct, string? clientId = null)
            => throw Blip();

        public Task<JeebGateway.Availability.WithdrawOfferOutcome> TryWithdrawAsync(
            string offerId, string requestId, string jeeberId, DateTimeOffset at, CancellationToken ct)
            => throw Blip();
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
