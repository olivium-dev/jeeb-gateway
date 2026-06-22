using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Conversations.Client;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// iter6 B6 — the MOBILE chat-message BFF. The mobile chat gateway
/// (<c>DioChatGateway</c>) drives three routes the S08 conversation BFF does NOT
/// expose under the <c>/v1/chat/jeeb/conversations/{id}</c> prefix the client
/// hard-codes:
/// <list type="bullet">
///   <item><c>GET  /v1/chat/jeeb/conversations/{id}</c>          → conversation phase</item>
///   <item><c>GET  /v1/chat/jeeb/conversations/{id}/messages</c> → history (mobile shape)</item>
///   <item><c>POST /v1/chat/jeeb/conversations/{id}/messages</c> → send (mobile shape)</item>
/// </list>
/// Before this controller those three paths 404'd (only
/// <c>POST /v1/chat/jeeb/conversations</c> was wired), so send/receive never
/// round-tripped through the gateway.
///
/// <para>
/// SCHEMA TRANSLATION (the B6 mismatch). The mobile contract is:
///   • send  body: <c>{ kind, senderId, body:{ ...nested object... } }</c>
///   • read  item: <c>{ id, senderId, createdAt, kind, body:{ ...nested... } }</c>
///   • list envelope key is <c>items</c> (a list).
/// chat-service (<c>:5803</c>) speaks a FLAT contract:
///   • send  body: <c>{ kind, author_id, body:&lt;flat string&gt; }</c>
///   • read  item: <c>{ message_id, kind, author_id, body:&lt;flat string&gt;, created_at }</c>
///   • list envelope key is <c>messages</c>.
/// This BFF bridges the two so the MOBILE CONTRACT WORKS UNCHANGED (no mobile
/// edit): on send it JSON-encodes the mobile's nested <c>body</c> object into
/// chat-service's flat <c>body</c> string and stamps <c>author_id</c> from the
/// BEARER (never <c>senderId</c> in the payload — a caller cannot post as another
/// user); on read it decodes the flat <c>body</c> string back into the nested
/// object (falling back to <c>{text: &lt;body&gt;}</c> for a plain-text/legacy body
/// that is not a JSON object, so the pre-existing plain-text and any system
/// messages still render). The flat <c>body</c> string round-trips chat-service
/// verbatim (proven live), so EVERY message kind survives intact without relying
/// on chat-service's payload-object fidelity.
/// </para>
///
/// FLAG-GATED on <c>FeatureFlags:UseUpstream:Chat</c> (same gate + 503 shape as
/// <see cref="JeebConversationsController"/>); the gateway holds no conversation
/// state. Identity is resolved via the shared <see cref="UserIdentity"/> helper so
/// the viewer/author id matches the S08 seat/read paths exactly.
/// </summary>
[ApiController]
[Produces("application/json")]
public sealed class JeebChatMessagesController : ControllerBase
{
    private readonly IJeebConversationClient _client;
    private readonly IRealtimeCommunicationClient _realtime;
    private readonly JeebGateway.Push.IEventPushNotifier _push;
    private readonly UpstreamFeatureFlags _flags;
    private readonly ILogger<JeebChatMessagesController> _logger;

    public JeebChatMessagesController(
        IJeebConversationClient client,
        IRealtimeCommunicationClient realtime,
        JeebGateway.Push.IEventPushNotifier push,
        IOptions<UpstreamFeatureFlags> flags,
        ILogger<JeebChatMessagesController> logger)
    {
        _client = client;
        _realtime = realtime;
        _push = push;
        _flags = flags.Value;
        _logger = logger;
    }

    // ---------------------------------------------------------------------
    // GET conversation (phase read) — mobile DioChatGateway.loadPhase
    // ---------------------------------------------------------------------

    /// <summary>
    /// Read the conversation projection by its conversation_id. The mobile client
    /// reads <c>phase</c> off the body to decide broadcasting vs accepted UI.
    /// </summary>
    [HttpGet("v1/chat/jeeb/conversations/{conversationId}")]
    [Authorize]
    [RequireCapability(Capabilities.ChatRead)]
    [ProducesResponseType(typeof(MobileConversationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetConversation(
        string conversationId,
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

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return Problem(
                title: "conversationId is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var convo = await _client.GetConversationByIdAsync(conversationId, ct);
            return Ok(new MobileConversationResponse
            {
                Id = convo.ConversationId,
                CorrelationKey = convo.CorrelationKey,
                Phase = convo.Phase,
            });
        }
        catch (JeebConversationApiException ex)
        {
            return ForwardUpstream(ex, "read conversation");
        }
    }

    // ---------------------------------------------------------------------
    // GET messages — mobile DioChatGateway.loadHistory
    // ---------------------------------------------------------------------

    /// <summary>
    /// List the conversation's messages FILTERED for the bearer viewer, projected
    /// into the mobile <c>{ items:[ { id, senderId, createdAt, kind, body } ] }</c>
    /// shape (chat-service owns the VisibilityFilter; the gateway forwards the
    /// viewer and re-projects the result).
    /// </summary>
    [HttpGet("v1/chat/jeeb/conversations/{conversationId}/messages")]
    [Authorize]
    [RequireCapability(Capabilities.ChatRead)]
    [ProducesResponseType(typeof(MobileMessageList), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
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
            var upstream = await _client.ListMessagesForViewerAsync(conversationId, viewerId, ct);
            var items = new List<MobileMessage>(upstream.Messages.Count);
            foreach (var m in upstream.Messages)
            {
                items.Add(ProjectMessage(m));
            }

            return Ok(new MobileMessageList { Items = items });
        }
        catch (JeebConversationApiException ex)
        {
            return ForwardUpstream(ex, "list messages");
        }
    }

    // ---------------------------------------------------------------------
    // POST message — mobile DioChatGateway.send
    // ---------------------------------------------------------------------

    /// <summary>
    /// Append a message in the MOBILE shape. The nested <c>body</c> object is
    /// JSON-encoded into chat-service's flat <c>body</c> string; <c>author_id</c> is
    /// stamped from the bearer (the payload <c>senderId</c> is IGNORED for
    /// authorization — a caller cannot post as another user). The created message is
    /// projected back into the mobile shape (carrying <c>id</c>).
    /// </summary>
    [HttpPost("v1/chat/jeeb/conversations/{conversationId}/messages")]
    [Authorize]
    [RequireCapability(Capabilities.ChatSend)]
    [ProducesResponseType(typeof(MobileMessage), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SendMessage(
        string conversationId,
        [FromBody] MobileSendMessageBody? body,
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

        var clientIdempotencyKey = Request.Headers.TryGetValue("Idempotency-Key", out var hdr)
            && !string.IsNullOrWhiteSpace(hdr)
            ? hdr.ToString()
            : null;

        // Encode the mobile's nested body object into chat-service's flat body
        // string so it round-trips verbatim regardless of message kind.
        var flatBody = EncodeBody(body.Body);
        var kind = string.IsNullOrWhiteSpace(body.Kind) ? "text" : body.Kind;

        // CHAT BURST-PERSISTENCE FIX (iter6 race hardening). The mobile chat
        // composer mints its Idempotency-Key as `msg-<conversationId>-<counter>`
        // where the counter is a PER-CUBIT-INSTANCE value that restarts at 0 every
        // time the chat screen is (re)opened / the socket reconnects, and is NOT
        // namespaced by sender. So under rapid two-way sending the SAME client key
        // recurs for DISTINCT messages — either across the two participants (client
        // #i vs jeeber #i on one conversation) or for the same author after a
        // counter reset. chat-service de-dupes by (idempotency_key, author, kind),
        // so a recurring key made chat-service treat a brand-new distinct message as
        // an idempotent replay: it returned 201 to the app but DID NOT persist /
        // fan out the new message (the intermittent 'optimistic 201 but not durable'
        // loss). FIX: forward chat-service a CONTENT-SCOPED key derived from the
        // author + the client key + a hash of the actual (kind, body) payload.
        //  - A genuine retry of the SAME logical message (identical author + client
        //    key + payload) derives the SAME key  -> still de-duped exactly-once.
        //  - A DISTINCT message that merely reuses/collides the client key derives a
        //    DIFFERENT key (different payload hash and/or author)  -> persists.
        // Persistence becomes exactly-once-durable; the realtime fan-out stays
        // best-effort (below). Degrade-don't-fail: if no client key is sent we
        // forward null (chat-service then skips de-dupe, prior behaviour).
        var idempotencyKey = DeriveDurableIdempotencyKey(authorId, kind, flatBody, clientIdempotencyKey);

        try
        {
            var created = await _client.AppendMessageAsync(conversationId, new AppendJeebMessageRequest
            {
                Kind = kind,
                Body = flatBody,
                // SECURITY: author is the bearer sub, NEVER the body senderId.
                AuthorId = authorId,
                IdempotencyKey = idempotencyKey,
            }, ct);

            // CHAT LIVE-PUSH (iter6): fan the persisted message out to the OTHER
            // participant's realtime stream (user:{recipientId}) so a connected
            // recipient receives it live with no reload. Degrade-don't-fail: a
            // realtime hiccup never breaks the send (history still works over REST).
            await TryFanOutAsync(conversationId, authorId, created, ct);

            return StatusCode(StatusCodes.Status201Created, ProjectMessage(created));
        }
        catch (JeebConversationApiException ex)
        {
            return ForwardUpstream(ex, "send message");
        }
    }

    // ---------------------------------------------------------------------
    // realtime fan-out (CHAT LIVE-PUSH)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Publish the just-persisted message to the OTHER participant's realtime
    /// stream (<c>user:{recipientId}</c> under topic <c>jeeb:chat</c>) so a
    /// connected recipient receives it live. Recipient(s) = the conversation's
    /// ACTIVE participants whose <c>user_id != senderId</c> (resolved from the
    /// conversation aggregate). The published <c>data</c> matches the shape the
    /// mobile <c>LiveRealtimeChatSocket</c> parses:
    /// <c>{ messageId, senderId, recipientId, type, body, sentAt }</c> where
    /// <c>body</c> is the SAME nested object the REST send/read path carries
    /// (decoded from chat-service's flat string), so the live bubble and the
    /// reloaded bubble render identically.
    /// DEGRADE-DON'T-FAIL: any realtime error is swallowed (logged) — the send
    /// already succeeded and the REST history is the source of truth.
    /// </summary>
    private async Task TryFanOutAsync(
        string conversationId,
        string senderId,
        JeebMessageResponse created,
        CancellationToken ct)
    {
        if (!_flags.Realtime)
        {
            return;
        }

        try
        {
            var convo = await _client.GetConversationByIdAsync(conversationId, ct);
            if (convo?.Participants is null || convo.Participants.Count == 0)
            {
                return;
            }

            // The message projection (nested body object) the recipient renders —
            // identical to the REST read projection so live == reload.
            var projected = ProjectMessage(created);
            var bodyJson = projected.Body.GetRawText();
            using var bodyDoc = JsonDocument.Parse(bodyJson);
            var sentAt = (created.CreatedAt ?? DateTimeOffset.UtcNow).ToString("O");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in convo.Participants)
            {
                if (p is null || string.IsNullOrWhiteSpace(p.UserId))
                {
                    continue;
                }
                // Skip the sender and any soft-removed (losing) participant.
                if (string.Equals(p.UserId, senderId, StringComparison.Ordinal)
                    || p.RemovedAt is not null
                    || !seen.Add(p.UserId))
                {
                    continue;
                }

                var data = new Dictionary<string, object?>
                {
                    ["messageId"] = projected.Id,
                    ["senderId"] = senderId,
                    ["recipientId"] = p.UserId,
                    // FIX (iter6 GAP A1): carry the conversation id so the realtime
                    // JeebChatV2Channel (joined on jeeb:chat:<conv_id>) can route an
                    // HTTP-ingested fan-out frame ONLY to the socket(s) of THIS
                    // conversation, never bleeding into another conversation the same
                    // recipient happens to have open. Both snake_case + camelCase keys
                    // are emitted so the channel matcher is tolerant.
                    ["conversationId"] = conversationId,
                    ["conversation_id"] = conversationId,
                    ["type"] = projected.Kind,
                    // Carry the SAME nested body object the REST path uses (as raw
                    // JSON) so the mobile socket normalizer reads it unchanged.
                    ["body"] = bodyDoc.RootElement.Clone(),
                    ["sentAt"] = sentAt,
                };

                await _realtime.FanOutChatMessageAsync(p.UserId, data, ct);

                // SEND-ON-EVENT (iter6): also push the OTHER participant so a
                // BACKGROUNDED recipient gets a heads-up in the shade (not just a
                // live in-app bubble over the WS). Recipient is THIS non-sender
                // participant (p.UserId); the actor never pushes themselves.
                // Best-effort (the notifier swallows all faults), so a push hiccup
                // never affects the realtime fan-out or the already-committed send.
                await _push.NotifyUserAsync(
                    p.UserId,
                    "New message",
                    BuildChatPreview(projected),
                    new Dictionary<string, string>
                    {
                        ["type"] = "chat",
                        ["conversationId"] = conversationId,
                        ["messageId"] = projected.Id,
                        ["senderId"] = senderId,
                    },
                    ct);
            }
        }
        catch (Exception ex)
        {
            // Never let a realtime hiccup fail the send. The message is already
            // persisted; the recipient still gets it on reload over REST.
            _logger.LogWarning(ex, "Chat live-push fan-out failed for conversation {ConversationId}.", conversationId);
        }
    }

    // ---------------------------------------------------------------------
    // translation helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Build a SMALL push preview from the projected message body. For a text
    /// message the nested body is <c>{ text: \"...\" }</c> (the same object the REST
    /// read/live paths carry), so we surface a trimmed <c>text</c>; for a non-text
    /// kind (image/location/system) we surface a generic label so the shade still
    /// shows something meaningful without leaking a large payload. Best-effort:
    /// any parse hiccup degrades to a generic label.
    /// </summary>
    private static string BuildChatPreview(MobileMessage projected)
    {
        const int max = 120;
        try
        {
            if (projected.Body.ValueKind == JsonValueKind.Object
                && projected.Body.TryGetProperty("text", out var textEl)
                && textEl.ValueKind == JsonValueKind.String)
            {
                var text = textEl.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    text = text.Trim();
                    return text.Length <= max ? text : text.Substring(0, max) + "\u2026";
                }
            }
        }
        catch
        {
            // fall through to the generic label
        }

        var kind = string.IsNullOrWhiteSpace(projected.Kind) ? "text" : projected.Kind;
        return kind switch
        {
            "image" => "Sent a photo",
            "location" => "Shared a location",
            _ => "Sent you a message",
        };
    }

    /// <summary>
    /// Encode the mobile nested <c>body</c> object into the flat string chat-service
    /// stores. A null/empty body encodes to an empty JSON object so the read path
    /// always decodes back to a (possibly empty) map. Serialized with default STJ so
    /// the nested keys (text/url/lat/…) survive intact.
    /// </summary>
    /// <summary>
    /// Derive a DURABLE, content-scoped idempotency key for the chat-service append.
    /// Combines the authenticated author, the message (kind, body), and the client's
    /// own key (if any) into a single stable token: <c>v2:&lt;author&gt;:&lt;clientKey&gt;:&lt;sha256(kind|body)&gt;</c>.
    /// Two appends collapse (de-dupe) IFF they are the SAME author posting the SAME
    /// (kind, body) under the SAME client key — i.e. a genuine retry. A distinct
    /// message can never be dropped just because the mobile composer recycled a
    /// per-cubit counter value across reconnects or participants. Returns null only
    /// when the caller sent no client key (preserving the prior no-de-dupe path).
    /// </summary>
    private static string? DeriveDurableIdempotencyKey(
        string authorId, string kind, string flatBody, string? clientKey)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
        {
            // No client key -> no de-dupe (unchanged behaviour). The await-then-201
            // contract still guarantees a 201 only follows a confirmed persist.
            return null;
        }

        var canonical = string.Concat(
            authorId ?? string.Empty, "\u0001",
            kind ?? string.Empty, "\u0001",
            flatBody ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var digest = Convert.ToHexString(hash);
        return string.Concat("v2:", authorId, ":", clientKey, ":", digest);
    }

    private static string EncodeBody(JsonElement? body)
    {
        if (body is null || body.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "{}";
        }

        return body.Value.GetRawText();
    }

    /// <summary>
    /// Project a chat-service message onto the mobile shape, decoding the flat
    /// <c>body</c> string back into the nested object. If the stored body is NOT a
    /// JSON object (a pre-existing plain-text message, or a system line), it is
    /// surfaced as <c>{ "text": &lt;body&gt; }</c> so the mobile text bubble still
    /// renders it.
    /// </summary>
    private MobileMessage ProjectMessage(JeebMessageResponse m)
    {
        return new MobileMessage
        {
            Id = m.MessageId,
            SenderId = m.AuthorId ?? string.Empty,
            CreatedAt = m.CreatedAt,
            Kind = string.IsNullOrWhiteSpace(m.Kind) ? "text" : m.Kind,
            Body = DecodeBody(m.Body),
        };
    }

    private JsonElement DecodeBody(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return WrapAsText(string.Empty);
        }

        var trimmed = stored.TrimStart();
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(stored);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return doc.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                // not valid JSON — fall through to the plain-text wrap.
                _logger.LogDebug("Chat message body is not a JSON object; wrapping as text.");
            }
        }

        return WrapAsText(stored);
    }

    private static JsonElement WrapAsText(string text)
    {
        using var doc = JsonDocument.Parse(
            JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = text }));
        return doc.RootElement.Clone();
    }

    // ---------------------------------------------------------------------
    // shared helpers (mirrors JeebConversationsController)
    // ---------------------------------------------------------------------

    private IActionResult ForwardUpstream(JeebConversationApiException ex, string action)
    {
        var status = (int)ex.StatusCode;
        if (status is < 400 or >= 600)
        {
            status = StatusCodes.Status503ServiceUnavailable;
        }

        _logger.LogWarning(
            "Mobile chat BFF: chat-service rejected {Action} with {Status}.",
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
            Title = "The Jeeb chat surface is not enabled.",
            Detail = "chat-service is not wired (FeatureFlags:UseUpstream:Chat is off).",
            Status = StatusCodes.Status503ServiceUnavailable,
        });

    private bool TryGetUserId(out string userId, out IActionResult problem)
        => UserIdentity.TryGetUserId(HttpContext, out userId, out problem);
}

// -------------------------------------------------------------------------
// mobile-shaped DTOs (camelCase out via the default STJ policy)
// -------------------------------------------------------------------------

/// <summary>
/// The mobile send body: <c>{ kind, senderId, body:{...} }</c>. <c>senderId</c> is
/// bound but IGNORED for authorization (the author is the bearer). <c>body</c> is an
/// open nested object the gateway JSON-encodes into chat-service's flat body string.
/// </summary>
public sealed class MobileSendMessageBody
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("senderId")]
    public string? SenderId { get; set; }

    [JsonPropertyName("body")]
    public JsonElement? Body { get; set; }
}

/// <summary>One message in the mobile shape.</summary>
public sealed class MobileMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("senderId")]
    public string SenderId { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "text";

    [JsonPropertyName("body")]
    public JsonElement Body { get; set; }
}

/// <summary>The mobile list envelope — <c>items</c> (not chat-service's <c>messages</c>).</summary>
public sealed class MobileMessageList
{
    [JsonPropertyName("items")]
    public IList<MobileMessage> Items { get; set; } = new List<MobileMessage>();
}

/// <summary>The mobile conversation projection — the client reads <c>phase</c>.</summary>
public sealed class MobileConversationResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("correlationKey")]
    public string CorrelationKey { get; set; } = string.Empty;

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;
}
