using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Conversations.Client;
using JeebGateway.Services;
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
    private readonly UpstreamFeatureFlags _flags;
    private readonly ILogger<JeebChatMessagesController> _logger;

    public JeebChatMessagesController(
        IJeebConversationClient client,
        IOptions<UpstreamFeatureFlags> flags,
        ILogger<JeebChatMessagesController> logger)
    {
        _client = client;
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

        var idempotencyKey = Request.Headers.TryGetValue("Idempotency-Key", out var hdr)
            && !string.IsNullOrWhiteSpace(hdr)
            ? hdr.ToString()
            : null;

        // Encode the mobile's nested body object into chat-service's flat body
        // string so it round-trips verbatim regardless of message kind.
        var flatBody = EncodeBody(body.Body);

        try
        {
            var created = await _client.AppendMessageAsync(conversationId, new AppendJeebMessageRequest
            {
                Kind = string.IsNullOrWhiteSpace(body.Kind) ? "text" : body.Kind,
                Body = flatBody,
                // SECURITY: author is the bearer sub, NEVER the body senderId.
                AuthorId = authorId,
                IdempotencyKey = idempotencyKey,
            }, ct);

            return StatusCode(StatusCodes.Status201Created, ProjectMessage(created));
        }
        catch (JeebConversationApiException ex)
        {
            return ForwardUpstream(ex, "send message");
        }
    }

    // ---------------------------------------------------------------------
    // translation helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Encode the mobile nested <c>body</c> object into the flat string chat-service
    /// stores. A null/empty body encodes to an empty JSON object so the read path
    /// always decodes back to a (possibly empty) map. Serialized with default STJ so
    /// the nested keys (text/url/lat/…) survive intact.
    /// </summary>
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
