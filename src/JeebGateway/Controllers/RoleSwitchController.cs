using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// T-backend-041 — role switching for dual-role accounts. BR-1 enforcement:
/// a user cannot switch roles while they have active deliveries under the
/// current role, preventing them from acting as both Client and Jeeber on
/// the same delivery.
/// </summary>
[ApiController]
[Route("users")]
public class RoleSwitchController : ControllerBase
{
    private readonly IDualRoleService _dualRole;
    private readonly IUsersStore _store;

    public RoleSwitchController(IDualRoleService dualRole, IUsersStore store)
    {
        _dualRole = dualRole;
        _store = store;
    }

    [HttpPost("{id}/switch-role")]
    [RequireActiveUser]
    [ProducesResponseType(typeof(SwitchRoleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SwitchRole(string id, [FromBody] SwitchRoleRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var problem))
            return problem;

        if (!string.Equals(callerId, id, StringComparison.Ordinal)
            && !UserIdentity.IsAdmin(HttpContext))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "You may only switch your own role.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/forbidden-role-switch"
            });
        }

        if (body is null || string.IsNullOrWhiteSpace(body.TargetRole))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "targetRole is required.",
                Detail = $"Allowed values: {Roles.Client}, {Roles.Jeeber}.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var targetRole = body.TargetRole.Trim().ToLowerInvariant();
        if (targetRole != Roles.Client && targetRole != Roles.Jeeber)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"'{body.TargetRole}' is not a switchable role.",
                Detail = $"Allowed values: {Roles.Client}, {Roles.Jeeber}.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/invalid-target-role"
            });
        }

        var validation = await _dualRole.ValidateRoleSwitchAsync(id, targetRole, ct);
        if (!validation.IsAllowed)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Role switch denied (BR-1).",
                Detail = validation.DenialReason,
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/role-switch-denied"
            });
        }

        var updated = await _store.SwitchRoleAsync(id, targetRole, ct);
        if (updated is null) return NotFound();

        return Ok(new SwitchRoleResponse
        {
            UserId = updated.Id,
            PreviousRole = validation.PreviousRole!,
            ActiveRole = updated.ActiveRole,
            SwitchedAt = updated.RoleSwitchedAt ?? DateTimeOffset.UtcNow,
            Roles = updated.Roles
        });
    }
}
