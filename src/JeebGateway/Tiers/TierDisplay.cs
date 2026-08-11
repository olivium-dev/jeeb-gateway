using System.Text;

namespace JeebGateway.Tiers;

/// <summary>
/// O11 — the two DISPLAY projections an order row needs and the request store does not hold:
/// a human order reference and a stable tier token.
///
/// <para>Both were missing from <c>GET /requests?role=client</c>, so a client's own order card
/// rendered neither an <c>ORD-…</c> reference nor a tier chip. The row carries only
/// <c>tierId</c>, which since the delivery-service cut-over is a UUIDv5 — an opaque value no
/// client lexicon can match, so every chip fell through to "unknown". These helpers derive the
/// display forms ONCE, here, so every order surface projects the same strings.</para>
/// </summary>
public static class TierDisplay
{
    private const int ShortReferenceLength = 6;

    /// <summary>
    /// A stable lowercase token for a resolved tier, derived from its catalog NAME
    /// (upstream Flash/Express/Standard ⇒ <c>flash</c>/<c>express</c>/<c>standard</c>, which is
    /// exactly the client tier lexicon). Whitespace becomes <c>_</c>. Null when the tier did not
    /// resolve — the caller then falls back to the raw id rather than inventing a tier.
    /// </summary>
    public static string? Slug(DeliveryTier? tier)
    {
        var name = tier?.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var slug = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            slug.Append(char.IsWhiteSpace(ch) ? '_' : char.ToLowerInvariant(ch));
        }

        return slug.ToString();
    }

    /// <summary>
    /// The human order reference (<c>ORD-3F2A1B</c>) — the last
    /// <see cref="ShortReferenceLength"/> alphanumeric characters of the request id, uppercased.
    /// Deliberately identical to the derivation the mobile clients already apply when the field
    /// is absent, so the server-sent value and any client-derived fallback cannot disagree.
    /// Null for a blank id.
    /// </summary>
    public static string? OrderReference(string? requestId)
    {
        var trimmed = requestId?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var alphanumeric = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch))
            {
                alphanumeric.Append(char.ToUpperInvariant(ch));
            }
        }

        if (alphanumeric.Length == 0)
        {
            return null;
        }

        var tail = alphanumeric.Length <= ShortReferenceLength
            ? alphanumeric.ToString()
            : alphanumeric.ToString(alphanumeric.Length - ShortReferenceLength, ShortReferenceLength);

        return $"ORD-{tail}";
    }
}
