using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Options;

namespace JeebGateway.Requests;

/// <summary>
/// Create-time tier-existence probe (FR-4.1 / T-backend-007's "validate tier
/// exists" acceptance criterion), consumed by the request-create surfaces
/// (legacy JSON, voice multipart, and the V1 BFF path).
///
/// <para>feat/tier-unify-names: this seam is now a THIN view over the single
/// tier source of truth — the runtime-mutable catalog at
/// <see cref="JeebGateway.Tiers.ITiersStore"/> (the same store served at
/// <c>GET /v1/tiers</c> and used by <c>NewRequestPushNotifier</c> for display
/// names). The former parallel static set ({flash, express, standard,
/// on_the_way, eco} from db/migrations/0011) is retired; those legacy codes
/// remain ACCEPTED via the <see cref="JeebGateway.Tiers.LegacyTierCodes"/>
/// alias table so old clients don't break.</para>
/// </summary>
public interface ITiersStore
{
    /// <summary>
    /// Returns true when <paramref name="tierCode"/> resolves to an active
    /// catalog tier — either directly (a catalog id such as <c>urgent</c>)
    /// or via the legacy alias table (<c>flash</c> → <c>urgent</c>, …).
    /// Lookups are case-insensitive, matching the catalog store's id
    /// semantics; blank input is false.
    /// </summary>
    Task<bool> ExistsAsync(string tierCode, CancellationToken ct);
}

/// <summary>
/// The unified implementation: delegates existence to whichever tier source is
/// authoritative for the current environment, branching on the SAME flag the
/// read path (<see cref="JeebGateway.Controllers.V1.JeebTiersController"/>)
/// branches on — <c>FeatureFlags:UseUpstream:Delivery</c>.
///
/// <para><b>Delivery upstream ON.</b> The create-time probe validates against the
/// EXACT list the read surface serves: <see cref="IDeliveryServiceClient.ListTiersAsync"/>
/// (delivery-service <c>GET /api/v1/tiers</c>). This is the list the mobile
/// tier-picker rendered from, so the UUIDv5 id the app faithfully submits (e.g.
/// Standard = <c>2bd0d5df-db76-5d14-9e4d-741d60b2fa12</c>) resolves. A tierId is
/// valid iff it matches (case-insensitive) an upstream tier id. <b>This closes the
/// P0</b>: before this branch existed the probe consulted ONLY the gateway-local
/// slug catalog (urgent/same-day/…) and 400'd every upstream id → no customer could
/// create a request while Delivery upstream was live.</para>
///
/// <para><b>Delivery upstream OFF.</b> Unchanged legacy behavior — existence is
/// delegated to the runtime-mutable gateway-local catalog
/// (<see cref="JeebGateway.Tiers.ITiersStore"/>) after canonicalizing legacy codes
/// through <see cref="JeebGateway.Tiers.LegacyTierCodes"/>. Admin CRUD at
/// <c>/admin/tiers</c> takes effect on the next create with no gateway restart, and
/// deleting a catalog row genuinely retires its legacy aliases too.</para>
///
/// <para>The upstream branch is read live per call via
/// <see cref="IOptionsMonitor{TOptions}"/> (this store is a process-lifetime
/// singleton, so a snapshot captured at construction would freeze the flag). The
/// injected <see cref="IDeliveryServiceClient"/> mirrors the established singleton
/// consumption precedent in <c>DurableRequestsStore</c>.</para>
/// </summary>
public class CatalogBackedTiersStore : ITiersStore
{
    private readonly JeebGateway.Tiers.ITiersStore _catalog;
    private readonly IDeliveryServiceClient _upstream;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;

    public CatalogBackedTiersStore(
        JeebGateway.Tiers.ITiersStore catalog,
        IDeliveryServiceClient upstream,
        IOptionsMonitor<UpstreamFeatureFlags> flags)
    {
        _catalog = catalog;
        _upstream = upstream;
        _flags = flags;
    }

    public async Task<bool> ExistsAsync(string tierCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tierCode)) return false;

        // Consistency with the read path (JeebTiersController.List): when
        // delivery-service is the authoritative tier source, a supplied tierId is
        // valid iff it is an id in the SAME upstream catalog the tier-picker
        // rendered from. Same flag, same upstream call — no divergence.
        if (_flags.CurrentValue.Delivery)
        {
            var trimmed = tierCode.Trim();
            var upstreamTiers = await _upstream.ListTiersAsync(ct);
            return upstreamTiers.Any(t =>
                string.Equals(t.Id, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        // Delivery upstream off: local catalog + LegacyTierCodes canonicalization.
        var canonical = JeebGateway.Tiers.LegacyTierCodes.Canonicalize(tierCode);
        return await _catalog.GetAsync(canonical, ct) is not null;
    }
}
