using JeebGateway.Availability;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Jeeber-facing offer acceptance endpoint. Accepting an offer is the
/// moment a Jeeber commits to a specific request — at that point the
/// gateway enforces BR-10 (T-backend-039): a Jeeber may have at most
/// <see cref="ActiveDeliveriesLimit"/> active deliveries
/// (statuses <c>accepted</c>, <c>picked_up</c>, <c>heading_off</c>).
///
/// 409 outcomes:
///   * <c>too-many-active-deliveries</c> — BR-10 cap hit.
///   * <c>request-not-acceptable</c> — request moved out of
///     pre-acceptance (another Jeeber won the race, expired, cancelled).
///   * <c>offer-not-pending</c> — offer was already accepted or withdrawn.
/// </summary>
[ApiController]
[Route("offers")]
public class OffersController : ControllerBase
{
    /// <summary>BR-10: per-Jeeber maximum of concurrent active deliveries.</summary>
    public const int ActiveDeliveriesLimit = 2;

    /// <summary>
    /// Mobile-app renders this verbatim in the error banner when the cap
    /// is hit — matches the wording used for the BR-9 sibling rule.
    /// </summary>
    internal const string LimitExceededMessage =
        "Maximum 2 active deliveries. Complete a delivery before accepting another.";

    private readonly IPendingOffersStore _offers;
    private readonly IRequestsStore _requests;
    private readonly TimeProvider _clock;

    public OffersController(
        IPendingOffersStore offers,
        IRequestsStore requests,
        TimeProvider clock)
    {
        _offers = offers;
        _requests = requests;
        _clock = clock;
    }

    [HttpPost("{offerId}/accept")]
    [RequireRole(Roles.Jeeber)]
    [RequireActiveUser]
    [ProducesResponseType(typeof(DeliveryRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Accept(string offerId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var jeeberId, out var problem)) return problem;

        var offer = await _offers.GetAsync(offerId, ct);
        if (offer is null) return NotFound();

        // The offer is bound to a specific Jeeber at creation. Another
        // Jeeber trying to claim it is forbidden, not "not found" — the
        // distinction matters so the mobile app can show the correct
        // banner and ops can spot the case in audit logs.
        if (!string.Equals(offer.JeeberId, jeeberId, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "This offer was extended to a different Jeeber.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/offer-not-owned"
            });
        }

        if (offer.Status != PendingOfferStatus.Pending)
        {
            return Conflict(new ProblemDetails
            {
                Title = $"Offer is no longer pending (current={offer.Status}).",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/offer-not-pending"
            });
        }

        var now = _clock.GetUtcNow();
        DeliveryRequest? accepted;
        try
        {
            accepted = await _requests.TryAcceptByJeeberAsync(
                offer.RequestId,
                jeeberId,
                ActiveDeliveriesLimit,
                now,
                ct);
        }
        catch (TooManyActiveDeliveriesException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = LimitExceededMessage,
                Detail = $"Jeeber has {ex.ActiveCount} active deliveries (limit {ex.Limit}).",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/too-many-active-deliveries"
            });
        }
        catch (RequestNotAcceptableException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Request is no longer acceptable.",
                Detail = $"Current status: {ex.CurrentStatus}. Another Jeeber may have accepted, or it expired.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/request-not-acceptable"
            });
        }

        if (accepted is null)
        {
            // The offer pointed at a request the store could not find. In
            // production this would be a referential-integrity bug; we
            // surface it as 404 so the mobile app retries the listing
            // instead of looping on a phantom offer.
            return NotFound();
        }

        // Mark the offer accepted (and withdraw the Jeeber's siblings)
        // only after the request transition succeeded — keeps the two
        // sides of the relationship consistent if the request flip threw.
        await _offers.AcceptAsync(offerId, now, ct);

        return Ok(ToDto(accepted));
    }

    private static DeliveryRequestDto ToDto(DeliveryRequest r) => new()
    {
        Id = r.Id,
        ClientId = r.ClientId,
        Status = r.Status,
        Description = r.Description,
        PickupAddress = r.PickupAddress,
        DropoffAddress = r.DropoffAddress,
        CreatedAt = r.CreatedAt,
        ScheduledAt = r.ScheduledAt,
        JeeberId = r.JeeberId,
        AcceptedAt = r.AcceptedAt
    };
}
