using System.Net;
using System.Security.Claims;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Thin BFF seam over the shared <c>realtime-comunication-service</c>
/// (Elixir/Phoenix "LiveComm"). Exposes the SERVER-SIDE FAN-OUT path the Jeeb
/// chat flow needs: a per-recipient publish to the <c>jeeb:chat</c> Phoenix
/// topic so a backgrounded recipient receives a 1:1 message even when their
/// WebSocket is not connected. Mobile clients connect the realtime WebSocket
/// directly (Phoenix channel <c>topic:jeeb:chat</c>, membership-validated join);
/// the gateway does NOT proxy the WebSocket.
///
/// Serves JEB-1453, JEB-1449, JEB-1432, JEB-626, JEB-444, JEB-50, JEB-51,
/// JEB-52 (jeeb:chat Phoenix channel, membership-validated join, per-recipient
/// fan-out filter).
///
/// <para>
/// FLAG-GATED. The realtime-comunication-service is NOT yet on the Jeeb swarm
/// (<c>Services:Realtime:BaseUrl</c> is a marked PLACEHOLDER in
/// appsettings.Production.json). Every action returns
/// <c>503 ProblemDetails</c> while <c>FeatureFlags:UseUpstream:Realtime</c> is
/// false (the default in every environment), so the endpoint is contract-stable
/// and observable but never targets an unbound upstream. Flip the flag on only
/// after the service is deployed and the placeholder BaseUrl is replaced.
/// </para>
///
/// The caller identity is taken from the bearer token's <c>sub</c> /
/// <see cref="ClaimTypes.NameIdentifier"/> claim (mirroring
/// <see cref="ChatController"/>); the bearer + X-Service-Auth are forwarded to
/// the upstream by the named client's delegating-handler chain.
/// </summary>
[ApiController]
[Route("realtime")]
public sealed class RealtimeController : ControllerBase
{
    private readonly IRealtimeCommunicationClient _realtime;
    private readonly UpstreamFeatureFlags _flags;

    public RealtimeController(
        IRealtimeCommunicationClient realtime,
        IOptions<UpstreamFeatureFlags> flags)
    {
        _realtime = realtime;
        _flags = flags.Value;
    }

    /// <summary>
    /// Fan a 1:1 chat message out to a single recipient over the realtime
    /// transport (per-recipient fan-out filter — published to the recipient's
    /// <c>user:{recipientId}</c> stream under the <c>jeeb:chat</c> topic). The
    /// sender identity is taken from the bearer token; the gateway never lets a
    /// caller publish as another user.
    /// </summary>
    [HttpPost("chat/fanout")]
    [ProducesResponseType(typeof(RealtimePublishResult), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> FanOutChat(
        [FromBody] RealtimeFanOutRequest? body,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var senderId, out var unauthorized))
        {
            return unauthorized;
        }

        if (!_flags.Realtime)
        {
            return UpstreamUnavailable();
        }

        if (body is null || string.IsNullOrWhiteSpace(body.RecipientId))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "recipientId is required.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        if (string.IsNullOrWhiteSpace(body.MessageId)
            || string.IsNullOrWhiteSpace(body.Type))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "messageId and type are required.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        // The fan-out envelope. The upstream stamps user_id/tenant/via itself;
        // we carry the chat-message projection. senderId comes from the token,
        // never from the body, so a caller cannot spoof another sender.
        var data = new Dictionary<string, object?>
        {
            ["messageId"] = body.MessageId,
            ["senderId"] = senderId,
            ["recipientId"] = body.RecipientId,
            ["type"] = body.Type,
            ["body"] = body.Body,
            ["sentAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };

        try
        {
            var result = await _realtime.FanOutChatMessageAsync(body.RecipientId, data, ct);
            return StatusCode(StatusCodes.Status202Accepted, result);
        }
        catch (RealtimePublishException ex)
        {
            // Translate the upstream's explicit 401/403/429 envelopes to the
            // matching RFC 7807 ProblemDetails; everything else is a 502-class
            // upstream failure surfaced as 503 (the upstream is unreachable or
            // misbehaving — readiness/observability concern, not a client error).
            var status = ex.StatusCode switch
            {
                HttpStatusCode.Unauthorized => StatusCodes.Status401Unauthorized,
                HttpStatusCode.Forbidden => StatusCodes.Status403Forbidden,
                HttpStatusCode.TooManyRequests => StatusCodes.Status429TooManyRequests,
                _ => StatusCodes.Status503ServiceUnavailable,
            };

            return StatusCode(status, new ProblemDetails
            {
                Title = "realtime-comunication-service rejected the publish.",
                Detail = ex.Message,
                Status = status,
            });
        }
    }

    private ObjectResult UpstreamUnavailable() => StatusCode(
        StatusCodes.Status503ServiceUnavailable,
        new ProblemDetails
        {
            Title = "Realtime fan-out is not enabled.",
            Detail = "realtime-comunication-service is not yet deployed on the Jeeb swarm "
                + "(FeatureFlags:UseUpstream:Realtime is off and Services:Realtime:BaseUrl "
                + "is a placeholder). Enable the flag after the service is deployed.",
            Status = StatusCodes.Status503ServiceUnavailable,
        });

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

        if (Request.Headers.TryGetValue("X-User-Id", out var header)
            && !string.IsNullOrWhiteSpace(header))
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

/// <summary>
/// Gateway-side fan-out input. The sender is resolved from the bearer token, not
/// from the body, so this carries only the recipient + the message projection
/// the realtime transport fans out.
/// </summary>
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
