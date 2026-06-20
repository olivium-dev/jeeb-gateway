using JeebGateway.Auth.Capabilities;
using JeebGateway.Calls;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
[Route("api/calls")]
// ADR-005 L2 §E delivery dual-party: masked-call session create/end are between the delivery's
// client and jeeber. Coarse CLAIM {client, jeeber}; party-on-delivery legality stays STATE.
[RequireCapability(Capabilities.DeliveryParticipate)]
public sealed class CallsController : ControllerBase
{
    private readonly IMaskedCallService _calls;

    public CallsController(IMaskedCallService calls) => _calls = calls;

    [HttpPost("session")]
    public async Task<IActionResult> CreateSession(
        [FromBody] CreateCallSessionRequest request,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem))
            return problem;

        if (request is null || string.IsNullOrWhiteSpace(request.DeliveryId))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "deliveryId is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var session = await _calls.CreateSessionAsync(
            request.DeliveryId.Trim(),
            userId,
            request.CalleeUserId?.Trim() ?? string.Empty,
            ct);

        if (session is null)
            return NotFound(new { error = "Masked calls are not enabled" });

        return Ok(session);
    }

    [HttpDelete("session/{sessionId}")]
    public async Task<IActionResult> EndSession(string sessionId, CancellationToken ct)
    {
        var ok = await _calls.EndSessionAsync(sessionId, ct);
        return ok ? NoContent() : NotFound();
    }
}

public sealed class CreateCallSessionRequest
{
    public required string DeliveryId { get; set; }
    public string? CalleeUserId { get; set; }
}
