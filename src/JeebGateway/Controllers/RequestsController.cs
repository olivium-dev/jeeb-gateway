using JeebGateway.Auth.Capabilities;
using JeebGateway.ProhibitedItems;
using JeebGateway.ProhibitedItems.Scanner;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Client-facing delivery-request endpoints. Covers creation (immediate
/// and scheduled — T-backend-046) and client-initiated cancellation. Full
/// lifecycle (status transitions during matching/pickup/delivery) stays
/// with the downstream delivery-service.
///
/// BR-9 active-request concurrency is currently unlimited. The store path still
/// accepts a limit so the historical 409 plumbing remains available if another
/// conflict raises it, but gateway create routes pass <see cref="int.MaxValue"/>.
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("requests")]
public class RequestsController : ControllerBase
{
    /// <summary>Retired BR-9 cap: active requests are unlimited.</summary>
    public const int ActiveRequestsLimit = int.MaxValue;

    /// <summary>
    /// Exact wording from the ticket acceptance criteria — clients render
    /// this verbatim in the mobile error banner.
    /// </summary>
    internal const string LimitExceededMessage =
        "Active request concurrency is unlimited.";

    // JEBV4-65: MaxPhotos, the URL-shape regex, the legal-initial-status set, and
    // the description/tier/status/url/photo validation envelopes now live in the
    // shared RequestCreateValidation (single source of truth across the three
    // create surfaces; the JEBV4-62 tier-not-found coupling point).

    private readonly IRequestsStore _store;
    private readonly ITiersStore _tiers;
    private readonly TimeProvider _clock;
    private readonly ScheduledDeliveryOptions _scheduledOptions;
    private readonly IProhibitedItemScanner _scanner;
    private readonly IProhibitedItemsStore _prohibited;
    private readonly CreateModerationOptions _moderation;
    private readonly ILogger<RequestsController> _logger;

    public RequestsController(
        IRequestsStore store,
        ITiersStore tiers,
        TimeProvider clock,
        IOptions<ScheduledDeliveryOptions> scheduledOptions,
        IProhibitedItemScanner scanner,
        IProhibitedItemsStore prohibited,
        IOptions<CreateModerationOptions> moderation,
        ILogger<RequestsController> logger)
    {
        _store = store;
        _tiers = tiers;
        _clock = clock;
        _scheduledOptions = scheduledOptions.Value;
        _scanner = scanner;
        _prohibited = prohibited;
        _moderation = moderation.Value;
        _logger = logger;
    }

    /// <summary>
    /// Create a delivery request. Accepts BOTH <c>application/json</c> (the
    /// existing typed body) AND <c>multipart/form-data</c> (A2, the voice-first
    /// create path) on the SAME route — the action reads the request shape from
    /// the Content-Type and binds either path into the canonical
    /// <see cref="CreateRequestBody"/>, then runs one create pipeline. Kept as a
    /// SINGLE action (not two <c>[Consumes]</c>-split actions) so the OpenAPI 3.0
    /// document — consumed by NSwag BFF client generation and the Swagger UI —
    /// has no conflicting method/path combination.
    /// </summary>
    [HttpPost]
    // ADR-005 L2 §C client-only: replaces [RequireRole(Roles.Client)]. BR-9 cap stays STATE (in-action).
    [Consumes("application/json", "multipart/form-data")]
    [RequireCapability(Capabilities.RequestCreate)]
    [RequireActiveUser]
    [ProducesResponseType(typeof(DeliveryRequestDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        // A2: bind from multipart form when the submit is multipart/form-data;
        // otherwise read the typed JSON body (existing behaviour). Both converge
        // on the canonical CreateRequestBody and the single create pipeline.
        CreateRequestBody? body;
        if (Request.HasFormContentType)
        {
            body = await BindFromFormAsync();
        }
        else
        {
            try
            {
                body = await Request.ReadFromJsonAsync<CreateRequestBody>(ct);
            }
            catch (System.Text.Json.JsonException)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Request body is not valid JSON.",
                    Status = StatusCodes.Status400BadRequest
                });
            }
        }

        return await CreateCore(body, ct);
    }

    /// <summary>
    /// Create pipeline shared by the JSON and multipart entry points. Order is
    /// load-bearing: identity → body/description → N5 initial-transition guard
    /// (422) → scheduled validation → location/tier/url/photo validation (400) →
    /// moderation gate (N1 block 409 / A1.1 ack 409) → BR-9 atomic store insert
    /// (N3 409). A rejection at any gate persists no row.
    /// </summary>
    private async Task<IActionResult> CreateCore(CreateRequestBody? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var clientId, out var problem)) return problem;

        if (body is null || string.IsNullOrWhiteSpace(body.Description))
        {
            // JEBV4-65: shared description-required envelope (single source of truth).
            return BadRequest(RequestCreateValidation.DescriptionRequiredProblem());
        }

        // JEB-45 (S05 N5): create-time initial-transition guard. The status is
        // server-authoritative — a client may not seed the row into an arbitrary
        // lifecycle state. A supplied status that is not a legal INITIAL state
        // (pending / scheduled) is an illegal initial transition: 422
        // transition_not_allowed, no row persisted. A legal value (or none) is a
        // no-op and falls through to the normal server-assigned status.
        // JEBV4-65: shared status-legality validation (single source of truth).
        var statusProblem = RequestCreateValidation.ValidateInitialStatus(body.Status);
        if (statusProblem is not null) return UnprocessableEntity(statusProblem);

        if (body.ScheduledAt is { } scheduledAt)
        {
            // Must be strictly in the future relative to the gateway clock.
            // Rejecting at/in-the-past prevents a degenerate row that would
            // race the activator (and trip up the BR-9 cap with a row that
            // can never be acted on).
            var now = _clock.GetUtcNow();
            if (scheduledAt <= now)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "scheduledAt must be in the future.",
                    Detail = $"scheduledAt={scheduledAt:o} now={now:o}",
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/scheduled-at-not-future"
                });
            }
            // The activator opens the matching window at
            // ScheduledAt - MatchingBuffer. Demanding ScheduledAt > now +
            // MatchingBuffer would over-constrain the API for short-notice
            // scheduling, so we accept anything strictly future and the
            // activator will fire immediately on the next sweep if the
            // window is already open.
        }

        // T-backend-007: structured pickup/dropoff are required. Free-text
        // PickupAddress / DropoffAddress remain optional human-readable
        // labels — the matching engine joins on the GEOGRAPHY columns.
        if (body.PickupLocation is null || body.DropoffLocation is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "pickupLocation and dropoffLocation are required.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/location-required"
            });
        }
        if (!body.PickupLocation.IsValid() || !body.DropoffLocation.IsValid())
        {
            return BadRequest(new ProblemDetails
            {
                Title = "pickupLocation / dropoffLocation must be valid WGS84 coordinates.",
                Detail = "lat must be in [-90, 90]; lng must be in [-180, 180]; NaN is not allowed.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/location-invalid"
            });
        }

        // T-backend-007 acceptance criterion: validate tier exists.
        if (string.IsNullOrWhiteSpace(body.TierId))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "tierId is required.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/tier-required"
            });
        }
        // JEBV4-65: shared tier-exists validation (single source of truth; the
        // JEBV4-62 tier-not-found status coupling point). fieldLabel "tierId"
        // preserves this surface's exact Title/Detail wording.
        var tierProblem = await RequestCreateValidation.ValidateTierExistsAsync(_tiers, body.TierId, "tierId", ct);
        if (tierProblem is not null) return NotFound(tierProblem);

        // JEBV4-65: shared audio/photo URL-shape + photo-count validation (single
        // source of truth). Preserves the exact order and envelopes.
        var photos = body.Photos ?? new List<string>();
        var urlProblem = RequestCreateValidation.ValidateUrlAndPhotos(body.AudioUrl, photos);
        if (urlProblem is not null) return BadRequest(urlProblem);

        // JEB-63 (S05 N1 / A1.1): gateway-owned create-time prohibited-items
        // moderation gate. Flag-gated (default OFF) so today's green path is
        // unchanged until the lexicon is seeded and the flip is owner-approved.
        // Runs AFTER all field validation (so 400 ordering is preserved) and
        // BEFORE the store insert (so a rejected create persists no row).
        var moderation = await EvaluateModerationAsync(clientId, body.Description, ct);
        if (moderation is not null) return moderation;

        var input = new CreateRequestInput
        {
            ClientId = clientId,
            Description = body.Description.Trim(),
            Transcription = string.IsNullOrWhiteSpace(body.Transcription) ? null : body.Transcription.Trim(),
            AudioUrl = body.AudioUrl,
            Photos = photos.ToArray(),
            TierId = body.TierId,
            PickupLocation = body.PickupLocation,
            DropoffLocation = body.DropoffLocation,
            PickupAddress = body.PickupAddress,
            DropoffAddress = body.DropoffAddress,
            // T-BE-019 (JEB-55): thread the recipient phone through to the
            // store so DeliveryRequest.RecipientPhone is populated and the
            // at-door handover OTP can be dispatched (else the OTP trigger
            // returns 400 recipient-phone-missing for every delivery).
            RecipientPhone = body.RecipientPhone,
            ScheduledAt = body.ScheduledAt
        };

        DeliveryRequest created;
        try
        {
            created = await _store.TryCreateWithLimitAsync(input, ActiveRequestsLimit, ct);
        }
        catch (TooManyActiveRequestsException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = LimitExceededMessage,
                Detail = $"Client has {ex.ActiveCount} active requests (limit {ex.Limit}).",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/too-many-active-requests"
            });
        }

        return Created($"/requests/{created.Id}", ToDto(created));
    }

    /// <summary>
    /// A2 (S05): binds the multipart/form-data create body into the canonical
    /// <see cref="CreateRequestBody"/>. Pickup/dropoff are flat lat/lng form
    /// fields (form data is not nested). A voice-payload file may accompany the
    /// fields and is accepted (and ignored here — STT lives in S04
    /// POST /transcribe), so a voice-first upload binds cleanly. When no
    /// <c>description</c> is supplied the raw <c>transcription</c> text is used as
    /// the description, so a voice-first submit still satisfies the
    /// "description required" contract.
    /// </summary>
    private async Task<CreateRequestBody> BindFromFormAsync()
    {
        var form = await Request.ReadFormAsync();

        GeoPoint? Point(string latKey, string lngKey) =>
            double.TryParse(form[latKey], System.Globalization.CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(form[lngKey], System.Globalization.CultureInfo.InvariantCulture, out var lng)
                ? new GeoPoint { Lat = lat, Lng = lng }
                : null;

        string? Field(string key) => form.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.ToString()
            : null;

        var description = Field("description");
        var transcription = Field("transcription");

        return new CreateRequestBody
        {
            // Voice-first: fall back to the transcription text when no edited
            // description was supplied so the "description required" contract holds.
            Description = string.IsNullOrWhiteSpace(description) ? transcription : description,
            Transcription = transcription,
            TierId = Field("tierId"),
            PickupLocation = Point("pickupLat", "pickupLng"),
            DropoffLocation = Point("dropoffLat", "dropoffLng"),
            PickupAddress = Field("pickupAddress"),
            DropoffAddress = Field("dropoffAddress"),
            // T-BE-019 (JEB-55): multipart parity with the JSON create body so
            // a voice-first form submit also carries the handover OTP phone.
            RecipientPhone = Field("recipientPhone"),
            Status = Field("status")
        };
    }

    /// <summary>
    /// Owner-scoped list of the calling Client's delivery requests
    /// (active and terminal), oldest first. Additive read counterpart to
    /// the existing <see cref="Create"/> / <see cref="Cancel"/> writes —
    /// reads only the existing in-memory store via <see cref="IRequestsStore"/>;
    /// the BFF migration target swaps the store for the NSwag-generated
    /// delivery-service client without touching this controller.
    ///
    /// Returns 200 with a (possibly empty) array of the caller's own
    /// requests. A Client only ever sees their own rows — the result is
    /// filtered by the resolved caller id, so there is no cross-client
    /// data exposure and no id is required in the path.
    /// </summary>
    [HttpGet]
    // ADR-005 L2 §C client-only (STATE: ownership — caller-scoped list stays in-action).
    [RequireCapability(Capabilities.RequestReadOwn)]
    [RequireActiveUser]
    [ProducesResponseType(typeof(IReadOnlyList<DeliveryRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var clientId, out var problem)) return problem;

        var rows = await _store.ListForClientAsync(clientId, ct);
        var dtos = rows.Select(ToDto).ToArray();
        return Ok(dtos);
    }

    /// <summary>
    /// Single-request read-by-id for the calling Client. Additive read
    /// counterpart to the existing <see cref="Cancel"/> route on the same
    /// <c>{requestId}</c> template — reuses the store's
    /// <see cref="IRequestsStore.GetAsync"/> and the same ownership guard
    /// as cancellation.
    ///
    /// Returns 200 with the request when it exists and belongs to the
    /// caller, 404 when the id is unknown, and 404 (NOT 403) when the row
    /// belongs to a different Client — the not-owner case is masked as
    /// not-found so a Client cannot probe for the existence of another
    /// Client's request ids.
    /// </summary>
    [HttpGet("{requestId}")]
    // ADR-005 L2 §C client-only (STATE: ownership-masking 404 stays in-action).
    [RequireCapability(Capabilities.RequestReadOwn)]
    [RequireActiveUser]
    [ProducesResponseType(typeof(DeliveryRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(string requestId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var clientId, out var problem)) return problem;

        var existing = await _store.GetAsync(requestId, ct);
        if (existing is null) return NotFound();

        // Ownership masking: a row owned by a different Client is reported
        // as 404 rather than 403 so request-id existence cannot be probed.
        if (!string.Equals(existing.ClientId, clientId, StringComparison.Ordinal))
        {
            return NotFound();
        }

        return Ok(ToDto(existing));
    }

    /// <summary>
    /// Client-initiated cancellation. Shared rules for immediate and
    /// scheduled deliveries (T-backend-046 acceptance criterion):
    ///   * Only the owning Client may cancel.
    ///   * The request must not already be in a terminal state.
    ///   * Cancelling frees a BR-9 slot.
    ///
    /// Returns 204 on success, 404 when the id is unknown, 403 when a
    /// different Client tries to cancel, 409 when the request is already
    /// terminal (delivered/rated/cancelled/expired/disputed).
    /// </summary>
    [HttpDelete("{requestId}")]
    // ADR-005 L2 §C client-only (STATE: ownership + terminal-state 403/409 stay in-action).
    [RequireCapability(Capabilities.RequestCancelOwn)]
    [RequireActiveUser]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(string requestId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var clientId, out var problem)) return problem;

        var existing = await _store.GetAsync(requestId, ct);
        if (existing is null) return NotFound();

        if (!string.Equals(existing.ClientId, clientId, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Only the owning Client may cancel this request.",
                Status = StatusCodes.Status403Forbidden
            });
        }

        if (RequestStatus.IsTerminal(existing.Status))
        {
            return Conflict(new ProblemDetails
            {
                Title = $"Request is already terminal ({existing.Status}); cannot cancel.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/request-terminal"
            });
        }

        // SetStatusAsync refuses transitions out of terminal states, so
        // this is race-safe — a sweeper or activator that just moved the
        // row to expired/delivered will lose the cancel attempt cleanly.
        var ok = await _store.SetStatusAsync(requestId, RequestStatus.Cancelled, ct);
        if (!ok)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Request is no longer cancellable (terminal state reached).",
                Status = StatusCodes.Status409Conflict
            });
        }

        return NoContent();
    }

    /// <summary>
    /// JEB-63 (S05 N1 / A1.1) create-time moderation gate. Pure orchestration:
    /// composes the gateway-owned <see cref="IProhibitedItemScanner"/> over the
    /// gateway-owned lexicon (N11 keeps the lexicon out of ban-service) and the
    /// per-user ack ledger. Returns:
    ///   <list type="bullet">
    ///     <item>null — allowed (gate off, no review-grade match, or warn already
    ///       acknowledged): the create proceeds.</item>
    ///     <item>503 <c>moderation_unavailable</c> — lexicon empty or unloadable
    ///       (FT-04 fail-closed: an empty lexicon means the gate cannot screen,
    ///       so the safe action is to block the create until the lexicon is
    ///       restored). JEB-1504 claimed 503 was already returned — this is the
    ///       fix that makes it true.</item>
    ///     <item>409 <c>prohibited_item_blocked</c> — a block-severity match;
    ///       an ack does NOT override it (AC7).</item>
    ///     <item>409 <c>prohibited_item_requires_ack</c> — a warn-severity match
    ///       and the caller has not acknowledged the current lexicon version.</item>
    ///   </list>
    /// No-op (returns null) when the gate flag is OFF — today's green path.
    /// </summary>
    private async Task<IActionResult?> EvaluateModerationAsync(string clientId, string description, CancellationToken ct)
    {
        if (!_moderation.Enabled) return null;

        // JEB-1504 / WS-06 fail-closed gate: if the lexicon cannot be loaded (0 active
        // items) we must NOT allow the request through silently. A 503 is surfaced so
        // callers know the moderation service is temporarily unavailable and can retry.
        // This guards against seeder failures, store outages, or a fresh startup race
        // before the lexicon is seeded. The load + fail-closed + scan + version logic is
        // shared with the standalone POST /moderation/jeeb/check endpoint via ModerationGate
        // so the two paths can never drift on what counts as "unavailable".
        ModerationGateOutcome outcome;
        try
        {
            outcome = await new ModerationGate(_prohibited, _scanner).EvaluateAsync(description, ct);
        }
        catch (LexiconUnavailableException ex)
        {
            _logger.LogError(ex, "Prohibited-items lexicon unavailable while moderation gate is enabled; failing closed with 503.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Moderation service temporarily unavailable",
                Detail = ex.Message
            });
        }

        var severity = outcome.GatingSeverity;
        if (severity is null) return null;

        var matchDtos = outcome.Scan.Matches
            .Select(m => new ModerationMatchDto(m.ItemName, m.Category, m.Severity.ToString().ToLowerInvariant()))
            .ToList();

        if (severity == ProhibitedSeverity.Block)
        {
            // AC1 / AC7: block is a hard reject; prohibited_ack must NOT override.
            return Conflict(new ProhibitedItemProblemDetails
            {
                Title = "This request contains a prohibited item and cannot be created.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/prohibited-item-blocked",
                Reason = "prohibited_item_blocked",
                Matches = matchDtos
            });
        }

        // Warn severity: allowed only once the caller has acknowledged the
        // CURRENT lexicon version. Re-using the same version semantics as
        // GET /prohibited-items + POST /prohibited-items/acknowledge so the ack
        // the mobile ack-dialog records is the one that clears this gate.
        // (version computed by ModerationGate above — no second round-trip needed.)
        var currentVersion = outcome.Version;
        var ack = await _prohibited.GetAcknowledgmentAsync(clientId, ct);
        var acknowledged = ack is not null && string.Equals(ack.Version, currentVersion, StringComparison.Ordinal);

        if (acknowledged) return null;

        return Conflict(new ProhibitedItemProblemDetails
        {
            Title = "This request contains an item that requires acknowledgment before it can be created.",
            Status = StatusCodes.Status409Conflict,
            Type = "https://jeeb.dev/errors/prohibited-item-requires-ack",
            Reason = "prohibited_item_requires_ack",
            Matches = matchDtos
        });
    }

    private static DeliveryRequestDto ToDto(DeliveryRequest r) => new()
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
        ConversationId = r.ConversationId
    };
}

/// <summary>
/// RFC7807 ProblemDetails extended with the create-moderation <c>reason</c> and
/// the matched-lexicon <c>matches</c> (JEB-63 AC1/AC3). Serialised as additional
/// members alongside the standard ProblemDetails fields.
/// </summary>
public sealed class ProhibitedItemProblemDetails : ProblemDetails
{
    [System.Text.Json.Serialization.JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("matches")]
    public IReadOnlyList<ModerationMatchDto> Matches { get; set; } = Array.Empty<ModerationMatchDto>();
}

/// <summary>One matched lexicon entry surfaced on a moderation 409.</summary>
public sealed record ModerationMatchDto(string Keyword, string Category, string Severity);
