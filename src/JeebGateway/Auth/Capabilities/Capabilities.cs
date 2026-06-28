namespace JeebGateway.Auth.Capabilities;

/// <summary>
/// ADR-005 Layer 2 (user-type capability authorization) — the capability registry.
///
/// <para>Each constant is a <b>capability</b>: a verb on the Jeeb domain (e.g.
/// <c>offer.submit</c>, <c>kyc.review</c>), NOT a bare role name. Capabilities are the
/// stable, fine-grained unit a route declares via <see cref="RequireCapabilityAttribute"/>;
/// the capability→roles mapping (which user TYPE holds each capability) is the single
/// source of truth in <see cref="CapabilityRolePolicy"/>.</para>
///
/// <para><b>Two layers, never conflated (ADR-005).</b> Layer 1 (ADR-004,
/// <see cref="JeebGateway.Auth.GatewayAudienceRequirement"/>) proves a valid caller via
/// audience → 401 on failure. Layer 2 (this) proves the caller's user TYPE holds the
/// capability the action requires → 403 on failure. The capability handler reads ONLY the
/// <c>roles</c> claim (canonicalized), never the audience.</para>
///
/// <para><b>CLAIM vs STATE (binding, PR-blocking).</b> These capabilities authorize purely
/// on the coarse role claim. Ownership / party-on-delivery / state-machine legality /
/// business rules (BR-1 self-offer, BR-9 cap, BR-10) are STATE and stay in the OWNING
/// service, which already returns 403/409 from state. No STATE rule may ever be expressed
/// as an L2 policy — an action marked <c>(STATE)</c> in the ADR carries ONLY its coarse
/// capability here.</para>
///
/// <para>The naming, grouping, and the authoritative role mapping are transcribed verbatim
/// from the "AUTHORITATIVE capability → roles map" section of ADR-005. Adding a capability
/// here is meaningless until it is added to <see cref="CapabilityRolePolicy"/> (the guard
/// will not register a policy for an unmapped capability).</para>
/// </summary>
public static class Capabilities
{
    // ── B. Self / any authenticated {client, jeeber, admin} ───────────────────────────
    public const string ProfileReadSelf = "profile.read.self";
    public const string ProfileWriteSelf = "profile.write.self";
    public const string DataExportSelf = "data.export.self";
    public const string NotificationPrefsSelf = "notification.prefs.self";
    public const string NotificationsReadSelf = "notifications.read.self";
    public const string AuthLogoutSelf = "auth.logout.self";

    // ── C. Client-only {client} ───────────────────────────────────────────────────────
    public const string RequestCreate = "request.create";
    public const string RequestReadOwn = "request.read.own";          // STATE: ownership
    public const string RequestEditOwn = "request.edit.own";          // STATE
    public const string RequestCancelOwn = "request.cancel.own";      // STATE
    public const string RequestVoiceCreate = "request.voice.create";
    public const string DeliveryTrackOwn = "delivery.track.own";      // STATE
    public const string MatchingRun = "matching.run";

    // ── D. Jeeber-only {jeeber} ───────────────────────────────────────────────────────
    public const string AvailabilityToggle = "availability.toggle";
    // GAP-2 (sprint-002, contract-freeze §3): the jeeber's request-discovery feed
    // (GET /v1/jeebers/me/feed). Jeeber-only — a client never reads the jeeber feed.
    // Own-scoped: the feed surfaces pending requests an ONLINE jeeber can act on
    // (online + status=pending + clientId != jeeberId); the row visibility is the
    // §1 projection predicate, not an L2 STATE rule.
    public const string JeeberFeedRead = "jeeber.feed.read";
    // Submitting an offer (bid) on a client's request is a JEEBER action; accepting is not
    // (see offer.accept in the client-only group below).
    public const string OfferSubmit = "offer.submit";

    // S07: offer.accept is keyed {client}, NOT {jeeber}. In the Jeeb auction jeebers SUBMIT
    // offers and the request-owning CLIENT ACCEPTS one to award the delivery, so the
    // capability lives in the client-only group (CapabilityRolePolicy section C). The constant
    // stays here next to its sibling offer.* names for discoverability; the role mapping is the
    // authority. The JEB-1509 {jeeber} mapping was inverted and produced the live S07 H5/A6 403.
    public const string OfferAccept = "offer.accept";                // CLAIM {client}; BR-1/race/status = STATE
    // S08 A5: rejecting a single jeeber's bid is a CLIENT action (the request owner
    // declines one offer), mirroring offer.accept's {client} mapping — NOT a jeeber
    // action. STATE (only the request's client may reject, the rejected transition)
    // stays in offer-service.
    public const string OfferReject = "offer.reject";                // CLAIM {client}; authz/status = STATE
    public const string OfferEditOwn = "offer.edit.own";              // STATE
    public const string OfferWithdraw = "offer.withdraw";            // STATE
    public const string DeliveryGpsStream = "delivery.gps.stream";
    public const string EarningsReadOwn = "earnings.read.own";        // STATE: ownership
    public const string EarningsPdfOwn = "earnings.pdf.own";          // STATE: ownership

    // ── E. Delivery state-machine / dual-party — coarse CLAIM, party+SM-legality = STATE ─
    public const string DeliveryParticipate = "delivery.participate"; // {client, jeeber}
    public const string HandoverOtpRead = "handover.otp.read";        // STATE: party/SM
    public const string RatingSubmit = "rating.submit";               // STATE: party

    // ── F. Chat {client, jeeber} (membership = STATE) ─────────────────────────────────
    public const string ChatRead = "chat.read";
    public const string ChatSend = "chat.send";
    public const string ChatModerate = "chat.moderate";               // OPEN-1: baked {admin}

    // ── G. Disputes ───────────────────────────────────────────────────────────────────
    public const string DisputeFile = "dispute.file";                 // {client, jeeber}
    public const string DisputeReadMine = "dispute.read.mine";        // {client, jeeber, admin}; own-vs-any = STATE
    public const string DisputeResolve = "dispute.resolve";           // {admin}

    // ── H–J. Misc participant caps {client, jeeber} ───────────────────────────────────
    public const string ProhibitedAck = "prohibited.ack";
    public const string ProhibitedScan = "prohibited.scan";
    public const string WalletReadOwn = "wallet.read.own";            // STATE: scoping (OPEN-2)
    public const string FeedbackSubmit = "feedback.submit";
    public const string TranscriptionRequest = "transcription.request";
    public const string CdnBroker = "cdn.broker";

    // ── K. Admin-only {admin} — authorized purely from the 'admin' role claim (super-login) ─
    public const string KycReview = "kyc.review";
    public const string ZonesManage = "zones.manage";
    public const string TiersManage = "tiers.manage";
    public const string ProhibitedManage = "prohibited.manage";
    public const string FlaggedReview = "flagged.review";
    public const string FlaggedDecide = "flagged.decide";
    public const string CancellationsReview = "cancellations.review";
    public const string CancellationsDecide = "cancellations.decide";
    public const string SettlementsManage = "settlements.manage";
    public const string FinanceRead = "finance.read";
    public const string WalletManage = "wallet.manage";

    // JEB-1509: a FLEET-WIDE push broadcast is an operator action. TIGHTENING — pre-cleanup the
    // broadcast route carried only the L1 fallback (any identified caller). Now {admin}, authorized
    // purely from the 'admin' role claim a super-login token carries. Self-scoped / device-targeted
    // sends remain notification.prefs.self (§B), NOT this capability.
    public const string PushBroadcast = "push.broadcast";

    // ── L. KYC submission {client, jeeber} ─────────────────────────────────────────────
    public const string KycSubmitSelf = "kyc.submit.self";

    // ── L2. Contract-signing (JEB-1509) ────────────────────────────────────────────────
    // Authoring contract templates and instantiating contracts are operator actions.
    // TIGHTENING — pre-cleanup these writes carried only the L1 fallback (class-level
    // [PublicEndpoint] => any identified caller). Now {admin}. Template/contract READS stay
    // L2-public ([PublicEndpoint], L1 fallback unchanged).
    public const string ContractTemplateManage = "contract.template.manage"; // {admin}: RegisterTemplate + CreateContract
    // Signing a contract is the END-USER accepting Terms-of-Service. {client, jeeber}.
    public const string ContractSign = "contract.sign";                       // {client, jeeber}

    // ── M. Legacy UserController — admin/internal ──────────────────────────────────────
    public const string UsersAdminManage = "users.admin.manage";      // DeleteByEmails, payment-auth-token

    // ── N. Notification dispatch (JEB-1494) — operator-triggered outbound notification ──
    public const string NotificationDispatch = "notification.dispatch"; // {admin}

    /// <summary>
    /// The ASP.NET Core policy name for a capability. One named policy is registered per
    /// capability at startup (see Program.cs); <see cref="RequireCapabilityAttribute"/> sets
    /// <c>Policy = PolicyFor(capability)</c> so the framework returns the standard 401/403 and
    /// Swagger/IAuthorizationPolicyProvider discover the policy.
    /// </summary>
    public static string PolicyFor(string capability) => $"cap:{capability}";
}
