using System;

namespace JeebGateway.Conversations;

/// <summary>
/// JEB-1488 (correction #1 / GR2) — the boundary translation between the Jeeb
/// participant vocabulary (which lives ONLY in the gateway) and the GENERIC,
/// product-agnostic permission tags chat-service stores and maps to its coarse
/// visibility lane.
///
/// <para>
/// chat-service's <c>MessageVisibilityResolver.MapRole</c> maps <c>client</c> (or
/// any client-prefixed tag) to the see-all Client lane and EVERY other tag to the
/// Restricted (own-private + broadcast) lane. The generic tags below therefore
/// preserve chat-service's visibility behaviour byte-for-byte while guaranteeing
/// that NO Jeeb role name (<c>jeeber</c> / <c>jeeber_offerer</c> /
/// <c>jeeber_winner</c>) and NO <c>jeeb:</c>-prefixed value ever crosses the
/// upstream boundary — the abstract permission-tag set is computed HERE, in the
/// gateway, from the Jeeb role.
/// </para>
///
/// <para>
/// The gateway maps Jeeb role → generic tag on the way IN (the membership/role
/// payload sent to chat-service) and generic tag → Jeeb role on the way OUT (the
/// read-back the gateway projects to its OWN Jeeb realtime/mobile clients). Unknown
/// values pass through unchanged, so the translation is additive and non-breaking
/// (GR1): a legacy or already-generic value is never mangled.
/// </para>
/// </summary>
public static class ConversationParticipantTag
{
    // ---- generic permission tags that cross the upstream boundary -----------

    /// <summary>
    /// The conversation owner / see-all lane. MUST be <c>client</c> (or
    /// client-prefixed) so chat-service's MapRole resolves the owner to its Client
    /// lane; "client" is a generic, product-agnostic role, not a Jeeb token.
    /// </summary>
    public const string Owner = "client";

    /// <summary>Generic non-owner participant lane (chat-service Restricted lane).</summary>
    public const string Participant = "participant";

    /// <summary>
    /// Generic promoted/primary non-owner participant (still the Restricted lane in
    /// chat-service — visibility-identical to <see cref="Participant"/>). Lets the
    /// gateway re-derive the winner on read-back WITHOUT chat-service ever seeing a
    /// Jeeb role name.
    /// </summary>
    public const string PrimaryParticipant = "participant_primary";

    // ---- Jeeb roles (gateway-only vocabulary; NEVER cross the wire) ----------

    public const string JeebOwnerRole = "client";
    public const string JeebOffererRole = "jeeber_offerer";
    public const string JeebWinnerRole = "jeeber_winner";

    /// <summary>
    /// Map a Jeeb participant role to the generic permission tag sent upstream. The
    /// winner maps to <see cref="PrimaryParticipant"/>; the owner/client maps to
    /// <see cref="Owner"/>; every other Jeeb role (offerer, etc.) maps to the plain
    /// <see cref="Participant"/> lane. Null/blank defaults to a non-owner participant
    /// (fail-restrictive).
    /// </summary>
    public static string FromJeebRole(string? jeebRole)
    {
        if (string.IsNullOrWhiteSpace(jeebRole)) return Participant;

        var r = jeebRole.Trim();
        if (string.Equals(r, JeebWinnerRole, StringComparison.OrdinalIgnoreCase)) return PrimaryParticipant;
        if (string.Equals(r, JeebOwnerRole, StringComparison.OrdinalIgnoreCase)
            || r.StartsWith("client", StringComparison.OrdinalIgnoreCase)) return Owner;

        // jeeber_offerer and any other non-owner Jeeb role → generic participant lane.
        return Participant;
    }

    /// <summary>
    /// Map a generic permission tag read back from chat-service to the Jeeb role the
    /// gateway projects to its Jeeb realtime/mobile clients. Unknown/legacy values
    /// pass through unchanged (non-breaking), so a value chat-service already stored
    /// as a generic or legacy string is never corrupted.
    /// </summary>
    public static string? ToJeebRole(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return tag;

        var t = tag.Trim();
        if (string.Equals(t, PrimaryParticipant, StringComparison.OrdinalIgnoreCase)) return JeebWinnerRole;
        if (string.Equals(t, Participant, StringComparison.OrdinalIgnoreCase)) return JeebOffererRole;
        if (string.Equals(t, Owner, StringComparison.OrdinalIgnoreCase)) return JeebOwnerRole;

        return tag;
    }

    /// <summary>
    /// Boundary guard: true if <paramref name="value"/> carries a Jeeb-domain token
    /// (a Jeeb role name or a <c>jeeb:</c>/<c>jeeb.</c>-prefixed value) that GR2
    /// forbids from crossing the upstream membership/role boundary. Case-insensitive.
    /// NB: this is the guard for the (subject, permission-tag) visibility payload — it
    /// is intentionally NOT applied to the opaque structured-message envelope
    /// (kind/subtype/payload), which chat-service stores verbatim (correction #3).
    /// </summary>
    public static bool IsForbiddenUpstreamToken(string? value)
        => !string.IsNullOrEmpty(value)
           && value.IndexOf("jeeb", StringComparison.OrdinalIgnoreCase) >= 0;
}
