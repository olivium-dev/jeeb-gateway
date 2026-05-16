using JeebGateway.Admin;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
[Route("admin/users")]
[RequireRole(Roles.Admin)]
public class AdminUsersController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxSuspensionReasonLength = 500;

    private const string EntityType = "user";
    private const string ActionSuspend = "suspend_user";
    private const string ActionUnsuspend = "unsuspend_user";

    private readonly IUsersStore _store;
    private readonly ITokenService _tokens;
    private readonly IAdminAuditLog _auditLog;

    public AdminUsersController(IUsersStore store, ITokenService tokens, IAdminAuditLog auditLog)
    {
        _store = store;
        _tokens = tokens;
        _auditLog = auditLog;
    }

    [HttpGet("search")]
    [ProducesResponseType(typeof(AdminUserSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Search(
        [FromQuery] string? name,
        [FromQuery] string? phone,
        [FromQuery] string? email,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)
            && string.IsNullOrWhiteSpace(phone)
            && string.IsNullOrWhiteSpace(email))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "At least one of name, phone, or email must be provided.",
                Status = StatusCodes.Status400BadRequest
            });
        }

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

        var result = await _store.SearchAsync(new UserSearchQuery
        {
            Name = name,
            Phone = phone,
            Email = email,
            Page = page,
            PageSize = pageSize
        }, ct);

        return Ok(new AdminUserSearchResponse
        {
            Items = result.Items.Select(ToItem).ToList(),
            Page = page,
            PageSize = pageSize,
            Total = result.Total
        });
    }

    /// <summary>
    /// Suspend a user account (T-backend-030). Marks the profile as
    /// suspended with the supplied reason, revokes every active refresh
    /// token (forcing re-auth on access-token expiry, ≤15 min), and
    /// appends an entry to <c>admin_actions</c>. The
    /// <see cref="RequireActiveUserAttribute"/> filter then makes the
    /// 403-on-Client/Jeeber-action acceptance criterion automatic for
    /// any endpoint that opts in.
    /// </summary>
    [HttpPatch("{id}/suspend")]
    [ProducesResponseType(typeof(SuspendUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Suspend(string id, [FromBody] SuspendUserRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var problem)) return problem;

        var reason = body?.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "reason is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (reason.Length > MaxSuspensionReasonLength)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"reason must be {MaxSuspensionReasonLength} characters or fewer.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var before = await _store.GetByIdAsync(id, ct);
        if (before is null) return NotFound();

        var updated = await _store.SuspendAsync(id, reason, adminId, ct);
        if (updated is null) return NotFound();

        var revoked = await _tokens.RevokeAllForUserAsync(id, RevocationReason.Suspended, ct);

        await _auditLog.AppendAsync(new AdminAuditAppend
        {
            AdminUserId = adminId,
            Action = ActionSuspend,
            EntityType = EntityType,
            EntityId = id,
            BeforeState = SuspensionSnapshot(before),
            AfterState = SuspensionSnapshot(updated),
            RequestId = HttpContext.TraceIdentifier
        }, ct);

        return Ok(new SuspendUserResponse
        {
            UserId = id,
            IsSuspended = updated.IsSuspended,
            Reason = updated.SuspensionReason,
            SuspendedAt = updated.SuspendedAt ?? DateTimeOffset.UtcNow,
            SuspendedBy = updated.SuspendedBy ?? adminId,
            RevokedTokenCount = revoked
        });
    }

    /// <summary>
    /// Lift a suspension (T-backend-030). Clears the suspension fields
    /// and appends the action to <c>admin_actions</c>. Refresh tokens
    /// are not re-issued — the user logs in again via the normal phone
    /// OTP flow.
    /// </summary>
    [HttpPatch("{id}/unsuspend")]
    [ProducesResponseType(typeof(UnsuspendUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unsuspend(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var problem)) return problem;

        var before = await _store.GetByIdAsync(id, ct);
        if (before is null) return NotFound();

        var updated = await _store.UnsuspendAsync(id, adminId, ct);
        if (updated is null) return NotFound();

        var now = DateTimeOffset.UtcNow;
        await _auditLog.AppendAsync(new AdminAuditAppend
        {
            AdminUserId = adminId,
            Action = ActionUnsuspend,
            EntityType = EntityType,
            EntityId = id,
            BeforeState = SuspensionSnapshot(before),
            AfterState = SuspensionSnapshot(updated),
            RequestId = HttpContext.TraceIdentifier
        }, ct);

        return Ok(new UnsuspendUserResponse
        {
            UserId = id,
            IsSuspended = updated.IsSuspended,
            UnsuspendedAt = now,
            UnsuspendedBy = adminId
        });
    }

    /// <summary>
    /// Returns the admin action audit log for a single user (T-backend-030
    /// acceptance criterion #4). One row per recorded mutation, newest
    /// first, mirroring the on-disk order in <c>admin_actions</c>. Returns
    /// 404 when the user does not exist so an empty <c>Items</c> array
    /// unambiguously means "user has never been the subject of an admin
    /// action" rather than "wrong id".
    /// </summary>
    [HttpGet("{id}/actions")]
    [ProducesResponseType(typeof(AdminUserActionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListActions(string id, CancellationToken ct)
    {
        var user = await _store.GetByIdAsync(id, ct);
        if (user is null) return NotFound();

        var entries = await _auditLog.ListForEntityAsync(EntityType, id, ct);

        return Ok(new AdminUserActionsResponse
        {
            UserId = id,
            Items = entries.Select(e => new AdminUserActionItem
            {
                Id = e.Id,
                AdminUserId = e.AdminUserId,
                Action = e.Action,
                CreatedAt = e.CreatedAt,
                BeforeState = e.BeforeState,
                AfterState = e.AfterState,
                RequestId = e.RequestId
            }).ToList()
        });
    }

    private static IReadOnlyDictionary<string, object?> SuspensionSnapshot(UserProfile p) =>
        new Dictionary<string, object?>
        {
            ["is_suspended"] = p.IsSuspended,
            ["suspension_reason"] = p.SuspensionReason,
            ["suspended_at"] = p.SuspendedAt,
            ["suspended_by"] = p.SuspendedBy
        };

    private static AdminUserSearchResultItem ToItem(UserProfile p) => new()
    {
        Id = p.Id,
        Phone = p.Phone,
        Email = p.Email,
        Name = p.Name,
        Roles = p.Roles,
        Rating = p.Rating,
        CreatedAt = p.CreatedAt,
        IsNew = p.RatingCount == 0
    };
}
