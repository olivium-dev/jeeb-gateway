namespace JeebGateway.Requests;

/// <summary>
/// Additive deprecated-alias dual-read layer (ADR-002 §3 / PR-2, owner-approved
/// 2026-06-04).
///
/// In-flight orders carry persisted gateway status strings in the LEGACY
/// vocabulary (<c>picked_up</c>, <c>heading_off</c>, <c>delivered</c>,
/// <c>at_door</c>, <c>disputed</c>, …). The reconciliation onto the canonical
/// SM-1 lexicon must NOT 422 a running delivery mid-flight (D4), so this layer:
///
///   * <see cref="ToCanonical"/> — dual-reads: accepts BOTH the legacy token and
///     the canonical token and returns the canonical form. The gateway writes
///     canonical tokens going forward; legacy tokens keep validating until the
///     active set drains (ADR-002 §6).
///   * <see cref="IsDeprecated"/> — lets a one-line drain check
///     (<c>SELECT DISTINCT status</c>) and observability flag rows still on the
///     old vocabulary so the alias layer can be removed once the count is zero.
///
/// This is a TRANSLATION-only layer: no persisted value is rewritten at freeze
/// time, and the canonical <see cref="DeliverySm"/> table is the single arbiter
/// of legality. The aliases are a drain-then-remove convenience, not a second
/// source of truth.
///
/// Mapping is the frozen ADR-002 §3 table:
///   picked_up   ⇒ Picked                  (deprecated alias)
///   heading_off ⇒ InTransit               (deprecated alias)
///   at_door     ⇒ AtDoor                  (case/format normalize)
///   delivered   ⇒ Done                    (deprecated alias; settlement keys off Done)
///   cancelled   ⇒ Cancelled               (case normalize)
///   expired     ⇒ Expired                 (request-lifecycle terminal; not in the delivery table)
///   disputed    ⇒ FailedNeedsEscalation   (deprecated alias)
///   rated       ⇒ Done                    (rated is a ratings-context concern, NOT a delivery state)
///   accepted    ⇒ Ordered                 (entry edge — delivery created in Ordered)
/// </summary>
public static class DeliveryStatusAlias
{
    /// <summary>
    /// Legacy gateway token → canonical token. Includes both the genuinely
    /// deprecated aliases and the pure case-normalizations. Canonical tokens
    /// map to themselves via <see cref="ToCanonical"/> so dual-read is
    /// idempotent (feeding a canonical token back yields the same token).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LegacyToCanonical =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Entry edge: 'accepted' is the offer-accept moment; the delivery is
            // created in Ordered (ADR-002 §3).
            [RequestStatus.Accepted]   = CanonicalDeliveryStatus.Ordered,
            // Deprecated aliases (dual-read; write canonical).
            [RequestStatus.PickedUp]   = CanonicalDeliveryStatus.Picked,
            [RequestStatus.HeadingOff] = CanonicalDeliveryStatus.InTransit,
            [RequestStatus.Delivered]  = CanonicalDeliveryStatus.Done,
            [RequestStatus.Disputed]   = CanonicalDeliveryStatus.FailedNeedsEscalation,
            // Case / format normalizations.
            [RequestStatus.AtDoor]     = CanonicalDeliveryStatus.AtDoor,
            [RequestStatus.Cancelled]  = CanonicalDeliveryStatus.Cancelled,
            [RequestStatus.Expired]    = CanonicalDeliveryStatus.Expired,
            // rated is NOT a delivery state — it resolves to Done for
            // delivery-status reads; the ratings-context row lives elsewhere
            // (ADR-002 §3, negative-consequence note).
            [RequestStatus.Rated]      = CanonicalDeliveryStatus.Done,
        };

    /// <summary>
    /// The subset of <see cref="LegacyToCanonical"/> keys that are genuinely
    /// DEPRECATED (a row carrying one of these still needs to drain before the
    /// alias layer can be removed). Excludes the pure case-normalizations
    /// (<c>at_door</c>/<c>cancelled</c>/<c>expired</c>) which are not "old
    /// vocabulary" so much as a different spelling.
    /// </summary>
    private static readonly IReadOnlySet<string> DeprecatedTokens = new HashSet<string>(StringComparer.Ordinal)
    {
        RequestStatus.PickedUp,
        RequestStatus.HeadingOff,
        RequestStatus.Delivered,
        RequestStatus.Disputed,
        RequestStatus.Rated,
        RequestStatus.Accepted,
    };

    /// <summary>
    /// Resolves any gateway or canonical status token to its canonical form.
    /// Dual-read: a legacy token resolves through the alias map; a token that is
    /// ALREADY canonical resolves to itself (idempotent). Returns null when the
    /// token is neither a known legacy alias nor a known canonical state — the
    /// caller then rejects rather than guessing.
    /// </summary>
    public static string? ToCanonical(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }
        if (LegacyToCanonical.TryGetValue(status, out var canonical))
        {
            return canonical;
        }
        // Already canonical? (Picked / InTransit / Done / … and the reserved
        // Expired token.) Idempotent dual-read.
        if (CanonicalDeliveryStatus.IsKnown(status) ||
            string.Equals(status, CanonicalDeliveryStatus.Expired, StringComparison.Ordinal))
        {
            return status;
        }
        return null;
    }

    /// <summary>
    /// True if <paramref name="status"/> is a deprecated legacy token that still
    /// needs to drain (used by the §6 one-line <c>SELECT DISTINCT status</c>
    /// drain check and by observability). Canonical tokens and pure
    /// case-normalizations return false.
    /// </summary>
    public static bool IsDeprecated(string? status)
        => status is not null && DeprecatedTokens.Contains(status);

    /// <summary>
    /// True when the gateway can resolve the token at all (legacy alias OR
    /// canonical). A convenience for callers that want to fail fast on an
    /// entirely unknown string before consulting the SM.
    /// </summary>
    public static bool CanResolve(string? status) => ToCanonical(status) is not null;
}
