using JeebGateway.Users;
using Microsoft.AspNetCore.Http;

namespace JeebGateway.Requests;

/// <summary>
/// Pure mapping helpers for the canonical delivery SM-1 vocab (JEB-45 / T-BE-009)
/// used when <c>FeatureFlags:UseUpstream:Delivery</c> is ON.
///
/// The gateway is a THIN BFF on the SM path: delivery-service owns the
/// (from, trigger) → to table and the legality of every edge. These helpers only
/// translate the suite/mobile request body shape into the canonical
/// <c>POST /api/v1/deliveries/{id}/transition</c> contract — <c>{ to, trigger }</c>
/// where <c>trigger</c> is the PARTY SOURCE (jeeber|client|admin|system) — and
/// the caller's role into that party source. NO state-machine validation happens
/// here; an illegal edge is rejected by delivery-service with a typed 422.
///
/// Canonical states: Ordered → Picked → InTransit → AtDoor → Done
///                   (+ Cancelled / FailedNeedsEscalation).
/// </summary>
public static class CanonicalDeliveryVocab
{
    // Canonical SM-1 state literals (mirror delivery-service internal/status/status.go).
    public const string Ordered = "Ordered";
    public const string Picked = "Picked";
    public const string InTransit = "InTransit";
    public const string AtDoor = "AtDoor";
    public const string Done = "Done";
    public const string Cancelled = "Cancelled";
    public const string FailedNeedsEscalation = "FailedNeedsEscalation";

    // delivery-service party sources (the REST `trigger` field).
    public const string SourceJeeber = "jeeber";
    public const string SourceClient = "client";
    public const string SourceAdmin = "admin";
    public const string SourceSystem = "system";

    /// <summary>
    /// Resolves the canonical target state from the request body, accepting all
    /// the shapes the suite drives:
    /// <list type="bullet">
    ///   <item>explicit <c>{ to: "Picked" }</c> — canonical state verbatim;</item>
    ///   <item>friendly <c>{ trigger: "pickup"|"depart"|"arrive"|"admin_resolve" }</c>;</item>
    ///   <item>legacy <c>{ status: "in_transit"|"picked_up"|... }</c> snake-case alias.</item>
    /// </list>
    /// Returns false when no recognizable target can be derived (controller → 400).
    /// </summary>
    public static bool TryResolveTarget(PatchStatusBody body, out string canonicalTo)
    {
        // 1) Explicit canonical target wins.
        if (TryNormalizeState(body.To, out canonicalTo))
        {
            return true;
        }

        // 2) Friendly trigger word.
        if (!string.IsNullOrWhiteSpace(body.Trigger)
            && TriggerWordToState(body.Trigger!.Trim()) is { } fromTrigger)
        {
            canonicalTo = fromTrigger;
            return true;
        }

        // 3) Legacy status string (canonical literal OR snake-case alias).
        if (TryNormalizeState(body.Status, out canonicalTo))
        {
            return true;
        }

        canonicalTo = string.Empty;
        return false;
    }

    /// <summary>
    /// Maps a friendly SM-1 trigger word onto its canonical destination state.
    /// Returns null for an unknown word (the caller falls through to other shapes).
    /// </summary>
    private static string? TriggerWordToState(string trigger) => trigger.ToLowerInvariant() switch
    {
        "pickup" => Picked,
        "depart" => InTransit,
        "arrive" => AtDoor,
        "verify" or "otp_verified" => Done,
        "admin_resolve" => Done,
        "escalate" => FailedNeedsEscalation,
        _ => null
    };

    /// <summary>
    /// Normalizes a state token to its canonical literal. Accepts the canonical
    /// PascalCase vocab as-is and the legacy gateway snake_case aliases so a body
    /// like <c>{ status: "in_transit" }</c> (S09) still drives the canonical SM.
    /// </summary>
    public static bool TryNormalizeState(string? value, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "ordered" or "accepted" or "pending" or "matched":
                canonical = Ordered; return true;
            case "picked" or "picked_up":
                canonical = Picked; return true;
            case "intransit" or "in_transit" or "heading_off":
                canonical = InTransit; return true;
            case "atdoor" or "at_door":
                canonical = AtDoor; return true;
            case "done" or "delivered":
                canonical = Done; return true;
            case "cancelled" or "canceled":
                canonical = Cancelled; return true;
            case "failedneedsescalation" or "disputed":
                canonical = FailedNeedsEscalation; return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Maps the caller's gateway role onto the delivery-service party source the
    /// canonical transition contract expects. Defaults to <c>system</c> when no
    /// participant role is present (the upstream actor guard then decides).
    /// </summary>
    public static string PartySourceFor(HttpContext http)
    {
        if (UserIdentity.HasRole(http, Roles.Admin)) return SourceAdmin;
        if (UserIdentity.HasRole(http, Roles.Jeeber)) return SourceJeeber;
        if (UserIdentity.HasRole(http, Roles.Client)) return SourceClient;
        return SourceSystem;
    }

    /// <summary>
    /// The gateway role literal forwarded as <c>X-Actor-Role</c>. delivery-service
    /// reads jeeber|client|admin|system; this mirrors <see cref="PartySourceFor"/>.
    /// </summary>
    public static string ActorRoleFor(HttpContext http) => PartySourceFor(http);
}
