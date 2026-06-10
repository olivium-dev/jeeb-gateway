using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Ratings;
using JeebGateway.Ratings.Jeeb;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using JeebGateway.service.ServiceFeedback;
using FeedbackApiException = JeebGateway.service.ServiceFeedback.ApiException;

namespace JeebGateway.Controllers;

/// <summary>
/// JEB-1489 — the Jeeb-domain mutual-blind ratings BFF backed by the SHARED,
/// product-agnostic blind-rating reveal primitive in feedback-service
/// (<c>POST /ratings</c>, <c>GET /ratings/{correlationId}/reveal</c>), consumed via
/// the NSwag-generated <see cref="ServiceFeedbackClient"/> typed client (GR4).
///
/// <para>
/// ALL Jeeb semantics live HERE (GR2): the Sami/Kamal role vocabulary, the
/// deliveryId &lt;-&gt; correlationId linkage, the partition value, the tag taxonomy,
/// and the generic -&gt; Jeeb <c>{state, ...}</c> projection — see
/// <see cref="JeebRatingVocabulary"/> / <see cref="JeebRatingProjection"/>. The
/// shared service stores only opaque correlationId + rater/ratee + generic tags[].
/// </para>
///
/// <para>
/// GR1 (non-breaking): this is a NET-NEW route surface
/// (<c>/v1/ratings/jeeb/*</c>) gated by <c>FeatureFlags:UseUpstream:Ratings</c>,
/// which DEFAULTS OFF in every environment. While off, these endpoints return 503
/// (mirroring the cdn / form-builder net-new kill-switch shape) and the existing
/// in-gateway mutual-blind path (<see cref="RatingsController"/>,
/// <c>/api/deliveries/{id}/rate</c>) remains the untouched live default. No
/// existing route is renamed or removed.
/// </para>
/// </summary>
[ApiController]
[Route("v1/ratings/jeeb/deliveries/{deliveryId}")]
[RequireCapability(Capabilities.DeliveryParticipate)]
public class JeebRatingsController : ControllerBase
{
    private const int MinStars = 1;
    private const int MaxStars = 5;

    private readonly ServiceFeedbackClient _feedback;
    private readonly IRequestsStore _requests;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;

    public JeebRatingsController(
        ServiceFeedbackClient feedback,
        IRequestsStore requests,
        IOptionsMonitor<UpstreamFeatureFlags> flags)
    {
        _feedback = feedback;
        _requests = requests;
        _flags = flags;
    }

    /// <summary>
    /// Submit one side of the delivery's mutual-blind rating. The caller's Jeeb
    /// role (Sami/Kamal) is resolved from the delivery parties; the rating is
    /// stored as an opaque two-party record in the shared feedback-service.
    /// </summary>
    [HttpPost]
    [RequireCapability(Capabilities.RatingSubmit)]
    [ProducesResponseType(typeof(JeebRatingStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Submit(
        string deliveryId,
        [FromBody] JeebSubmitRatingRequest? body,
        CancellationToken ct)
    {
        if (!_flags.CurrentValue.Ratings) return UpstreamDisabled();
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        if (body is null)
        {
            return BadRequest(Problem400("Request body is required."));
        }

        if (body.Stars < MinStars || body.Stars > MaxStars)
        {
            return BadRequest(Problem400($"stars must be between {MinStars} and {MaxStars}."));
        }

        var parties = await ResolvePartiesAsync(deliveryId, callerId, ct);
        if (parties.Result is not null) return parties.Result;
        var (callerRole, raterId, rateeId) = parties.Value;

        List<string> tags;
        try
        {
            tags = JeebRatingVocabulary.BuildTags(callerRole, body.Tags);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(Problem400(ex.Message));
        }

        var request = new SubmitBlindRatingRequest
        {
            CorrelationId = JeebRatingVocabulary.CorrelationForDelivery(deliveryId),
            RaterId = raterId,
            RateeId = rateeId,
            Score = body.Stars,
            Comment = body.Comment?.Trim(),
            Tags = tags,
        };

        try
        {
            var result = await _feedback.RatingsSubmitAsync(request, ct);
            return Ok(JeebRatingProjection.Project(deliveryId, callerRole, result.State));
        }
        catch (FeedbackApiException ex)
        {
            return Problem(
                title: "Upstream feedback-service rejected the rating.",
                detail: ex.Message,
                statusCode: ex.StatusCode is >= 400 and < 600 ? ex.StatusCode : StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Read the caller-specific Jeeb view of the delivery's mutual-blind rating.
    /// The counterparty's rating stays hidden until both sides submit.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(JeebRatingStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get(string deliveryId, CancellationToken ct)
    {
        if (!_flags.CurrentValue.Ratings) return UpstreamDisabled();
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        var parties = await ResolvePartiesAsync(deliveryId, callerId, ct);
        if (parties.Result is not null) return parties.Result;
        var (callerRole, raterId, _) = parties.Value;

        try
        {
            var correlationId = JeebRatingVocabulary.CorrelationForDelivery(deliveryId);
            var state = await _feedback.RatingsRevealAsync(correlationId, raterId, ct);
            return Ok(JeebRatingProjection.Project(deliveryId, callerRole, state));
        }
        catch (FeedbackApiException ex)
        {
            return Problem(
                title: "Upstream feedback-service rejected the reveal read.",
                detail: ex.Message,
                statusCode: ex.StatusCode is >= 400 and < 600 ? ex.StatusCode : StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Resolve the caller's Jeeb role + the rater/ratee GUIDs from the delivery
    /// parties. Returns a populated <see cref="ActionResult"/> on any failure
    /// (404 unknown delivery, 403 not-a-party, 400 non-GUID party ids).
    /// </summary>
    private async Task<(IActionResult? Result, (JeebRatingRole Role, Guid RaterId, Guid RateeId) Value)> ResolvePartiesAsync(
        string deliveryId,
        string callerId,
        CancellationToken ct)
    {
        var delivery = await _requests.GetAsync(deliveryId, ct);
        if (delivery is null || string.IsNullOrEmpty(delivery.JeeberId))
        {
            return (NotFound(), default);
        }

        var callerIsClient = string.Equals(delivery.ClientId, callerId, StringComparison.Ordinal);
        var callerIsJeeber = string.Equals(delivery.JeeberId, callerId, StringComparison.Ordinal);
        if (!callerIsClient && !callerIsJeeber)
        {
            return (StatusCode(StatusCodes.Status403Forbidden, Problem403("You are not a party to this delivery.")), default);
        }

        if (!Guid.TryParse(callerId, out var raterId)
            || !Guid.TryParse(callerIsClient ? delivery.JeeberId : delivery.ClientId, out var rateeId))
        {
            return (BadRequest(Problem400("Delivery party identifiers are not valid GUIDs.")), default);
        }

        var role = JeebRatingVocabulary.RoleFor(callerIsClient);
        return (null, (role, raterId, rateeId));
    }

    private static ProblemDetails Problem400(string title) => new()
    {
        Title = title,
        Status = StatusCodes.Status400BadRequest,
        Type = "https://jeeb.dev/errors/invalid-rating",
    };

    private static ProblemDetails Problem403(string title) => new()
    {
        Title = title,
        Status = StatusCodes.Status403Forbidden,
        Type = "https://jeeb.dev/errors/not-a-party",
    };

    private IActionResult UpstreamDisabled() => Problem(
        title: "Jeeb ratings upstream disabled",
        detail: "FeatureFlags:UseUpstream:Ratings is off in this environment. "
              + "The shared feedback-service blind-rating primitive is not yet wired live; "
              + "the legacy in-gateway /api/deliveries/{id}/rate path remains the default.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
