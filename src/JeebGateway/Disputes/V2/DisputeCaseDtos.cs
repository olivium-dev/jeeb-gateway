using System.Text.Json.Serialization;

namespace JeebGateway.Disputes.V2;

/// <summary>
/// POST /v1/deliveries/{id}/escalate body.
/// </summary>
public sealed class EscalateDeliveryRequest
{
    public string? Reason { get; set; }
    public string? Comment { get; set; }
    public List<string>? Photos { get; set; }
}

/// <summary>
/// POST /admin/v1/disputes/{id}/resolve body.
/// </summary>
public sealed class ResolveCaseRequest
{
    /// <summary>One of <c>refund</c> or <c>no_action</c>.</summary>
    public string? Decision { get; set; }

    public decimal? RefundUsd { get; set; }

    public string? Notes { get; set; }
}

public sealed class DisputeCaseResponse
{
    public required string Id { get; init; }
    public required string DeliveryId { get; init; }

    /// <summary>
    /// JEB-64 / T-BE-028 wire contract: the user (Client or Jeeber) who
    /// escalated the delivery. The C# property keeps the verbose
    /// <c>OpenedByUserId</c> name (shared with the persisted row + the
    /// integration tests) but the JSON contract the mobile / admin / S14
    /// console consume is <c>openedBy</c>.
    /// </summary>
    [JsonPropertyName("openedBy")]
    public required string OpenedByUserId { get; init; }
    public string? CounterpartyUserId { get; init; }
    public required string Reason { get; init; }
    public string? Comment { get; init; }
    public IReadOnlyList<string> PhotoUrls { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Lifecycle state on the wire. The internal enum is underscore-cased
    /// (<c>resolved_refund</c> / <c>resolved_no_action</c>) but the
    /// T-BE-028 contract serializes the two resolved states hyphenated
    /// (<c>resolved-refund</c> / <c>resolved-no-action</c>); <c>open</c>,
    /// <c>under_review</c> and <c>closed</c> are unchanged. See
    /// <see cref="DisputeCaseState.ToWire"/>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// JEB-64 wire contract: case open time. Internal column is
    /// <c>OpenedAt</c>; the JSON contract is <c>createdAt</c>.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public required DateTimeOffset OpenedAt { get; init; }

    /// <summary>
    /// JEB-64 wire contract: the admin verdict (<c>refund</c> /
    /// <c>no-action</c>) once the case is resolved, derived from the
    /// terminal state. Null while the case is still open / under_review.
    /// </summary>
    [JsonPropertyName("decision")]
    public string? Decision { get; init; }

    /// <summary>JEB-64: admin who claimed the case (open → under_review). Null until claimed.</summary>
    [JsonPropertyName("reviewedBy")]
    public string? ReviewedByAdminId { get; init; }

    public DateTimeOffset? ReviewedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }
    public string? ResolverAdminId { get; init; }
    public string? ResolutionNotes { get; init; }
    public decimal? RefundUsd { get; init; }
    public string? RefundLedgerEntryId { get; init; }

    public DisputeEvidenceDto Evidence { get; init; } = DisputeEvidenceDto.Empty;

    public static DisputeCaseResponse From(DisputeCase c) => new()
    {
        Id = c.Id,
        DeliveryId = c.DeliveryId,
        OpenedByUserId = c.OpenedByUserId,
        CounterpartyUserId = c.CounterpartyUserId,
        Reason = c.Reason,
        Comment = c.Comment,
        PhotoUrls = c.PhotoUrls.ToList(),
        State = DisputeCaseState.ToWire(c.State),
        Decision = DisputeCaseState.DecisionForState(c.State),
        OpenedAt = c.OpenedAt,
        ReviewedByAdminId = c.ReviewedByAdminId,
        ReviewedAt = c.ReviewedAt,
        ClosedAt = c.ClosedAt,
        ResolvedAt = c.ResolvedAt,
        ResolverAdminId = c.ResolverAdminId,
        ResolutionNotes = c.ResolutionNotes,
        RefundUsd = c.RefundUsd,
        RefundLedgerEntryId = c.RefundLedgerEntryId,
        Evidence = new DisputeEvidenceDto
        {
            ChatTranscriptMessageCount = c.Evidence.ChatTranscriptMessageCount,
            ChatTranscriptJson = c.Evidence.ChatTranscriptJson,
            GpsPolyline = c.Evidence.GpsPolyline.Select(p => p.ToArray()).ToList(),
            Degraded = c.Evidence.Degraded,
            DegradedReason = c.Evidence.DegradedReason
        }
    };
}

public sealed class DisputeEvidenceDto
{
    public static readonly DisputeEvidenceDto Empty = new();

    public int ChatTranscriptMessageCount { get; init; }
    public string? ChatTranscriptJson { get; init; }
    public IReadOnlyList<double[]> GpsPolyline { get; init; } = Array.Empty<double[]>();
    public bool Degraded { get; init; }
    public string? DegradedReason { get; init; }
}

public sealed class DisputeCaseListResponse
{
    public required IReadOnlyList<DisputeCaseResponse> Items { get; init; }
    public required int Total { get; init; }
}
