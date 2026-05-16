namespace JeebGateway.Requests;

/// <summary>
/// Outcome of <see cref="IRequestsStore.TryTransitionAsync"/> (T-backend-013).
/// The controller pattern-matches on the outcome to choose between 200,
/// 400, and 404 — keeping the HTTP mapping out of the store and the
/// store guard out of the controller.
/// </summary>
public enum DeliveryTransitionOutcome
{
    /// <summary>Transition committed; <see cref="DeliveryTransitionResult.Request"/> is the updated row.</summary>
    Committed,

    /// <summary>The id was not found in the store.</summary>
    NotFound,

    /// <summary>The requested transition violates the state machine. Controller returns 400.</summary>
    InvalidTransition,

    /// <summary>Transitioning to <c>delivered</c> requires the OTP, which the caller did not supply.</summary>
    OtpRequired,

    /// <summary>The supplied OTP does not match the row's stored OTP.</summary>
    OtpMismatch,
}

/// <summary>
/// Bundle returned by <see cref="IRequestsStore.TryTransitionAsync"/>. The
/// store hands back enough information for the controller to:
/// <list type="bullet">
///   <item>Render the updated row to <see cref="DeliveryRequestDto"/> on success.</item>
///   <item>Produce a ProblemDetails with the rejection reason on failure.</item>
///   <item>Fan out a push to the "other party" only when
///     <see cref="Outcome"/> is <see cref="DeliveryTransitionOutcome.Committed"/>.</item>
/// </list>
/// </summary>
public sealed record DeliveryTransitionResult(
    DeliveryTransitionOutcome Outcome,
    DeliveryRequest? Request,
    string? PreviousStatus,
    string? Reason);
