namespace JeebGateway.Requests.Cancellation.V2;

/// <summary>
/// T-BE-030 (JEB-66) — orchestrator for the v1 cancellation policy
/// surface. Owns the policy table for who-can-cancel-when, drives the
/// <see cref="JeebGateway.Requests.IRequestsStore"/> mutations, posts
/// fees into unified_payment_gateway, applies jeeber-role suspensions,
/// and emits the <c>cancel.policy_applied</c> structured log (AC4).
/// Returning a <see cref="CancellationPolicyResult"/> instead of throwing
/// keeps every controller error mapping in one place.
/// </summary>
public interface ICancellationPolicyService
{
    Task<CancellationPolicyResult> ApplyAsync(
        string deliveryId,
        string callerUserId,
        bool callerIsClient,
        bool callerIsJeeber,
        string? reason,
        CancellationToken ct);
}

public enum CancellationPolicyOutcome
{
    /// <summary>Client cancel committed — fee may or may not have been posted.</summary>
    CancelledByClient,

    /// <summary>Jeeber cancel committed — strike may or may not have been issued.</summary>
    CancelledByJeeber,

    /// <summary>The delivery id is unknown.</summary>
    NotFound,

    /// <summary>Caller is neither the Client nor the bound Jeeber on the row.</summary>
    NotAuthorized,

    /// <summary>Row is past the cancellable boundary (status &gt; <c>picked_up</c>). AC6 → 422.</summary>
    TooLateToCancel,

    /// <summary>Client hit the hard limit for this ISO-week. AC2 → 429.</summary>
    RateLimited,

    /// <summary>Row is already terminal / pending admin / etc.</summary>
    NotCancellable,

    /// <summary>Jeeber didn't supply the mandatory reason.</summary>
    ReasonRequired,
}

public sealed record CancellationPolicyResult(
    CancellationPolicyOutcome Outcome,
    JeebGateway.Requests.DeliveryRequest? Request,
    string? PreviousStatus,
    string? Reason,
    bool FeeApplied,
    decimal FeeAmount,
    string? FeeCurrency,
    string? FeeIdempotencyKey,
    int? ClientCancellationsThisWeek,
    int? JeeberStrikesLast30Days,
    bool JeeberRoleSuspended,
    DateTimeOffset? SuspensionExpiresAt,
    DateTimeOffset? RateLimitResetAt,
    int? RateLimitCap,
    int? RateLimitUsed);
