namespace JeebGateway.Requests.Cancellation;

/// <summary>
/// Outcomes returned by <see cref="ICancellationService.CancelAsync"/>.
/// The controller pattern-matches on the outcome to choose between 200,
/// 400, 403, 404, 409, and 423.
/// </summary>
public enum CancellationOutcome
{
    /// <summary>Pre-pickup Client cancel: row transitioned to <c>cancelled</c>.</summary>
    CancelledImmediately,

    /// <summary>Post-pickup Client cancel: row transitioned to <c>cancellation_requested</c>.</summary>
    PendingAdminApproval,

    /// <summary>Jeeber cancel: row transitioned to <c>cancelled</c>.</summary>
    CancelledByJeeber,

    /// <summary>The delivery id is unknown.</summary>
    NotFound,

    /// <summary>The caller is neither the Client nor the bound Jeeber on the row.</summary>
    NotAuthorized,

    /// <summary>Row is already terminal (cancelled/expired/delivered/...). Returns 409.</summary>
    NotCancellable,

    /// <summary>Jeeber tried to cancel but didn't supply a reason. Returns 400.</summary>
    ReasonRequired,
}

/// <summary>
/// Result bundle returned by <see cref="ICancellationService.CancelAsync"/>.
/// Captures everything the controller needs to render the response and
/// fan out notifications.
/// </summary>
public sealed record CancellationResult(
    CancellationOutcome Outcome,
    DeliveryRequest? Request,
    string? PreviousStatus,
    string? Reason,
    bool JeeberRestrictionTriggered,
    DateTimeOffset? RestrictionExpiresAt,
    int? JeeberCancellationsLast7Days);

/// <summary>
/// Outcomes for the admin approve/reject path.
/// </summary>
public enum AdminCancellationDecisionOutcome
{
    Approved,
    Rejected,
    NotFound,
    NotPending,
    UnknownAction,
}

public sealed record AdminCancellationDecisionResult(
    AdminCancellationDecisionOutcome Outcome,
    DeliveryRequest? Request,
    string? PreviousStatus);
