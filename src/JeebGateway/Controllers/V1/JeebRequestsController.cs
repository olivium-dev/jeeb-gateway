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
    private readonly IOfferServiceClient _offerService;
    private readonly IDeliveryServiceClient _delivery;
    private readonly ITiersStore _tiers;
    private readonly UpstreamFeatureFlags _flags;
    private readonly string _tenantId;
    private readonly INewRequestPushNotifier _newRequestPush;
    private readonly ILogger<JeebRequestsController> _logger;

    public JeebRequestsController(
        IRequestsStore requests,
        IPendingOffersStore offers,
        IOfferServiceClient offerService,
        IDeliveryServiceClient delivery,
        ITiersStore tiers,
        IOptions<UpstreamFeatureFlags> flags,
        IConfiguration config,
        INewRequestPushNotifier newRequestPush,
        ILogger<JeebRequestsController> logger)
    {
        _requests = requests;
        _offers = offers;
        _offerService = offerService;
        _delivery = delivery;
        _tiers = tiers;
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
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
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

        // feat/tier-unify-names: the V1 path previously accepted ANY tierId verbatim,
        // which let unknown/divergent codes flow through to matching + push (where the
        // display-name resolve silently failed). A SUPPLIED tierId must now resolve in
        // the unified catalog — either a catalog id (urgent/same-day/…) or a mapped
        // legacy code (flash/express/standard/on_the_way/eco). tierId stays OPTIONAL on
        // this surface (a tier-less create is still allowed; it simply skips the
        // delivery-row seed), so only a present-but-unknown id is rejected. Same
        // envelope (tier-not-found type URI) as the legacy create surfaces.
        if (!string.IsNullOrWhiteSpace(body.TierId)
            && !await _tiers.ExistsAsync(body.TierId, ct))
        {
            return NotFound(new ProblemDetails
            {
                Title = "tierId does not match any active delivery tier.",
                Detail = $"tierId={body.TierId}",
                Status = StatusCodes.Status404NotFound,
                Type = "https://jeeb.dev/errors/tier-not-found"
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
    ///
    /// JEBV4-61: this is now the SOLE action mapped to this route. It previously
    /// carried an explicit <c>Order = 1</c> to yield to
    /// <see cref="JeebGateway.Controllers.RequestVoiceController.GetVoiceById"/>,
    /// which ALSO mapped <c>GET v1/requests/{id}</c> at the default (higher-
    /// priority) <c>Order = 0</c> — so the narrow voice shape won for EVERY
    /// request, voice-created or not, and the full DTO action here was dead code:
    /// clients reading via <see cref="DeliveryRequestDto"/> (jeeberId,
    /// conversationId, pickup/dropoff locations, …) got a clean 200 with those
    /// fields silently absent. The voice read-back moved to its own
    /// non-colliding route (<c>GET v1/requests/{id}/voice</c>), so there is no
    /// same-path/same-verb ambiguity left for an integer to resolve.
    /// </summary>
    [HttpGet("v1/requests/{id}")]
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
    /// iter5 BATCHED-FIX B12 — flat offers-list alias, now dual-scoped.
    ///
    /// <para><b>Client request-scoped</b> (<c>GET /v1/offers?requestId=&lt;id&gt;</c>): the
    /// installed APK's bid-review (<c>DioOffersRepository</c>) lists all offers on a request
    /// the caller OWNS, returning the <c>{ items: [...] }</c> envelope the mobile repo parses.
    /// Ownership / 404-unknown / 403-not-owner are identical to the nested
    /// <c>GET /v1/requests/{id}/offers</c> route.</para>
    ///
    /// <para><b>Jeeber self-scoped</b> (sprint-009 Lane E, <c>GET /v1/offers?jeeberId=&lt;me&gt;</c>):
    /// the "my-offers" surface — a jeeber lists the offers THEY have submitted. Self-scoped:
    /// the <c>jeeberId</c> MUST equal the caller's own id (else 403), so one jeeber can never
    /// read another's bids. Delegates to the existing
    /// <see cref="IOfferServiceClient.ListOffersForJeeberAsync"/> (the same seam the jeeber
    /// feed's <c>myOffer</c> annotation uses). Takes precedence over <c>requestId</c> when
    /// both are supplied.</para>
    ///
    /// <para>ADR-005: this action carries the coarse <c>offer.read.own</c> capability
    /// (held by BOTH client and jeeber); the actor-appropriate STATE check — request
    /// ownership for the client branch, self-scope for the jeeber branch — is enforced in
    /// the body, exactly the CLAIM/STATE split the ADR prescribes.</para>
    /// </summary>
    [HttpGet("v1/offers")]
    [RequireCapability(Capabilities.OfferReadOwn)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListOffersFlat(
        [FromQuery] string? requestId,
        [FromQuery] string? jeeberId,
        [FromQuery] string? status,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var problem))
            return problem;

        // sprint-009 Lane E — jeeber "my-offers" branch (self-scoped). Takes precedence
        // over requestId so a jeeber querying their own bids never falls into the
        // client-ownership path.
        if (!string.IsNullOrWhiteSpace(jeeberId))
        {
            if (!string.Equals(jeeberId, callerId, StringComparison.Ordinal))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                {
                    Title = "Access denied — you can only list your own offers.",
                    Status = StatusCodes.Status403Forbidden,
                    Type = "https://jeeb.dev/errors/offers-not-self-scoped"
                });
            }

            return Ok(new { items = await ComposeMyOffersAsync(jeeberId, status, ct) });
        }

        if (string.IsNullOrWhiteSpace(requestId))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Query parameter 'requestId' or 'jeeberId' is required.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/request-id-required"
            });
        }

        var clientId = callerId;

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

    /// <summary>
    /// fix/offer-visibility (run-23 CHECK C) — <c>GET /v1/jeebers/me/offers</c>: the
    /// jeeber "my-offers" surface keyed purely off the bearer identity (the route the
    /// mobile client called first in run-23 and 404'd on). Same composition, envelope
    /// (<c>{ items: [...] }</c>) and optional <c>?status=</c> filter as the flat
    /// <c>GET /v1/offers?jeeberId=&lt;me&gt;</c> sibling; self-scoped by construction
    /// (no id parameter to mismatch).
    /// </summary>
    [HttpGet("v1/jeebers/me/offers")]
    [RequireCapability(Capabilities.OfferReadOwn)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListMyOffers([FromQuery] string? status, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var problem))
            return problem;

        return Ok(new { items = await ComposeMyOffersAsync(callerId, status, ct) });
    }

    /// <summary>
    /// fix/offer-visibility (run-23 CHECK C) — the jeeber "my-offers" composition shared by
    /// <c>GET /v1/offers?jeeberId=&lt;me&gt;</c> and <c>GET /v1/jeebers/me/offers</c>.
    ///
    /// <para><b>The defect.</b> The branch previously delegated ONLY to
    /// <see cref="IOfferServiceClient.ListOffersForJeeberAsync"/> (offer-service
    /// <c>GET /api/v1/jeebers/{id}/offers</c>) — a route the deployed offer-service does
    /// not expose, so the read 404'd upstream and degrade-don't-fail collapsed the
    /// jeeber's own list to <c>[]</c>: after the customer accepted a competing bid, the
    /// losing jeeber's offer VANISHED instead of showing its terminal state.</para>
    ///
    /// <para><b>The fix.</b> Merge two sources, deduped by offer id:
    /// (1) the direct jeeber-scoped upstream read (kept first-class so the surface starts
    /// winning automatically if offer-service ever grows that route; raw upstream statuses
    /// pass through unchanged), and (2) <see cref="IPendingOffersStore.ListForJeeberAsync"/> —
    /// on the in-memory store a full any-status scan, on the upstream store the
    /// routing-index + owner-scoped request-list composition with the HONEST terminal
    /// status mapping (lost/expired → <c>superseded</c>, self-retracted →
    /// <c>withdrawn</c>). DEFAULT INCLUDES TERMINAL offers; <paramref name="statusFilter"/>
    /// narrows the merged list (<c>pending</c> also matches the upstream live vocabulary
    /// <c>submitted</c>/<c>edited</c>). Newest-first. Customer-facing offers surfaces are
    /// untouched.</para>
    /// </summary>
    private async Task<List<OfferDto>> ComposeMyOffersAsync(
        string jeeberId, string? statusFilter, CancellationToken ct)
    {
        // (1) Direct jeeber-scoped upstream read. offer-service authorizes on
        // x-user-id == path :jeeber_id; a non-2xx / transport blip degrades to an
        // EMPTY list (never a 5xx) — the contract of ListOffersForJeeberAsync.
        var upstream = await _offerService.ListOffersForJeeberAsync(jeeberId, status: null, ct);

        var merged = new List<OfferDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var o in upstream)
        {
            if (!seen.Add(o.OfferId)) continue;
            merged.Add(new OfferDto
            {
                Id = o.OfferId,
                RequestId = o.RequestId,
                JeeberId = jeeberId,
                Status = o.Status,
                Fee = o.FeeCents / 100m,
                EtaMinutes = o.EtaMinutes,
                Note = o.Note,
                CreatedAt = o.CreatedAt ?? default,
                UpdatedAt = null,
            });
        }

        // (2) The store-backed composition (terminal rows included). Store items never
        // shadow a direct upstream row with the same id — upstream is authoritative
        // where it answers.
        var stored = await _offers.ListForJeeberAsync(jeeberId, ct);
        foreach (var o in stored)
        {
            if (!seen.Add(o.Id)) continue;
            merged.Add(new OfferDto
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
            });
        }

        IEnumerable<OfferDto> result = merged.OrderByDescending(o => o.CreatedAt);

        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            var wanted = statusFilter.Trim();
            result = result.Where(o => MatchesStatusFilter(o.Status, wanted));
        }

        return result.ToList();
    }

    /// <summary>
    /// <c>?status=</c> filter matcher for the my-offers surfaces. Case-insensitive exact
    /// match, with one vocabulary bridge: <c>pending</c> (the gateway's live state) also
    /// matches the upstream live forms <c>submitted</c> / <c>edited</c>, so a mobile
    /// "awaiting decision" query works identically against both sources.
    /// </summary>
    private static bool MatchesStatusFilter(string offerStatus, string wanted)
    {
        if (string.Equals(offerStatus, wanted, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(wanted, "pending", StringComparison.OrdinalIgnoreCase)
               && (string.Equals(offerStatus, "submitted", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(offerStatus, "edited", StringComparison.OrdinalIgnoreCase));
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
