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
/// BR-9: a Client may have at most <see cref="ActiveRequestsLimit"/>
/// active (non-delivered) requests. Active = any status strictly before
/// <c>delivered</c>: scheduled, pending, matched, accepted, picked_up,
/// heading_off. Terminal states (delivered, rated, cancelled, expired,
/// disputed) do not count against the cap. <c>scheduled</c> is included
/// so Clients cannot bypass the cap by stacking future-dated requests.
/// </summary>
[ApiController]
[Route("requests")]
public class RequestsController : ControllerBase
{
    /// <summary>BR-9: per-Client maximum of concurrent active requests.</summary>
    public const int ActiveRequestsLimit = 3;

    /// <summary>
    /// Exact wording from the ticket acceptance criteria — clients render
    /// this verbatim in the mobile error banner.
    /// </summary>
    internal const string LimitExceededMessage =
        "Maximum 3 active requests. Complete or cancel an existing request.";

    /// <summary>T-backend-007: MVP cap on attached photos per request.</summary>
    public const int MaxPhotos = 10;

    /// <summary>
    /// audio_url / photos[] entries must look like absolute URLs. Mirrors
    /// the DB CHECK <c>delivery_requests_audio_url_format</c> in 0004 —
    /// gateway-side validation here so a bad URL never reaches the store.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex UrlShape =
        new(@"^(https?|s3)://[^\s]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly IRequestsStore _store;
    private readonly ITiersStore _tiers;
    private readonly TimeProvider _clock;
    private readonly ScheduledDeliveryOptions _scheduledOptions;

    public RequestsController(
        IRequestsStore store,
        ITiersStore tiers,
        TimeProvider clock,
        IOptions<ScheduledDeliveryOptions> scheduledOptions)
    {
        _store = store;
        _tiers = tiers;
        _clock = clock;
        _scheduledOptions = scheduledOptions.Value;
    }

    [HttpPost]
    [RequireRole(Roles.Client)]
    [RequireActiveUser]
    [ProducesResponseType(typeof(DeliveryRequestDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateRequestBody? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var clientId, out var problem)) return problem;

        if (body is null || string.IsNullOrWhiteSpace(body.Description))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "description is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

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
        if (!await _tiers.ExistsAsync(body.TierId, ct))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "tierId does not match any active delivery tier.",
                Detail = $"tierId={body.TierId}",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/tier-not-found"
            });
        }

        if (!string.IsNullOrEmpty(body.AudioUrl) && !UrlShape.IsMatch(body.AudioUrl))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "audioUrl must be an absolute http(s):// or s3:// URL.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/audio-url-invalid"
            });
        }

        var photos = body.Photos ?? new List<string>();
        if (photos.Count > MaxPhotos)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"Too many photos attached (max {MaxPhotos}).",
                Detail = $"received={photos.Count}",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/photos-too-many"
            });
        }
        foreach (var photo in photos)
        {
            if (string.IsNullOrWhiteSpace(photo) || !UrlShape.IsMatch(photo))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Every photos[] entry must be an absolute http(s):// or s3:// URL.",
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/photo-url-invalid"
                });
            }
        }

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
    [RequireRole(Roles.Client)]
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
        CreatedAt = r.CreatedAt,
        ScheduledAt = r.ScheduledAt,
        JeeberId = r.JeeberId,
        AcceptedAt = r.AcceptedAt
    };
}
