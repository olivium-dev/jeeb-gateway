using JeebGateway.Admin;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// E2E 8.2 / 8.3 / 8.4 — admin user roster + account-suspension moderation.
/// This is a THIN BFF over <see cref="IAdminUserProjection"/>, which composes
/// user-management identity, ban-service suspension, and feedback-service ratings
/// on demand, plus the gateway-owned token-revocation seam
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

    // The CMS status contract (OA-34): {action:"suspend"|"reinstate"} and the panel's
    // activeRole sentinel for a suspended account.
    private const string CmsActionSuspend = "suspend";
    private const string CmsActionReinstate = "reinstate";
    private const string SuspendedRoleLabel = "suspended";

    private readonly IAdminUserProjection _users;
    private readonly ITokenService _tokens;
    private readonly IAdminAuditLog _auditLog;
    private readonly ILogger<AdminUsersController> _log;

    public AdminUsersController(
        IAdminUserProjection users,
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

    /// <summary>
    /// OA-34 / owner decision O3 — CMS-compat alias for the two actions above. The DEPLOYED
    /// back-office bundle issues
    /// <c>PATCH &lt;origin&gt;/gateway/user-management/admin/users/{userId}/status</c> with
    /// <c>{action:"suspend"|"reinstate", reason?}</c> (verbatim in the live release's
    /// <c>mf/users/606.js</c>); the vhost strips one <c>/gateway/</c>, so the gateway must serve
    /// <c>user-management/admin/users/{id}/status</c>. It served nothing there, so the suspend
    /// button has been hitting a 404. Same compat-facade pattern as
    /// <see cref="CmsKycAdminController"/>: serve the path the shipped bundle already emits, no CMS
    /// redeploy. The <c>user-management/</c> segment is a CMS URL namespace, not a proxy — nothing
    /// here calls user-management beyond the projection the native routes already use.
    ///
    /// <para><b>Why this lives on THIS controller and delegates to its own actions (OA-36).</b>
    /// <c>POST /v1/auth/refresh</c> is a fifth session-minting door and carries NO suspension gate.
    /// A suspended account is stopped there only because <see cref="Suspend"/> revokes the
    /// refresh-token family on the SAME request as the ban write. A route that wrote ban-service
    /// directly — or a CMS repointed at ban-service — would refuse the account at every gated door
    /// while it rotated refresh tokens indefinitely. Delegating to <see cref="Suspend"/> /
    /// <see cref="Unsuspend"/> makes that drift impossible by construction: there is exactly one
    /// suspend implementation, and it is the one that revokes.</para>
    /// </summary>
    [HttpPatch("/user-management/admin/users/{id}/status")]
    [RequireCapability(Capabilities.UsersAdminManage)]
    [ProducesResponseType(typeof(CmsUserStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CmsUpdateStatus(
        string id, [FromBody] CmsUserStatusRequest? body, CancellationToken ct)
    {
        var action = body?.Action?.Trim().ToLowerInvariant();
        if (action is not (CmsActionSuspend or CmsActionReinstate))
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"action must be '{CmsActionSuspend}' or '{CmsActionReinstate}'.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var suspending = action == CmsActionSuspend;

        // The one suspend/unsuspend implementation, ban write and refresh-family revocation
        // included. Any non-200 (400/401/404) is returned unchanged.
        var inner = suspending
            ? await Suspend(id, new SuspendUserRequest { Reason = body!.Reason }, ct)
            : await Unsuspend(id, ct);

        if (inner is not OkObjectResult ok)
        {
            return inner;
        }

        var profile = await _users.GetByIdAsync(id, ct);
        return Ok(ToCmsRow(id, profile, suspending, ok.Value));
    }

    /// <summary>
    /// The CMS <c>AdminUserListItem</c> the panel merges over its row. It reports suspension TWICE
    /// on purpose: the shipped panel derives state from <c>activeRole === "suspended"</c> while the
    /// newer bundle reads <c>isSuspended</c>, and the button only flips if both agree.
    /// </summary>
    private static CmsUserStatusResponse ToCmsRow(
        string id, UserProfile? profile, bool suspending, object? innerBody)
    {
        var suspend = innerBody as SuspendUserResponse;

        return new CmsUserStatusResponse
        {
            Id = profile?.Id ?? id,
            UserId = profile?.Id ?? id,
            Phone = profile?.Phone ?? string.Empty,
            Email = profile?.Email,
            Name = profile?.Name ?? string.Empty,
            AvatarUrl = profile?.AvatarUrl,
            Language = profile?.Language ?? "en",
            AvailableRoles = profile?.Roles.ToList() ?? new List<string>(),
            ActiveRole = suspending ? SuspendedRoleLabel : profile?.ActiveRole ?? Roles.Client,
            CreatedAt = profile?.CreatedAt,
            Rating = profile?.Rating,
            RatingCount = profile?.RatingCount ?? 0,
            IsSuspended = suspending,
            SuspensionReason = suspending ? suspend?.Reason ?? profile?.SuspensionReason : null,
            SuspendedAt = suspending ? suspend?.SuspendedAt ?? profile?.SuspendedAt : null,
            SuspendedBy = suspending ? suspend?.SuspendedBy : null,
            RevokedTokenCount = suspend?.RevokedTokenCount
        };
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
        IsSuspended = u.IsSuspended,
        SuspensionReason = u.SuspensionReason,
        SuspendedAt = u.SuspendedAt,
        // BR-10: a Jeeber with zero ratings renders a "New" badge in the roster.
        IsNew = u.RatingCount == 0
    };
}

/// <summary>CMS <c>UserStatusAction</c>: <c>{ action: suspend|reinstate, reason?: string }</c>.</summary>
public sealed class CmsUserStatusRequest
{
    public string? Action { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// CMS <c>AdminUserListItem</c>, plus the suspension fields the panel merges over its row.
/// The CMS spreads this onto its cached detail, so every field it reads must be present.
/// </summary>
public sealed class CmsUserStatusResponse
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string Phone { get; init; }
    public string? Email { get; init; }
    public required string Name { get; init; }
    public string? AvatarUrl { get; init; }
    public required string Language { get; init; }
    public required IReadOnlyList<string> AvailableRoles { get; init; }
    public required string ActiveRole { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public decimal? Rating { get; init; }
    public required int RatingCount { get; init; }
    public required bool IsSuspended { get; init; }
    public string? SuspensionReason { get; init; }
    public DateTimeOffset? SuspendedAt { get; init; }
    public string? SuspendedBy { get; init; }
    /// <summary>How many refresh tokens the suspension swept — the OA-36 invariant, made visible.</summary>
    public int? RevokedTokenCount { get; init; }
}
