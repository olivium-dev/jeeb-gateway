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
/// An immutable read of the effective tier catalog plus the id/name/legacy-alias matching
/// rules. <see cref="Source"/> names which catalog answered, for the exclusion logs.
/// </summary>
public sealed class TierCatalogSnapshot
{
    public static readonly TierCatalogSnapshot Empty =
        new(Array.Empty<DeliveryTier>(), "none");

    public TierCatalogSnapshot(IReadOnlyList<DeliveryTier> rows, string source)
    {
        Rows = rows ?? Array.Empty<DeliveryTier>();
        Source = source;
    }

    public IReadOnlyList<DeliveryTier> Rows { get; }

    public string Source { get; }

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
/// </summary>
public sealed class TierCatalogResolver : ITierCatalogResolver
{
    private readonly ITiersStore _catalog;
    private readonly IDeliveryServiceClient? _upstream;
    private readonly IOptionsMonitor<UpstreamFeatureFlags>? _flags;
    private readonly ILogger<TierCatalogResolver>? _logger;

    public TierCatalogResolver(
        ITiersStore catalog,
        IDeliveryServiceClient? upstream,
        IOptionsMonitor<UpstreamFeatureFlags>? flags,
        ILogger<TierCatalogResolver>? logger = null)
    {
        _catalog = catalog;
        _upstream = upstream;
        _flags = flags;
        _logger = logger;
    }

    /// <summary>Local-catalog-only resolver, for tests and flag-less call sites.</summary>
    public TierCatalogResolver(ITiersStore catalog)
        : this(catalog, null, null, null)
    {
    }

    public async Task<TierCatalogSnapshot> SnapshotAsync(CancellationToken ct)
    {
        if (_upstream is not null && _flags?.CurrentValue.Delivery == true)
        {
            try
            {
                var upstreamRows = await _upstream.ListTiersAsync(ct);
                if (upstreamRows is { Count: > 0 })
                {
                    return new TierCatalogSnapshot(
                        upstreamRows.Select(FromDto).ToArray(), "delivery-upstream");
                }
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
            return new TierCatalogSnapshot(await _catalog.ListAsync(ct), "gateway-local");
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
