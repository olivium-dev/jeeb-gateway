using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Tiers;

/// <summary>
/// The tier catalog AS THE CLIENT SAW IT, for policy reads (D2 radius, display name).
///
/// <para>Bug D2-b: the D2 evaluators resolved a tier through the gateway-LOCAL
/// <see cref="ITiersStore"/> (slug ids urgent/same-day/scheduled), while the read surface the
/// tier-picker rendered from — <c>GET /v1/tiers</c>
/// (<see cref="JeebGateway.Controllers.V1.JeebTiersController"/>) — short-circuits to
/// delivery-service whenever <c>FeatureFlags:UseUpstream:Delivery</c> is on and returns UUIDv5
/// ids. The app faithfully submits the UUID, the local store returns null, and every D2 branch
/// fail-closed on <c>UnknownTier</c>: no fan-out push, an empty feed, and a 409 on the offer
/// route. Two catalogs, one policy — this seam collapses them, exactly the way
/// <see cref="JeebGateway.Requests.CatalogBackedTiersStore"/> already collapses them for
/// create-time validation (same flag, same upstream call).</para>
/// </summary>
public interface ITierCatalogResolver
{
    /// <summary>
    /// One read of the effective catalog, so a page that evaluates many requests resolves
    /// every tier without re-reading upstream.
    /// </summary>
    Task<TierCatalogSnapshot> SnapshotAsync(CancellationToken ct);

    /// <summary>
    /// The tier a request's <c>tierId</c> refers to, or null when it resolves in NEITHER
    /// taxonomy — which every caller must treat as fail-closed (rule: unknown never allows).
    /// </summary>
    Task<DeliveryTier?> ResolveAsync(string? tierId, CancellationToken ct);
}

/// <summary>
/// How long a catalog read is reused, and how far past that a LAST-GOOD catalog may still be
/// served while the authoritative source is unreachable.
/// </summary>
public sealed class TierCatalogCacheOptions
{
    public const string SectionName = "TierCatalogCache";

    /// <summary>
    /// Reuse window for a good read. 30 s is the deliberate trade: an admin radius/TTL edit
    /// takes at most this long to reach the D2 evaluators, and in exchange the feed, the
    /// fan-out and the offer route stop making one upstream catalog call EACH, per request.
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far beyond <see cref="Ttl"/> the last good catalog is still served when the
    /// authoritative source cannot be read. Bounded on purpose: past it the catalog is treated
    /// as unknown again and the D2 cut resumes failing CLOSED rather than routing on a radius
    /// nobody can vouch for.
    /// </summary>
    public TimeSpan StaleGrace { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// An immutable read of the effective tier catalog plus the id/name/legacy-alias matching
/// rules. <see cref="Source"/> names which catalog answered, for the exclusion logs.
/// </summary>
public sealed class TierCatalogSnapshot
{
    public static readonly TierCatalogSnapshot Empty =
        new(Array.Empty<DeliveryTier>(), "none", authoritative: false);

    public TierCatalogSnapshot(
        IReadOnlyList<DeliveryTier> rows, string source, bool authoritative = true)
    {
        Rows = rows ?? Array.Empty<DeliveryTier>();
        Source = source;
        IsAuthoritative = authoritative;
    }

    public IReadOnlyList<DeliveryTier> Rows { get; }

    public string Source { get; }

    /// <summary>
    /// False when the source that OWNS the catalog did not answer — including the degrade to the
    /// gateway-local slug catalog while delivery-service is the authority, which cannot resolve
    /// an upstream UUID at all and so is not a substitute for it.
    /// </summary>
    public bool IsAuthoritative { get; }

    /// <summary>
    /// True when the authoritative catalog answered with rows. Distinguishes "this tier id is
    /// garbage" (fail closed on ONE request) from "the catalog itself is unreadable" (fail
    /// closed on EVERYTHING) — two causes that used to log the identical <c>UnknownTier</c>.
    /// </summary>
    public bool IsAvailable => IsAuthoritative && Rows.Count > 0;

    /// <summary>
    /// Match order: exact id, then exact name, then the
    /// <see cref="LegacyTierCodes"/> canonical form of both. The name rung is what makes an
    /// upstream UUID row reachable from a legacy code (upstream names Flash/Express/Standard
    /// align 1:1 with the legacy codes) — the same rungs, in the same order, as
    /// <see cref="JeebGateway.Requests.CatalogBackedTiersStore.ResolveAsync"/>, so the tier a
    /// create validated against and the tier the radius is read from cannot disagree.
    /// </summary>
    public DeliveryTier? Resolve(string? tierId)
    {
        if (string.IsNullOrWhiteSpace(tierId) || Rows.Count == 0)
        {
            return null;
        }

        var trimmed = tierId.Trim();

        var byId = Rows.FirstOrDefault(t =>
            string.Equals(t.Id, trimmed, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId;
        }

        var byName = Rows.FirstOrDefault(t =>
            string.Equals(t.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        var canonical = LegacyTierCodes.Canonicalize(trimmed);
        return Rows
            .Where(t =>
                string.Equals(LegacyTierCodes.Canonicalize(t.Name), canonical, StringComparison.OrdinalIgnoreCase)
                || string.Equals(LegacyTierCodes.Canonicalize(t.Id), canonical, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.SlaHours)
            .ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}

/// <summary>
/// Reads the catalog from whichever source is authoritative for this environment, branching on
/// the SAME <c>FeatureFlags:UseUpstream:Delivery</c> flag the read surface branches on. Upstream
/// on ⇒ delivery-service's rows (ids, names and RADII verbatim: Flash 3 km / Express 10 km /
/// Standard 25 km). Upstream off — or an upstream read that faults — ⇒ the gateway-local
/// catalog, which is the pre-existing behaviour. A tier that matches neither still resolves to
/// null, so genuine garbage remains fail-closed.
///
/// <para>The read is CACHED for <see cref="TierCatalogCacheOptions.Ttl"/> behind a single-flight
/// gate. Uncached, the three D2 evaluators made one upstream catalog call EACH per request, and
/// any delivery-service blip therefore silently emptied a live feed. A last-good catalog is
/// served for a bounded <see cref="TierCatalogCacheOptions.StaleGrace"/> past the TTL when the
/// authoritative source cannot be read; past that the catalog reads unavailable and the D2 cut
/// fails CLOSED again. Only an AUTHORITATIVE read refreshes the cache — a degraded read (the
/// local slug catalog while upstream is the authority) is never cached, because it cannot
/// resolve the UUIDs the app submits and would pin the D2-b failure in place.</para>
/// </summary>
public sealed class TierCatalogResolver : ITierCatalogResolver
{
    private readonly ITiersStore _catalog;
    private readonly IDeliveryServiceClient? _upstream;
    private readonly IOptionsMonitor<UpstreamFeatureFlags>? _flags;
    private readonly ILogger<TierCatalogResolver>? _logger;
    private readonly TierCatalogCacheOptions _cache;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private CachedCatalog? _cached;

    public TierCatalogResolver(
        ITiersStore catalog,
        IDeliveryServiceClient? upstream,
        IOptionsMonitor<UpstreamFeatureFlags>? flags,
        ILogger<TierCatalogResolver>? logger = null,
        TierCatalogCacheOptions? cache = null,
        TimeProvider? clock = null)
    {
        _catalog = catalog;
        _upstream = upstream;
        _flags = flags;
        _logger = logger;
        _cache = cache ?? new TierCatalogCacheOptions();
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Local-catalog-only resolver, for tests and flag-less call sites.</summary>
    public TierCatalogResolver(ITiersStore catalog)
        : this(catalog, null, null, null)
    {
    }

    public async Task<TierCatalogSnapshot> SnapshotAsync(CancellationToken ct)
    {
        var cached = _cached;
        if (cached is not null && _clock.GetUtcNow() < cached.ServeUntil)
        {
            return cached.Snapshot;
        }

        await _refreshGate.WaitAsync(ct);
        try
        {
            var now = _clock.GetUtcNow();
            cached = _cached;
            if (cached is not null && now < cached.ServeUntil)
            {
                return cached.Snapshot;
            }

            var snapshot = await ReadEffectiveAsync(ct);
            if (snapshot.IsAvailable)
            {
                _cached = new CachedCatalog(snapshot, now, now + _cache.Ttl);
                return snapshot;
            }

            if (cached is not null
                && cached.Snapshot.IsAvailable
                && now - cached.ReadAt <= _cache.Ttl + _cache.StaleGrace)
            {
                // ReadAt is NOT refreshed, so the grace keeps shrinking and a sustained outage
                // still reaches fail-closed; ServeUntil is, so retries stay one per TTL.
                _cached = cached with { ServeUntil = now + _cache.Ttl };
                _logger?.LogWarning(
                    "event={Event} source={Source} rows={Rows} ageSeconds={AgeSeconds} "
                    + "graceSecondsLeft={GraceSecondsLeft}",
                    "tier-catalog.stale-serve", cached.Snapshot.Source, cached.Snapshot.Rows.Count,
                    (int)(now - cached.ReadAt).TotalSeconds,
                    (int)((cached.ReadAt + _cache.Ttl + _cache.StaleGrace) - now).TotalSeconds);
                return cached.Snapshot;
            }

            return snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private sealed record CachedCatalog(
        TierCatalogSnapshot Snapshot, DateTimeOffset ReadAt, DateTimeOffset ServeUntil);

    /// <summary>
    /// One read of the effective catalog. The flag decides which source is AUTHORITATIVE;
    /// anything else that answers is a degrade, reported as such so the caller can prefer a
    /// recent authoritative snapshot over it.
    /// </summary>
    private async Task<TierCatalogSnapshot> ReadEffectiveAsync(CancellationToken ct)
    {
        var upstreamIsAuthority = _upstream is not null && _flags?.CurrentValue.Delivery == true;

        if (upstreamIsAuthority)
        {
            try
            {
                var upstreamRows = await _upstream!.ListTiersAsync(ct);
                if (upstreamRows is { Count: > 0 })
                {
                    return new TierCatalogSnapshot(
                        upstreamRows.Select(FromDto).ToArray(), "delivery-upstream");
                }

                _logger?.LogWarning(
                    "tier catalog upstream returned no rows; falling back to the gateway-local catalog.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Degrade to the local catalog: it cannot resolve an upstream UUID, so the
                // caller still fails closed rather than allowing an unbounded radius.
                _logger?.LogWarning(ex,
                    "tier catalog upstream read failed; falling back to the gateway-local catalog.");
            }
        }

        try
        {
            // While upstream is the authority this is a DEGRADE, not an answer: it carries slug
            // ids and cannot resolve the UUIDs the app submits.
            return new TierCatalogSnapshot(
                await _catalog.ListAsync(ct),
                upstreamIsAuthority ? "gateway-local-degraded" : "gateway-local",
                authoritative: !upstreamIsAuthority);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "tier catalog read failed; every tier resolves unknown.");
            return TierCatalogSnapshot.Empty;
        }
    }

    public async Task<DeliveryTier?> ResolveAsync(string? tierId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tierId))
        {
            return null;
        }

        return (await SnapshotAsync(ct)).Resolve(tierId);
    }

    private static DeliveryTier FromDto(DeliveryTierDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        SlaHours = dto.SlaHours,
        RadiusKm = dto.RadiusKm,
        RequestTtlSeconds = dto.RequestTtlSeconds,
        CommissionRate = dto.CommissionRate,
        PriceHint = dto.PriceHint,
        CreatedAt = dto.CreatedAt,
        UpdatedAt = dto.UpdatedAt,
    };
}
