using System.Security.Claims;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Active-role switch for the signed-in user (R2-ROLE-SWITCH).
///
/// <c>POST /v1/users/me/role/switch</c> was referenced as a documented contract
/// path but never exposed — the BR-1 dual-role switch logic existed only in
/// <see cref="IDualRoleService"/> with no HTTP surface. This controller adds the
/// route ADDITIVELY and simply wires existing pieces together:
///
///   1. <see cref="IDualRoleService.ValidateRoleSwitchAsync"/> (already implemented)
///      enforces BR-1: the user must hold the target role and have no active
///      deliveries under the current role.
///   2. On success, <see cref="IUsersStore.SwitchRoleAsync"/> (already implemented)
///      flips <c>ActiveRole</c> in the in-memory store.
///   3. Tokens are re-minted via the existing <see cref="ITokenService"/> so the
///      caller gets a fresh pair reflecting the new active role.
///
/// Response is the existing <see cref="TokenPairResponse"/> shape (no new fields) —
/// identical to <c>POST /auth/tokens</c>. Status codes:
///   - 200 + TokenPairResponse on success
///   - 400 ProblemDetails for an invalid/unheld target role
///   - 409 ProblemDetails when active deliveries block the switch
///   - 401 when the caller identity cannot be resolved
///
/// ADDITIVE: NEW route only. No existing controller, route, DTO, or store method
/// is modified. Identity resolution mirrors the established gateway pattern
/// (<c>sub</c> / NameIdentifier claim, with the same MVP <c>X-User-Id</c> header
/// fallback used by <see cref="NotificationPreferencesController"/>).
/// </summary>
[ApiController]
[Route("v1/users/me/role")]
[Produces("application/json", "application/problem+json")]
public sealed class RoleSwitchController : ControllerBase
{
    private readonly IDualRoleService _dualRole;
    private readonly IUsersStore _users;
    private readonly ITokenService _tokens;
    private readonly ILogger<RoleSwitchController> _log;

    public RoleSwitchController(
        IDualRoleService dualRole,
        IUsersStore users,
        ITokenService tokens,
        ILogger<RoleSwitchController> log)
    {
        _dualRole = dualRole;
        _users = users;
        _tokens = tokens;
        _log = log;
    }

    [HttpPost("switch")]
    [ProducesResponseType(typeof(TokenPairResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Switch([FromBody] RoleSwitchRequest? body, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        if (body is null || string.IsNullOrWhiteSpace(body.TargetRole))
        {
            return Problem(
                title: "targetRole is required.",
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.1");
        }

        var result = await _dualRole.ValidateRoleSwitchAsync(userId, body.TargetRole, ct);
        if (!result.IsAllowed)
        {
            // "Active delivery" denials are a transient conflict (409); everything
            // else (unknown user, role not held, already-active) is a 400. The
            // DualRoleService denial reason text distinguishes the active-delivery
            // case via the "active delivery" phrase it emits.
            var reason = result.DenialReason ?? "Role switch denied.";
            var isConflict = reason.Contains("active delivery", StringComparison.OrdinalIgnoreCase);

            return Problem(
                title: isConflict ? "Role switch blocked by active deliveries" : "Role switch not allowed",
                detail: reason,
                statusCode: isConflict ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest,
                type: isConflict
                    ? "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.8"
                    : "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.1");
        }

        var profile = await _users.SwitchRoleAsync(userId, body.TargetRole, ct);
        if (profile is null)
        {
            // Race: profile vanished between validation and the switch.
            return Problem(
                title: "Role switch not allowed",
                detail: "User not found.",
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.1");
        }

        var pair = await _tokens.IssueAsync(profile.Id, profile.Roles, ct);

        _log.LogInformation(
            "Active role switched for user {UserId}: {From} -> {To}",
            userId, result.PreviousRole, result.NewRole);

        return Ok(ToResponse(pair));
    }

    private bool TryGetUserId(out string userId)
    {
        var fromClaim = User?.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? User?.FindFirstValue("sub");
        if (!string.IsNullOrWhiteSpace(fromClaim))
        {
            userId = fromClaim;
            return true;
        }

        // MVP fallback while JWT validation isn't wired up yet: header injected by
        // edge (same precedent as NotificationPreferencesController).
        if (Request.Headers.TryGetValue("X-User-Id", out var header) && !string.IsNullOrWhiteSpace(header))
        {
            userId = header.ToString();
            return true;
        }

        userId = string.Empty;
        return false;
    }

    private static TokenPairResponse ToResponse(TokenPair pair)
    {
        var seconds = (int)Math.Max(
            (pair.AccessTokenExpiresAt - DateTimeOffset.UtcNow).TotalSeconds, 0);
        return new TokenPairResponse
        {
            AccessToken = pair.AccessToken,
            RefreshToken = pair.RefreshToken,
            TokenType = "Bearer",
            AccessTokenExpiresInSeconds = seconds,
            AccessTokenExpiresAt = pair.AccessTokenExpiresAt,
            RefreshTokenExpiresAt = pair.RefreshTokenExpiresAt
        };
    }
}

/// <summary>Request body for <c>POST /v1/users/me/role/switch</c>.</summary>
public sealed record RoleSwitchRequest(string TargetRole);
