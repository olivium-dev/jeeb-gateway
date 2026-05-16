using System.Security.Claims;
using JeebGateway.Chat;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// REST shim over the chat dispatcher (T-backend-012). Two reasons it
/// lives alongside the SignalR hub:
///   1. Tests and operator scripts can drive the chat flow without a
///      SignalR client.
///   2. Browsers behind aggressive proxies that strip WS upgrade still
///      need a fallback for sending and marking read; the hub remains
///      the canonical real-time surface.
///
/// History reads land here only — we do not stream history over the hub
/// to avoid double-replay on reconnect.
/// </summary>
[ApiController]
[Route("chat")]
public class ChatController : ControllerBase
{
    private readonly IChatMessageStore _store;
    private readonly IChatDispatcher _dispatcher;

    public ChatController(IChatMessageStore store, IChatDispatcher dispatcher)
    {
        _store = store;
        _dispatcher = dispatcher;
    }

    /// <summary>Send a message via REST (mirrors ChatHub.SendMessage).</summary>
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

        try
        {
            var message = await _dispatcher.SendAsync(userId, body, ct);
            return StatusCode(StatusCodes.Status201Created, ChatMessageDto.From(message));
        }
        catch (ChatValidationException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
    }

    /// <summary>
    /// Mark a message as read by the calling user. Mirrors ChatHub.MarkRead;
    /// the dispatcher fans out the receipt to the conversation group so
    /// connected senders see it regardless of which surface marked it.
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
    /// Fetch chat history with another user. The conversation id is
    /// derived server-side from the (caller, otherUserId) pair so clients
    /// cannot read foreign conversations by guessing ids.
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

        var conversationId = ConversationKey.For(userId, otherUserId);
        var messages = await _store.GetByConversationAsync(conversationId, limit, ct);
        return Ok(messages.Select(ChatMessageDto.From).ToArray());
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
