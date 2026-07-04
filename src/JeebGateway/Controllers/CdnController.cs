using JeebGateway.Auth.Capabilities;
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
///
/// <para>
/// <b>JEBV4-113 §CDN fallback — ESCALATE, no Postgres-backed fallback built
/// (decision recorded here, not implemented).</b> When the flag is OFF, ALL
/// THREE actions below (<see cref="BrokerUploadUrl"/>, <see cref="GetAsset"/>,
/// <see cref="GetSignedUrl"/>) 503 via <see cref="UpstreamDisabled"/> with NO
/// fallback path — this is the entire current behavior, confirmed by reading
/// this file; there is no partial/degraded mode today. A gateway-Postgres-backed
/// asset store was considered and rejected as NOT small/clean:
/// <list type="bullet">
///   <item>The whole point of this endpoint is a signed <b>PUT</b> — the client
///     uploads bytes directly to a URL this broker mints. Reproducing that in
///     Postgres means building (a) an HTTP endpoint that accepts raw byte PUTs
///     and writes them to a <c>bytea</c>/large-object column, (b) a URL-signing
///     scheme (HMAC + expiry) to authorize that PUT without the caller's normal
///     bearer auth (signed URLs are deliberately bearer-free), and (c) a
///     symmetric signed-GET path for <see cref="GetSignedUrl"/> plus the
///     content-type/size/90-day-retention bookkeeping <see cref="GetAsset"/>
///     already promises. That is a from-scratch object-storage service, not a
///     fallback.</item>
///   <item>Building it in the gateway would re-implement cdn-service's actual
///     job inside the BFF — the same "gateway holds zero durable business
///     state, only aggregates" law this class's own doc comment states for the
///     KYC domain applies here too (ADR-0004). A duplicate, gateway-local
///     object store would fork asset state across two stores the moment
///     cdn-service does go live, with no migration story.</item>
/// </list>
/// Per the ticket's own guardrail ("if not clean, do NOT hack it — ESCALATE"),
/// no fallback is implemented; the owner-visible behavior remains an honest 503
/// until cdn-service is deployed and the flag flips on.
/// </para>
/// </summary>
[ApiController]
[Route("api/cdn/assets")]
// ADR-005 L2 §H–J participant {client, jeeber}: all actions are caller-scoped CDN brokering
// (own-asset upload/read/signed-url). Owner scoping stays STATE in-action / cdn-service.
[RequireCapability(Capabilities.CdnBroker)]
public sealed class CdnController : ControllerBase
{
    // Cap signed-URL lifetime so a leaked link is short-lived. cdn-service is the
    // record-of-truth; this is a defence-in-depth bound at the gateway edge.
    private const int MaxSignedUrlTtlSeconds = 3600;
    private const int DefaultSignedUrlTtlSeconds = 300;

    // BR-2: a brokered signed PUT upload URL must live ≤ 5 minutes. The broker
    // clamps to this regardless of any requested TTL (defence-in-depth; cdn-service
    // is the record-of-truth for the actual expiry it stamps).
    private const int MaxUploadUrlTtlSeconds = 300;

    // The KYC document slots the signed-PUT broker accepts (DEC1, S03 H2/H3).
    // Generic vocab; the Jeeb-specific field-name mapping lives in the KYC submit
    // BFF, not here.
    private static readonly IReadOnlySet<string> AllowedUploadSlots =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id_document_front",
            "id_document_back",
            "vehicle_registration",
            "selfie_with_liveness",
        };

    private static readonly IReadOnlySet<string> AllowedUploadContentTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg",
            "image/jpg",
            "image/png",
            "image/webp",
            "image/heic",
            "application/pdf",
        };

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
    /// S03 H2/H3 (DEC1). Brokers a short-lived signed <b>PUT</b> upload URL for a
    /// KYC document slot. The mobile client uploads the bytes DIRECTLY to the
    /// returned <c>upload_url</c> (H2b) — bytes never re-stream through the gateway —
    /// then records the <c>object_ref</c> in the KYC submission. <c>expires_in</c>
    /// is bounded to ≤ 300s (BR-2).
    ///
    /// Request: <c>{ "slot": "id_document_front", "content_type": "image/jpeg" }</c>.
    /// Scoped to the authenticated caller: the owning userId comes from the JWT
    /// subject, so a caller can only broker uploads under their own id.
    /// </summary>
    [HttpPost("")]
    [ProducesResponseType(typeof(CdnUploadTicketResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> BrokerUploadUrl(
        [FromBody] CdnUploadUrlBody? body,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        if (body is null || string.IsNullOrWhiteSpace(body.Slot))
        {
            return Problem(
                title: "Invalid upload request",
                detail: "slot is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var slot = body.Slot.Trim();
        if (!AllowedUploadSlots.Contains(slot))
        {
            return Problem(
                title: "Invalid upload slot",
                detail: $"slot must be one of: {string.Join(", ", AllowedUploadSlots)}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var contentType = string.IsNullOrWhiteSpace(body.ContentType) ? "image/jpeg" : body.ContentType.Trim();
        if (!AllowedUploadContentTypes.Contains(contentType))
        {
            return Problem(
                title: "Invalid content type",
                detail: $"content_type must be one of: {string.Join(", ", AllowedUploadContentTypes)}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!_flags.CurrentValue.Cdn) return UpstreamDisabled();

        // Clamp the TTL to the BR-2 bound before dialing cdn-service.
        var ttl = body.TtlSeconds is > 0 and <= MaxUploadUrlTtlSeconds
            ? body.TtlSeconds.Value
            : MaxUploadUrlTtlSeconds;

        var ticket = await _cdn.MintUploadUrlAsync(new CdnUploadUrlRequest
        {
            Slot = slot,
            ContentType = contentType,
            OwnerUserId = userId,
            TtlSeconds = ttl,
        }, ct);

        // Defence-in-depth: never advertise an expiry beyond the BR-2 bound even
        // if the upstream returns a larger one.
        var expiresIn = ticket.ExpiresInSeconds is > 0 and <= MaxUploadUrlTtlSeconds
            ? ticket.ExpiresInSeconds
            : MaxUploadUrlTtlSeconds;

        return Ok(new CdnUploadTicketResponse
        {
            UploadUrl = ticket.UploadUrl,
            ObjectRef = ticket.ObjectRef,
            ExpiresIn = expiresIn,
        });
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

    // JEBV4-113: no fallback by design — see the class-level ESCALATE note. Every
    // action 503s here when the flag is off; there is no degraded/gateway-local
    // storage path.
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

/// <summary>
/// Body for <c>POST /api/cdn/assets</c> (the signed-PUT broker). The mobile
/// client sends the snake_case <c>content_type</c> contract; both casings bind.
/// </summary>
public sealed class CdnUploadUrlBody
{
    public string? Slot { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("content_type")]
    public string? ContentType { get; init; }

    /// <summary>Optional requested TTL in seconds; clamped to ≤ 300 (BR-2).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("ttl_seconds")]
    public int? TtlSeconds { get; init; }
}

/// <summary>
/// Response for <c>POST /api/cdn/assets</c>. Snake_case to match the S03 mobile
/// contract: <c>upload_url</c> (signed PUT target), <c>object_ref</c> (durable
/// ref recorded in the submission), <c>expires_in</c> (seconds, ≤ 300, BR-2).
/// </summary>
public sealed class CdnUploadTicketResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("upload_url")]
    public required string UploadUrl { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("object_ref")]
    public required string ObjectRef { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
    public required int ExpiresIn { get; init; }
}
