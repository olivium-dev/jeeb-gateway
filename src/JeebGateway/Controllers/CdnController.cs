using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Observability;
using JeebGateway.Services;
using JeebGateway.Services.Cdn;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Idempotency;
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
/// FOUR actions below (<see cref="BrokerUploadUrl"/>, <see cref="GetAsset"/>,
/// <see cref="GetSignedUrl"/>, <see cref="GetAssetContent"/>) 503 via
/// <see cref="UpstreamDisabled"/> with NO
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
    private static readonly JsonSerializerOptions CdnIdempotencyJson =
        new(JsonSerializerDefaults.Web);

    // The upload slots the signed-PUT broker accepts: the KYC document slots
    // (DEC1, S03 H2/H3) plus proof_of_delivery (JEBV4-200, companion to
    // jeeb-mobile PR #117 — the delivery-proof photo slot). Generic vocab; the
    // Jeeb-specific field-name mapping lives in the respective submit BFFs, not
    // here.
    private static readonly IReadOnlySet<string> AllowedUploadSlots =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id_document_front",
            "id_document_back",
            "vehicle_registration",
            "selfie_with_liveness",
            "proof_of_delivery",
            // P4/P5 (b01-20260725): in-chat image attachment (camera + gallery).
            // Same brokered signed-PUT path as the KYC/POD slots — only this
            // allowlist entry differs. cdn-service does NOT validate slots (it
            // sanitizes + uses the value as a storage dir), so no upstream change.
            "chat_attachment",
            "dispute_evidence",
            "support_attachment",
            // F5 — profile picture upload. Same brokered signed-PUT path; the
            // caller-scoped POST here is unchanged (bearer-authenticated,
            // OwnerUserId = the caller's own userId). Public serving of the
            // uploaded bytes is a SEPARATE, narrowly-scoped route
            // (AvatarController) — this allowlist entry only lets an
            // authenticated caller broker an upload ticket for their own avatar.
            "profile_avatar",
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
            "audio/mp4",
        };

    private readonly ICDNServiceClient _cdn;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly IConfiguration _config;
    private readonly IIdempotencyStore _idempotency;

    /// <summary>
    /// P4/P5 — used ONLY by <see cref="GetAssetContent"/> to dial cdn-service's
    /// fetch route through the dedicated, resilience-free
    /// <c>cdn-proxy</c> named client (<see cref="CdnUploadUrlResolver.ProxyHttpClientName"/>,
    /// registered in ServiceClientExtensions with the cdn BaseAddress, a generous
    /// timeout and AllowAutoRedirect=false).
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CdnController> _logger;

    public CdnController(
        ICDNServiceClient cdn,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        IConfiguration config,
        IIdempotencyStore idempotency,
        IHttpClientFactory httpClientFactory,
        ILogger<CdnController> logger)
    {
        _cdn = cdn;
        _flags = flags;
        _config = config;
        _idempotency = idempotency;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
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
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
        {
            return Problem(
                title: "Invalid idempotency key",
                detail: "Idempotency-Key is required and must be at most 200 characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

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
        if (contentType.Equals("audio/mp4", StringComparison.OrdinalIgnoreCase)
            && !slot.Equals("dispute_evidence", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(
                title: "Invalid content type for upload slot",
                detail: "audio/mp4 is accepted only for dispute_evidence.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!_flags.CurrentValue.Cdn) return UpstreamDisabled();

        if (_idempotency is not IExternalIdempotencyStore)
        {
            RecordCdnOutcome("idempotency_unavailable", slot);
            _logger.LogError(
                "CDN upload-ticket reservation has no external idempotency store slot={Slot} user_id={UserId} "
                + "correlation_id={CorrelationId}",
                slot, userId, Activity.Current?.TraceId.ToString() ?? "none");
            return Problem(
                title: "Upload ticket reservation unavailable",
                detail: "The external upload-ticket reservation service is unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Clamp the TTL to the BR-2 bound before dialing cdn-service.
        var ttl = body.TtlSeconds is > 0 and <= MaxUploadUrlTtlSeconds
            ? body.TtlSeconds.Value
            : MaxUploadUrlTtlSeconds;

        var requestHash = Hash($"{slot.ToLowerInvariant()}\n{contentType.ToLowerInvariant()}\n{ttl}");
        var operationScope = Hash($"cdn-upload-ticket\n{userId}\n{idempotencyKey.Trim()}");
        var reservationKey = $"cdn-upload-ticket:{operationScope}:reservation";
        var resultKey = $"cdn-upload-ticket:{operationScope}:result";

        try
        {
            var stored = await _idempotency.GetAsync(resultKey, ct);
            if (stored is not null)
                return ReplayTicket(stored, requestHash, slot);

            var reservationJson = JsonSerializer.Serialize(
                new CdnUploadReservation(requestHash), CdnIdempotencyJson);
            var reservation = await _idempotency.PutOrGetAsync(
                reservationKey, StatusCodes.Status202Accepted, reservationJson, ttl, ct);
            if (!ReservationMatches(reservation, requestHash))
                return IdempotencyConflict(slot);

            if (!reservation.Inserted)
            {
                stored = await _idempotency.GetAsync(resultKey, ct);
                if (stored is not null)
                    return ReplayTicket(stored, requestHash, slot);

                RecordCdnOutcome("reserved", slot);
                return Problem(
                    title: "Upload ticket request unresolved",
                    detail: "This idempotency key already has an in-progress or unresolved upload-ticket reservation. Retry after its ticket window expires if no result becomes available.",
                    statusCode: StatusCodes.Status409Conflict);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordCdnOutcome("idempotency_unavailable", slot);
            _logger.LogError(error,
                "CDN broker could not reserve upload ticket slot={Slot} user_id={UserId} correlation_id={CorrelationId}",
                slot, userId, Activity.Current?.TraceId.ToString() ?? "none");
            return Problem(
                title: "Upload ticket reservation unavailable",
                detail: "The durable upload-ticket reservation service is unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        CdnUploadTicket ticket;
        try
        {
            ticket = await _cdn.MintUploadUrlAsync(new CdnUploadUrlRequest
            {
                Slot = slot,
                ContentType = contentType,
                OwnerUserId = userId,
                TtlSeconds = ttl,
            }, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordCdnOutcome("upstream_failure", slot);
            _logger.LogError(error,
                "CDN broker failed to mint upload ticket slot={Slot} user_id={UserId} correlation_id={CorrelationId}",
                slot, userId, Activity.Current?.TraceId.ToString() ?? "none");
            return Problem(
                title: "Upload broker failed",
                detail: "The asset store could not mint an upload ticket.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        // Defence-in-depth: never advertise an expiry beyond the BR-2 bound even
        // if the upstream returns a larger one.
        if (ticket.ExpiresInSeconds < ttl || string.IsNullOrWhiteSpace(ticket.ObjectRef))
        {
            RecordCdnOutcome("upstream_invalid", slot);
            _logger.LogError(
                "CDN broker received invalid ticket metadata slot={Slot} requested_ttl={RequestedTtl} "
                + "ticket_ttl={TicketTtl} correlation_id={CorrelationId}",
                slot, ttl, ticket.ExpiresInSeconds, Activity.Current?.TraceId.ToString() ?? "none");
            return Problem(
                title: "Upload broker failed",
                detail: "The asset store returned an invalid upload ticket.",
                statusCode: StatusCodes.Status502BadGateway);
        }
        var expiresIn = Math.Min(ttl, Math.Min(ticket.ExpiresInSeconds, MaxUploadUrlTtlSeconds));

        // JEBV4-259 — ABSOLUTIZE the upload_url (approach B). cdn-service's Local
        // provider mints a relative, host-less signed-PUT URL the client cannot
        // reach (cdn is internal-only, no edge route). Rewrite it to the gateway's
        // absolute streaming-proxy route so the client PUTs to a reachable URL and
        // the gateway streams the bytes to cdn (CdnUploadProxyController). An
        // already-public absolute URL (future S3 / approach A) passes through.
        var gatewayPublicBase = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        Uri.TryCreate(_config["Services:Cdn:BaseUrl"], UriKind.Absolute, out var cdnInternalBase);
        string uploadUrl;
        try
        {
            uploadUrl = CdnUploadUrlResolver.Resolve(ticket.UploadUrl, cdnInternalBase, gatewayPublicBase);
        }
        catch (InvalidOperationException ex)
        {
            RecordCdnOutcome("upstream_invalid", slot);
            _logger.LogError(ex, "CDN broker: cdn-service returned an unusable upload_url for slot {Slot}.", slot);
            return Problem(
                title: "Upload broker failed",
                detail: "The asset store returned an upload target the gateway cannot make reachable.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        // JEBV4-259 — stop DROPPING method + requiredHeaders. Relay cdn's method
        // (default PUT) and requiredHeaders, and GUARANTEE a Content-Type so the
        // mobile client's dedicated, interceptor-free Dio sends the right media
        // type (the shared-Dio JSON default is exactly what corrupted the body).
        var method = string.IsNullOrWhiteSpace(ticket.Method) ? "PUT" : ticket.Method;
        var requiredHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in ticket.RequiredHeaders)
        {
            requiredHeaders[header.Key] = header.Value;
        }
        if (!requiredHeaders.ContainsKey("Content-Type"))
        {
            requiredHeaders["Content-Type"] = contentType;
        }

        var response = new CdnUploadTicketResponse
        {
            UploadUrl = uploadUrl,
            ObjectRef = ticket.ObjectRef,
            ExpiresIn = expiresIn,
            Method = method,
            RequiredHeaders = requiredHeaders,
        };

        try
        {
            var resultJson = JsonSerializer.Serialize(
                new CdnUploadReplay(requestHash, response), CdnIdempotencyJson);
            var result = await _idempotency.PutOrGetAsync(
                resultKey, StatusCodes.Status200OK, resultJson, expiresIn, ct);
            if (!ReplayMatches(result, requestHash, out var storedResponse))
                return IdempotencyConflict(slot);

            Response.Headers["Idempotency-Replayed"] = result.Inserted ? "false" : "true";
            RecordCdnOutcome(result.Inserted ? "minted" : "replayed", slot);
            _logger.LogInformation(
                "CDN upload ticket {Outcome} slot={Slot} user_id={UserId} expires_in={ExpiresIn} "
                + "correlation_id={CorrelationId}",
                result.Inserted ? "minted" : "replayed", slot, userId, expiresIn,
                Activity.Current?.TraceId.ToString() ?? "none");
            return Ok(storedResponse);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordCdnOutcome("result_persist_failed", slot);
            _logger.LogError(error,
                "CDN broker minted but could not persist upload ticket result slot={Slot} user_id={UserId} "
                + "correlation_id={CorrelationId}",
                slot, userId, Activity.Current?.TraceId.ToString() ?? "none");
            return Problem(
                title: "Upload ticket result unavailable",
                detail: "The upload ticket could not be committed for safe replay.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
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

    /// <summary>
    /// P4/P5 (b01-20260725) — the AUTHENTICATED read path for a brokered asset.
    /// cdn-service is internal-only (no edge route) and exposes NO signed-download
    /// endpoint: its surface is
    /// <c>api/ImageUpload/{upload,fetch,presign-put,put-signed,…}</c>, so
    /// <see cref="GetSignedUrl"/> above (which dials the non-existent
    /// <c>api/v1/assets/{id}/signed-url</c>) can never serve a signed-PUT object.
    /// This streams the bytes from cdn's own fetch route instead.
    ///
    /// <para><b>ADR-005 Layer 2 / auth.</b> Covered by the CLASS-level
    /// <c>[RequireCapability(Capabilities.CdnBroker)]</c> — participant
    /// {client, jeeber} (CapabilityRolePolicy) — exactly like
    /// <see cref="GetAsset"/> and <see cref="GetSignedUrl"/>, which likewise carry
    /// no per-action marker. The class attribute is Inherited and lands on every
    /// action's endpoint metadata, so <c>CapabilityCoverageGuard</c> sees this
    /// action as covered without a new attribute. This route is deliberately NOT
    /// <c>[PublicEndpoint]</c>: unlike the signed PUT (whose HMAC query IS the
    /// authz) a plain fetch carries no signature, so the bearer / edge identity is
    /// the only gate — <see cref="UserIdentity.TryGetUserId"/> below is the 401.</para>
    ///
    /// <para><b>GR-1 dumb pipe.</b> No business logic, no durable state, body
    /// STREAMED (<c>ResponseHeadersRead</c> + <c>File(stream)</c>) — an image is
    /// never buffered whole in gateway memory. Mirrors
    /// <see cref="CdnUploadProxyController"/>.</para>
    ///
    /// <para><b>Route precedence.</b> "content" is a literal segment and beats the
    /// <c>{assetId}</c> parameter, so a 3+-segment path lands here; a tail-less
    /// <c>GET /api/cdn/assets/content</c> also binds this catch-all with an empty
    /// objectPath and fails the guard below with 400 — harmless either way, and
    /// never a 500.</para>
    /// </summary>
    [HttpGet("content/{**objectPath}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetAssetContent(string objectPath, CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized)) return unauthorized;

        // SSRF / traversal fail-closed on the RAW route value — same guard shape as
        // CdnUploadProxyController.PutSigned (CWE-22/918). The ref is appended to a
        // FIXED cdn prefix; a traversal segment could otherwise redirect the read at
        // a different cdn endpoint. Kestrel SINGLE-decodes the route value, so a
        // double-encoded "%252e%252e" surfaces here as the literal "%2e%2e" (still
        // carrying '%'), which a plain ".." check misses; a '\' can normalise to '/'
        // inside System.Uri. cdn mints slug-only refs ("{slot}/{guid:N}{ext}"), so
        // '%' and '\' are never legitimate here.
        if (string.IsNullOrWhiteSpace(objectPath)
            || objectPath.Contains("..", StringComparison.Ordinal)
            || objectPath.Contains('%')
            || objectPath.Contains('\\'))
        {
            return Problem(
                title: "Invalid asset reference",
                detail: "The asset object reference is missing or malformed.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!_flags.CurrentValue.Cdn) return UpstreamDisabled();

        var client = _httpClientFactory.CreateClient(CdnUploadUrlResolver.ProxyHttpClientName);
        if (client.BaseAddress is null)
        {
            // cdn base unconfigured (placeholder host) — never dial an unroutable host.
            _logger.LogError("CDN read proxy: cdn-service base address is not configured.");
            return Problem(
                title: "CDN upstream not configured",
                detail: "The asset store fetch endpoint is not configured in this environment.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        // cdn's fetch route takes {fileName} as ONE segment, so the nested objectRef
        // must be percent-encoded into a single segment (verified live on MSI: a raw
        // slash 404s).
        var upstreamUri = new Uri(
            client.BaseAddress,
            CdnUploadUrlResolver.CdnFetchPathPrefix + Uri.EscapeDataString(objectPath));

        // Fail-closed on the CANONICALIZED sink: it must stay on cdn's own
        // scheme/host/port AND under the fixed fetch prefix. Validate the sink, not
        // just the raw route string.
        if (!CdnUploadUrlResolver.IsOnFetchPrefix(upstreamUri, client.BaseAddress))
        {
            _logger.LogWarning(
                "CDN read proxy: rejected off-prefix upstream target (resolved path {ResolvedPath}).",
                upstreamUri.AbsolutePath);
            return Problem(
                title: "Invalid asset reference",
                detail: "The asset object reference resolves outside the asset store.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.GetAsync(upstreamUri, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "CDN read proxy: fetch from cdn-service failed.");
            return Problem(
                title: "CDN upstream unavailable",
                detail: "The asset store could not be reached to serve the requested object.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        // Dispose AFTER the response body has been written (never `using` — the
        // FileStreamResult reads the stream after this method returns).
        HttpContext.Response.RegisterForDispose(upstream);

        if (upstream.StatusCode == HttpStatusCode.NotFound)
        {
            return Problem(
                title: "Asset not found",
                detail: "The asset does not exist or has aged out of the retention window.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // NOTE: cdn's fetch documents 206 as a success status (range-capable).
        // IsSuccessStatusCode covers 200 AND 206 and we relay bytes either way —
        // do NOT compare against HttpStatusCode.OK.
        if (!upstream.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "CDN read proxy: cdn-service returned {Status} for an asset fetch.",
                (int)upstream.StatusCode);
            return Problem(
                title: "Asset fetch failed",
                detail: "The asset store could not serve the requested object.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var stream = await upstream.Content.ReadAsStreamAsync(ct);
        return File(stream, contentType);
    }

    private IActionResult ReplayTicket(IdempotencyOutcome stored, string requestHash, string slot)
    {
        if (!ReplayMatches(stored, requestHash, out var response))
            return IdempotencyConflict(slot);
        Response.Headers["Idempotency-Replayed"] = "true";
        RecordCdnOutcome("replayed", slot);
        return Ok(response);
    }

    private IActionResult IdempotencyConflict(string slot)
    {
        RecordCdnOutcome("collision", slot);
        return Problem(
            title: "Idempotency key conflict",
            detail: "This Idempotency-Key was already used for a different upload-ticket request.",
            statusCode: StatusCodes.Status409Conflict);
    }

    private static bool ReservationMatches(IdempotencyOutcome stored, string requestHash)
    {
        try
        {
            var reservation = JsonSerializer.Deserialize<CdnUploadReservation>(
                stored.ResponseBodyJson, CdnIdempotencyJson);
            return reservation is not null
                && string.Equals(reservation.RequestHash, requestHash, StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    private static bool ReplayMatches(
        IdempotencyOutcome stored, string requestHash, out CdnUploadTicketResponse response)
    {
        response = null!;
        try
        {
            var replay = JsonSerializer.Deserialize<CdnUploadReplay>(
                stored.ResponseBodyJson, CdnIdempotencyJson);
            if (replay is null || replay.Response is null
                || !string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal))
                return false;
            response = replay.Response;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void RecordCdnOutcome(string outcome, string slot) =>
        BusinessOutcomeTelemetry.CdnUploadTicketOperations.Add(
            1, new("outcome", outcome), new("slot", slot.ToLowerInvariant()));

    private sealed record CdnUploadReservation(string RequestHash);
    private sealed record CdnUploadReplay(string RequestHash, CdnUploadTicketResponse Response);

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

    /// <summary>
    /// JEBV4-259 — the HTTP method the client must use for the signed upload
    /// ("PUT"). Previously dropped; the client had to assume the verb.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("method")]
    public required string Method { get; init; }

    /// <summary>
    /// JEBV4-259 — headers the client must send on the signed upload PUT (always
    /// includes <c>Content-Type</c>). Previously dropped; the client fell back to
    /// its shared-Dio JSON default and corrupted the binary body. The mobile fix
    /// applies these verbatim on a dedicated, interceptor-free Dio.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("required_headers")]
    public required IReadOnlyDictionary<string, string> RequiredHeaders { get; init; }
}
