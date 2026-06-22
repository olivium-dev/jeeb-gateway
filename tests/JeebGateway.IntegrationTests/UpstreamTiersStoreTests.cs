using System.Net;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// iter6 B2 (tier-contract): proves <see cref="UpstreamTiersStore"/> reconciles
/// the request-create tier namespace with the catalog GET /v1/tiers serves.
///
/// The live catalog (delivery-service, surfaced when
/// FeatureFlags:UseUpstream:Delivery is on) keys tiers by a UUID id + a name —
/// and mobile #64 now sends that UUID as the create-time tierId. The legacy
/// slug-only store rejected it ("tierId does not match any active delivery
/// tier"). These tests pin: the UUID, the name, AND the slugified name are all
/// accepted; the legacy slug codes still pass; and with the flag OFF the store
/// degrades to the slug allowlist (and never hits the network).
/// </summary>
public class UpstreamTiersStoreTests
{
    // The exact shape GET /v1/tiers returns live (curl http://localhost:10090/v1/tiers).
    private static readonly DeliveryTierDto FlashTier = new()
    {
        Id = "0be308ce-01b5-5cb9-a3e8-9adb60668d9c",
        Name = "Flash",
        SlaHours = 1,
        RadiusKm = 3,
        CommissionRate = 0.25,
        PriceHint = "Within 30 minutes",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static readonly DeliveryTierDto OnTheWayTier = new()
    {
        Id = "11111111-2222-3333-4444-555555555555",
        Name = "On-the-Way",
        SlaHours = 4,
        RadiusKm = 10,
        CommissionRate = 0.18,
        PriceHint = "Matched to nearby Jeebers",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static UpstreamTiersStore Make(bool deliveryFlag, IReadOnlyList<DeliveryTierDto>? catalog = null, bool throwOnList = false)
    {
        var fake = new TierCatalogDeliveryClient(catalog ?? new[] { FlashTier, OnTheWayTier }, throwOnList);
        var flags = Options.Create(new UpstreamFeatureFlags { Delivery = deliveryFlag });
        return new UpstreamTiersStore(fake, flags, NullLogger<UpstreamTiersStore>.Instance);
    }

    [Fact]
    public async Task Accepts_The_UUID_GetV1Tiers_Returns_When_Delivery_Upstream_On()
    {
        var store = Make(deliveryFlag: true);
        // The exact UUID GET /v1/tiers hands the mobile picker for Flash — the
        // value mobile #64 now POSTs as tierId, that the slug store rejected.
        (await store.ExistsAsync("0be308ce-01b5-5cb9-a3e8-9adb60668d9c", CancellationToken.None))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("Flash")]        // exact catalog name
    [InlineData("flash")]        // case-insensitive name (also a legacy slug)
    [InlineData("On-the-Way")]   // name with separators
    [InlineData("on-the-way")]   // slugified name
    public async Task Accepts_Name_And_Slugified_Name(string tierId)
    {
        var store = Make(deliveryFlag: true);
        (await store.ExistsAsync(tierId, CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [InlineData("flash")]
    [InlineData("express")]
    [InlineData("standard")]
    [InlineData("on_the_way")]
    [InlineData("eco")]
    public async Task Legacy_Slug_Codes_Still_Accepted_With_Flag_On(string slug)
    {
        var store = Make(deliveryFlag: true);
        (await store.ExistsAsync(slug, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Rejects_Unknown_Uuid()
    {
        var store = Make(deliveryFlag: true);
        (await store.ExistsAsync("deadbeef-0000-0000-0000-000000000000", CancellationToken.None))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("platinum_super_fast")]
    public async Task Rejects_Blank_And_Unknown(string tierId)
    {
        var store = Make(deliveryFlag: true);
        (await store.ExistsAsync(tierId, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Flag_Off_Uses_Slug_Allowlist_And_Never_Calls_Upstream()
    {
        var fake = new TierCatalogDeliveryClient(new[] { FlashTier }, throwOnList: true);
        var flags = Options.Create(new UpstreamFeatureFlags { Delivery = false });
        var store = new UpstreamTiersStore(fake, flags, NullLogger<UpstreamTiersStore>.Instance);

        // Slug still accepted...
        (await store.ExistsAsync("flash", CancellationToken.None)).Should().BeTrue();
        // ...the UUID is NOT (flag off => slug allowlist is the whole truth)...
        (await store.ExistsAsync("0be308ce-01b5-5cb9-a3e8-9adb60668d9c", CancellationToken.None))
            .Should().BeFalse();
        // ...and the upstream list route was never invoked (would have thrown).
        fake.ListTiersCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Catalog_Read_Failure_Fails_Soft_For_Slug()
    {
        // Upstream catalog read throws, but a legacy slug must still be accepted
        // (checked before the network) so a transient delivery-service hiccup
        // never blocks a slug-based create.
        var store = Make(deliveryFlag: true, throwOnList: true);
        (await store.ExistsAsync("flash", CancellationToken.None)).Should().BeTrue();
        // A UUID we cannot verify under the failure is denied, not 500.
        (await store.ExistsAsync("0be308ce-01b5-5cb9-a3e8-9adb60668d9c", CancellationToken.None))
            .Should().BeFalse();
    }

    /// <summary>
    /// Minimal <see cref="IDeliveryServiceClient"/> fake — only ListTiersAsync is
    /// meaningful; every other member is unused by UpstreamTiersStore.
    /// </summary>
    private sealed class TierCatalogDeliveryClient : IDeliveryServiceClient
    {
        private readonly IReadOnlyList<DeliveryTierDto> _tiers;
        private readonly bool _throw;
        public int ListTiersCallCount { get; private set; }

        public TierCatalogDeliveryClient(IReadOnlyList<DeliveryTierDto> tiers, bool throwOnList)
        {
            _tiers = tiers;
            _throw = throwOnList;
        }

        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct)
        {
            ListTiersCallCount++;
            if (_throw) throw new HttpRequestException("simulated delivery-service outage");
            return Task.FromResult(_tiers);
        }

        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotSupportedException();
    }
}
