using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Thin BFF surface over the <c>cdn-service</c> asset store
/// (<see cref="ICDNServiceClient"/>). The gateway holds NO asset bytes durably —
/// every persist/read resolves to cdn-service, which owns storage, the 90-day
/// retention window, and signed-URL minting (JEB-527 / JEB-519 / JEB-59).
///
/// Scoped to the authenticated caller: the owning userId comes from the JWT
/// subject (falling back to the edge-injected <c>X-User-Id</c> header for the
/// MVP) via <see cref="UserIdentity"/>, so a caller can only register assets
/// under their own id and request signed URLs for their own assets.
///
/// Gated by <c>FeatureFlags:UseUpstream:Cdn</c>. cdn-service is NOT yet deployed
/// (its Production BaseUrl is a placeholder — see <see cref="ICDNServiceClient"/>),
/// so this path is a runtime kill switch: when off, the endpoints return 503
/// ProblemDetails rather than dialing an unconfigured/unroutable downstream.
/// This mirrors the remote-user-preferences net-new kill-switch shape.
/// </summary>
[ApiController]
[Route("api/cdn/assets")]
public sealed class CdnController : ControllerBase
{
    // Cap signed-URL lifetime so a leaked link is short-lived. cdn-service is the
    // record-of-truth; this is a defence-in-depth bound at the gateway edge.
    private const int MaxSignedUrlTtlSeconds = 3600;
    private const int DefaultSignedUrlTtlSeconds = 300;

    private readonly ICDNServiceClient _cdn;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;

    public CdnController(
        ICDNServiceClient cdn,
        IOptionsMonitor<UpstreamFeatureFlags> flags)
    {
        _cdn = cdn;
        _flags = flags;
    }

    /// <summary>
    /// Reads metadata (content type, size, retention/expiry) for a stored asset.
    /// Real path: <c>GET /api/v1/assets/{assetId}</c> on cdn-service. Returns 404
    /// when the asset has aged out of the 90-day retention window.
    /// </summary>
    [HttpGet("{assetId}")]
    [ProducesResponseType(typeof(CdnAsset), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetAsset(string assetId, CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized)) return unauthorized;
        if (string.IsNullOrWhiteSpace(assetId)) return InvalidAssetId();
        if (!_flags.CurrentValue.Cdn) return UpstreamDisabled();

        var asset = await _cdn.GetAssetAsync(assetId, ct);
        if (asset is null)
        {
            return Problem(
                title: "Asset not found",
                detail: $"Asset '{assetId}' does not exist or has aged out of the retention window.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Ok(asset);
    }

    /// <summary>
    /// Mints a short-lived signed download URL for a stored asset. Real path:
    /// <c>GET /api/v1/assets/{assetId}/signed-url?ttlSeconds=...</c>. The mobile
    /// client downloads directly from cdn-service; bytes never re-stream through
    /// the gateway (JEB-519).
    /// </summary>
    [HttpGet("{assetId}/signed-url")]
    [ProducesResponseType(typeof(CdnSignedUrl), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetSignedUrl(
        string assetId,
        [FromQuery] int? ttlSeconds,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized)) return unauthorized;
        if (string.IsNullOrWhiteSpace(assetId)) return InvalidAssetId();

        var ttl = ttlSeconds ?? DefaultSignedUrlTtlSeconds;
        if (ttl < 1 || ttl > MaxSignedUrlTtlSeconds)
        {
            return Problem(
                title: "Invalid signed-URL TTL",
                detail: $"ttlSeconds must be between 1 and {MaxSignedUrlTtlSeconds}.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (!_flags.CurrentValue.Cdn) return UpstreamDisabled();

        var signed = await _cdn.GetSignedUrlAsync(assetId, ttl, ct);
        return Ok(signed);
    }

    private IActionResult UpstreamDisabled() => Problem(
        title: "CDN upstream disabled",
        detail: "FeatureFlags:UseUpstream:Cdn is off in this environment "
              + "(cdn-service is not yet deployed; its BaseUrl is a placeholder).",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private IActionResult InvalidAssetId() => Problem(
        title: "Invalid asset id",
        detail: "Asset id must be a non-empty string.",
        statusCode: StatusCodes.Status400BadRequest);
}
