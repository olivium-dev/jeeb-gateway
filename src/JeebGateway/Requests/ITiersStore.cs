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
/// The unified implementation: delegates existence to the catalog
/// (<see cref="JeebGateway.Tiers.ITiersStore"/>) after canonicalizing legacy
/// codes through <see cref="JeebGateway.Tiers.LegacyTierCodes"/>. Because the
/// catalog is runtime-mutable (admin CRUD at <c>/admin/tiers</c>), tier changes
/// take effect on the next create with no gateway restart — and deleting a
/// catalog row genuinely retires its legacy aliases too.
/// </summary>
public class CatalogBackedTiersStore : ITiersStore
{
    private readonly JeebGateway.Tiers.ITiersStore _catalog;

    public CatalogBackedTiersStore(JeebGateway.Tiers.ITiersStore catalog)
    {
        _catalog = catalog;
    }

    public async Task<bool> ExistsAsync(string tierCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tierCode)) return false;

        var canonical = JeebGateway.Tiers.LegacyTierCodes.Canonicalize(tierCode);
        return await _catalog.GetAsync(canonical, ct) is not null;
    }
}
