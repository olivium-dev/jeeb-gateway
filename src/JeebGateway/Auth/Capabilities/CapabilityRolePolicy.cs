using JeebGateway.Users;

namespace JeebGateway.Auth.Capabilities;

/// <summary>
/// ADR-005 Layer 2 — the AUTHORITATIVE capability → roles map, transcribed verbatim from
/// the "AUTHORITATIVE capability → roles map" section of ADR-005. This is the SINGLE source
/// of truth for which Jeeb user TYPES hold each capability.
///
/// <para><b>Canonical key space.</b> The map keys on the canonical Jeeb roles
/// <c>{client, jeeber, admin}</c> — referenced via <see cref="JeebRoleTranslator.ContractClient"/>,
/// <see cref="JeebRoleTranslator.ContractJeeber"/> and <see cref="Roles.Admin"/> so the
/// vocabulary is single-sourced from existing code. The PRODUCTION <c>roles</c> claim carries
/// OPAQUE values (<c>customer</c>/<c>driver</c>); the handler canonicalizes each principal role
/// via <see cref="JeebRoleTranslator.ToContract"/> BEFORE intersecting with this map. The map
/// itself never sees opaque vocabulary (SA load-bearing finding — ADR-005).</para>
///
/// <para><b>CLAIM only.</b> Every entry authorizes purely on the coarse role claim. STATE rules
/// (ownership, party-on-delivery, SM legality, BR-1/BR-9/BR-10) are NOT in this map — they stay
/// in the owning service. An action the ADR marks <c>(STATE)</c> appears here with only its coarse
/// role set; the state check remains downstream.</para>
/// </summary>
public static class CapabilityRolePolicy
{
    // Canonical role sets, single-sourced from existing seed code (JeebRoleTranslator + Roles),
    // so the canonical vocabulary {client, jeeber, admin} is never re-spelled as a literal here.
    private static string[] ClientOnly => new[] { JeebRoleTranslator.ContractClient };
    private static string[] JeeberOnly => new[] { JeebRoleTranslator.ContractJeeber };
    private static string[] AdminOnly => new[] { Roles.Admin };
    private static string[] Participant => new[] { JeebRoleTranslator.ContractClient, JeebRoleTranslator.ContractJeeber };
    private static string[] AnyAuthenticated => new[] { JeebRoleTranslator.ContractClient, JeebRoleTranslator.ContractJeeber, Roles.Admin };

    /// <summary>
    /// The authoritative map. Keys are capability names from <see cref="Capabilities"/>; values
    /// are the canonical roles that hold the capability. Order-insensitive; membership is what
    /// matters. <see cref="StringComparer.Ordinal"/> keys because capability names are exact.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> Map =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            // B. Self / any authenticated {client, jeeber, admin}
            [Capabilities.ProfileReadSelf] = AnyAuthenticated,
            [Capabilities.ProfileWriteSelf] = AnyAuthenticated,
            [Capabilities.DataExportSelf] = AnyAuthenticated,
            [Capabilities.NotificationPrefsSelf] = AnyAuthenticated,
            [Capabilities.NotificationsReadSelf] = AnyAuthenticated,
            [Capabilities.AuthLogoutSelf] = AnyAuthenticated,

            // C. Client-only {client}
            [Capabilities.RequestCreate] = ClientOnly,
            [Capabilities.RequestReadOwn] = ClientOnly,         // STATE: ownership
            [Capabilities.RequestEditOwn] = ClientOnly,         // STATE
            [Capabilities.RequestCancelOwn] = ClientOnly,       // STATE
            [Capabilities.RequestVoiceCreate] = ClientOnly,
            [Capabilities.DeliveryTrackOwn] = ClientOnly,       // STATE
            [Capabilities.MatchingRun] = ClientOnly,

            // D. Jeeber-only {jeeber}
            [Capabilities.AvailabilityToggle] = JeeberOnly,
            [Capabilities.OfferSubmit] = JeeberOnly,
            // JEB-1509: offer.accept repointed {client}->{jeeber}. The previous {client} row was
            // DEAD (no route declared offer.accept; OffersController.Accept used offer.submit). The
            // accepting party is the JEEBER the offer was extended to, so the capability lives in the
            // jeeber family. Runtime allowed type for the Accept route is UNCHANGED ({jeeber}).
            [Capabilities.OfferAccept] = JeeberOnly,            // CLAIM {jeeber}; BR-1/BR-10/status = STATE
            [Capabilities.OfferEditOwn] = JeeberOnly,           // STATE
            [Capabilities.OfferWithdraw] = JeeberOnly,          // STATE
            [Capabilities.DeliveryGpsStream] = JeeberOnly,
            [Capabilities.EarningsReadOwn] = JeeberOnly,        // STATE: ownership
            [Capabilities.EarningsPdfOwn] = JeeberOnly,         // STATE: ownership

            // E. Delivery SM / dual-party — coarse CLAIM, party+SM-legality = STATE
            [Capabilities.DeliveryParticipate] = Participant,
            [Capabilities.HandoverOtpRead] = Participant,       // STATE: party/SM
            [Capabilities.RatingSubmit] = Participant,          // STATE: party

            // F. Chat {client, jeeber} (membership = STATE)
            [Capabilities.ChatRead] = Participant,
            [Capabilities.ChatSend] = Participant,
            [Capabilities.ChatModerate] = AdminOnly,            // OPEN-1: baked {admin}

            // G. Disputes
            [Capabilities.DisputeFile] = Participant,
            // dispute.read.mine: the coarse READ claim admits client, jeeber AND admin. WHICH rows
            // each may read is STATE/ownership in the action body (a filer reads only their own
            // rows; an admin reads any row — the documented "admins may read any row" path). Keeping
            // admin out of the coarse claim would 403 the admin BEFORE the ownership branch runs,
            // contradicting the ADR §G "admins read any row" and the tested product contract. The
            // claim/state split is preserved: L2 grants the read by type, the service scopes the rows.
            [Capabilities.DisputeReadMine] = AnyAuthenticated,  // party/own-vs-admin = STATE
            [Capabilities.DisputeResolve] = AdminOnly,

            // H–J. Misc participant caps {client, jeeber}
            [Capabilities.ProhibitedAck] = Participant,
            [Capabilities.ProhibitedScan] = Participant,
            [Capabilities.WalletReadOwn] = Participant,         // STATE: scoping (OPEN-2)
            [Capabilities.FeedbackSubmit] = Participant,
            [Capabilities.TranscriptionRequest] = Participant,
            [Capabilities.CdnBroker] = Participant,

            // K. Admin-only {admin} — from the 'admin' role claim (super-login)
            [Capabilities.KycReview] = AdminOnly,
            [Capabilities.ZonesManage] = AdminOnly,
            [Capabilities.TiersManage] = AdminOnly,
            [Capabilities.ProhibitedManage] = AdminOnly,
            [Capabilities.FlaggedReview] = AdminOnly,
            [Capabilities.FlaggedDecide] = AdminOnly,
            [Capabilities.CancellationsReview] = AdminOnly,
            [Capabilities.CancellationsDecide] = AdminOnly,
            [Capabilities.SettlementsManage] = AdminOnly,
            [Capabilities.FinanceRead] = AdminOnly,
            [Capabilities.WalletManage] = AdminOnly,
            // JEB-1509: fleet-wide push broadcast is admin-only (TIGHTENING — was any-auth L1 fallback).
            [Capabilities.PushBroadcast] = AdminOnly,

            // L. KYC submission {client, jeeber}
            [Capabilities.KycSubmitSelf] = Participant,

            // L2. Contract-signing (JEB-1509) — writes tightened from L1-public to typed L2.
            [Capabilities.ContractTemplateManage] = AdminOnly,  // RegisterTemplate + CreateContract
            [Capabilities.ContractSign] = Participant,          // end-user ToS acceptance {client, jeeber}

            // M. Legacy UserController admin/internal
            [Capabilities.UsersAdminManage] = AdminOnly,
        };

    /// <summary>All capability names in the map — drives the startup policy-registration loop.</summary>
    public static IEnumerable<string> All => Map.Keys;

    /// <summary>
    /// The canonical roles that hold <paramref name="capability"/>.
    /// Throws <see cref="KeyNotFoundException"/> for an unknown capability — a typo'd capability
    /// name must fail loudly at startup/lookup, never silently authorize.
    /// </summary>
    public static IReadOnlyCollection<string> RolesFor(string capability) =>
        Map.TryGetValue(capability, out var roles)
            ? roles
            : throw new KeyNotFoundException(
                $"Unknown capability '{capability}'. Add it to CapabilityRolePolicy.Map (ADR-005 authoritative table).");

    /// <summary>True if <paramref name="capability"/> is a known capability in the map.</summary>
    public static bool IsKnown(string capability) => Map.ContainsKey(capability);
}
