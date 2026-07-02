using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
using JeebGateway.Notifications;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers.V1;

/// <summary>
/// JEB-1431: V1 BFF slice for the requests/offers lifecycle.
///
/// <list type="bullet">
///   <item><c>POST /v1/requests</c> — create a delivery request and kick off
///     tier-aware matching. Delegates BR-9 enforcement to the store and
///     optionally seeds the canonical delivery-service row + fires matching
///     when the <c>FeatureFlags:UseUpstream:Delivery</c> and <c>Matching</c>
///     flags are on. Matching kick-off is best-effort (a transient matching
///     failure never blocks the 201 response).</item>
///   <item><c>GET /v1/requests/{id}</c> — single-request read for the owning
///     client. Checks ownership (clientId == actor) and returns the full DTO
///     including conversation id.</item>
///   <item><c>GET /v1/requests/{id}/offers</c> — lists all offers currently
///     attached to a request, newest first. Ownership-gated (same client check
///     as the single-read). Delegates to <see cref="IPendingOffersStore"/> so
///     both the in-memory and upstream offer-service paths are covered.</item>
/// </list>
///
/// Coexists with the legacy (Obsolete) <see cref="JeebGateway.Controllers.RequestsController"/>
/// — that surface is frozen per the GATEWAY-REMEDIATION-PLAN; all new work lands here.
/// </summary>
[ApiController]
public sealed class JeebRequestsController : ControllerBase
{
    /// <summary>BR-9: per-client maximum of concurrent active requests.</summary>
    private const int ActiveRequestsLimit = 3;

    private const string DefaultTenantId = "default";

    private readonly IRequestsStore _requests;
    private readonly IPendingOffersStore _offers;
    private readonly IDeliveryServiceClient _delivery;
    private readonly UpstreamFeatureFlags _flags;
    private readonly string _tenantId;
    private readonly INewRequestPushNotifier _newRequestPush;
    private readonly ILogger<JeebRequestsController> _logger;

    public JeebRequestsController(
        IRequestsStore requests,
        IPendingOffersStore offers,
        IDeliveryServiceClient delivery,
        IOptions<UpstreamFeatureFlags> flags,
        IConfiguration config,
        INewRequestPushNotifier newRequestPush,
        ILogger<JeebRequestsController> logger)
    {
        _requests = requests;
        _offers = offers;
        _delivery = delivery;
        _flags = flags.Value;
        _tenantId = config["Services:Delivery:TenantId"] ?? DefaultTenantId;
        _newRequestPush = newRequestPush;
        _logger = logger;
    }

    /// <summary>
    /// POST /v1/requests — create a delivery request and kick off matching.
    ///
    /// Orchestration order (load-bearing):
    /// 1. Resolve caller identity (401 on failure).
    /// 2. Validate description (required field — 400 on missing).
    /// 3. Atomically enforce BR-9 cap + insert request row in the store
    ///    (409 on cap exceeded — the check and insert are locked so they
    ///    cannot race).
    /// 4. BEST-EFFORT: if <c>FeatureFlags:UseUpstream:Delivery</c> is on and
    ///    the row has a tier, seed the canonical delivery-service deliveries
    ///    row (<c>POST /api/v1/deliveries</c> — idempotent) so the matching run
    ///    resolves the request id instead of 404-ing (ErrUnknownRequest).
    /// 5. BEST-EFFORT: if <c>FeatureFlags:UseUpstream:Matching</c> is also on,
    ///    fire <c>POST /api/v1/matching/run</c> with the minted request id. A
    ///    transient matching error is logged and swallowed — the 201 response
    ///    is never blocked by a matching hiccup. Clients poll
    ///    <c>GET /v1/requests/{id}</c> to observe the status transition.
    ///
    /// Returns 201 Created with the full <see cref="DeliveryRequestDto"/> and a
    /// <c>Location</c> header pointing at <c>GET /v1/requests/{id}</c>.
    /// </summary>
    [HttpPost("v1/requests")]
    // Explicit [Consumes] disambiguates this JSON action from the existing
    // RequestVoiceController's multipart/form-data [FromForm] path on the same
    // route. ASP.NET Core selects the more-specific [Consumes("application/json")]
    // action for JSON requests and falls back to the voice controller for multipart.
    [Consumes("application/json")]
    [RequireCapability(Capabilities.RequestCreate)]
    [RequireActiveUser]
    [ProducesResponseType(typeof(DeliveryRequestDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateRequestBody? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var clientId, out var problem))
            return problem;

        if (body is null || string.IsNullOrWhiteSpace(body.Description))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "description is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        DeliveryRequest created;
        try
        {
            created = await _requests.TryCreateWithLimitAsync(
                new CreateRequestInput
                {
                    ClientId = clientId,
                    Description = body.Description,
                    Transcription = body.Transcription,
                    AudioUrl = body.AudioUrl,
                    Photos = body.Photos ?? [],
                    TierId = body.TierId,
                    PickupLocation = body.PickupLocation,
                    DropoffLocation = body.DropoffLocation,
                    PickupAddress = body.PickupAddress,
                    DropoffAddress = body.DropoffAddress,
                    RecipientPhone = body.RecipientPhone,
                    ScheduledAt = body.ScheduledAt,
                },
                ActiveRequestsLimit,
                ct);
        }
        catch (TooManyActiveRequestsException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Maximum 3 active requests. Complete or cancel an existing request.",
                Detail = $"Client has {ex.ActiveCount} active requests (limit {ex.Limit}).",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/too-many-active-requests"
            });
        }

        // Best-effort: seed delivery row in delivery-service so the matching run
        // can resolve this request id. Only fires when the row has a tier and the
        // Delivery upstream is live.
        if (_flags.Delivery && !string.IsNullOrWhiteSpace(created.TierId))
        {
            try
            {
                await _delivery.CreateDeliveryRowAsync(new CreateDeliveryRowUpstream
                {
                    Id = created.Id,
                    TenantId = _tenantId,
                    ClientId = clientId,
                    TierId = created.TierId!,
                    PickupLat = created.PickupLocation?.Lat ?? 0.0,
                    PickupLng = created.PickupLocation?.Lng ?? 0.0,
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "JEB-1431: delivery row seed failed for request {RequestId} — matching run may 404; continuing",
                    created.Id);
            }

            // Best-effort matching kick-off (fire-and-forget).
            if (_flags.Matching)
            {
                try
                {
                    await _delivery.RunMatchingAsync(new DeliveryMatchingRunRequest
                    {
                        RequestId = created.Id,
                        TenantId = _tenantId,
                    }, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "JEB-1431: matching kick-off failed for request {RequestId} — client polls GET /v1/requests/{RequestId}; continuing",
                        created.Id, created.Id);
                }
            }
        }

        // BUILD-NEWREQ-PUSH — best-effort "finding jeebers" broadcast. Belt-and-braces
        // try/catch (the notifier is already degrade-don't-fail internally, but the hook
        // must NEVER flip the create 201 even on a DI/synchronous fault). This single hook
        // covers BOTH mobile create paths: the standard compose screen and the chat-compose
        // screen both POST /v1/requests as application/json and land in this action.
        try
        {
            await _newRequestPush.NotifyNewRequestAsync(
                created.Id, created.TierId, created.Description, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "New-request push hook for request {RequestId} failed; create stays 201.",
                created.Id);
        }

        return CreatedAtAction(nameof(Get), new { id = created.Id }, ToRequestDto(created));
    }

    /// <summary>
    /// GET /v1/requests/{id} — single-request read for the owning client.
    /// Returns the full <see cref="DeliveryRequestDto"/> including conversation id
    /// and GPS-tracking state. 404 when the id is unknown; 403 when the caller
    /// is not the request owner.
    /// </summary>
    // Order = 1 deprioritises this V1 read so it yields to RequestVoiceController.GetById
    // (default Order 0) which ALSO maps GET /v1/requests/{id}. The two actions cannot be
    // disambiguated by content-type (GET has no request body), so without an explicit
    // precedence ASP.NET Core throws AmbiguousMatchException → 500. The voice read-back
    // (S04 H2, a locked cross-surface contract) owns this exact route and echoes the
    // transcript/confidence/language fields that DeliveryRequestDto does not carry; this
    // V1 action remains the registered fallback. See JEB-1431 / #177 disambiguation note.
    [HttpGet("v1/requests/{id}", Order = 1)]
    [RequireCapability(Capabilities.RequestReadOwn)]
    [ProducesResponseType(typeof(DeliveryRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var clientId, out var problem))
            return problem;

        var req = await _requests.GetAsync(id, ct);
        if (req is null)
            return NotFound();

        if (!string.Equals(req.ClientId, clientId, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Access denied — you do not own this request.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/request-not-owned"
            });
        }

        return Ok(ToRequestDto(req));
    }

    /// <summary>
    /// GET /v1/requests/{id}/offers — lists all offers attached to a request,
    /// newest-first. Ownership-gated: only the request's owning client may read
    /// the offer list. Returns an empty array (never 404) when the request exists
    /// but has no offers yet — the client polls this endpoint during the auction
    /// window.
    /// </summary>
    [HttpGet("v1/requests/{id}/offers")]
    [RequireCapability(Capabilities.RequestReadOwn)]
    [ProducesResponseType(typeof(IReadOnlyList<OfferDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListOffers(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var clientId, out var problem))
            return problem;

        var req = await _requests.GetAsync(id, ct);
        if (req is null)
            return NotFound();

        if (!string.Equals(req.ClientId, clientId, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Access denied — you do not own this request.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/request-not-owned"
            });
        }

        var offers = await _offers.ListForRequestAsync(id, ct);
        var dtos = offers.Select(o => new OfferDto
        {
            Id = o.Id,
            RequestId = o.RequestId,
            JeeberId = o.JeeberId,
            Status = o.Status,
            Fee = o.Fee,
            EtaMinutes = o.EtaMinutes,
            Note = o.Note,
            CreatedAt = o.CreatedAt,
            UpdatedAt = o.UpdatedAt,
        }).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// iter5 BATCHED-FIX B12 — flat offers-list alias. The installed APK's
    /// bid-review (<c>DioOffersRepository</c>) calls <c>GET /v1/offers?requestId=&lt;id&gt;</c>,
    /// but the gateway only exposed the nested <c>GET /v1/requests/{id}/offers</c>
    /// (the flat route 404'd EMPTY). This alias reads the <c>requestId</c> query
    /// param and delegates to the SAME ownership-gated listing logic, returning the
    /// <c>{ items: [...] }</c> envelope the mobile repo parses. A missing
    /// <c>requestId</c> is a 400 (the flat surface is request-scoped). Ownership /
    /// 404-unknown / 403-not-owner are identical to the nested route.
    /// </summary>
    [HttpGet("v1/offers")]
    [RequireCapability(Capabilities.RequestReadOwn)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListOffersFlat([FromQuery] string? requestId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var clientId, out var problem))
            return problem;

        if (string.IsNullOrWhiteSpace(requestId))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Query parameter 'requestId' is required.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/request-id-required"
            });
        }

        var req = await _requests.GetAsync(requestId, ct);
        if (req is null)
            return NotFound();

        if (!string.Equals(req.ClientId, clientId, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Access denied — you do not own this request.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/request-not-owned"
            });
        }

        var offers = await _offers.ListForRequestAsync(requestId, ct);
        var dtos = offers.Select(o => new OfferDto
        {
            Id = o.Id,
            RequestId = o.RequestId,
            JeeberId = o.JeeberId,
            Status = o.Status,
            Fee = o.Fee,
            EtaMinutes = o.EtaMinutes,
            Note = o.Note,
            CreatedAt = o.CreatedAt,
            UpdatedAt = o.UpdatedAt,
        }).ToList();

        return Ok(new { items = dtos });
    }

    private static DeliveryRequestDto ToRequestDto(DeliveryRequest r) => new()
    {
        Id = r.Id,
        ClientId = r.ClientId,
        Status = r.Status,
        Description = r.Description,
        Transcription = r.Transcription,
        AudioUrl = r.AudioUrl,
        Photos = r.Photos,
        TierId = r.TierId,
        PickupLocation = r.PickupLocation,
        DropoffLocation = r.DropoffLocation,
        PickupAddress = r.PickupAddress,
        DropoffAddress = r.DropoffAddress,
        RecipientPhone = r.RecipientPhone,
        CreatedAt = r.CreatedAt,
        ScheduledAt = r.ScheduledAt,
        JeeberId = r.JeeberId,
        AcceptedAt = r.AcceptedAt,
        ConversationId = r.ConversationId,
        GpsTrackingActive = r.GpsTrackingActive,
        OtpAttemptCount = r.OtpAttemptCount,
        OtpLockedAt = r.OtpLockedAt,
        ClientUnreachableAt = r.ClientUnreachableAt,
        OtpEscalationId = r.OtpEscalationId,
    };
}
