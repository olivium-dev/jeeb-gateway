using JeebGateway.Admin;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// E2E 8.2 / 8.3 / 8.4 — admin user roster + account-suspension moderation.
/// This is a THIN BFF over the gateway's user projection seam
/// (<see cref="IUsersStore"/>) plus the gateway-owned token-revocation seam
/// (<see cref="ITokenService"/>). It holds NO user state itself.
///
/// <para><b>Why the gateway owns suspend, not user-management.</b> The live
/// user-management service (port 10001) exposes user reads
/// (<c>GET /api/User/all</c>, <c>GET /api/User/profile/{id}</c>) and role
/// mutations, but its public contract has NO suspended/active status field on
/// <c>UserProfileResponse</c> and NO suspend/unsuspend mutation. Account
/// suspension is a Jeeb product concern (it gates Client/Jeeber mutations via
/// the gateway's SuspensionGuard and revokes the offending user's refresh
/// tokens) — it is therefore composed at the BFF, exactly like the KYC
/// role-grant in <see cref="AdminKycController"/>. The gateway never invents an
/// upstream status field UM does not own.</para>
///
/// <list type="bullet">
///   <item>GET <c>/admin/users/search</c> — paged roster, optional name/phone/email
///     filters (8.2).</item>
///   <item>PATCH <c>/admin/users/{id}/suspend</c> — flag the account suspended,
///     record the reason + admin, and revoke every live refresh token so the
///     user is signed out within one token lifetime (8.3).</item>
///   <item>PATCH <c>/admin/users/{id}/unsuspend</c> — lift a suspension (8.4).</item>
/// </list>
///
/// Every action is admin-gated via <see cref="Capabilities.UsersAdminManage"/>
/// (mapped AdminOnly in <c>CapabilityRolePolicy</c>) and every mutation lands in
/// <see cref="IAdminAuditLog"/> — same contract as the other Admin* controllers.
/// </summary>
[ApiController]
[Route("admin/users")]
public class AdminUsersController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxReasonLength = 500;

    private const string EntityType = "user";
    private const string ActionSuspend = "suspend_user";
    private const string ActionUnsuspend = "unsuspend_user";

    private readonly IUsersStore _users;
    private readonly ITokenService _tokens;
    private readonly IAdminAuditLog _auditLog;
    private readonly ILogger<AdminUsersController> _log;

    public AdminUsersController(
        IUsersStore users,
        ITokenService tokens,
        IAdminAuditLog auditLog,
        ILogger<AdminUsersController> log)
    {
        _users = users;
        _tokens = tokens;
        _auditLog = auditLog;
        _log = log;
    }

    /// <summary>
    /// E2E 8.2. Paged admin roster with optional case-insensitive substring
    /// filters on name, phone, and email. Newest accounts first.
    /// </summary>
    [HttpGet("search")]
    [RequireCapability(Capabilities.UsersAdminManage)]
    [ProducesResponseType(typeof(AdminUserSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Search(
        [FromQuery] string? name = null,
        [FromQuery] string? phone = null,
        [FromQuery] string? email = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        if (page < 1)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "page must be >= 1.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"pageSize must be between 1 and {MaxPageSize}.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var result = await _users.SearchAsync(new UserSearchQuery
        {
            Name = name,
            Phone = phone,
            Email = email,
            Page = page,
            PageSize = pageSize
        }, ct);

        return Ok(new AdminUserSearchResponse
        {
            Items = result.Items.Select(ToSearchItem).ToList(),
            Page = page,
            PageSize = pageSize,
            Total = result.Total
        });
    }

    /// <summary>
    /// E2E 8.3. Flags <paramref name="id"/> suspended (recording reason +
    /// acting admin) and revokes every live refresh token for the user so the
    /// session cannot survive the suspension. Idempotent — re-suspending an
    /// already-suspended user simply refreshes the reason and re-runs the
    /// revocation sweep.
    /// </summary>
    [HttpPatch("{id}/suspend")]
    [RequireCapability(Capabilities.UsersAdminManage)]
    [ProducesResponseType(typeof(SuspendUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Suspend(
        string id, [FromBody] SuspendUserRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized))
        {
            return unauthorized;
        }

        var reason = body?.Reason?.Trim();
        if (reason is { Length: > MaxReasonLength })
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"reason must be {MaxReasonLength} characters or fewer.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var before = await _users.GetByIdAsync(id, ct);
        var profile = await _users.SuspendAsync(id, reason ?? string.Empty, adminId, ct);
        if (profile is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = $"User '{id}' was not found.",
                Status = StatusCodes.Status404NotFound
            });
        }

        // Suspension must terminate the user's live sessions. The gateway owns
        // the refresh-token store, so the revocation sweep is composed here on
        // the same request as the status flip.
        var revoked = await _tokens.RevokeAllForUserAsync(id, RevocationReason.Suspended, ct);

        await _auditLog.AppendAsync(new AdminAuditAppend
        {
            AdminUserId = adminId,
            Action = ActionSuspend,
            EntityType = EntityType,
            EntityId = id,
            BeforeState = new Dictionary<string, object?>
            {
                ["is_suspended"] = before?.IsSuspended ?? false
            },
            AfterState = new Dictionary<string, object?>
            {
                ["is_suspended"] = true,
                ["reason"] = profile.SuspensionReason,
                ["revoked_token_count"] = revoked
            },
            RequestId = HttpContext.TraceIdentifier
        }, ct);

        _log.LogInformation(
            "admin {AdminId} suspended user {UserId} ({Revoked} tokens revoked)",
            adminId, id, revoked);

        return Ok(new SuspendUserResponse
        {
            UserId = profile.Id,
            IsSuspended = profile.IsSuspended,
            Reason = profile.SuspensionReason,
            SuspendedAt = profile.SuspendedAt ?? DateTimeOffset.UtcNow,
            SuspendedBy = profile.SuspendedBy ?? adminId,
            RevokedTokenCount = revoked
        });
    }

    /// <summary>
    /// E2E 8.4. Lifts a suspension. Safe to call on a user who is not currently
    /// suspended (no-op flip).
    /// </summary>
    [HttpPatch("{id}/unsuspend")]
    [RequireCapability(Capabilities.UsersAdminManage)]
    [ProducesResponseType(typeof(UnsuspendUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unsuspend(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized))
        {
            return unauthorized;
        }

        var before = await _users.GetByIdAsync(id, ct);
        var profile = await _users.UnsuspendAsync(id, adminId, ct);
        if (profile is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = $"User '{id}' was not found.",
                Status = StatusCodes.Status404NotFound
            });
        }

        await _auditLog.AppendAsync(new AdminAuditAppend
        {
            AdminUserId = adminId,
            Action = ActionUnsuspend,
            EntityType = EntityType,
            EntityId = id,
            BeforeState = new Dictionary<string, object?>
            {
                ["is_suspended"] = before?.IsSuspended ?? false
            },
            AfterState = new Dictionary<string, object?>
            {
                ["is_suspended"] = false
            },
            RequestId = HttpContext.TraceIdentifier
        }, ct);

        _log.LogInformation("admin {AdminId} unsuspended user {UserId}", adminId, id);

        return Ok(new UnsuspendUserResponse
        {
            UserId = profile.Id,
            IsSuspended = profile.IsSuspended,
            UnsuspendedAt = DateTimeOffset.UtcNow,
            UnsuspendedBy = adminId
        });
    }

    private static AdminUserSearchResultItem ToSearchItem(UserProfile u) => new()
    {
        Id = u.Id,
        Phone = u.Phone,
        Email = u.Email,
        Name = u.Name,
        Roles = u.Roles.ToList(),
        Rating = u.Rating,
        CreatedAt = u.CreatedAt,
        // BR-10: a Jeeber with zero ratings renders a "New" badge in the roster.
        IsNew = u.RatingCount == 0
    };
}
