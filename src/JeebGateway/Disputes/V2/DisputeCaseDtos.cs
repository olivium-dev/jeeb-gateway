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
    public required string OpenedByUserId { get; init; }
    public string? CounterpartyUserId { get; init; }
    public required string Reason { get; init; }
    public string? Comment { get; init; }
    public IReadOnlyList<string> PhotoUrls { get; init; } = Array.Empty<string>();
    public required string State { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
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
        State = c.State,
        OpenedAt = c.OpenedAt,
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
