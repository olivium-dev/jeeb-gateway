using System.Security.Claims;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Cms;
using JeebGateway.Security;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// WS-01 — CMS authoring plane (W4). Gateway-owned surfaces mounted under
/// <c>/gateway/admin/v1/cms/*</c> (the mock's <c>cms-admin-service</c>). This
/// is the surface that drives every MFE config envelope
/// (<c>ofl-cms-orders/users/wallet/kyc-mfe</c>).
///
/// Endpoints:
/// <list type="bullet">
///   <item>GET    surfaces — list authored surfaces.</item>
///   <item>GET    config/{surfaceId}/published — live published envelope.</item>
///   <item>GET    config/{surfaceId}/draft — current draft (404 if none).</item>
///   <item>PUT    config/{surfaceId}/draft — upsert draft (X-Cms-Capability gate).</item>
///   <item>POST   config/{surfaceId}/publish — STEP-UP TOTP gated; version bump.</item>
///   <item>GET    config/{surfaceId}/versions — published history.</item>
///   <item>GET    config/{surfaceId}/diff — key-level diff between two versions.</item>
///   <item>GET    dev/step-up-totp — dev helper returning the mock code.</item>
/// </list>
///
/// PUBLISH gate ordering is load-bearing (§4 of the contract):
/// <c>X-Cms-Capability: deny</c> → 403 runs FIRST; step-up validation runs
/// BEFORE surface lookup, so an unknown surface without a valid TOTP yields
/// 401 (step_up_required), NOT 404. Errors are RFC 7807 ProblemDetails with a
/// stable <c>type</c> URN.
/// </summary>
[ApiController]
[Route("gateway/admin/v1/cms")]
[PublicEndpoint("CMS authoring plane: access is governed by the X-Cms-Capability header gate and step-up TOTP. ADR-005 L2 is intentionally not used here.")]
public sealed class CmsAuthoringController : ControllerBase
{
    private const string CapabilityHeaderName = "X-Cms-Capability";
    private const string UserIdHeaderName = "X-User-Id";

    private const string ErrStepUpRequired = "urn:jeeb:error:step_up_required";
    private const string ErrStepUpInvalid = "urn:jeeb:error:step_up_invalid";
    private const string ErrForbidden = "urn:jeeb:error:forbidden";
    private const string ErrSurfaceNotFound = "urn:jeeb:error:surface_not_found";
    private const string ErrVersionNotFound = "urn:jeeb:error:version_not_found";

    private readonly ICmsSurfaceStore _store;
    private readonly TimeProvider _clock;

    public CmsAuthoringController(ICmsSurfaceStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    // ---- surfaces -----------------------------------------------------------

    [HttpGet("surfaces")]
    [ProducesResponseType(typeof(CmsSurfaceListResponse), StatusCodes.Status200OK)]
    public IActionResult ListSurfaces()
    {
        var summaries = _store.ListSurfaces()
            .Select(s => new CmsSurfaceSummaryDto(
                s.SurfaceId, s.Title, s.LatestPublishedVersion, s.Draft is not null))
            .ToList();
        return Ok(new CmsSurfaceListResponse(summaries));
    }

    // ---- published / draft reads -------------------------------------------

    [HttpGet("config/{surfaceId}/published")]
    [ProducesResponseType(typeof(CmsConfigEnvelopeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult GetPublished(string surfaceId)
    {
        var surface = _store.GetSurface(surfaceId);
        if (surface is null)
        {
            return SurfaceNotFound(surfaceId);
        }

        var live = surface.LatestPublished;
        if (live is null)
        {
            return SurfaceNotFound(surfaceId);
        }

        return Ok(new CmsConfigEnvelopeDto(
            surfaceId, live.Version, live.Config.Data, live.PublishedAt));
    }

    [HttpGet("config/{surfaceId}/draft")]
    [ProducesResponseType(typeof(CmsConfigEnvelopeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult GetDraft(string surfaceId)
    {
        var surface = _store.GetSurface(surfaceId);
        if (surface is null || surface.Draft is null)
        {
            return SurfaceNotFound(surfaceId);
        }

        return Ok(new CmsConfigEnvelopeDto(
            surfaceId, surface.LatestPublishedVersion, surface.Draft.Data, PublishedAt: null));
    }

    // ---- draft upsert (capability gate) ------------------------------------

    [HttpPut("config/{surfaceId}/draft")]
    [ProducesResponseType(typeof(CmsConfigEnvelopeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult UpsertDraft(string surfaceId, [FromBody] CmsDraftUpsertRequest? body)
    {
        if (IsCapabilityDenied())
        {
            return CapabilityDenied();
        }

        var config = new CmsConfig
        {
            Data = body?.Config ?? new Dictionary<string, object?>(),
        };

        var surface = _store.UpsertDraft(surfaceId, config);
        if (surface is null)
        {
            return SurfaceNotFound(surfaceId);
        }

        return Ok(new CmsConfigEnvelopeDto(
            surfaceId, surface.LatestPublishedVersion, config.Data, PublishedAt: null));
    }

    // ---- publish (STEP-UP TOTP gate) ---------------------------------------

    [HttpPost("config/{surfaceId}/publish")]
    [ProducesResponseType(typeof(CmsPublishResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult Publish(string surfaceId)
    {
        // §4 ordering: capability deny FIRST, then step-up, then surface lookup.
        if (IsCapabilityDenied())
        {
            return CapabilityDenied();
        }

        var totp = Request.Headers[CmsStepUpValidator.StepUpHeaderName].ToString();
        switch (CmsStepUpValidator.Evaluate(totp))
        {
            case CmsStepUpResult.Required:
                return Problem401(ErrStepUpRequired, "Step-up TOTP required",
                    "A valid 6-digit step-up TOTP is required to publish.");
            case CmsStepUpResult.Invalid:
                return Problem403(ErrStepUpInvalid, "Step-up TOTP invalid",
                    "The supplied step-up TOTP is not valid.");
        }

        // TOTP valid → surface lookup now drives 404.
        // SEC-C1 (Leg-11): the publish actor / audit id must come from the VALIDATED principal,
        // not the raw X-User-Id header. A raw client could otherwise forge the publish actor after
        // clearing the CMS capability/TOTP gates. The header is honoured ONLY when EdgeIdentityTrust
        // permits it (Dev/Testing or a secret-gated trusted edge); otherwise the actor is "unknown".
        var userId = ResolvePublishActor();
        var version = _store.Publish(
            surfaceId,
            string.IsNullOrWhiteSpace(userId) ? "unknown" : userId,
            _clock.GetUtcNow());

        if (version is null)
        {
            return SurfaceNotFound(surfaceId);
        }

        return Ok(new CmsPublishResponse(
            surfaceId, version.Version, version.Config.Data,
            version.PublishedAt, version.PublishedByUserId));
    }

    // ---- versions + diff ----------------------------------------------------

    [HttpGet("config/{surfaceId}/versions")]
    [ProducesResponseType(typeof(CmsVersionListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult GetVersions(string surfaceId)
    {
        var surface = _store.GetSurface(surfaceId);
        if (surface is null)
        {
            return SurfaceNotFound(surfaceId);
        }

        var rows = surface.Versions
            .OrderByDescending(v => v.Version)
            .Select(v => new CmsVersionSummaryDto(v.Version, v.PublishedAt, v.PublishedByUserId))
            .ToList();

        return Ok(new CmsVersionListResponse(surfaceId, rows));
    }

    [HttpGet("config/{surfaceId}/diff")]
    [ProducesResponseType(typeof(CmsDiffResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult GetDiff(string surfaceId, [FromQuery] int? from, [FromQuery] int? to)
    {
        var surface = _store.GetSurface(surfaceId);
        if (surface is null)
        {
            return SurfaceNotFound(surfaceId);
        }

        // Default: diff the previous published version against the latest.
        var latest = surface.LatestPublishedVersion;
        var fromVersion = from ?? Math.Max(1, latest - 1);
        var toVersion = to ?? latest;

        var fromV = surface.Versions.FirstOrDefault(v => v.Version == fromVersion);
        var toV = surface.Versions.FirstOrDefault(v => v.Version == toVersion);
        if (fromV is null || toV is null)
        {
            return Problem(ErrVersionNotFound, StatusCodes.Status404NotFound,
                "Version not found",
                $"One of versions {fromVersion}/{toVersion} does not exist for surface '{surfaceId}'.");
        }

        return Ok(CmsConfigDiffer.Diff(surfaceId, fromV, toV));
    }

    // ---- dev helper ---------------------------------------------------------

    [HttpGet("dev/step-up-totp")]
    [ProducesResponseType(typeof(CmsStepUpDevCodeResponse), StatusCodes.Status200OK)]
    public IActionResult GetStepUpDevCode() =>
        Ok(new CmsStepUpDevCodeResponse(CmsStepUpValidator.DevStepUpCode, ExpiresInSeconds: 900));

    // ---- helpers ------------------------------------------------------------

    /// <summary>
    /// SEC-C1 — resolve the publish actor / audit id from the validated JWT principal, mirroring
    /// <see cref="JeebGateway.Users.UserIdentity"/>'s claim precedence (sid → NameIdentifier → sub).
    /// The raw <c>X-User-Id</c> header is only honoured when <see cref="EdgeIdentityTrust"/> permits
    /// it, so a raw public caller cannot forge the publish actor after passing the capability/TOTP
    /// gates. Returns <c>null</c> when no trustworthy identity is available (caller stamps "unknown").
    /// </summary>
    private string? ResolvePublishActor()
    {
        var fromClaim = User?.FindFirstValue(ClaimTypes.Sid)
                        ?? User?.FindFirstValue("sid")
                        ?? User?.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? User?.FindFirstValue("sub");
        if (!string.IsNullOrWhiteSpace(fromClaim))
        {
            return fromClaim;
        }

        if (EdgeIdentityTrust.HeadersTrusted(HttpContext)
            && Request.Headers.TryGetValue(UserIdHeaderName, out var header)
            && !string.IsNullOrWhiteSpace(header))
        {
            return header.ToString();
        }

        return null;
    }

    private bool IsCapabilityDenied() =>
        string.Equals(
            Request.Headers[CapabilityHeaderName].ToString(),
            "deny",
            StringComparison.OrdinalIgnoreCase);

    private IActionResult CapabilityDenied() =>
        Problem403(ErrForbidden, "Forbidden", "CMS capability denied for this caller.");

    private IActionResult SurfaceNotFound(string surfaceId) =>
        Problem(ErrSurfaceNotFound, StatusCodes.Status404NotFound, "Surface not found",
            $"CMS surface '{surfaceId}' does not exist.");

    private IActionResult Problem401(string type, string title, string detail) =>
        Problem(type, StatusCodes.Status401Unauthorized, title, detail);

    private IActionResult Problem403(string type, string title, string detail) =>
        Problem(type, StatusCodes.Status403Forbidden, title, detail);

    private IActionResult Problem(string type, int status, string title, string detail)
    {
        var problem = new ProblemDetails
        {
            Type = type,
            Title = title,
            Status = status,
            Detail = detail,
        };
        return StatusCode(status, problem);
    }
}
