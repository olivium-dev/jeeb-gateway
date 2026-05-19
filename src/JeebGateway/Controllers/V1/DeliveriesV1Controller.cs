using JeebGateway.Requests.Cancellation.V2;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers.V1;

/// <summary>
/// T-BE-030 / JEB-66 — v1 delivery cancellation surface.
///
/// Route: <c>POST /v1/deliveries/{deliveryId}/cancel</c>
/// Body:  <c>{ "reason": "..." }</c> (reason mandatory for jeeber-initiated cancels)
///
/// Responses:
/// <list type="bullet">
///   <item>200 <see cref="CancelV1Response"/> — cancel committed; <c>feeApplied</c>
///     true when the client breached the soft limit, <c>jeeberRoleSuspended</c>
///     true when the jeeber strike threshold tripped.</item>
///   <item>400 ProblemDetails — jeeber didn't supply a reason.</item>
///   <item>401 — no caller identity on the request.</item>
///   <item>403 ProblemDetails — caller has neither client nor jeeber role,
///     OR caller is not a party to the delivery.</item>
///   <item>404 — delivery id is unknown.</item>
///   <item>409 ProblemDetails — row is terminal / pending admin sign-off /
///     otherwise not in a cancellable state.</item>
///   <item>422 ProblemDetails type=<c>https://jeeb.dev/errors/too-late-to-cancel</c>
///     — row is past the cancellable boundary (status &gt; <c>picked_up</c>). AC6.</item>
///   <item>429 ProblemDetails type=<c>https://jeeb.dev/errors/cancellation-rate-limited</c>
///     — client breached the hard limit for this ISO-week. AC2. Extensions
///     carry <c>cap</c>, <c>used</c>, <c>resetAt</c>, <c>retryAfterSeconds</c>;
///     also surfaced via the standard <c>Retry-After</c> header in seconds.</item>
/// </list>
///
/// The controller is intentionally additive to the legacy
/// <see cref="JeebGateway.Controllers.DeliveriesController.Cancel"/>
/// endpoint at <c>POST /deliveries/{id}/cancel</c> — AC5 demands no existing
/// service is broken. The mobile client picks which surface to call.
/// </summary>
[ApiController]
[Route("v1/deliveries")]
[Produces("application/json", "application/problem+json")]
public sealed class DeliveriesV1Controller : ControllerBase
{
    private const string ProblemBase = "https://jeeb.dev/errors/";

    private readonly ICancellationPolicyService _policy;

    public DeliveriesV1Controller(ICancellationPolicyService policy)
    {
        _policy = policy;
    }

    [HttpPost("{deliveryId}/cancel")]
    [ProducesResponseType(typeof(CancelV1Response), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Cancel(
        string deliveryId,
        [FromBody] CancelV1Request? body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized))
        {
            return unauthorized;
        }

        var callerIsClient = UserIdentity.HasRole(HttpContext, Roles.Client);
        var callerIsJeeber = UserIdentity.HasRole(HttpContext, Roles.Jeeber);

        if (!callerIsClient && !callerIsJeeber)
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                type: ProblemBase + "forbidden-role",
                title: "Cancel requires the customer or driver role.");
        }

        var result = await _policy.ApplyAsync(
            deliveryId, callerId, callerIsClient, callerIsJeeber, body?.Reason, ct);

        switch (result.Outcome)
        {
            case CancellationPolicyOutcome.NotFound:
                return NotFound();

            case CancellationPolicyOutcome.NotAuthorized:
                return Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    type: ProblemBase + "not-a-party",
                    title: "You are not a party to this delivery.");

            case CancellationPolicyOutcome.ReasonRequired:
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    type: ProblemBase + "cancellation-reason-required",
                    title: "Reason is required when a driver cancels a delivery.");

            case CancellationPolicyOutcome.NotCancellable:
                return Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    type: ProblemBase + "not-cancellable",
                    title: "Delivery cannot be cancelled in its current state.",
                    detail: $"current status: {result.Request?.Status}");

            case CancellationPolicyOutcome.TooLateToCancel:
                // AC6
                return Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    type: ProblemBase + "too-late-to-cancel",
                    title: "too_late_to_cancel",
                    detail: $"Delivery status '{result.Request?.Status}' is past the cancellable window.");

            case CancellationPolicyOutcome.RateLimited:
            {
                // AC2 — 429 with Retry-After. Retry-After is in seconds per
                // RFC 9110 §10.2.3 (the HTTP-date form is also valid but
                // mobile clients in this codebase parse the delta).
                var resetAt = result.RateLimitResetAt!.Value;
                var retryAfterSeconds = (int)Math.Max(1, Math.Ceiling((resetAt - DateTimeOffset.UtcNow).TotalSeconds));
                Response.Headers["Retry-After"] = retryAfterSeconds.ToString();

                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Type = ProblemBase + "cancellation-rate-limited",
                    Title = "Too many cancellations this week.",
                    Detail = $"You have used {result.RateLimitUsed} of {result.RateLimitCap} cancellations this week. Resets {resetAt:u}.",
                    Instance = HttpContext.Request.Path,
                };
                problem.Extensions["cap"] = result.RateLimitCap;
                problem.Extensions["used"] = result.RateLimitUsed;
                problem.Extensions["resetAt"] = resetAt;
                problem.Extensions["retryAfterSeconds"] = retryAfterSeconds;

                return new ObjectResult(problem)
                {
                    StatusCode = StatusCodes.Status429TooManyRequests,
                    ContentTypes = { "application/problem+json" },
                };
            }

            case CancellationPolicyOutcome.CancelledByClient:
            case CancellationPolicyOutcome.CancelledByJeeber:
                return Ok(new CancelV1Response
                {
                    DeliveryId = result.Request!.Id,
                    Status = result.Request.Status,
                    PreviousStatus = result.PreviousStatus!,
                    CancelledBy = result.Outcome == CancellationPolicyOutcome.CancelledByClient
                        ? "client" : "jeeber",
                    FeeApplied = result.FeeApplied,
                    FeeAmount = result.FeeAmount,
                    FeeCurrency = result.FeeCurrency,
                    FeeIdempotencyKey = result.FeeIdempotencyKey,
                    ClientCancellationsThisWeek = result.ClientCancellationsThisWeek,
                    JeeberStrikesLast30Days = result.JeeberStrikesLast30Days,
                    JeeberRoleSuspended = result.JeeberRoleSuspended,
                    SuspensionExpiresAt = result.SuspensionExpiresAt,
                });

            default:
                return Problem(
                    title: "Unhandled cancellation policy outcome.",
                    statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private IActionResult Problem(int statusCode, string type, string title, string? detail = null)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Type = type,
            Title = title,
            Detail = detail,
            Instance = HttpContext.Request.Path,
        };
        return new ObjectResult(problem)
        {
            StatusCode = statusCode,
            ContentTypes = { "application/problem+json" },
        };
    }
}
