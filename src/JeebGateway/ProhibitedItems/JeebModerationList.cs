namespace JeebGateway.ProhibitedItems;

/// <summary>
/// Jeeb-domain namespace anchor for the prohibited-items moderation catalog.
///
/// The shared ban-service exposes a PRODUCT-AGNOSTIC moderation surface
/// (<c>/v1/moderation/*</c>) whose entries are namespaced by a caller-supplied
/// <c>list_key</c>. Per the N11 boundary guard + GR2, none of the Jeeb-specific
/// choices may be hard-coded in that shared service. This type is where the
/// Jeeb product selects its namespace:
///   * <see cref="ListKey"/> — the namespace Jeeb's lexicon occupies. When the
///     gateway consumes ban-service's generic surface via
///     <c>?list_key=jeeb-prohibited-items</c>; shared service code remains
///     product-agnostic. Vocabulary is owner data reconciled by ban-service.
///
/// The namespace remains a Jeeb decision while all durable catalog state stays
/// in its owner service.
/// </summary>
public static class JeebModerationList
{
    /// <summary>
    /// The caller-supplied <c>list_key</c> identifying Jeeb's prohibited-items
    /// catalog on the shared moderation service. This is the gateway source of
    /// truth for that product-specific namespace choice.
    /// </summary>
    public const string ListKey = "jeeb-prohibited-items";
}
