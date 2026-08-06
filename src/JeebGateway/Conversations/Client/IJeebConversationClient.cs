using System.Threading;
using System.Threading.Tasks;

namespace JeebGateway.Conversations.Client;

/// <summary>
/// S08 (JEB-50/51/52/53) — the thin typed seam the gateway BFF uses to talk to
/// chat-service's Jeeb <b>conversation aggregate</b>. ONE typed client, no domain
/// logic: chat-service owns the conversation, the structured-message envelope,
/// the participants/roles, the phase, and the VisibilityFilter. The gateway only
/// composes these calls and forwards the viewer identity (bearer sub) downstream.
///
/// Implemented by <see cref="JeebConversationClient"/> (HTTP over the named
/// IHttpClientFactory client "JeebConversationClient", same delegating-handler
/// chain — bearer + X-Service-Auth forwarding — as the other chat surfaces).
/// Behind an interface so the BFF controller is unit/integration-testable with a
/// fake (mirroring the IServiceOTPClient / IRealtimeCommunicationClient pattern).
/// </summary>
public interface IJeebConversationClient
{
    /// <summary>
    /// Create-or-get the broadcasting conversation for a request. Idempotent on
    /// the forwarded Idempotency-Key (== request_id): a replay returns the SAME
    /// conversation_id (INV-3). chat-service is the idempotency authority.
    /// </summary>
    Task<JeebConversationResponse> CreateConversationAsync(
        CreateJeebConversationRequest request,
        CancellationToken ct);

    /// <summary>
    /// Membership / phase read by correlation key (== request_id). Returns the
    /// participants[] (with role_in_convo + removed_at) and the current phase.
    /// </summary>
    Task<JeebConversationResponse> GetConversationByCorrelationAsync(
        string correlationKey,
        CancellationToken ct);

    /// <summary>
    /// Append a structured/text message. author_id is stamped by the gateway from
    /// the bearer (never the caller body) and forwarded; chat-service persists and
    /// echoes the message projection incl. message_id.
    /// </summary>
    Task<JeebMessageResponse> AppendMessageAsync(
        string conversationId,
        AppendJeebMessageRequest request,
        CancellationToken ct);

    /// <summary>
    /// Viewer-filtered message list. The gateway forwards <paramref name="viewerUserId"/>;
    /// chat-service applies its VisibilityFilter (JEB-51) and returns ONLY what the
    /// viewer may see. The gateway computes no visibility itself.
    /// </summary>
    Task<JeebMessageListResponse> ListMessagesForViewerAsync(
        string conversationId,
        string viewerUserId,
        CancellationToken ct);

    Task<JeebConversationExportPage> ExportMessagesForViewerAsync(
        string conversationId,
        string viewerUserId,
        DateTimeOffset? asOf,
        string? cursor,
        int limit,
        CancellationToken ct) => throw new NotSupportedException(
            "This test double does not implement the canonical paged export route.");

    /// <summary>
    /// S08 A6 — viewer-filtered DELTA read. Returns ONLY the messages created
    /// AFTER <paramref name="cursor"/> that the viewer may see. The gateway forwards
    /// both the viewer and the cursor verbatim; chat-service applies the SAME
    /// VisibilityFilter as <see cref="ListMessagesForViewerAsync"/> (the parity
    /// invariant — the delta path must never leak a message the full read hides).
    /// The gateway computes no visibility and no windowing itself. A non-member is
    /// denied with 403 by chat-service's membership gate, forwarded verbatim.
    /// </summary>
    /// <param name="cursor">
    /// Opaque resume token (a message id or timestamp) the client held before
    /// reconnecting; chat-service interprets it. Forwarded verbatim — the gateway
    /// does not parse it.
    /// </param>
    Task<JeebMessageListResponse> ListMessagesSinceForViewerAsync(
        string conversationId,
        string viewerUserId,
        string cursor,
        CancellationToken ct);

    /// <summary>
    /// Authoritative membership check used by the REST visibility gate
    /// (N1 read-403, N2 realtime-403) and the WS-ticket issuer. chat-service is the
    /// membership authority; the gateway holds no membership state.
    /// </summary>
    Task<JeebConversationMembership> GetMembershipAsync(
        string conversationId,
        string viewerUserId,
        CancellationToken ct);

    /// <summary>
    /// S08 (B) — seat a participant on the conversation aggregate. chat-service
    /// already exposes <c>POST /api/conversations/{id}/participants</c>
    /// (AddParticipantAsync) and owns the membership state; the gateway only
    /// composes the call. Used when a jeeber submits an offer on a request so the
    /// offer jeebers become members and can read (200) / non-members 403. The
    /// gateway never invents membership — it forwards (conversationId, userId, role).
    /// Idempotent on the chat-service side (re-seating the same user is a no-op).
    /// </summary>
    Task<JeebConversationParticipant> AddParticipantAsync(
        string conversationId,
        AddJeebParticipantRequest request,
        CancellationToken ct);

    /// <summary>
    /// S08 (D / H7,N9) — advance the conversation aggregate to the accepted phase.
    /// chat-service exposes <c>PATCH /api/conversations/{id}/phase</c>
    /// (AdvancePhaseAsync) which atomically flips phase=accepted, promotes the
    /// winner role (jeeber_winner) and soft-removes losers. chat-service owns the
    /// transition; the gateway only composes it post-accept and reads back the
    /// resulting phase to surface as <c>conversation_phase</c>. This is the
    /// conversation-aggregate path (NOT the legacy channels provisioner, which
    /// cannot flip phase — see ChatServiceConversationProvisioner PHASE NOTE).
    /// </summary>
    Task<JeebConversationResponse> AdvancePhaseAsync(
        string conversationId,
        AdvanceJeebPhaseRequest request,
        CancellationToken ct);

    /// <summary>
    /// GW5 / W1.6-gateway — SEAT AND SETTLE IN ONE CALL. chat-service's additive
    /// <c>POST /api/conversations/{id}/settle</c> (CB4) seats the winner, sets the
    /// phase and soft-removes the losing bidders against one loaded aggregate,
    /// committed by one store write and followed by one projection reconcile.
    ///
    /// <para>WHY THIS EXISTS AND WHY THE TWO OLDER CALLS ARE NOT ENOUGH. The
    /// post-accept path used to issue <see cref="AddParticipantAsync"/> then
    /// <see cref="AdvancePhaseAsync"/> back-to-back from inside a POST-COMMIT
    /// best-effort block. The accept is the money-committing step and chat is the only
    /// coordination channel a cash handover has, so both partial outcomes are silent
    /// damage: seat-only leaves the winner in a pre-settlement conversation with every
    /// losing bidder still seated, and neither leaves a committed accept the
    /// conversation knows nothing about. "Fail loud" cannot fix it — the caller has
    /// already committed by the time this runs. One request removes the window.</para>
    ///
    /// <para>NEITHER OLDER METHOD IS DEPRECATED OR REMOVED. Both remain on this
    /// interface with their exact previous behaviour, and chat-service keeps both
    /// routes byte-identical; the offer-SUBMIT seat
    /// (<see cref="AddParticipantAsync"/> in <c>RequestOffersController</c>) still uses
    /// the participants route, because seating a bidder mid-auction is not a
    /// settlement.</para>
    ///
    /// <para>CONVERGENT: the request states an end state, so a verbatim replay is the
    /// intended recovery action and is what <c>AcceptChatSettleReconciler</c> issues.
    /// chat-service answers 200 with <c>already_settled: true</c> and STILL reconciles
    /// its direct-read projection — never branch on that flag to skip a retry.</para>
    /// </summary>
    Task<JeebConversationSettleResponse> SettleAsync(
        string conversationId,
        SettleJeebConversationRequest request,
        CancellationToken ct);
}
