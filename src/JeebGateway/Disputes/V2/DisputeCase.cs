namespace JeebGateway.Disputes.V2;

/// <summary>
/// One row in the v2 dispute-case ledger (T-BE-028 / JEB-64). The
/// production swap targets a Postgres <c>dispute_cases</c> table; the
/// shape here mirrors the eventual columns 1:1 so the controller,
/// service and tests share a single contract.
///
/// Evidence is captured synchronously at escalate time (chat transcript
/// snapshot, GPS polyline of the delivery's pings) and persisted on the
/// row so the admin queue (T-CMS-004) doesn't have to chase data
/// post-hoc.
/// </summary>
public sealed class DisputeCase
{
    public required string Id { get; init; }
    public required string DeliveryId { get; init; }

    /// <summary>The user (Client or Jeeber) who escalated the delivery.</summary>
    public required string OpenedByUserId { get; init; }

    /// <summary>
    /// The counter-party id — pulled off the delivery row at escalate time
    /// so resolution-time notifications can fan out to both sides even
    /// when the delivery has since been re-assigned or anonymised.
    /// </summary>
    public string? CounterpartyUserId { get; init; }

    public required string Reason { get; init; }
    public string? Comment { get; init; }

    /// <summary>
    /// Already-uploaded evidence photo URLs (mirrors the legacy disputes
    /// flow). Validated for shape by the controller; bytes round-trip
    /// through upload-service outside of this row.
    /// </summary>
    public IReadOnlyList<string> PhotoUrls { get; init; } = Array.Empty<string>();

    public required string State { get; set; }
    public required DateTimeOffset OpenedAt { get; init; }

    /// <summary>
    /// Caller-supplied <c>Idempotency-Key</c> on the escalate request. A
    /// replay with the same key returns the existing case row instead of
    /// creating a duplicate (PO review blocker #6).
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Captured at escalate time per AC1.</summary>
    public DisputeEvidence Evidence { get; set; } = DisputeEvidence.Empty;

    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolverAdminId { get; set; }
    public string? ResolutionNotes { get; set; }

    /// <summary>
    /// USD refund amount handed to <c>unified_payment_gateway</c> at
    /// resolve time when <c>decision = refund</c>. Null for
    /// no-action resolutions.
    /// </summary>
    public decimal? RefundUsd { get; set; }

    /// <summary>
    /// The ledger / refund id returned by <c>unified_payment_gateway</c>
    /// when the refund landed. Null on no-action resolutions or when the
    /// refund failed and was rolled back (PO review blocker #4).
    /// </summary>
    public string? RefundLedgerEntryId { get; set; }

    /// <summary>
    /// <c>Idempotency-Key</c> on the resolve request (PO review blocker #6).
    /// A replay with the same key returns the existing terminal row.
    /// </summary>
    public string? ResolveIdempotencyKey { get; set; }
}

/// <summary>
/// The bundle of evidence attached to a dispute case at open time. Pure
/// snapshot — once captured, the row is immutable.
/// </summary>
public sealed record DisputeEvidence
{
    public static readonly DisputeEvidence Empty = new();

    /// <summary>JSON snapshot of the conversation transcript at escalate time.</summary>
    public string? ChatTranscriptJson { get; init; }

    public int ChatTranscriptMessageCount { get; init; }

    /// <summary>
    /// Lat/Lng pair list (<c>[[lat,lng], [lat,lng], …]</c>) representing
    /// the Jeeber's tracked path during the delivery. Empty when the
    /// delivery had no GPS pings (cash-pickup, never-accepted, …).
    /// </summary>
    public IReadOnlyList<double[]> GpsPolyline { get; init; } = Array.Empty<double[]>();

    /// <summary>
    /// True when either evidence call timed-out or failed; the case is
    /// still created but the admin queue surfaces the gap (PO review
    /// blocker #3 — must NOT block the AC6 1-second budget).
    /// </summary>
    public bool Degraded { get; init; }

    public string? DegradedReason { get; init; }
}
