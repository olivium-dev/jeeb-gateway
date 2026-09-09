using JeebGateway.Auth.Capabilities;
using JeebGateway.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>Compatibility tombstone for retired chat-only Phoenix fan-out.
/// Normal message append still invokes push notifications. Location is unaffected.</summary>
[ApiController]
[Route("realtime")]
public sealed class RealtimeController : ControllerBase
{
    [HttpPost("chat/fanout")]
    [Authorize]
    [RequireCapability(Capabilities.ChatSend)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    public IActionResult FanOutChat([FromBody] RealtimeFanOutRequest? body)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized)) return unauthorized;
        return Problem(title: "Chat socket fan-out is retired.",
            detail: "Append messages through the conversation API; chat-service owns storage and Firebase realtime.",
            statusCode: StatusCodes.Status410Gone,
            type: "https://jeeb.dev/errors/chat-socket-retired");
    }
}

public sealed class RealtimeFanOutRequest
{
    /// <summary>The single recipient (per-recipient fan-out filter).</summary>
    public string? RecipientId { get; init; }

    /// <summary>The originating chat message id (idempotency / client de-dup).</summary>
    public string? MessageId { get; init; }

    /// <summary>The chat message type (text / media / location / system).</summary>
    public string? Type { get; init; }

    /// <summary>The message body (text or serialized payload).</summary>
    public string? Body { get; init; }
}
