namespace JeebGateway.Requests.Cancellation.V2;

/// <summary>
/// POST /v1/deliveries/{id}/cancel request body. Reason is mandatory
/// for a Jeeber-initiated cancellation; optional and informational for
/// a Client cancellation.
/// </summary>
public sealed class CancelV1Request
{
    public string? Reason { get; set; }
}

/// <summary>
/// POST /v1/deliveries/{id}/cancel response body. Includes the policy
/// counters surfaced to the mobile client so the in-app "n of 3 this
/// week" copy can render without a second round-trip.
/// </summary>
public sealed class CancelV1Response
{
    public required string DeliveryId { get; init; }
    public required string Status { get; init; }
    public required string PreviousStatus { get; init; }
    public required string CancelledBy { get; init; }

    /// <summary>True when the soft limit was breached and the cancellation
    /// fee was posted to unified_payment_gateway.</summary>
    public bool FeeApplied { get; init; }

    /// <summary>Amount posted to unified_payment_gateway (currency =
    /// <see cref="FeeCurrency"/>). Zero when <see cref="FeeApplied"/> is
    /// false.</summary>
    public decimal FeeAmount { get; init; }

    public string? FeeCurrency { get; init; }

    /// <summary>Idempotency key shipped to unified_payment_gateway. Null
    /// when no fee was posted. Exposed so QA can correlate gateway logs
    /// with the downstream wallet entry.</summary>
    public string? FeeIdempotencyKey { get; init; }

    /// <summary>Client only: cancellations the user has accumulated in
    /// the current ISO-week (including this one).</summary>
    public int? ClientCancellationsThisWeek { get; init; }

    /// <summary>Jeeber only: strikes accumulated in the rolling 30-day
    /// window (including this one).</summary>
    public int? JeeberStrikesLast30Days { get; init; }

    /// <summary>True when this Jeeber cancellation tripped the strike
    /// threshold and the user's jeeber role was suspended.</summary>
    public bool JeeberRoleSuspended { get; init; }

    public DateTimeOffset? SuspensionExpiresAt { get; init; }
}

/// <summary>
/// 429 payload returned when the client hard-limit is breached. Mobile
/// clients render <see cref="RetryAfter"/> as "Resets Monday at midnight".
/// Mirrors the ProblemDetails+extensions pattern in the rest of the
/// gateway — top-level fields land in <c>extensions</c>.
/// </summary>
public sealed class CancelV1RateLimitedExtensions
{
    public required int Cap { get; init; }
    public required int Used { get; init; }
    public required DateTimeOffset ResetAt { get; init; }
    public required int RetryAfterSeconds { get; init; }
    public DateTimeOffset RetryAfter => ResetAt;
}
