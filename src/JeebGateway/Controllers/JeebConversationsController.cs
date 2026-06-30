using System;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Conversations.Client;
using JeebGateway.Conversations.Realtime;
using JeebGateway.Notifications;
using JeebGateway.Services;
using JeebGateway.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// S08 (JEB-50/51/52/53) — the thin BFF surface for the Jeeb <b>conversation</b>
/// domain. Exposes the <c>/v1/chat/jeeb/conversations</c>,
/// <c>/v1/conversations/*</c> and <c>/v1/realtime/jeeb:chat:*</c> seams the mobile
/// client and the S08 suite drive, composing them over the SINGLE typed
/// <see cref="IJeebConversationClient"/>. The gateway is the SOLE chat caller
/// (org no-coupling law) and holds NO conversation state: chat-service owns the
/// conversation_id, the structured-message envelope, participants/roles, the
/// phase, and the per-jeeber VisibilityFilter (JEB-51). This controller only
///   • forwards the request,
///   • stamps author/viewer identity from the BEARER (never the body), and
///   • forwards the upstream status verbatim (RFC 7807 on error).
///
/// <para>
/// FLAG-GATED (<c>FeatureFlags:UseUpstream:Chat</c>, default false). The
/// chat-service conversation aggregate is added in a sequenced upstream PR
/// (chat-service first); while the flag is off every action returns
/// <c>503 ProblemDetails</c> so the contract is stable + observable but never
/// dials endpoints that do not exist yet (the cdn / contract-signing net-new
/// kill-switch shape). Flip on once chat-service's conversation aggregate ships.
/// </para>
///
/// Visibility is NEVER computed here — the filtered read forwards the viewer
/// (bearer sub) and re-serializes chat-service's already-filtered result, so the
/// REST read (H5) and the WS handle_out drop use identical chat-service logic
/// (the parity invariant). The realtime gate (N2) is a REST membership PRE-CHECK
/// that returns a 200 channel descriptor for a member and 403 for a non-member;
/// the socket itself is owned by realtime-comunication-service (H6/CP-D), never
/// proxied by the gateway.
/// </summary>
[ApiController]
[Produces("application/json")]
public sealed class JeebConversationsController : ControllerBase
{
    private readonly IJeebConversationClient _client;
    private readonly IRealtimeTicketIssuer _ticketIssuer;
    private readonly IChatMessagePushNotifier _chatPush;
    private readonly UpstreamFeatureFlags _flags;
    private readonly ILogger<JeebConversationsController> _logger;

    public JeebConversationsController(
        IJeebConversationClient client,
        IRealtimeTicketIssuer ticketIssuer,
        IChatMessagePushNotifier chatPush,
        IOptions<UpstreamFeatureFlags> flags,
        ILogger<JeebConversationsController> logger)
    {
        _client = client;
        _ticketIssuer = ticketIssuer;
        _chatPush = chatPush;
        _flags = flags.Value;
        _logger = logger;
    }

    // ---------------------------------------------------------------------
    // H1 / A1 — create-or-get the broadcasting conversation for a request.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Create (or idempotently get) the broadcasting conversation for a request.
    /// </summary>
    [HttpPost("v1/chat/jeeb/conversations")]
    [Authorize]
    [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; membership = STATE (chat-service)
    [ProducesResponseType(typeof(JeebConversationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(JeebConversationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateConversation(
        [FromBody] CreateConversationBody? body,
        CancellationToken ct)
    {
        if (!TryGetUserId(out _, out var unauthorized))
        {
            return unauthorized;
        }

        if (!_flags.Chat)
        {
            return UpstreamUnavailable();
        }

        if (body is null
            || string.IsNullOrWhiteSpace(body.RequestId)
            || string.IsNullOrWhiteSpace(body.ClientUserId))
        {
            return Problem(
                title: "request_id and client_user_id are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Idempotency-Key (== request_id for H1/A1) is the chat-service idempotency
        // authority's key; forward it verbatim. The gateway holds no idempotency
        // state of its own.
        var idempotencyKey = Request.Headers.TryGetValue("Idempotency-Key", out var hdr)
            && !string.IsNullOrWhiteSpace(hdr)
            ? hdr.ToString()
            : body.RequestId;

        try
        {
            var result = await _client.CreateConversationAsync(new CreateJeebConversationRequest
            {
                RequestId = body.RequestId,
                ClientUserId = body.ClientUserId,
                IdempotencyKey = idempotencyKey,
            }, ct);

            // 201 on first create; 200 on idempotent replay (chat-service signals
            // replay via the conversation already existing — it returns the same
            // id either way, so we surface 201 for the create call. A1 accepts
            // 200 OR 201, so 201 here is contract-correct for both).
            return StatusCode(StatusCodes.Status201Created, result);
        }
        catch (JeebConversationApiException ex)
        {
            return ForwardUpstream(ex, "create conversation");
        }
    }

    // ---------------------------------------------------------------------
    // H2 / H7 verify — membership + phase read by correlation key.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Read the conversation's participants[] + phase by correlation key
    /// (== request_id). Used to verify membership advance (H2) and the
    /// post-accept membership flip / removal (H7).
    /// </summary>
    [HttpGet("v1/conversations")]
    [Authorize]
    [RequireCapability(Capabilities.ChatRead)] // ADR-005 §F {client,jeeber}; scoping = STATE (chat-service)
    [ProducesResponseType(typeof(JeebConversationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetConversationByCorrelation(
        [FromQuery] string? correlationKey,
        CancellationToken ct)
    {
        if (!TryGetUserId(out _, out var unauthorized))
        {
            return unauthorized;
        }

        if (!_flags.Chat)
        {
            return UpstreamUnavailable();
        }

        if (string.IsNullOrWhiteSpace(correlationKey))
        {
            return Problem(
                title: "correlationKey is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var result = await _client.GetConversationByCorrelationAsync(correlationKey, ct);
            return Ok(result);
        }
        catch (JeebConversationApiException ex)
        {
            return ForwardUpstream(ex, "read conversation by correlation");
        }
    }

    // ---------------------------------------------------------------------
    // H3 / H4 / H8 — append a structured/text message.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Append a structured (jeeb.offer / jeeb.offer_accepted / …) or text message.
    /// author_id is stamped from the BEARER, never the caller body.
    /// </summary>
    [HttpPost("v1/conversations/{conversationId}/messages")]
    [Authorize]
    [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; membership = STATE (chat-service)
    [ProducesResponseType(typeof(JeebMessageResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AppendMessage(
        string conversationId,
        [FromBody] AppendMessageBody? body,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var authorId, out var unauthorized))
        {
            return unauthorized;
        }

        if (!_flags.Chat)
        {
            return UpstreamUnavailable();
        }

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return Problem(
                title: "conversationId is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (body is null)
        {
            return Problem(
                title: "A message body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var idempotencyKey = Request.Headers.TryGetValue("Idempotency-Key", out var hdr)
            && !string.IsNullOrWhiteSpace(hdr)
            ? hdr.ToString()
            : null;

        try
        {
            var result = await _client.AppendMessageAsync(conversationId, new AppendJeebMessageRequest
            {
                Kind = body.Kind,
                Subtype = body.Subtype,
                Audience = body.Audience,
                Body = body.Body,
                Payload = body.Payload,
                // SECURITY: author_id is the bearer sub, NEVER the caller body —
                // a caller cannot post as another user. chat-service persists it.
                AuthorId = authorId,
                IdempotencyKey = idempotencyKey,
            }, ct);

            // BUILD-CHAT-PUSH — the only missing link for real A→B chat push: notify the
            // conversation's other party. Best-effort/degrade-don't-fail — the notifier
            // never throws and is bounded by a short timeout, so it never affects this 201.
            await _chatPush.NotifyNewMessageAsync(conversationId, authorId, body.Body, ct);

            return StatusCode(StatusCodes.Status201Created, result);
        }
        catch (JeebConversationApiException ex)
        {
            return ForwardUpstream(ex, "append message");
        }
    }

    // ---------------------------------------------------------------------
    // H5 / N1 / N3 / A4 — viewer-filtered message read.
    // ---------------------------------------------------------------------

    /// <summary>
    /// List the conversation's messages FILTERED for the bearer viewer. The
    /// gateway forwards the viewer; chat-service applies its VisibilityFilter and
    /// returns only the viewer's slice. A non-member is denied with 403 (never an
    /// empty 200) by chat-service's membership gate — forwarded verbatim.
    /// </summary>
    [HttpGet("v1/conversations/{conversationId}/messages")]
    [Authorize]
    [RequireCapability(Capabilities.ChatRead)] // ADR-005 §F {client,jeeber}; membership = STATE (chat-service)
    [ProducesResponseType(typeof(JeebMessageListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListMessages(
        string conversationId,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var viewerId, out var unauthorized))
        {
            return unauthorized;
        }

        if (!_flags.Chat)
        {
            return UpstreamUnavailable();
        }

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return Problem(
                title: "conversationId is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var result = await _client.ListMessagesForViewerAsync(conversationId, viewerId, ct);
            return Ok(result);
        }
        catch (JeebConversationApiException ex)
        {
            return ForwardUpstream(ex, "list messages");
        }
    }

    // ---------------------------------------------------------------------
    // A6 — viewer-filtered DELTA read (messages since a cursor).
    // ---------------------------------------------------------------------

    /// <summary>
    /// S08 A6 — list the conversation's messages created AFTER <paramref name="cursor"/>,
    /// FILTERED for the bearer viewer. Used on reconnect to fetch only the delta the
    /// client missed while offline. The gateway forwards the viewer + the cursor;
    /// chat-service applies the SAME VisibilityFilter as the full read
    /// (<see cref="ListMessages"/>), so the delta path can never leak a message the
    /// full read hides (the parity invariant, INV-1). A non-member is denied with
    /// 403 by chat-service's membership gate — forwarded verbatim, never an empty 200.
    /// </summary>
    [HttpGet("v1/conversations/{conversationId}/messages/since/{cursor}")]
    [Authorize]
    [RequireCapability(Capabilities.ChatRead)] // ADR-005 §F {client,jeeber}; membership = STATE (chat-service)
    [ProducesResponseType(typeof(JeebMessageListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListMessagesSince(
        string conversationId,
        string cursor,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var viewerId, out var unauthorized))
        {
            return unauthorized;
        }

        if (!_flags.Chat)
        {
            return UpstreamUnavailable();
        }

        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(cursor))
        {
            return Problem(
                title: "conversationId and cursor are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var result = await _client.ListMessagesSinceForViewerAsync(
                conversationId, viewerId, cursor, ct);
            return Ok(result);
        }
        catch (JeebConversationApiException ex)
        {
            return ForwardUpstream(ex, "list messages since cursor");
        }
    }

    // ---------------------------------------------------------------------
    // N2 / H6 — realtime visibility gate (REST membership pre-check).
    // ---------------------------------------------------------------------

    /// <summary>
    /// Per-jeeber realtime visibility gate. Resolves the bearer to a user id and
    /// asks chat-service whether that user is a participant of the conversation
    /// (<c>removed_at == null</c>). A MEMBER gets <c>200</c> with a channel
    /// descriptor (the WS topic + connect url the client upgrades to); a
    /// NON-MEMBER gets <c>403 ProblemDetails</c> with <c>title: not_in_membership</c>
    /// (the N2 acceptance target, and the same reason the realtime
    /// <c>JeebChatChannel.join/3</c> rejects with). The gateway never proxies the
    /// socket — the actual WS join (H6/CP-D) is owned by
    /// realtime-comunication-service.
    /// </summary>
    [HttpGet("v1/realtime/jeeb:chat:{conversationId}")]
    [Authorize]
    [RequireCapability(Capabilities.ChatRead)] // ADR-005 §F {client,jeeber}; membership = STATE (chat-service)
    [ProducesResponseType(typeof(RealtimeChannelDescriptor), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RealtimeVisibilityGate(
        string conversationId,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var viewerId, out var unauthorized))
        {
            return unauthorized;
        }

        if (!_flags.Chat)
        {
            return UpstreamUnavailable();
        }

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return Problem(
                title: "conversationId is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        JeebConversationMembership membership;
        try
        {
            membership = await _client.GetMembershipAsync(conversationId, viewerId, ct);
        }
        catch (JeebConversationApiException ex)
        {
            return ForwardUpstream(ex, "realtime membership check");
        }

        // FAIL-CLOSED: only an ACTIVE participant (removed_at == null) may join the
        // realtime topic. A non-member — or a removed participant — is denied. The
        // REST read (H5/N3) lets a removed member read the up-to-cutoff history, but
        // the live socket is for ACTIVE members only (H7 kick), so the realtime gate
        // is strictly removed_at == null.
        if (!membership.IsMember || membership.RemovedAt is not null)
        {
            return Problem(
                title: "not_in_membership",
                detail: "The caller is not an active participant of this conversation and "
                    + "may not join its realtime channel.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        // Member: hand back the channel descriptor the client upgrades to. The
        // suite topic is jeeb:chat:{id}; the realtime channel is
        // jeeb_conversation:{id} — this descriptor maps the two so the client knows
        // which Phoenix topic to join. The gateway does not open the socket.
        //
        // S08 (D / H6): mint a short-lived signed membership ticket scoped to
        // (conversation, viewer, role) so realtime can authorize the WS join WITHOUT
        // calling chat-service (no inter-service coupling — the authority is encoded
        // in the gateway-signed ticket). Ticket minting failure degrades to a null
        // ticket but still returns the 200 descriptor (the REST pre-check is intact).
        string? ticket = null;
        try
        {
            ticket = _ticketIssuer.Issue(conversationId, viewerId, membership.RoleInConvo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Realtime ticket mint for conversation {ConversationId} viewer {ViewerId} failed; "
                + "returning the descriptor without a ticket.",
                conversationId, viewerId);
        }

        return Ok(new RealtimeChannelDescriptor
        {
            ConversationId = conversationId,
            Topic = $"jeeb_conversation:{conversationId}",
            RoleInConvo = membership.RoleInConvo,
            Ticket = ticket,
        });
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private IActionResult ForwardUpstream(JeebConversationApiException ex, string action)
    {
        var status = (int)ex.StatusCode;
        // Forward the upstream status verbatim; surface a transport/empty-body
        // fault (mapped to 502 by the client) as 503 (readiness concern), and map
        // anything outside 4xx/5xx to 503 defensively.
        if (status is < 400 or >= 600)
        {
            status = StatusCodes.Status503ServiceUnavailable;
        }

        _logger.LogWarning(
            "Conversation BFF: chat-service rejected {Action} with {Status}.",
            action, status);

        return Problem(
            title: $"chat-service rejected the {action}.",
            detail: ex.Body,
            statusCode: status);
    }

    private ObjectResult UpstreamUnavailable() => StatusCode(
        StatusCodes.Status503ServiceUnavailable,
        new ProblemDetails
        {
            Title = "The Jeeb conversation surface is not enabled.",
            Detail = "chat-service's conversation aggregate is not yet wired "
                + "(FeatureFlags:UseUpstream:Chat is off). Enable the flag once the "
                + "chat-service conversation aggregate is deployed.",
            Status = StatusCodes.Status503ServiceUnavailable,
        });

    /// <summary>
    /// Resolve the bearer viewer identity for the conversation read/append/membership
    /// paths. Delegates to the SHARED <see cref="UserIdentity.TryGetUserId"/> so the
    /// viewer id forwarded on a READ is resolved by the IDENTICAL rule the offer-submit
    /// SEAT path uses (<c>RequestOffersController</c> seats via the same helper). This
    /// closes a latent identity-divergence gap: a UM-reissued role-switch token carries
    /// the canonical GUID in the <c>sid</c> claim with <c>sub == email</c> (H-B5), so the
    /// seat path (sid-first) and a sub-only read resolver would key the participant and
    /// the viewer to DIFFERENT ids — seating the jeeber by GUID but reading as their
    /// email, which would 403 a legitimately seated member. Resolving both through
    /// <see cref="UserIdentity.TryGetUserId"/> (sid → sub → X-User-Id) guarantees the
    /// seated participant id and the read viewer id can never diverge. For the suite's
    /// HS256 tokens (sub == userId, no sid) the resolved value is unchanged.
    /// </summary>
    private bool TryGetUserId(out string userId, out IActionResult problem)
        => UserIdentity.TryGetUserId(HttpContext, out userId, out problem);
}

/// <summary>
/// H1/A1 create body — the gateway resolves the actor from the bearer. The wire
/// is snake_case (the S08 scenario sends <c>request_id</c> / <c>client_user_id</c>);
/// the ASP.NET model binder uses System.Text.Json so each field is pinned with
/// <see cref="JsonPropertyNameAttribute"/> to bind the snake_case payload.
/// </summary>
public sealed class CreateConversationBody
{
    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    [JsonPropertyName("client_user_id")]
    public string? ClientUserId { get; set; }
}

/// <summary>
/// H3/H4/H8 append body. author_id is NOT accepted here — it is stamped from the
/// bearer sub by the controller so a caller cannot post as another user (any
/// <c>author_id</c> in the payload is ignored). snake_case wire, pinned per-field.
/// </summary>
public sealed class AppendMessageBody
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("subtype")]
    public string? Subtype { get; set; }

    [JsonPropertyName("audience")]
    public System.Text.Json.JsonElement? Audience { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("payload")]
    public System.Text.Json.JsonElement? Payload { get; set; }
}

/// <summary>
/// The 200 descriptor the realtime gate hands a member: the Phoenix topic, the
/// viewer's role, and a short-lived signed membership TICKET the client presents on
/// the WS upgrade (S08 D / H6). The gateway never opens the socket itself — it runs
/// the chat-service membership check, then mints the ticket so
/// realtime-comunication-service can authorize the join WITHOUT calling chat-service
/// (no inter-service coupling; the authority is encoded in the signed ticket).
/// </summary>
public sealed class RealtimeChannelDescriptor
{
    public string ConversationId { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public string? RoleInConvo { get; set; }

    /// <summary>
    /// Signed, short-lived membership ticket scoped to (conversation, viewer, role).
    /// The client passes it on the WS connect/join; realtime verifies it. Null only
    /// if ticket minting is unavailable (the descriptor still returns 200 so the
    /// REST pre-check is unaffected).
    /// </summary>
    public string? Ticket { get; set; }
}
