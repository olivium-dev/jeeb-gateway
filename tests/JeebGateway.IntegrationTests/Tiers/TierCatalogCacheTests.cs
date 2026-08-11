using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Geo;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Tiers;

/// <summary>
/// The short-TTL tier-catalog cache. Two problems it answers:
///
/// <list type="number">
///   <item>COST — the feed, the fan-out and the offer route each resolved the tier through their
///     own uncached catalog read, so one client request cost three delivery-service round-trips
///     on the hot path.</item>
///   <item>AVAILABILITY — a delivery-service catalog blip resolved every UUID tier to null, and
///     the D2 cut correctly failed CLOSED on all of them: a silently EMPTY feed for every jeeber
///     for the duration of the blip. Degrading to the gateway-local slug catalog does not help,
///     because it cannot resolve an upstream UUID at all.</item>
/// </list>
///
/// <para>The bound is deliberate on both ends: a good read is reused for
/// <see cref="TierCatalogCacheOptions.Ttl"/>, and a LAST-GOOD catalog is served only while the
/// upstream is unreadable and only within <see cref="TierCatalogCacheOptions.StaleGrace"/>.
/// Past that the catalog is unknown again and D2 fails closed — a stale radius must not route
/// deliveries indefinitely.</para>
/// </summary>
public sealed class TierCatalogCacheTests
{
    private const string FlashId = "0be308ce-01b5-5cb9-a3e8-9adb60668d9c";
    private const string StandardId = "2bd0d5df-db76-5d14-9e4d-741d60b2fa12";

    private static readonly TierCatalogCacheOptions Cache = new()
    {
        Ttl = TimeSpan.FromSeconds(30),
        StaleGrace = TimeSpan.FromMinutes(5),
    };

    [Fact]
    public async Task ManyResolves_CostOneUpstreamRead_WithinTheTtl()
    {
        var upstream = new CountingCatalogClient();
        var resolver = Resolver(upstream, out _);

        for (var i = 0; i < 25; i++)
        {
            (await resolver.ResolveAsync(StandardId, CancellationToken.None)).Should().NotBeNull();
        }

        upstream.Reads.Should().Be(1);
    }

    [Fact]
    public async Task TheCatalog_IsReReadAfterTheTtl()
    {
        var upstream = new CountingCatalogClient();
        var resolver = Resolver(upstream, out var clock);

        await resolver.SnapshotAsync(CancellationToken.None);
        clock.Advance(Cache.Ttl + TimeSpan.FromSeconds(1));
        await resolver.SnapshotAsync(CancellationToken.None);

        upstream.Reads.Should().Be(2, "a 30 s TTL is the cost of picking up an admin radius edit");
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneUpstreamRead()
    {
        var upstream = new CountingCatalogClient { LatencyGate = new SemaphoreSlim(0, 1) };
        var resolver = Resolver(upstream, out _);

        var callers = Enumerable.Range(0, 8)
            .Select(_ => resolver.SnapshotAsync(CancellationToken.None))
            .ToArray();

        upstream.LatencyGate!.Release();
        await Task.WhenAll(callers);

        upstream.Reads.Should().Be(1, "the refresh is single-flighted, so a burst is not a stampede");
        callers.Should().OnlyContain(c => c.Result.Rows.Count == 3);
    }

    [Fact]
    public async Task AnUpstreamBlip_ServesTheLastGoodCatalog_WithinTheGraceWindow()
    {
        var upstream = new CountingCatalogClient();
        var resolver = Resolver(upstream, out var clock);

        await resolver.SnapshotAsync(CancellationToken.None);
        upstream.Faulting = true;
        clock.Advance(Cache.Ttl + TimeSpan.FromSeconds(1));

        var tier = await resolver.ResolveAsync(StandardId, CancellationToken.None);

        tier.Should().NotBeNull("a brief catalog outage must not blank every feed");
        tier!.RadiusKm.Should().Be(25.0);
    }

    [Fact]
    public async Task TheStaleWindow_DoesNotRenewItself()
    {
        // The grace is measured from the last GOOD read, so a sustained outage always reaches
        // fail-closed — a stale radius can never keep routing forever.
        var upstream = new CountingCatalogClient();
        var resolver = Resolver(upstream, out var clock);

        await resolver.SnapshotAsync(CancellationToken.None);
        upstream.Faulting = true;

        for (var i = 0; i < 8; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await resolver.SnapshotAsync(CancellationToken.None);
        }

        var snapshot = await resolver.SnapshotAsync(CancellationToken.None);

        snapshot.IsAvailable.Should().BeFalse();
        TierRadiusPolicy
            .Evaluate(33.51, 36.27, new JeebGateway.Requests.GeoPoint { Lat = 33.51, Lng = 36.27 },
                snapshot.Resolve(StandardId), snapshot.IsAvailable)
            .Decision.Should().Be(TierRadiusDecision.TierCatalogUnavailable);
    }

    [Fact]
    public async Task AStaleServeCannotPushTheCachePastTheGraceEnd()
    {
        // The stale serve refreshes ServeUntil so an outage costs one upstream retry per TTL —
        // clamped to the grace end, or a serve at grace-minus-10s would keep the stale radius
        // alive for a further TTL through the fast path.
        var upstream = new CountingCatalogClient();
        var resolver = Resolver(upstream, out var clock);

        await resolver.SnapshotAsync(CancellationToken.None);
        upstream.Faulting = true;

        // Inside the grace (0:00 + 30 s TTL + 5 min = 5:30 grace end), but only just.
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(20));
        (await resolver.SnapshotAsync(CancellationToken.None)).IsAvailable.Should().BeTrue();

        // Now past the grace end but still inside the TTL a naive refresh would have granted.
        clock.Advance(TimeSpan.FromSeconds(20));

        (await resolver.SnapshotAsync(CancellationToken.None)).Resolve(StandardId).Should().BeNull(
            "the grace end is absolute — a stale serve may not extend it");
    }

    [Fact]
    public async Task AnOutageWithNoCachedCatalog_FailsClosedImmediately()
    {
        var upstream = new CountingCatalogClient { Faulting = true };
        var resolver = Resolver(upstream, out _);

        var snapshot = await resolver.SnapshotAsync(CancellationToken.None);

        // Degrading to the gateway-local slug catalog is NOT a resolution for an upstream UUID.
        snapshot.Resolve(FlashId).Should().BeNull();
    }

    [Fact]
    public async Task ADegradedLocalRead_IsNeverCachedOverAGoodUpstreamOne()
    {
        // Caching the local slug catalog while upstream is the authority would pin the D2-b
        // failure in place for a whole TTL after the upstream recovered.
        var upstream = new CountingCatalogClient { Faulting = true };
        var resolver = Resolver(upstream, out var clock);

        var degraded = await resolver.SnapshotAsync(CancellationToken.None);
        degraded.Source.Should().Be("gateway-local-degraded");
        degraded.IsAvailable.Should().BeFalse();

        upstream.Faulting = false;
        clock.Advance(TimeSpan.FromSeconds(1));

        (await resolver.SnapshotAsync(CancellationToken.None)).Source.Should().Be("delivery-upstream");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static ITierCatalogResolver Resolver(
        CountingCatalogClient upstream, out ManualClock clock)
    {
        clock = new ManualClock();
        return new TierCatalogResolver(
            new InMemoryTiersStore(),
            upstream,
            new StaticFlags(new UpstreamFeatureFlags { Delivery = true }),
            NullLogger<TierCatalogResolver>.Instance,
            Cache,
            clock);
    }

    private sealed class CountingCatalogClient : FakeDeliveryPresenceClient
    {
        private int _reads;

        public bool Faulting { get; set; }

        public SemaphoreSlim? LatencyGate { get; init; }

        public int Reads => Volatile.Read(ref _reads);

        public override async Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);

            if (LatencyGate is not null)
            {
                await LatencyGate.WaitAsync(ct);
            }

            if (Faulting)
            {
                throw new HttpRequestException("delivery-service unreachable");
            }

            return new[]
            {
                Row(FlashId, "Flash", 1, 3.0, 1800),
                Row("efe0629b-0b50-555c-b182-4bd41fcd6507", "Express", 2, 10.0, 7200),
                Row(StandardId, "Standard", 24, 25.0, 86400),
            };
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

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class StaticFlags : IOptionsMonitor<UpstreamFeatureFlags>
    {
        public StaticFlags(UpstreamFeatureFlags value) => CurrentValue = value;
        public UpstreamFeatureFlags CurrentValue { get; }
        public UpstreamFeatureFlags Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<UpstreamFeatureFlags, string?> listener) => null;
    }
}
