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
    // S09 (JEB-54) BR-TRK-1: the live-track READ surface is held by both delivery
    // parties + admin (admin live-ops needs a participant-equivalent map view for
    // ops triage). WHICH party may read a given row stays STATE in the action
    // (LocationController re-checks party membership / IsAdmin); this L2 set just
    // lets admin past the coarse capability gate so the in-action admin exemption
    // is reachable.
    private static string[] TrackReaders => new[] { JeebRoleTranslator.ContractClient, JeebRoleTranslator.ContractJeeber, Roles.Admin };
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
            // FT-06: admin or internal service token required for batch dispatch (WS-A JEB-57)
            [Capabilities.NotificationDispatch] = AdminOnly,
            [Capabilities.NotificationsReadSelf] = AnyAuthenticated,
            [Capabilities.AuthLogoutSelf] = AnyAuthenticated,

            // C. Client-only {client}
            [Capabilities.RequestCreate] = ClientOnly,
            [Capabilities.RequestReadOwn] = ClientOnly,         // STATE: ownership
            [Capabilities.RequestEditOwn] = ClientOnly,         // STATE
            [Capabilities.RequestCancelOwn] = ClientOnly,       // STATE
            [Capabilities.RequestVoiceCreate] = ClientOnly,
            // S09 BR-TRK-1: {client, jeeber, admin} read the live track; party/admin
            // membership for a given row is STATE, re-checked in LocationController.
            [Capabilities.DeliveryTrackOwn] = TrackReaders,     // STATE (party + admin)
            [Capabilities.MatchingRun] = ClientOnly,
            // S07 (fix/s07-gateway-accept-client-actor): offer.accept repointed {jeeber}->{client}.
            // In the Jeeb auction, JEEBERS SUBMIT offers (bids) on a CLIENT's delivery request; the
            // CLIENT who owns the request then ACCEPTS one jeeber's offer to award the delivery. The
            // accepting party is therefore the request-owning CLIENT, not the jeeber. The JEB-1509
            // {jeeber} mapping inverted this (it modelled "the jeeber accepts"), which 403'd the real
            // acceptor at L2 before the route ran, and — paired with the gateway forwarding the jeeber
            // as x-user-id — produced the live S07 H5/A6 403 against the offer-service request-owner
            // guard (acceptance.ex:177 `request.client_id == actor_id`). offer.submit remains {jeeber}.
            [Capabilities.OfferAccept] = ClientOnly,            // CLAIM {client}; BR-1/race/status = STATE
            // S08 A5: the request-owning CLIENT rejects one jeeber's bid (mirrors
            // offer.accept). authz (only the request's client) + the rejected
            // transition stay in offer-service.
            [Capabilities.OfferReject] = ClientOnly,            // CLAIM {client}; authz/status = STATE

            // D. Jeeber-only {jeeber}
            [Capabilities.AvailabilityToggle] = JeeberOnly,
            [Capabilities.OfferSubmit] = JeeberOnly,
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

            // G2. Support tickets (JM-063): any authenticated Jeeb user files & reads
            // their OWN tickets (owner-scoping is STATE, enforced in the action). The
            // static categories catalog shares the read cap.
            [Capabilities.SupportCreateSelf] = AnyAuthenticated,
            [Capabilities.SupportReadOwn] = AnyAuthenticated,   // own-vs-any = STATE

            // H–J. Misc participant caps {client, jeeber}
            [Capabilities.ProhibitedAck] = Participant,
            [Capabilities.ProhibitedScan] = Participant,
            [Capabilities.ProhibitedReport] = Participant,
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

            // N. Notification dispatch (JEB-1494): render + push a notification to a user — admin only.
            [Capabilities.NotificationDispatch] = AdminOnly,
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
