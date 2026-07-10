namespace JeebGateway.Tiers;

/// <summary>
/// feat/tier-unify-names — the single legacy→catalog tier alias table.
///
/// <para>The gateway historically carried TWO tier taxonomies:</para>
/// <list type="bullet">
///   <item><description>the CATALOG taxonomy (<see cref="ITiersStore"/>, seeded
///     {urgent, same-day, scheduled}) — what clients see at
///     <c>GET /v1/tiers</c> and what <c>NewRequestPushNotifier</c> resolves display
///     names from; and</description></item>
///   <item><description>the LEGACY taxonomy ({flash, express, standard, on_the_way,
///     eco} — the db/migrations/0011 seed) — used only for create-time validation on
///     the legacy <c>POST /requests</c> / voice surfaces.</description></item>
/// </list>
///
/// <para>The catalog taxonomy is now the SINGLE source of truth. Each legacy code is
/// aliased to its catalog equivalent so old clients keep working — a legacy code is
/// accepted at create time and resolves to a human display name in push bodies. The
/// mapping was chosen from the two seeds' semantics (SLA / commission / price-hint):</para>
/// <list type="bullet">
///   <item><description><c>flash</c> (30 min) → <c>urgent</c> (1 h SLA,
///     "Premium — fastest dispatch") — both are the fastest premium tier.</description></item>
///   <item><description><c>express</c> (60 min) → <c>urgent</c> (1 h SLA)
///     — an EXACT SLA match; the catalog has no second sub-hour tier.</description></item>
///   <item><description><c>standard</c> (3 h, default tier — the voice surface's
///     fallback) → <c>same-day</c> (2 h TTL, price hint literally "Standard same-day
///     rate") — the mid-tier default in both taxonomies.</description></item>
///   <item><description><c>on_the_way</c> (no SLA, jeeber already routed nearby) →
///     <c>same-day</c> — the compact three-tier catalog no longer has a separate
///     routed-nearby tier, so this remains on the mid tier.</description></item>
///   <item><description><c>eco</c> (24 h, lowest 10% take-rate) →
///     <c>scheduled</c> (24 h TTL) — the slowest tier in both taxonomies.</description></item>
/// </list>
///
/// <para>The catalog still keeps scheduling as an orthogonal <c>scheduledAt</c>
/// field; the <c>scheduled</c> tier id here only names the slowest TTL/radius
/// tier.</para>
///
/// <para>Lookups are case-insensitive, matching the catalog store's id semantics
/// (<see cref="InMemoryTiersStore"/> keys ids OrdinalIgnoreCase).</para>
/// </summary>
public static class LegacyTierCodes
{
    private static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["flash"] = "urgent",
            ["express"] = "urgent",
            ["standard"] = "same-day",
            ["on_the_way"] = "same-day",
            ["eco"] = "scheduled",
        };

    /// <summary>
    /// Maps a legacy tier code to its catalog id. Returns false when
    /// <paramref name="tierId"/> is not a known legacy code (it may still be a
    /// perfectly valid catalog id — callers should then use it as-is).
    /// </summary>
    public static bool TryMapToCatalogId(string? tierId, out string catalogId)
    {
        if (!string.IsNullOrWhiteSpace(tierId)
            && Map.TryGetValue(tierId.Trim(), out var mapped))
        {
            catalogId = mapped;
            return true;
        }

        catalogId = string.Empty;
        return false;
    }

    /// <summary>
    /// Canonicalizes a tier id for a catalog lookup: a known legacy code becomes its
    /// catalog equivalent; anything else (already-catalog ids, unknown/opaque ids) is
    /// returned trimmed and otherwise untouched — the CALLER decides what an unknown
    /// id means (validation 400, dropped push suffix, …).
    /// </summary>
    public static string Canonicalize(string tierId)
    {
        var trimmed = (tierId ?? string.Empty).Trim();
        return Map.TryGetValue(trimmed, out var mapped) ? mapped : trimmed;
    }
}
