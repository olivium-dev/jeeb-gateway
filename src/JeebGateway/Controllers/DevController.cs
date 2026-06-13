using System.Security.Cryptography;
using System.Text;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Security;
using JeebGateway.Services;
using JeebGateway.Users;
using JeebGateway.service.ServiceUserManagement;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UserManagementApiException = JeebGateway.service.ServiceUserManagement.ApiException;
// JEB-1472: the regenerated UserManagement NSwag client now emits a ProblemDetails
// DTO (the bumped UM 1.1.0 contract documents RFC 7807 error bodies). Alias the bare
// ProblemDetails name back to the ASP.NET Core MVC type these attributes intend, to
// resolve the CS0104 ambiguity with JeebGateway.service.ServiceUserManagement.ProblemDetails.
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace JeebGateway.Controllers;

/// <summary>
/// ADDITIVE, ENV-GATED developer / test-harness endpoints under <c>/dev/*</c>.
///
/// <para>
/// These routes exist ONLY so an external testing tool (the Jeeb E2E test
/// console) can create REAL user-management users on demand and inspect them,
/// per <c>SEED-SESSIONS-CONTRACT.md §1</c>. They are net-new (the
/// <c>/dev</c> prefix did not previously exist) and touch no existing route,
/// DTO, status code, or auth requirement.
/// </para>
///
/// <para>
/// <b>Gating.</b> The whole controller carries <see cref="DevOnlyAttribute"/>:
/// every action returns <b>404</b> unless <c>Features:DevEndpoints:Enabled</c>
/// is explicitly <c>true</c>. That flag is committed <c>false</c> in EVERY
/// environment (including <c>appsettings.Production.json</c>) and is flipped on
/// only via the environment variable <c>Features__DevEndpoints__Enabled=true</c>
/// in the single environment where the harness runs.
/// </para>
///
/// <para>
/// <b>No auto-seed.</b> Nothing here runs on its own. The gateway never seeds on
/// boot, in a hosted service, in a startup hook, in a migration, or in a
/// background sweeper. A user is created only when an explicit HTTP call hits
/// <see cref="SeedUser"/>.
/// </para>
///
/// <para>
/// <b>No minting.</b> The dev endpoints do not accept or return the token-mint
/// key and do not mint tokens. Token minting stays on the existing
/// <c>POST /auth/tokens</c> path (<c>X-Service-Auth-Key</c>). Seed and mint are
/// two separate steps.
/// </para>
/// </summary>
[ApiController]
[DevOnly]
[Route("dev")]
[Produces("application/json")]
// ADR-004 D1: public by design — dev/test seed + data routes used by the :3040 console.
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
// ADR-005 §A public: config-gated dev seam ([DevOnly]); bypasses L2.
[PublicEndpoint("Config-gated dev seed/data seam ([DevOnly]) — ADR-005 §A public.")]
public sealed class DevController : ControllerBase
{
    private readonly ServiceUserManagementClient _userManagement;
    private readonly IUserManagementDualRoleClient _phoneIdentity;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly ILogger<DevController> _logger;

    public DevController(
        ServiceUserManagementClient userManagement,
        IUserManagementDualRoleClient phoneIdentity,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        ILogger<DevController> logger)
    {
        _userManagement = userManagement;
        _phoneIdentity = phoneIdentity;
        _flags = flags;
        _logger = logger;
    }

    /// <summary>
    /// Create a REAL user-management user via the existing typed
    /// <see cref="ServiceUserManagementClient"/>.
    ///
    /// The gateway owns the mapping from the tool's semantic fields
    /// (<c>role</c> / <c>phone</c> / <c>displayName</c>) onto the real UM
    /// <see cref="RegisterUserRequest"/> contract (which has no phone/role/display
    /// field). <c>role</c> is carried as seed metadata + later token claim — the
    /// gateway does not invent a UM role column. The seeded user simply gives the
    /// harness a real <c>userId</c> + a usable login surface; phone identity is
    /// established by the OTP/phone-login flow during scenarios.
    /// </summary>
    /// <response code="200">User created upstream; canonical id returned.</response>
    /// <response code="400">Invalid body (missing role / phone / displayName).</response>
    /// <response code="404">Dev endpoints disabled (the <see cref="DevOnlyAttribute"/> gate).</response>
    /// <response code="409">Upstream collision (passthrough from user-management).</response>
    /// <response code="502">user-management unreachable.</response>
    [HttpPost("seed/user")]
    [ProducesResponseType(typeof(DevSeedUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> SeedUser([FromBody] DevSeedUserRequest? request, CancellationToken ct)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Role)
            || string.IsNullOrWhiteSpace(request.Phone)
            || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Problem(
                title: "Invalid dev seed request",
                detail: "role, phone and displayName are required.",
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.1");
        }

        var role = request.Role.Trim().ToLowerInvariant();
        var runId = string.IsNullOrWhiteSpace(request.RunId) ? null : request.RunId!.Trim();

        // Derive a unique username from the human display label (+ run id when
        // provided). The tool already guarantees uniqueness via its run scheme;
        // this is a deterministic, upstream-safe normalization.
        var username = DeriveUsername(request.DisplayName, request.Phone, runId);

        // Derive a unique, non-deliverable email when the caller did not supply
        // one. Uses the .test TLD so it can never be delivered.
        var email = string.IsNullOrWhiteSpace(request.Email)
            ? DeriveEmail(username, runId)
            : request.Email!.Trim();

        // Generate a strong random password when none is supplied. It is NEVER
        // logged and NEVER returned. confirmPassword always mirrors password.
        var password = string.IsNullOrWhiteSpace(request.Password)
            ? GenerateStrongPassword()
            : request.Password!;

        // referralCode is a NON-NULL column in user-management (User.ReferralCode
        // is IsRequired()/nullable:false). The public POST /api/User/register
        // 201s only because real clients always carry a referralCode in the body;
        // the dev seed previously omitted it, so it serialized to null and UM
        // rejected the insert (NOT NULL violation), surfacing as the seed 400.
        // Mirror the working path: always send a present, non-null value — the
        // caller's referralCode when supplied, otherwise an empty string (a valid
        // non-null value for the unbounded `text` column).
        var referralCode = string.IsNullOrWhiteSpace(request.ReferralCode)
            ? string.Empty
            : request.ReferralCode!.Trim();

        var registerRequest = new RegisterUserRequest
        {
            Email = email,
            Password = password,
            ConfirmPassword = password,
            Username = username,
            ReferralCode = referralCode,
            DateOfBirth = string.IsNullOrWhiteSpace(request.DateOfBirth) ? null : request.DateOfBirth,
        };

        try
        {
            var created = await _userManagement.RegisterAsync(registerRequest, ct);

            // --- Phone-identity convergence (the S02/H-B2 root fix) ---------------
            // The generic register surface keys identity by email/username and NEVER
            // writes a PhoneHash. The OTP sign-in path, by contrast, resolves identity
            // via user-management's phone-identity find-or-create (keyed by phone_hash).
            // On a virgin env that means a seeded user and the SAME phone's OTP login
            // mint DIFFERENT user ids — so a KYC role grant applied to the seeded id is
            // invisible to the OTP-resolved id (lease jeeb-20260613002036-8874: re-login
            // reads available_roles=["client"] missing "jeeber").
            //
            // Fix: when the UM upstream is enabled, establish the CANONICAL phone-keyed
            // identity through the SAME find-or-create the OTP path uses, and return
            // THAT user id from the seed. Now seed and OTP converge on one row, so a
            // later KYC grant lands on the id OTP re-login resolves. Idempotent and
            // additive; when UM is off we keep the legacy register-only id (unchanged).
            var canonicalUserId = created.UserId;
            if (_flags.CurrentValue.UserManagement)
            {
                try
                {
                    var identity = await _phoneIdentity.PhoneFindOrCreateAsync(request.Phone!.Trim(), ct);
                    if (!string.IsNullOrWhiteSpace(identity.UserId))
                    {
                        canonicalUserId = identity.UserId;
                    }
                }
                catch (UserManagementCallException ex)
                {
                    // Non-fatal: the user IS registered. Surface the seed with the
                    // register id and warn — the harness can still proceed, but flag
                    // that phone convergence did not complete (so a later grant/login
                    // mismatch is diagnosable). Never echo phone/password.
                    _logger.LogWarning(
                        "Dev seed phone-identity convergence failed (UM status {Status}); "
                        + "returning register id — seed and OTP login may diverge.",
                        ex.StatusCode);
                }
            }

            // Structured log — role/runId/username/email only. NEVER the password
            // or the phone (the phone is PII; only the derived handle is logged).
            _logger.LogInformation(
                "Dev-seeded user-management user username={Username} role={Role} runId={RunId}",
                username, role, runId ?? "(none)");

            var response = new DevSeedUserResponse
            {
                UserId = canonicalUserId,
                Role = role,
                Phone = request.Phone,
                DisplayName = request.DisplayName,
                Username = created.Username ?? username,
                Email = created.Email ?? email,
                Status = string.IsNullOrWhiteSpace(created.Status) ? "created" : created.Status!,
                CreatedAt = created.CreatedDate == default
                    ? DateTimeOffset.UtcNow
                    : created.CreatedDate,
                RunId = runId,
                Tags = request.Tags ?? Array.Empty<string>(),
            };

            return Ok(response);
        }
        catch (UserManagementApiException ex)
        {
            // Passthrough upstream 4xx (e.g. 409 on a collision) as a
            // ProblemDetails. Never echo the password — the UM message may carry
            // validation text, but our request never put the password in a field
            // UM reflects, so this is safe.
            return Problem(
                title: "user-management rejected the seed request",
                detail: $"Upstream returned status {ex.StatusCode}.",
                statusCode: ex.StatusCode is >= 400 and < 600 ? ex.StatusCode : StatusCodes.Status502BadGateway,
                type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.3");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dev seed failed: user-management unreachable");
            return Problem(
                title: "user-management unreachable",
                detail: "The dev seed call to user-management did not complete.",
                statusCode: StatusCodes.Status502BadGateway,
                type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.3");
        }
    }

    /// <summary>
    /// Read-only inspect route so Step Zero can SEE current data. Proxies the
    /// existing typed <c>ServiceUserManagementClient.AllAsync</c> (the same call
    /// <c>UserController.GetAllUsers</c> makes) and shapes it to the tool's view.
    /// Never returns passwords or tokens.
    /// </summary>
    /// <param name="runId">
    /// Optional filter: matches users whose derived username/email carries the
    /// run tag (the tool embeds the run id in both). Echoed back as
    /// <c>runIdFilter</c>.
    /// </param>
    [HttpGet("data/users")]
    [ProducesResponseType(typeof(DevUsersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetUsers(
        [FromQuery] string? runId,
        [FromQuery] int? skip,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        try
        {
            var upstream = await _userManagement.AllAsync(skip, limit, null, ct);

            var users = (upstream.Users ?? new List<UserProfileResponse>())
                .Select(u => new DevUserView
                {
                    UserId = u.UserId,
                    Username = u.Username,
                    Email = u.Email,
                    Status = "active",
                    CreatedAt = TryParseDate(u.CreatedDate),
                })
                .Where(u => MatchesRun(u, runId))
                .ToList();

            return Ok(new DevUsersResponse
            {
                Users = users,
                Count = users.Count,
                Source = "user-management",
                RunIdFilter = string.IsNullOrWhiteSpace(runId) ? null : runId,
            });
        }
        catch (UserManagementApiException ex)
        {
            return Problem(
                title: "user-management rejected the inspect request",
                detail: $"Upstream returned status {ex.StatusCode}.",
                statusCode: StatusCodes.Status502BadGateway,
                type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.3");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dev inspect (list) failed: user-management unreachable");
            return Problem(
                title: "user-management unreachable",
                detail: "The dev inspect call to user-management did not complete.",
                statusCode: StatusCodes.Status502BadGateway,
                type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.3");
        }
    }

    /// <summary>
    /// Single-user inspect, proxies
    /// <c>ServiceUserManagementClient.ProfileAsync</c>, shaped like one element
    /// of <see cref="GetUsers"/>. Same gating. Never returns passwords/tokens.
    /// </summary>
    [HttpGet("data/user/{userId}")]
    [ProducesResponseType(typeof(DevUserView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetUser([FromRoute] string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(
                title: "Invalid dev inspect request",
                detail: "userId is required.",
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.1");
        }

        try
        {
            var u = await _userManagement.ProfileAsync(userId, ct);

            return Ok(new DevUserView
            {
                UserId = u.UserId,
                Username = u.Username,
                Email = u.Email,
                Status = "active",
                CreatedAt = TryParseDate(u.CreatedDate),
            });
        }
        catch (UserManagementApiException ex)
        {
            if (ex.StatusCode == StatusCodes.Status404NotFound)
            {
                return Problem(
                    title: "User not found",
                    detail: "user-management has no user with that id.",
                    statusCode: StatusCodes.Status404NotFound,
                    type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.4");
            }

            return Problem(
                title: "user-management rejected the inspect request",
                detail: $"Upstream returned status {ex.StatusCode}.",
                statusCode: StatusCodes.Status502BadGateway,
                type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.3");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dev inspect (single) failed: user-management unreachable");
            return Problem(
                title: "user-management unreachable",
                detail: "The dev inspect call to user-management did not complete.",
                statusCode: StatusCodes.Status502BadGateway,
                type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.3");
        }
    }

    // ---------------------------------------------------------------------
    // Helpers (pure; no side effects, no auto-seed, no secrets in output).
    // ---------------------------------------------------------------------

    private static bool MatchesRun(DevUserView u, string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return true;
        var needle = runId.Trim();
        return (u.Username?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
            || (u.Email?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// Build an upstream-safe username base from the human display label, with a
    /// run-id suffix when provided. Falls back to digits of the phone so the
    /// result is never empty. Lowercased, alphanumeric + underscore only.
    /// </summary>
    internal static string DeriveUsername(string displayName, string phone, string? runId)
    {
        var slug = new string((displayName ?? string.Empty)
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "u" + new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
        }

        // Collapse runs of underscores so "Sami (run 7f3a)" -> "sami_run_7f3a".
        while (slug.Contains("__")) slug = slug.Replace("__", "_");

        if (!string.IsNullOrWhiteSpace(runId))
        {
            var r = new string(runId.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (!slug.Contains(r, StringComparison.Ordinal))
            {
                slug = $"{slug}_{r}";
            }
        }

        return slug.Length == 0 ? "devuser" : slug;
    }

    /// <summary>
    /// Derive a unique, non-deliverable (<c>.test</c> TLD) email from the
    /// username (which already carries the run id when provided).
    /// </summary>
    internal static string DeriveEmail(string username, string? runId)
    {
        var local = string.IsNullOrWhiteSpace(runId)
            ? $"seed-{username}"
            : $"seed-{runId.Trim().ToLowerInvariant()}-{username}";
        return $"{local}@jeeb.test";
    }

    /// <summary>
    /// Generate a strong random password used only to satisfy the upstream
    /// register contract. It is NEVER logged and NEVER returned. The seeded user
    /// authenticates through the phone-OTP flow, not this password.
    /// </summary>
    internal static string GenerateStrongPassword()
    {
        // 24 url-safe bytes + guaranteed character classes to satisfy any
        // upstream complexity rule.
        var bytes = RandomNumberGenerator.GetBytes(24);
        var body = Convert.ToBase64String(bytes)
            .Replace('+', 'A')
            .Replace('/', 'z')
            .Replace("=", string.Empty);
        return $"Aa1!{body}";
    }

    private static DateTimeOffset TryParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UtcNow;
}

// -------------------------------------------------------------------------
// DTOs — net-new, used only by the dev controller.
// -------------------------------------------------------------------------

/// <summary>Request body for <c>POST /dev/seed/user</c> (semantic fields).</summary>
public sealed class DevSeedUserRequest
{
    /// <summary>"client" | "jeeber" | "admin" (required; free-string tolerated, lowercased).</summary>
    public string? Role { get; set; }

    /// <summary>E.164 phone (required); the tool guarantees uniqueness.</summary>
    public string? Phone { get; set; }

    /// <summary>Human label (required) -> UM username base.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Optional; if omitted the gateway derives a unique non-deliverable one.</summary>
    public string? Email { get; set; }

    /// <summary>Optional; if omitted the gateway generates a strong random one (never returned).</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Optional referral code. user-management requires a NON-NULL referralCode
    /// (the column is IsRequired()); when omitted the gateway sends an empty
    /// string so the upstream insert succeeds, matching the public register path.
    /// </summary>
    public string? ReferralCode { get; set; }

    /// <summary>Optional date of birth, ISO date.</summary>
    public string? DateOfBirth { get; set; }

    /// <summary>Optional tool run id, for traceability/logging only.</summary>
    public string? RunId { get; set; }

    /// <summary>Optional free labels, echoed back.</summary>
    public string[]? Tags { get; set; }
}

/// <summary>Response body for <c>POST /dev/seed/user</c>.</summary>
public sealed class DevSeedUserResponse
{
    public string? UserId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Status { get; set; } = "created";
    public DateTimeOffset CreatedAt { get; set; }
    public string? RunId { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
}

/// <summary>One shaped user in the inspect views.</summary>
public sealed class DevUserView
{
    public string? UserId { get; set; }
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Response body for <c>GET /dev/data/users</c>.</summary>
public sealed class DevUsersResponse
{
    public List<DevUserView> Users { get; set; } = new();
    public int Count { get; set; }
    public string Source { get; set; } = "user-management";
    public string? RunIdFilter { get; set; }
}
