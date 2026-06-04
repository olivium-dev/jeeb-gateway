using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Requests;

/// <summary>
/// Builds the ADR-002 §4 typed <c>transition_not_allowed</c> HTTP 422 body for an
/// illegal delivery transition, matching the shape delivery-service emits:
/// <c>{ reason, from, trigger, to }</c>. RFC 7807 ProblemDetails with those four
/// fields carried as extensions.
///
/// CONTRACT-AFFECTING (PR-3): this 422 shape is only returned when
/// <c>FeatureFlags:UseUpstream:CanonicalTransition422</c> is on — see
/// <see cref="JeebGateway.Services.UpstreamFeatureFlags.CanonicalTransition422"/>.
/// While the flag is off the controller keeps returning the legacy 400.
/// </summary>
public static class CanonicalTransitionProblem
{
    public const int Status422UnprocessableEntity = 422;

    /// <summary>
    /// Builds a 422 <c>ObjectResult</c> for an illegal transition. The
    /// <paramref name="from"/> and <paramref name="to"/> are normalized to their
    /// canonical tokens (via <see cref="DeliveryStatusAlias"/>) so the body
    /// speaks the canonical lexicon even when the persisted row still carries a
    /// legacy alias. The trigger is the explicit one when supplied, otherwise the
    /// gateway-derived trigger, otherwise null (an unknown / underivable edge).
    /// </summary>
    public static ObjectResult Build(string? from, string? to, string? trigger, string? callerRole, string? detail)
    {
        var canonicalFrom = DeliveryStatusAlias.ToCanonical(from) ?? from;
        var canonicalTo = DeliveryStatusAlias.ToCanonical(to) ?? to;

        var resolvedTrigger = !string.IsNullOrWhiteSpace(trigger)
            ? trigger
            : (canonicalFrom is not null && canonicalTo is not null
                ? DeliverySm.DeriveTrigger(canonicalFrom, canonicalTo, callerRole ?? DeliveryTriggerSource.System)
                : null);

        var problem = new ProblemDetails
        {
            Title = "Transition not allowed.",
            Detail = detail,
            Status = Status422UnprocessableEntity,
            Type = "https://jeeb.dev/errors/transition-not-allowed"
        };
        problem.Extensions["reason"] = DeliverySm.ReasonTransitionNotAllowed;
        problem.Extensions["from"] = canonicalFrom;
        problem.Extensions["trigger"] = resolvedTrigger;
        problem.Extensions["to"] = canonicalTo;

        return new ObjectResult(problem)
        {
            StatusCode = Status422UnprocessableEntity,
            ContentTypes = { "application/problem+json" }
        };
    }
}
