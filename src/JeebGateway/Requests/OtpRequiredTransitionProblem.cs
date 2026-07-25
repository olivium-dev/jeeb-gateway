using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Requests;

/// <summary>
/// Builds the P6/G1 typed <c>otp_required</c> HTTP 422 body for a jeeber-sourced status
/// PATCH that targets <c>Done</c>.
///
/// <para>WHY a dedicated problem type: <c>AtDoor → Done</c> has exactly ONE legal edge in
/// the frozen SM (<see cref="DeliverySm"/> edge 10, trigger <c>otp_verified</c>), fired
/// only by <c>POST /v1/deliveries/{id}/otp/verify</c>. A bare <c>PATCH /status {to:"Done"}</c>
/// from a jeeber is not that trigger, and delivery-service answers it with the GENERIC
/// <c>transition_not_allowed</c> — which the app renders as "That transition is not
/// allowed" (incident 2026-07-25: five 1 ms 422s on one delivery). The gateway answers it
/// locally, with a reason token the client can match on, and never dials upstream.</para>
///
/// <para>Shape mirrors <see cref="CanonicalTransitionProblem"/>: RFC 7807 problem+json,
/// status 422, with <c>reason</c> / <c>from</c> / <c>to</c> / <c>trigger</c> carried as
/// extensions. <c>detail</c> is prose for humans; <c>reason</c> is the machine contract —
/// mobile matches the TOKEN, never the prose.</para>
/// </summary>
public static class OtpRequiredTransitionProblem
{
    public const int Status422UnprocessableEntity = 422;

    /// <summary>
    /// Builds the 422 <c>ObjectResult</c>. <paramref name="from"/> is the best-effort
    /// pre-transition status read off the gateway's ledger row (legacy or canonical
    /// token); it is normalized through <see cref="DeliveryStatusAlias"/> and falls back
    /// to <see cref="CanonicalDeliveryStatus.AtDoor"/> when the row could not be read —
    /// AtDoor being the only state from which the client is meant to attempt completion.
    /// </summary>
    public static ObjectResult Build(string? from, string? detail)
    {
        var canonicalFrom = DeliveryStatusAlias.ToCanonical(from) ?? CanonicalDeliveryStatus.AtDoor;

        var problem = new ProblemDetails
        {
            Title = "OTP is required to complete this transition.",
            Detail = detail,
            Status = Status422UnprocessableEntity,
            Type = "https://jeeb.dev/errors/otp-required"
        };
        problem.Extensions["reason"] = DeliverySm.ReasonOtpRequired;
        problem.Extensions["from"] = canonicalFrom;
        problem.Extensions["to"] = CanonicalDeliveryStatus.Done;
        problem.Extensions["trigger"] = DeliveryTrigger.OtpVerified;

        return new ObjectResult(problem)
        {
            StatusCode = Status422UnprocessableEntity,
            ContentTypes = { "application/problem+json" }
        };
    }
}
