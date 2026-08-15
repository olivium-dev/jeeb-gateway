using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Geo;
using JeebGateway.Notifications;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Tiers;

/// <summary>
/// Bug D2-b — the tier-id taxonomy split that made the D2 cut fail closed on EVERY live
/// request. <c>GET /v1/tiers</c> short-circuits to delivery-service when
/// <c>FeatureFlags:UseUpstream:Delivery</c> is on, so the app submits a UUIDv5 tier id, while
/// the D2 evaluators looked that id up in the gateway-LOCAL slug catalog
/// (urgent/same-day/scheduled) and got null ⇒ <c>UnknownTier</c> ⇒ no fan-out push, an empty
/// feed and a 409 on the offer route. These tests pin the resolution against the SAME catalog
/// the picker rendered from, and pin that genuinely-unknown input still fails closed.
/// </summary>
public sealed class TierCatalogResolverTests
{
    // The live delivery-service catalog, verbatim (ids + radii from GET /tiers on MSI).
    private const string FlashId = "0be308ce-01b5-5cb9-a3e8-9adb60668d9c";
    private const string ExpressId = "efe0629b-0b50-555c-b182-4bd41fcd6507";
    private const string StandardId = "2bd0d5df-db76-5d14-9e4d-741d60b2fa12";

    private const double PickupLat = 33.88;
    private const double PickupLng = 35.50;

    // ── Resolution ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FlashId, "Flash", 3.0)]
    [InlineData(ExpressId, "Express", 10.0)]
    [InlineData(StandardId, "Standard", 25.0)]
    public async Task UpstreamGuidTierId_Resolves_ToTheUpstreamRadius(
        string tierId, string expectedName, double expectedRadiusKm)
    {
        var resolver = UpstreamResolver();

        var tier = await resolver.ResolveAsync(tierId, CancellationToken.None);

        tier.Should().NotBeNull("the id the mobile tier-picker submits is the id GET /v1/tiers served");
        tier!.Name.Should().Be(expectedName);
        tier.RadiusKm.Should().Be(expectedRadiusKm);
    }

    [Theory]
    [InlineData("flash", "Flash", 3.0)]
    [InlineData("express", "Express", 10.0)]
    [InlineData("standard", "Standard", 25.0)]
    public async Task LegacyCode_Resolves_AgainstTheUpstreamCatalog(
        string tierCode, string expectedName, double expectedRadiusKm)
    {
        var resolver = UpstreamResolver();

        var tier = await resolver.ResolveAsync(tierCode, CancellationToken.None);

        tier.Should().NotBeNull();
        tier!.Name.Should().Be(expectedName);
        tier.RadiusKm.Should().Be(expectedRadiusKm);
    }

    [Theory]
    // Catalog slugs and legacy codes keep resolving EXACTLY as before on the local catalog.
    [InlineData("urgent", 3.0)]
    [InlineData("same-day", 10.0)]
    [InlineData("scheduled", 25.0)]
    [InlineData("flash", 3.0)]
    [InlineData("express", 3.0)]
    [InlineData("standard", 10.0)]
    [InlineData("on_the_way", 10.0)]
    [InlineData("eco", 25.0)]
    public async Task SlugTierId_Resolves_AgainstTheLocalCatalog_WhenUpstreamIsOff(
        string tierId, double expectedRadiusKm)
    {
        var resolver = LocalResolver();

        var tier = await resolver.ResolveAsync(tierId, CancellationToken.None);

        tier.Should().NotBeNull();
        tier!.RadiusKm.Should().Be(expectedRadiusKm);
    }

    [Theory]
    [InlineData("platinum_super_fast")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    [InlineData(null)]
    public async Task GenuinelyUnknownTier_StillResolvesToNull_OnBothBranches(string? tierId)
    {
        (await UpstreamResolver().ResolveAsync(tierId, CancellationToken.None)).Should().BeNull();
        (await LocalResolver().ResolveAsync(tierId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task UpstreamReadFault_FailsClosed_ForAnUpstreamOnlyTierId()
    {
        // Degrading to the local catalog must never turn into "allow": a UUID matches no local
        // slug, so the evaluator still excludes rather than assuming a radius.
        var resolver = new TierCatalogResolver(
            new InMemoryTiersStore(),
            new ThrowingDeliveryClient(),
            new StaticFlagsMonitor(new UpstreamFeatureFlags { Delivery = true }),
            NullLogger<TierCatalogResolver>.Instance);

        var tier = await resolver.ResolveAsync(StandardId, CancellationToken.None);

        tier.Should().BeNull();
        TierRadiusPolicy.Evaluate(PickupLat, PickupLng, Point(PickupLat, PickupLng), tier)
            .Decision.Should().Be(TierRadiusDecision.UnknownTier);
    }

    // ── The resolved tier through the D2 evaluator ────────────────────────────

    [Fact]
    public async Task GuidTier_JeeberInsideTheRadius_IsIncluded_WithARealDistance()
    {
        var tier = await UpstreamResolver().ResolveAsync(FlashId, CancellationToken.None);

        // ~1.1 km north of the pickup point: inside Flash's 3 km.
        var result = TierRadiusPolicy.Evaluate(
            PickupLat + 0.01, PickupLng, Point(PickupLat, PickupLng), tier);

        result.Decision.Should().Be(TierRadiusDecision.Included);
        result.DistanceMeters.Should().NotBeNull().And.BeInRange(1_000, 1_300);
    }

    [Fact]
    public async Task GuidTier_JeeberOutsideTheRadius_IsStillExcluded()
    {
        // D2 semantics preserved: ~5.5 km away is outside Flash's 3 km radius.
        var tier = await UpstreamResolver().ResolveAsync(FlashId, CancellationToken.None);

        var result = TierRadiusPolicy.Evaluate(
            PickupLat + 0.05, PickupLng, Point(PickupLat, PickupLng), tier);

        result.Decision.Should().Be(TierRadiusDecision.OutOfRadius);
        result.DistanceMeters.Should().NotBeNull().And.BeGreaterThan(5_000);
    }

    [Fact]
    public async Task GuidTier_WithNoJeeberFix_StillFailsClosed()
    {
        var tier = await UpstreamResolver().ResolveAsync(StandardId, CancellationToken.None);

        TierRadiusPolicy.Evaluate(null, null, Point(PickupLat, PickupLng), tier)
            .Decision.Should().Be(TierRadiusDecision.NoJeeberFix);
    }

    // ── The fan-out, end to end (the live symptom) ────────────────────────────

    [Fact]
    public async Task Fanout_WithAGuidTier_ReachesTheInRangeJeeber()
    {
        // THE live regression: every fan-out logged "geo-unresolvable … sending to NOBODY"
        // because the GUID tier resolved to no radius at all.
        var push = new RecordingPushClient();
        var logger = new CapturingLogger<NewRequestPushNotifier>();
        var notifier = Notifier(push, logger, P1Fanout.Jeeber("jeeberA", PickupLat + 0.01, PickupLng));

        await notifier.FanOutAsync(
            new NewRequestNotification("req-guid", StandardId, "Deliver a parcel", "client-1", PickupLat, PickupLng),
            CancellationToken.None);

        push.RecipientIds.Should().ContainSingle().Which.Should().Be("jeeberA");
        logger.HasAny("geo-unresolvable").Should().BeFalse("the tier now resolves to a real radius");
        var payload = (IDictionary<string, object?>)push.UserSends.Single().Payload;
        ((string)payload["body"]!).Should().EndWith(" • Standard");
    }

    [Fact]
    public async Task Fanout_WithAGuidTier_StillSendsToNobody_WhenEveryJeeberIsOutOfRange()
    {
        // Fail-closed is preserved — but for the RIGHT reason (a computed distance), which the
        // log now distinguishes from an unresolvable tier.
        var push = new RecordingPushClient();
        var logger = new CapturingLogger<NewRequestPushNotifier>();
        var notifier = Notifier(push, logger, P1Fanout.Jeeber("jeeberFar", PickupLat + 0.5, PickupLng));

        await notifier.FanOutAsync(
            new NewRequestNotification("req-far", FlashId, "Deliver a parcel", "client-1", PickupLat, PickupLng),
            CancellationToken.None);

        push.UserSends.Should().BeEmpty();
        logger.HasAny("geo-filter-emptied").Should().BeTrue();
        logger.HasAny("geo-unresolvable").Should().BeFalse();
    }

    [Fact]
    public async Task Fanout_WithAnUnknownTier_StillSendsToNobody()
    {
        var push = new RecordingPushClient();
        var logger = new CapturingLogger<NewRequestPushNotifier>();
        var notifier = Notifier(push, logger, P1Fanout.Jeeber("jeeberA", PickupLat, PickupLng));

        await notifier.FanOutAsync(
            new NewRequestNotification("req-unknown", "platinum_super_fast", "Deliver", "client-1", PickupLat, PickupLng),
            CancellationToken.None);

        push.UserSends.Should().BeEmpty();
        logger.HasAny("geo-unresolvable").Should().BeTrue();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static GeoPoint Point(double lat, double lng) => new() { Lat = lat, Lng = lng };

    private static ITierCatalogResolver UpstreamResolver()
        => new TierCatalogResolver(
            new InMemoryTiersStore(),
            new DeliveryServiceClient(new HttpClient(new LiveTierCatalogHandler())
            {
                BaseAddress = new Uri("http://upstream-delivery.test/")
            }),
            new StaticFlagsMonitor(new UpstreamFeatureFlags { Delivery = true }),
            NullLogger<TierCatalogResolver>.Instance);

    private static ITierCatalogResolver LocalResolver()
        => new TierCatalogResolver(new InMemoryTiersStore());

    private static NewRequestPushNotifier Notifier(
        RecordingPushClient push,
        ILogger<NewRequestPushNotifier> logger,
        JeebGateway.Availability.JeeberAvailability online)
        => new(
            push,
            UpstreamResolver(),
            logger,
            new FakeAvailabilityStore { Online = new[] { online } },
            new FakeUsersStore(),
            new RecordingFanoutQueue(),
            // No RadiusKm override: the radius must come from the request's OWN tier.
            Options.Create(new NewRequestFanoutOptions { FallbackToKnownJeebers = false }),
            TimeProvider.System);

    /// <summary>Serves the LIVE delivery-service tier catalog at <c>GET /tiers</c>.</summary>
    private sealed class LiveTierCatalogHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var tiers = new[]
            {
                Row(FlashId, "Flash", 1, 3.0, 1800),
                Row(ExpressId, "Express", 2, 10.0, 7200),
                Row(StandardId, "Standard", 24, 25.0, 86400),
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(tiers, Json), Encoding.UTF8, "application/json"),
            });
        }

        private static DeliveryTierDto Row(string id, string name, int sla, double radiusKm, int ttl) => new()
        {
            Id = id,
            Name = name,
            SlaHours = sla,
            RadiusKm = radiusKm,
            RequestTtlSeconds = ttl,
            CommissionRate = 0.10,
            PriceHint = name,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
    }

    private sealed class ThrowingDeliveryClient : FakeDeliveryPresenceClient
    {
        public override Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct)
            => throw new HttpRequestException("delivery-service unreachable");
    }

    private sealed class StaticFlagsMonitor : IOptionsMonitor<UpstreamFeatureFlags>
    {
        public StaticFlagsMonitor(UpstreamFeatureFlags value) => CurrentValue = value;
        public UpstreamFeatureFlags CurrentValue { get; }
        public UpstreamFeatureFlags Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<UpstreamFeatureFlags, string?> listener) => null;
    }
}
