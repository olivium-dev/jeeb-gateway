namespace JeebGateway.Requests.Cancellation;

/// <summary>
/// T-backend-024 / JEEB-42: cancellation lifecycle constants. Mirrors the
/// states added to the delivery-request enum for the cancellation flow
/// alongside the existing <see cref="RequestStatus"/> values.
///
/// Two new non-terminal statuses are introduced:
/// <list type="bullet">
///   <item><see cref="CancellationRequested"/> — a Client cancellation
///     after <c>picked_up</c> that needs admin sign-off. The row is
///     parked here until an admin approves (→ <see cref="RequestStatus.Cancelled"/>)
///     or rejects (→ the prior status, refunding the request to the
///     fulfilment lane).</item>
/// </list>
/// </summary>
public static class CancellationStatus
{
    public const string CancellationRequested = "cancellation_requested";
}

/// <summary>
/// POST /deliveries/{id}/cancel body. Reason is mandatory for Jeeber
/// cancellations and surfaced on the admin queue when a Client cancellation
/// gets escalated. For pre-pickup Client cancellations the field is
/// optional and informational only.
/// </summary>
public sealed class CancelDeliveryBody
{
    public string? Reason { get; set; }
}

/// <summary>
/// Response payload returned by POST /deliveries/{id}/cancel. The flag set
/// depends on which branch fired:
/// <list type="bullet">
///   <item><c>cancelled</c> — Client cancelled before pickup. Free, immediate,
///     row is terminal.</item>
///   <item><c>cancellation_requested</c> — Client cancelled after pickup.
///     Awaiting admin approval; <see cref="PendingApproval"/> is true.</item>
///   <item><c>cancelled</c> with <see cref="JeeberRestricted"/> true — Jeeber
///     cancelled and tripped the 3+/7d threshold.</item>
/// </list>
/// </summary>
public sealed class CancelDeliveryResponse
{
    public required string DeliveryId { get; init; }
    public required string Status { get; init; }
    public required string PreviousStatus { get; init; }
    public string? Reason { get; init; }
    public bool PendingApproval { get; init; }
    public bool JeeberRestricted { get; init; }
    public DateTimeOffset? RestrictionExpiresAt { get; init; }

    /// <summary>
    /// Rolling 7-day cancellation count for the Jeeber at the moment of
    /// this cancellation. Surfaced on Jeeber-initiated responses so the
    /// app can show "n of 3 in the last 7 days" without an extra round
    /// trip. Null for Client-initiated cancellations.
    /// </summary>
    public int? JeeberCancellationsLast7Days { get; init; }
}

/// <summary>
/// One row of the admin pending-cancellations queue. Returned by
/// GET /admin/cancellations. Ordered oldest-first so admins can drain
/// the queue in the order cancellations were requested.
/// </summary>
public sealed class AdminCancellationItem
{
    public required string DeliveryId { get; init; }
    public required string ClientId { get; init; }
    public string? JeeberId { get; init; }

    /// <summary>The status the row was in immediately before the cancel
    /// request landed — what the row will revert to on reject.</summary>
    public required string PreviousStatus { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }
    public string? Reason { get; init; }
}

public sealed class AdminCancellationsResponse
{
    public required IReadOnlyList<AdminCancellationItem> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int Total { get; init; }
}

/// <summary>
/// PATCH /admin/cancellations/{id} body. <c>action</c> is one of
/// <c>approve</c> or <c>reject</c>. A rejection note is optional.
/// </summary>
public sealed class AdminCancellationDecisionBody
{
    public string? Action { get; set; }
    public string? Note { get; set; }
}

public sealed class AdminCancellationDecisionResponse
{
    public required string DeliveryId { get; init; }
    public required string Action { get; init; }
    public required string Status { get; init; }
}
