using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Users.RoleSwitch;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers.V1;

/// <summary>
/// T-BE-003 / JEB-39 — role-switch endpoint for dual-role accounts.
///
/// Route: <c>POST /v1/users/me/role/switch</c>
/// Body:  <c>{ "role": "client" | "jeeber" }</c>
/// 200:   <see cref="RoleSwitchResponse"/> — fresh JWT pair + user snapshot.
/// 400:   ProblemDetails type=<see cref="RoleSwitchProblemTypes.InvalidRole"/>
///        (request role not in <c>{client, jeeber}</c>).
/// 401:   ProblemDetails type=<see cref="RoleSwitchProblemTypes.Unauthenticated"/>
///        (no JWT and no <c>X-User-Id</c> header).
/// 403:   ProblemDetails type=<see cref="RoleSwitchProblemTypes.RoleNotAvailable"/>
///        (requested role not in user's persisted <c>available_roles</c>).
/// 404:   ProblemDetails type=<see cref="RoleSwitchProblemTypes.UserNotFound"/>.
///
/// Sequence (per Jira system design):
///   1. Validate role ∈ {client, jeeber}.
///   2. Call <see cref="IUserManagementRoleSwitchClient.SwitchActiveRoleAsync"/>.
///   3. On Ok: mint a fresh JWT pair via <see cref="IJeebJwtIssuer"/> with the
///      new <c>active_role</c> claim — mobile swaps tokens on receipt.
///   4. Emit structured <c>role.switched</c> log (AC4: userId, from, to,
///      correlationId).
///   5. Return <see cref="RoleSwitchResponse"/>.
///
/// AC5 perf target (p99 ≤ 300 ms cached user record): the gateway-side path
/// is pure CPU (claim extraction + JWT mint + log); the only IO is the
/// user-management call, which the production adapter runs through the
/// standard Polly pipeline (retry-with-jitter + circuit breaker + 10 s
/// timeout). The MVP in-memory client is in-process — sub-ms on the hot path.
/// </summary>
[ApiController]
[Route("v1/users/me/role")]
[Produces("application/json", "application/problem+json")]
public sealed class UsersRoleController : ControllerBase
{
    private static readonly string[] AllowedRoles =
    {
        InMemoryUserManagementRoleSwitchClient.RoleClient,
        InMemoryUserManagementRoleSwitchClient.RoleJeeber,
    };

    private static readonly JwtSecurityTokenHandler JwtReader =
        new() { MapInboundClaims = false };

    private readonly IUserManagementRoleSwitchClient _userMgmt;
    private readonly IJeebJwtIssuer _jwt;
    private readonly ILogger<UsersRoleController> _log;

    public UsersRoleController(
        IUserManagementRoleSwitchClient userMgmt,
        IJeebJwtIssuer jwt,
        ILogger<UsersRoleController> log)
    {
        _userMgmt = userMgmt;
        _jwt      = jwt;
        _log      = log;
    }

    [HttpPost("switch")]
    [ProducesResponseType(typeof(RoleSwitchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails),     StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails),     StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails),     StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails),     StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Switch(
        [FromBody] RoleSwitchRequest? body,
        CancellationToken ct)
    {
        // 1. AuthN — accept JWT sub claim OR X-User-Id header (gateway MVP).
        //    The X-User-Id path is what existing tests exercise; production
        //    relies on JWT bearer. Either path produces a Guid that the
        //    role-switch storage uses as the primary key.
        if (!TryResolveUserId(out var userId, out var unauth))
        {
            return unauth!;
        }

        // 2. AC3 — role must be present AND lower-case AND in {client, jeeber}.
        var requestedRole = body?.Role?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(requestedRole))
        {
            return BuildProblem(
                StatusCodes.Status400BadRequest,
                RoleSwitchProblemTypes.InvalidRole,
                RoleSwitchProblemTitles.InvalidRole,
                "Field 'role' is required. Allowed values: client, jeeber.");
        }
        if (Array.IndexOf(AllowedRoles, requestedRole) < 0)
        {
            return BuildProblem(
                StatusCodes.Status400BadRequest,
                RoleSwitchProblemTypes.InvalidRole,
                RoleSwitchProblemTitles.InvalidRole,
                $"'{requestedRole}' is not a switchable role. " +
                $"Allowed values: {string.Join(", ", AllowedRoles)}.");
        }

        // 3. Call user-management (AC2 / AC-FINAL). The outcome determines
        //    the HTTP status; any non-Ok outcome carries the persisted
        //    snapshot so the controller can fold it into the response shape
        //    without an extra round-trip.
        var result = await _userMgmt.SwitchActiveRoleAsync(userId, requestedRole, ct);

        if (result.Outcome == RoleSwitchOutcome.UserNotFound)
        {
            return BuildProblem(
                StatusCodes.Status404NotFound,
                RoleSwitchProblemTypes.UserNotFound,
                RoleSwitchProblemTitles.UserNotFound,
                "No user found for the authenticated subject.");
        }

        if (result.Outcome == RoleSwitchOutcome.RoleNotAvailable)
        {
            var available = result.Snapshot?.AvailableRoles
                ?? (IReadOnlyList<string>)Array.Empty<string>();
            // AC2 audit trail — log the rejection so customer support can
            // see "Sami tried jeeber but only had [client]" without grepping
            // logs across services.
            _log.LogInformation(
                "role.switch_denied user_id={UserId} requested={RequestedRole} " +
                "available_roles={AvailableRoles} correlation_id={CorrelationId}",
                userId, requestedRole, string.Join(',', available), GetCorrelationId());

            return BuildProblem(
                StatusCodes.Status403Forbidden,
                RoleSwitchProblemTypes.RoleNotAvailable,
                RoleSwitchProblemTitles.RoleNotAvailable,
                $"Role '{requestedRole}' is not in your available_roles " +
                $"[{string.Join(", ", available)}]. " +
                $"Complete KYC for the missing role to unlock it.");
        }

        // 4. Ok — mint a fresh JWT pair carrying the new active_role claim.
        //    Preserve the phone_hash claim from the incoming access token
        //    when present so phone-correlated logs survive a role switch;
        //    fall back to empty when no phone_hash claim is on the wire
        //    (e.g. test harness sending bare X-User-Id).
        var snapshot = result.Snapshot
            ?? throw new InvalidOperationException(
                "RoleSwitchResult.Snapshot must be non-null when Outcome is Ok.");

        var availableRolesArr = snapshot.AvailableRoles.ToArray();
        var phoneHash = ExtractPhoneHashFromBearer() ?? string.Empty;

        var pair = _jwt.Issue(
            userId:         userId,
            activeRole:     snapshot.ActiveRole,
            availableRoles: availableRolesArr,
            phoneHash:      phoneHash);

        // 5. AC4 audit log — structured "role.switched" event the QA-PRE +
        //    QA-POST sweeps look for. PreviousRole is the value persisted
        //    just before the switch; when this is the first switch on a
        //    legacy account it equals the new role (idempotent).
        var previousRole = result.PreviousRole ?? snapshot.ActiveRole;
        _log.LogInformation(
            "role.switched user_id={UserId} from={FromRole} to={ToRole} " +
            "correlation_id={CorrelationId}",
            userId, previousRole, snapshot.ActiveRole, GetCorrelationId());

        return Ok(new RoleSwitchResponse
        {
            AccessToken           = pair.AccessToken,
            RefreshToken          = pair.RefreshToken,
            AccessTokenExpiresAt  = pair.AccessTokenExpiresAt,
            RefreshTokenExpiresAt = pair.RefreshTokenExpiresAt,
            User = new RoleSwitchUserBlock
            {
                UserId              = userId,
                AvailableRoles      = availableRolesArr,
                ActiveRole          = snapshot.ActiveRole,
                ActiveRoleChangedAt = snapshot.ActiveRoleChangedAt,
            },
        });
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// MVP identity resolution — JWT <c>sub</c> claim first (production
    /// path; HS512 JeebJwtIssuer-issued tokens), then <c>X-User-Id</c>
    /// header (test harness + edge fallback). User IDs are Guids
    /// throughout the dual-role identity model (T-BE-002 schema).
    /// </summary>
    private bool TryResolveUserId(out Guid userId, out IActionResult? problem)
    {
        var candidate = User?.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User?.FindFirstValue("sub");

        if (string.IsNullOrEmpty(candidate)
            && Request.Headers.TryGetValue("X-User-Id", out var header))
        {
            candidate = header.ToString();
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            userId  = Guid.Empty;
            problem = BuildProblem(
                StatusCodes.Status401Unauthorized,
                RoleSwitchProblemTypes.Unauthenticated,
                RoleSwitchProblemTitles.Unauthenticated,
                "Request must carry a valid bearer token or X-User-Id header.");
            return false;
        }

        if (!Guid.TryParse(candidate, out userId))
        {
            // The user identifier exists but is not a Guid — this is a
            // 400 from the role-switch surface's perspective (the dual-role
            // identity model is keyed by Guid), distinct from 401 (missing
            // identity entirely).
            problem = BuildProblem(
                StatusCodes.Status400BadRequest,
                RoleSwitchProblemTypes.UserNotFound,
                RoleSwitchProblemTitles.UserNotFound,
                "Authenticated user identifier must be a GUID.");
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>
    /// Pulls the <c>phone_hash</c> claim from the raw Authorization bearer
    /// token without re-validating the signature (the gateway has already
    /// run JWT bearer middleware upstream). When the request is using the
    /// X-User-Id MVP path the token is absent and we return null.
    /// </summary>
    private string? ExtractPhoneHashFromBearer()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var auth))
        {
            return null;
        }
        var header = auth.ToString();
        const string Prefix = "Bearer ";
        if (header.Length <= Prefix.Length
            || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var raw = header[Prefix.Length..].Trim();
        if (string.IsNullOrEmpty(raw)) return null;

        try
        {
            if (!JwtReader.CanReadToken(raw)) return null;
            var jwt = JwtReader.ReadJwtToken(raw);
            return jwt.Claims
                .FirstOrDefault(c => string.Equals(
                    c.Type, "phone_hash", StringComparison.Ordinal))
                ?.Value;
        }
        catch (Exception)
        {
            // Defensive: a malformed token must not crash the role-switch
            // path. Fall back to no phone_hash and continue.
            return null;
        }
    }

    private string GetCorrelationId()
    {
        if (HttpContext.Items.TryGetValue("CorrelationId", out var v)
            && v is string s && !string.IsNullOrEmpty(s))
        {
            return s;
        }
        if (HttpContext.Request.Headers.TryGetValue("X-Correlation-Id", out var h)
            && !string.IsNullOrWhiteSpace(h))
        {
            return h.ToString();
        }
        return Activity.Current?.TraceId.ToString() ?? string.Empty;
    }

    private IActionResult BuildProblem(int status, string type, string title, string detail)
    {
        var problem = new ProblemDetails
        {
            Status   = status,
            Type     = type,
            Title    = title,
            Detail   = detail,
            Instance = HttpContext.Request.Path,
        };
        return new ObjectResult(problem)
        {
            StatusCode   = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
