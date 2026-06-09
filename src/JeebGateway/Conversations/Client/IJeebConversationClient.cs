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
}
