namespace JeebGateway.ProhibitedItems;

/// <summary>
/// Jeeb-domain ownership anchor for the prohibited-items moderation lexicon
/// (JEB-1478 boundary remediation).
///
/// The shared ban-service exposes a PRODUCT-AGNOSTIC moderation surface
/// (<c>/v1/moderation/*</c>) whose entries are namespaced by a caller-supplied
/// <c>list_key</c>. Per the N11 boundary guard + GR2, none of the Jeeb-specific
/// choices may live in that shared service. This type is where the Jeeb product
/// makes — and owns — those choices:
///   * <see cref="ListKey"/> — the namespace Jeeb's lexicon occupies. When the
///     gateway federates its gateway-owned lexicon to ban-service's generic
///     surface it does so via <c>?list_key=jeeb-prohibited-items</c>; the
///     ban-service source itself never names "jeeb".
///   * The actual vocabulary (alcohol/<c>arak</c>, weapons, …) is seeded by
///     <see cref="DefaultLexiconSeeder"/> here in the gateway and/or pushed to
///     ban-service as DATA via its generic admin CRUD — never baked into the
///     shared service source.
///
/// Keeping this constant gateway-side is the concrete realisation of "relocate
/// ALL Jeeb specifics to jeeb-gateway": the <c>jeeb-prohibited-items</c> list
/// key is a Jeeb decision, expressed in Jeeb code.
/// </summary>
public static class JeebModerationList
{
    /// <summary>
    /// The caller-supplied <c>list_key</c> identifying Jeeb's prohibited-items
    /// lexicon on the shared moderation service. This is the single
    /// gateway-owned source of truth for that choice.
    /// </summary>
    public const string ListKey = "jeeb-prohibited-items";
}
