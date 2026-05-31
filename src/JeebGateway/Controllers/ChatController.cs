using System.Security.Claims;
using JeebGateway.Chat;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// REST shim over the chat surface (T-backend-012 / T-backend-bff-chat).
///
/// Send and History now proxy the REAL chat-api (Firestore-backed,
/// <c>Services:Chat:BaseUrl</c>) via <see cref="IChatServiceClient"/>.
/// MarkRead remains on the in-memory <see cref="IChatDispatcher"/> (no
/// chat-api surface for read receipts today; SignalR hub fans out receipts
/// to connected clients regardless).
///
/// <c>X-User-Id</c> is derived from the bearer token's <c>sub</c> /
/// <c>ClaimTypes.NameIdentifier</c> claim and forwarded to the upstream
/// service as a trusted BFF header — the chat-api trusts gateway-injected
/// identity rather than its own JWT validation for this surface.
/// </summary>
[ApiController]
[Route("chat")]
[Authorize]
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
    /// Send a message via REST. Proxies <c>POST /api/jeeb/chat/messages</c>
    /// on the real chat-api; sender identity is taken from the bearer token.
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

        var dto = await _chatServiceClient.SendMessageAsync(userId, body.RecipientId, body.Text, ct);
        return StatusCode(StatusCodes.Status201Created, dto);
    }

    /// <summary>
    /// Mark a message as read by the calling user. Remains on the
    /// in-memory <see cref="IChatDispatcher"/> — the chat-api does not
    /// yet expose a read-receipt surface. The dispatcher fans out the
    /// receipt via SignalR so connected senders see it.
    /// </summary>
    [HttpPost("messages/{messageId}/read")]
    [ProducesResponseType(typeof(ChatMessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MarkRead([FromRoute] string messageId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;

        var updated = await _dispatcher.MarkReadAsync(messageId, userId, ct);
        return updated is null ? NotFound() : Ok(ChatMessageDto.From(updated));
    }

    /// <summary>
    /// Fetch chat history with another user. Proxies
    /// <c>GET /api/jeeb/chat/conversations/{otherUserId}/messages?limit=N</c>
    /// on the real chat-api.
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
