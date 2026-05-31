using System.Security.Claims;
using JeebGateway.Chat;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// REST shim over the chat surface (T-backend-012 / T-backend-bff-chat).
///
/// Send and History are aggregated HERE in the gateway BFF via
/// <see cref="IChatServiceClient"/>, which calls only the GENERIC chat-service
/// member/channel/session/message primitives (Firestore-backed,
/// <c>Services:Chat:BaseUrl</c>). No product-specific chat route exists on the
/// shared chat-service; the Jeeb 1:1 conversation mapping is gateway-owned.
/// MarkRead is handled by <see cref="IChatDispatcher"/>, which now persists the
/// receipt on the generic chat-service via <see cref="IChatServiceClient"/>
/// (POST .../messages/{messageId}/seen) — the gateway holds no in-memory message
/// store — and fans the receipt out over SignalR to connected clients.
///
/// The sender identity is derived from the bearer token's <c>sub</c> /
/// <c>ClaimTypes.NameIdentifier</c> claim (see <see cref="TryGetUserId"/>) and
/// passed to the BFF client, which resolves it to a generic chat member.
/// </summary>
[ApiController]
[Route("chat")]
public class ChatController : ControllerBase
{
    private readonly IChatServiceClient _chatServiceClient;
    private readonly IChatDispatcher _dispatcher;

    public ChatController(IChatServiceClient chatServiceClient, IChatDispatcher dispatcher)
    {
        _chatServiceClient = chatServiceClient;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Send a message via REST. The BFF client ensures the deterministic 1:1
    /// channel for the sorted user pair exists on the generic chat-service, then
    /// posts the message. Sender identity is taken from the bearer token.
    /// </summary>
    [HttpPost("messages")]
    [ProducesResponseType(typeof(ChatMessageDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Send([FromBody] SendMessageRequest body, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;
        if (body is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Request body is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (string.IsNullOrWhiteSpace(body.RecipientId))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "RecipientId is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        try
        {
            // Route through the dispatcher — NOT the BFF client directly. The
            // dispatcher owns the four send side effects: (1) per-type payload
            // validation (self-message, empty text, media URL, location coords,
            // user-authored System) → ChatValidationException → 400; (2) persist
            // via the generic chat-service through IChatServiceClient; (3) SignalR
            // fan-out to the conversation group; (4) push fallback when the
            // recipient is backgrounded. Calling the client directly (an earlier
            // revision did) skips validation AND the live/push fan-out, so REST
            // sends silently diverge from the SignalR hub path.
            var message = await _dispatcher.SendAsync(userId, body, ct);
            return StatusCode(StatusCodes.Status201Created, ChatMessageDto.From(message));
        }
        catch (ChatValidationException ex)
        {
            // Per-type payload validation (self-message, missing/invalid media URL,
            // out-of-range coordinates, empty text, user-authored System) surfaces
            // as RFC 7807 Problem+JSON 400, mirroring the SignalR hub's HubException
            // mapping so REST and WS clients see the same rejection semantics.
            return BadRequest(new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
    }

    /// <summary>
    /// Mark a message as read by the calling user in the conversation with
    /// <paramref name="otherUserId"/>. The <see cref="IChatDispatcher"/> persists
    /// the receipt on the generic chat-service via <see cref="IChatServiceClient"/>
    /// (the gateway holds no in-memory message store) and fans the receipt out
    /// via SignalR so connected senders see it. Read receipts are
    /// conversation-scoped because the generic upstream addresses messages by
    /// (channelId, messageId) and the gateway resolves the channel from the
    /// sorted (reader, other) pair.
    /// </summary>
    [HttpPost("conversations/{otherUserId}/messages/{messageId}/read")]
    [ProducesResponseType(typeof(ChatMessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MarkRead(
        [FromRoute] string otherUserId,
        [FromRoute] string messageId,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;
        if (string.IsNullOrWhiteSpace(otherUserId))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "otherUserId is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var updated = await _dispatcher.MarkReadAsync(otherUserId, messageId, userId, ct);
        return updated is null ? NotFound() : Ok(ChatMessageDto.From(updated));
    }

    /// <summary>
    /// Fetch chat history with another user. The BFF client resolves the
    /// deterministic channel for the pair and reads its messages from the
    /// generic chat-service (channel summary / message GET).
    /// </summary>
    [HttpGet("conversations/{otherUserId}/messages")]
    [ProducesResponseType(typeof(ChatMessageDto[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> History(
        [FromRoute] string otherUserId,
        [FromQuery] int limit,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;
        if (string.IsNullOrWhiteSpace(otherUserId))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "otherUserId is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var effectiveLimit = limit > 0 ? limit : 50;
        var messages = await _chatServiceClient.GetConversationAsync(userId, otherUserId, effectiveLimit, ct);
        return Ok(messages.ToArray());
    }

    private bool TryGetUserId(out string userId, out IActionResult problem)
    {
        var fromClaim = User?.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? User?.FindFirstValue("sub");
        if (!string.IsNullOrWhiteSpace(fromClaim))
        {
            userId = fromClaim;
            problem = null!;
            return true;
        }

        if (Request.Headers.TryGetValue("X-User-Id", out var header) && !string.IsNullOrWhiteSpace(header))
        {
            userId = header.ToString();
            problem = null!;
            return true;
        }

        userId = string.Empty;
        problem = Unauthorized();
        return false;
    }
}
