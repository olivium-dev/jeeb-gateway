using JeebGateway.Ratings;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// T-backend-020 / JEEB-38 — mutual-blind ratings.
///
/// Two endpoints:
/// <list type="bullet">
///   <item><c>POST /api/deliveries/{id}/rate</c> — submit a 1..5-star rating
///     with an optional comment. Authoritative storage is the downstream
///     score-taking-service; the gateway tracks the per-delivery pair so
///     it can project the blind/reveal state.</item>
///   <item><c>GET /api/deliveries/{id}/rating</c> — caller-specific view
///     of the rating row. Counterparty's rating is hidden until both sides
///     submit. After the 7-day window closes, the row is locked.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/deliveries/{deliveryId}")]
public class RatingsController : ControllerBase
{
    private readonly IRatingService _service;

    public RatingsController(IRatingService service)
    {
        _service = service;
    }

    [HttpPost("rate")]
    [ProducesResponseType(typeof(SubmitRatingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Submit(
        string deliveryId,
        [FromBody] SubmitRatingRequest? body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        if (body is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Request body is required.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/invalid-request"
            });
        }

        var result = await _service.SubmitAsync(deliveryId, callerId, body.Stars, body.Comment, ct);

        return result.Outcome switch
        {
            RatingSubmissionOutcome.Submitted => Ok(new SubmitRatingResponse
            {
                Status = ToStatusResponse(deliveryId, result.View!, result.CallerIsClient)
            }),

            RatingSubmissionOutcome.InvalidStars => BadRequest(new ProblemDetails
            {
                Title = "stars must be between 1 and 5.",
                Detail = result.Detail,
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/invalid-rating"
            }),

            RatingSubmissionOutcome.CommentTooLong => BadRequest(new ProblemDetails
            {
                Title = "Comment is too long.",
                Detail = result.Detail,
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/invalid-rating"
            }),

            RatingSubmissionOutcome.DeliveryNotFound => NotFound(),

            RatingSubmissionOutcome.NotAParty => StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "You are not a party to this delivery.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/not-a-party"
            }),

            RatingSubmissionOutcome.NotDelivered => Conflict(new ProblemDetails
            {
                Title = "Delivery is not in a rateable state.",
                Detail = result.Detail,
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/not-rateable"
            }),

            RatingSubmissionOutcome.AlreadyRated => Conflict(new ProblemDetails
            {
                Title = "You have already rated this delivery.",
                Detail = result.Detail,
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/already-rated"
            }),

            RatingSubmissionOutcome.WindowClosed => Conflict(new ProblemDetails
            {
                Title = "Rating window has closed.",
                Detail = result.Detail,
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/rating-window-closed"
            }),

            _ => Problem(
                title: "Unhandled rating submission outcome.",
                statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    [HttpGet("rating")]
    [ProducesResponseType(typeof(RatingStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Get(string deliveryId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        var result = await _service.GetAsync(deliveryId, callerId, ct);

        return result.Outcome switch
        {
            RatingQueryOutcome.Ok =>
                Ok(ToStatusResponse(deliveryId, result.View!, result.CallerIsClient)),

            RatingQueryOutcome.NotFound => NotFound(),

            RatingQueryOutcome.NotAParty => StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "You are not a party to this delivery.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/not-a-party"
            }),

            RatingQueryOutcome.NotDelivered => Conflict(new ProblemDetails
            {
                Title = "Delivery is not in a rateable state.",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/not-rateable"
            }),

            _ => Problem(
                title: "Unhandled rating query outcome.",
                statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static RatingStatusResponse ToStatusResponse(
        string deliveryId,
        BlindRevealView view,
        bool callerIsClient)
    {
        return new RatingStatusResponse
        {
            DeliveryId = deliveryId,
            State = RatingStateCodes.For(view.Outcome),
            Mine = ToPartyView(view.MyRating),
            Theirs = ToPartyView(view.TheirRating),
            WindowClosesAt = view.WindowClosesAt,
            WindowExpired = view.WindowExpired,
        };
    }

    private static RatingPartyView ToPartyView(RatingEntry? entry)
    {
        if (entry is null)
        {
            return new RatingPartyView
            {
                Submitted = false,
                Stars = null,
                Comment = null,
                SubmittedAt = null
            };
        }

        return new RatingPartyView
        {
            Submitted = true,
            Stars = entry.Stars,
            Comment = entry.Comment,
            SubmittedAt = entry.SubmittedAt
        };
    }
}
