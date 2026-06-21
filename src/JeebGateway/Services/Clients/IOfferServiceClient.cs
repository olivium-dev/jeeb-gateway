namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed client over the real offer-service (Elixir/Phoenix, host port 10063,
/// liveness <c>/health</c>). The offer-service is the canonical owner of the
/// reverse-auction offer ledger; the gateway proxies the offer record-of-truth
/// through this client when <c>FeatureFlags:UseUpstream:Offer</c> is on
/// (see <see cref="JeebGateway.Services.UpstreamFeatureFlags.Offer"/>).
///
/// Hand-coded (not NSwag-generated) because offer-service exposes no OpenAPI
/// document — <c>/swagger/v1/swagger.json</c> and <c>/openapi.json</c> both 404;
/// only <c>/health</c>, <c>/healthz</c>, <c>/readyz</c> and the four
/// <c>/api/v1/...</c> auction routes exist. The contract below was lifted
/// directly from <c>offer-service/lib/offer_service_web/router.ex</c> and
/// <c>controllers/offer_controller.ex</c>. Follows the
/// <c>NotificationServiceClient</c> hand-coded precedent (since removed in
/// favour of the salehly-mirrored NSwag ServiceNotificationClient).
///
/// Auth seam: offer-service trusts a gateway-injected <c>x-user-id</c> header
/// (its <c>AuthenticatedUser</c> plug), NOT the mobile JWT bearer. The acting
/// user id is therefore passed explicitly into every call and set on the
/// outbound request inside <see cref="OfferServiceClient"/>.
///
/// Wire shape (snake_case): the upstream serializes
/// <c>fee_cents</c> (integer cents), <c>eta_minutes</c>, <c>note</c>,
/// <c>status</c> (one of submitted / edited / withdrawn / accepted / rejected /
/// expired / pending), <c>request_id</c>, <c>jeeber_id</c>, <c>created_at</c>,
/// <c>updated_at</c>, <c>withdrawn_at</c>.
/// </summary>
public interface IOfferServiceClient
{
    /// <summary>
    /// POST /api/v1/requests — gateway request-bridge (OS-1). Idempotently
    /// mirrors a gateway-issued delivery request into offer-service so that a
    /// subsequent <see cref="SubmitAsync"/> against the same <paramref name="requestId"/>
    /// resolves (offer-service requires the request row to exist before a bid
    /// can attach to it). The gateway is the system-of-record and forwards the
    /// id it already minted; offer-service returns 201 on first mirror and 200
    /// on idempotent replay (the existing lifecycle state is never reset).
    ///
    /// <para>This is allowed BFF composition — the gateway pushes a row it owns
    /// into the offer domain so the offer write resolves; it is NOT
    /// inter-service domain coupling (the gateway runs no offer-service
    /// business logic).</para>
    ///
    /// <para>Body: <c>{ "request_id", "client_id", "status": "open" }</c>. The
    /// acting <paramref name="clientId"/> is the request creator (taken from the
    /// gateway's own request row), NOT <paramref name="actingUserId"/> (the
    /// bidding Jeeber) — offer-service reads <c>client_id</c> from the body for
    /// exactly this on-behalf-of mirror.</para>
    ///
    /// <para>Returns the mirror outcome so the caller can distinguish a fresh
    /// mirror from a replay; a 422 (invalid <c>client_id</c>) or 400 (bad
    /// <c>request_id</c>) surfaces as <see cref="OfferUpstreamValidationException"/>
    /// rather than a raw throw.</para>
    /// </summary>
    Task<RequestMirrorResult> MirrorRequestAsync(
        string actingUserId,
        string requestId,
        string clientId,
        CancellationToken ct);

    /// <summary>
    /// POST /api/v1/requests/{requestId}/offers — submit a bid as
    /// <paramref name="actingUserId"/>. Returns the created offer on 201.
    /// Translates upstream conflict codes (<c>request_not_open</c>,
    /// duplicate-offer) into the same exceptions the in-memory store throws so
    /// the controller's catch blocks stay unchanged.
    ///
    /// <para>Error mapping (GW-2): a 404 means the request row was never
    /// mirrored into offer-service — surfaced as
    /// <see cref="OfferRequestNotMirroredException"/> so the caller can mirror
    /// (via <see cref="MirrorRequestAsync"/>) and retry instead of bubbling a
    /// raw <see cref="HttpRequestException"/> that the global handler would turn
    /// into a 502. A 422/400 (payload validation) surfaces as
    /// <see cref="OfferUpstreamValidationException"/>. Every other non-2xx still
    /// throws via <c>EnsureSuccessStatusCode()</c> so the global handler emits a
    /// ProblemDetails 502 rather than a silent mis-map.</para>
    /// </summary>
    Task<OfferWire> SubmitAsync(
        string actingUserId,
        string requestId,
        long feeCents,
        int etaMinutes,
        string? note,
        CancellationToken ct);

    /// <summary>
    /// DELETE /api/v1/requests/{requestId}/offers/{offerId} — withdraw a bid.
    /// Returns the withdraw outcome mapped from the upstream HTTP status:
    /// 200 → Withdrawn, 404 → NotFound, 403 → NotOwned, 409/410 → NotPending.
    /// </summary>
    Task<OfferWithdrawResult> WithdrawAsync(
        string actingUserId,
        string requestId,
        string offerId,
        CancellationToken ct);

    /// <summary>
    /// GET /api/v1/requests/{requestId}/offers — list every offer on a request
    /// for its owning Client (the accept-sheet / bid-review). offer-service is
    /// owner-gated on the gateway-injected <c>x-user-id</c>, so
    /// <paramref name="actingUserId"/> is the request owner the gateway has
    /// already authorized. Returns the offers newest-first (possibly empty);
    /// 404 (unknown request) → empty list, since the gateway controller has
    /// already proven the request exists and is owned before calling here.
    /// 403 (owner mismatch) and any other non-2xx throw via
    /// <c>EnsureSuccessStatusCode()</c> so the global handler surfaces a
    /// ProblemDetails rather than a silent mis-map.
    /// </summary>
    Task<IReadOnlyList<OfferWire>> ListForRequestAsync(
        string actingUserId,
        string requestId,
        CancellationToken ct);

    /// <summary>
    /// POST /api/v1/requests/{requestId}/offers/{offerId}/accept — atomic
    /// auction close. The <c>Idempotency-Key</c> header is mandatory upstream;
    /// callers supply <paramref name="idempotencyKey"/> (>= 8 chars). Returns
    /// the accept-result envelope on 200.
    ///
    /// <para>Throws <see cref="HttpRequestException"/> on any non-2xx. This is
    /// the legacy seam kept for the existing contract test; new callers should
    /// use <see cref="AcceptWithStatusAsync"/>, which forwards the upstream
    /// status verbatim instead of throwing — required so the gateway can map
    /// offer-service's 403/410/409/404 negatives through to the caller rather
    /// than masking them as a raw 500.</para>
    /// </summary>
    Task<OfferAcceptWire> AcceptAsync(
        string actingUserId,
        string requestId,
        string offerId,
        string idempotencyKey,
        CancellationToken ct);

    /// <summary>
    /// POST /api/v1/requests/{requestId}/offers/{offerId}/accept — atomic
    /// auction close that <b>preserves the upstream HTTP status</b> instead of
    /// throwing on non-2xx. offer-service's <c>FallbackController</c> already
    /// maps its saga outcomes to canonical statuses
    /// (200 accepted / 403 non-owner / 410 request_expired / 409 already_accepted
    /// or cap / 404 not_found); this method surfaces that status to the gateway
    /// controller so it can be forwarded verbatim. The gateway runs no auction
    /// rules of its own — offer-service is the sole owner of the generic
    /// single-winner transition (sibling rejection, request transition, SELECT FOR
    /// UPDATE + optimistic_lock race-safety). JEB-1474: OTP mint / chat-thread /
    /// notification fan-out are NOT part of that saga; the gateway orchestrates
    /// them after a successful accept.
    /// </summary>
    Task<OfferAcceptResult> AcceptWithStatusAsync(
        string actingUserId,
        string requestId,
        string offerId,
        string idempotencyKey,
        CancellationToken ct);

    /// <summary>
    /// S08 A3 — PUT /api/v1/requests/{requestId}/offers/{offerId} — a JEEBER edits
    /// their own pending bid (fee / eta / note). offer-service owns the generic
    /// edit transition (<c>Auction.edit_offer</c>, only the owning jeeber, only
    /// while submitted/edited) and the <c>edited</c> status transition; the gateway
    /// runs no edit rule of its own. The upstream route is REQUEST-scoped, so the
    /// gateway resolves <paramref name="requestId"/> from its routing index and
    /// forwards the actor as <c>x-user-id</c> (offer-service authorizes
    /// <c>offer.actor_id == actor</c>).
    ///
    /// <para>JEB-1474: the literal edit cap is a Jeeb PRODUCT policy and no longer
    /// lives in the shared service. The gateway supplies it per request via
    /// <paramref name="maxEdits"/> (the Jeeb cap is 2); offer-service enforces the
    /// supplied ceiling and returns 409/422 when it is exceeded.</para>
    ///
    /// Returns the status-preserving outcome so the controller forwards the
    /// upstream status verbatim (200 edited / 403 not-owner / 404 not-found /
    /// 409 not-editable or edit-cap-reached). Only non-null fee/eta/note are sent —
    /// a partial edit (e.g. fee only, the A3 body) leaves the other fields untouched.
    /// </summary>
    Task<OfferMutationResult> EditAsync(
        string actingUserId,
        string requestId,
        string offerId,
        long? feeCents,
        int? etaMinutes,
        string? note,
        int? maxEdits,
        CancellationToken ct);

    /// <summary>
    /// S08 A5 — POST /offers/{offerId}/reject — the request-owning CLIENT rejects a
    /// single jeeber's bid (distinct from the accept-saga's automatic sibling
    /// rejection). offer-service owns the reject rule (only the owning request's
    /// client may reject; <c>StateMachine.apply :reject</c>: submitted/edited →
    /// rejected, with an <c>:already_rejected</c> guard) and the <c>rejected</c>
    /// status transition. This route is OFFER-scoped upstream (mirroring the S07
    /// <c>accept_by_offer</c> route), so no requestId resolution is needed; the
    /// gateway forwards the actor as <c>x-user-id</c>. Returns the status-preserving
    /// outcome so the controller forwards the upstream status verbatim
    /// (200 rejected / 403 not-owner / 404 not-found / 409 not-rejectable or
    /// already-rejected).
    /// </summary>
    Task<OfferMutationResult> RejectAsync(
        string actingUserId,
        string offerId,
        CancellationToken ct);
}

/// <summary>
/// Status-preserving outcome of an offer mutation (edit / reject) derived verbatim
/// from the offer-service HTTP status, so the gateway controller can re-emit the
/// matching status without re-deriving any auction rule. Mirrors
/// <see cref="OfferAcceptResult"/>. <see cref="Offer"/> carries the updated offer
/// projection on <see cref="OfferMutationStatus.Ok"/> (for the edit response body);
/// it is null for reject (the reject contract returns no offer body) and every
/// negative status.
/// </summary>
public sealed class OfferMutationResult
{
    public required OfferMutationStatus Status { get; init; }

    /// <summary>Set only on <see cref="OfferMutationStatus.Ok"/> for the edit projection; null otherwise.</summary>
    public OfferWire? Offer { get; init; }

    /// <summary>offer-service error <c>code</c> for negative statuses, when the body carried one.</summary>
    public string? UpstreamCode { get; init; }
}

/// <summary>
/// Canonical outcome of an offer edit / reject, derived verbatim from the
/// offer-service HTTP status. The gateway maps each onto the same caller-facing
/// status, re-deriving no auction rule.
/// </summary>
public enum OfferMutationStatus
{
    /// <summary>200 — the mutation applied; <see cref="OfferMutationResult.Offer"/> set for edit.</summary>
    Ok,

    /// <summary>403 — caller is not authorized (not the offer's jeeber for edit; not the request's client for reject).</summary>
    NotOwner,

    /// <summary>404 — phantom offer/request unknown to offer-service.</summary>
    NotFound,

    /// <summary>409 — the offer is no longer mutable (not pending, edit-cap reached, already rejected).</summary>
    Conflict
}

/// <summary>
/// Outcome of <see cref="IOfferServiceClient.MirrorRequestAsync"/>. The mirror
/// is idempotent; the gateway treats <see cref="Created"/> and
/// <see cref="AlreadyMirrored"/> identically for control flow (both mean "the
/// request row now exists in offer-service, retry the submit"), but the
/// distinction is preserved for logging / diagnostics.
/// </summary>
public enum RequestMirrorResult
{
    /// <summary>201 — the request row was created in offer-service for the first time.</summary>
    Created,

    /// <summary>200 — the request was already mirrored (idempotent replay).</summary>
    AlreadyMirrored
}

/// <summary>
/// Decoded offer-service offer record (cents-based). Mapped to the gateway's
/// dollar-based <see cref="JeebGateway.Availability.PendingOffer"/> by
/// <see cref="JeebGateway.Availability.UpstreamPendingOffersStore"/>.
/// </summary>
public sealed class OfferWire
{
    public required string Id { get; init; }
    public required string RequestId { get; init; }
    public required string JeeberId { get; init; }
    public long FeeCents { get; init; }
    public int EtaMinutes { get; init; }
    public string? Note { get; init; }
    public required string Status { get; init; }
    public int EditsCount { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? WithdrawnAt { get; init; }
}

/// <summary>
/// Minimal decode of the accept envelope. JEB-1474: the shared offer-service
/// accept returns ONLY the generic transition outcome — the accepted offer id
/// and the rejected sibling ids. OTP minting, chat-thread provisioning and
/// notification fan-out are Jeeb-domain side effects the GATEWAY owns (via its
/// own typed OTP/chat/notification clients in <c>OrchestrateAcceptedAsync</c>),
/// so they are no longer read off this envelope.
/// </summary>
public sealed class OfferAcceptWire
{
    public required string AcceptedOfferId { get; init; }

    /// <summary>
    /// The winning jeeber's id, decoded from the accept envelope's generic
    /// <c>accepted_offer.actor_id</c> (falling back to the deprecated
    /// <c>jeeber_id</c> alias) when present. Lets the gateway surface the awarded
    /// jeeber on the accept DTO (the acting user is the CLIENT, not the jeeber, so
    /// it cannot be inferred from the caller). Null when the envelope omits it.
    /// </summary>
    public string? JeeberId { get; init; }

    public IReadOnlyList<string> RejectedOfferIds { get; init; } = Array.Empty<string>();
    public bool Replayed { get; init; }
}

/// <summary>Outcome of a withdraw, mirroring the upstream HTTP status mapping.</summary>
public enum OfferWithdrawResult
{
    Withdrawn,
    NotFound,
    NotOwned,
    NotPending
}

/// <summary>
/// Canonical outcome of an upstream accept, derived verbatim from the
/// offer-service HTTP status so the gateway controller can re-emit the matching
/// status without re-deriving any auction rule.
/// </summary>
public enum OfferAcceptStatus
{
    /// <summary>200 — auction closed; <see cref="OfferAcceptResult.Envelope"/> set.</summary>
    Accepted,

    /// <summary>403 — caller is not the offer's owning Jeeber (offer-service forbidden).</summary>
    NotOwner,

    /// <summary>410 — the request expired before acceptance (offer-service request_expired).</summary>
    Expired,

    /// <summary>409 — the offer/request is no longer acceptable (already accepted, cap hit).</summary>
    Conflict,

    /// <summary>404 — phantom offer/request unknown to offer-service.</summary>
    NotFound
}

/// <summary>
/// Status-preserving accept outcome. On <see cref="OfferAcceptStatus.Accepted"/>
/// the <see cref="Envelope"/> carries the generic transition result (winning
/// offer id, rejected sibling ids); for every negative status the
/// <see cref="UpstreamCode"/> carries the offer-service error code (when present)
/// purely for logging / diagnostics.
/// </summary>
public sealed class OfferAcceptResult
{
    public required OfferAcceptStatus Status { get; init; }

    /// <summary>Set only when <see cref="Status"/> is <see cref="OfferAcceptStatus.Accepted"/>.</summary>
    public OfferAcceptWire? Envelope { get; init; }

    /// <summary>offer-service error <c>code</c> for negative statuses, when the body carried one.</summary>
    public string? UpstreamCode { get; init; }
}
