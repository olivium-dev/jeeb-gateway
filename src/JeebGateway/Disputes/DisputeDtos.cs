namespace JeebGateway.Disputes;

/// <summary>
/// One dispute row (T-backend-025 / JEEB-43). Production wiring will swap
/// the in-memory store for Postgres; the contract here mirrors the future
/// table 1:1 so the controller, service, and tests share a single seam.
/// </summary>
public class Dispute
{
    public required string Id { get; init; }
    public required string DeliveryId { get; init; }

    /// <summary>The user (Client or Jeeber) who filed the dispute.</summary>
    public required string FiledByUserId { get; init; }

    /// <summary>One of <see cref="DisputeCategory.All"/>.</summary>
    public required string Category { get; init; }

    /// <summary>Free-text description supplied by the filer.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Up to 3 evidence-photo URLs persisted via upload-service. The
    /// gateway validates URL shape only; bytes round-trip through the
    /// production <c>UploadServiceClient</c> outside of this row.
    /// </summary>
    public IReadOnlyList<string> PhotoUrls { get; init; } = Array.Empty<string>();

    public required string State { get; set; }
    public required DateTimeOffset FiledAt { get; init; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ResolverAdminId { get; set; }

    /// <summary>
    /// Admin-supplied notes attached on resolve/dismiss so the filer can
    /// see why the case ended where it did.
    /// </summary>
    public string? Resolution { get; set; }
}

/// <summary>
/// POST /deliveries/{id}/dispute request body. Photo URLs must already
/// be persisted via upload-service; the gateway does not accept raw bytes
/// here (the file-upload happens against upload-service directly via the
/// mobile client, mirroring how attachments work elsewhere in the BFF).
/// </summary>
public class FileDisputeRequest
{
    public string? Category { get; set; }
    public string? Description { get; set; }
    public List<string>? PhotoUrls { get; set; }
}

/// <summary>
/// PUT /admin/disputes/{id}/resolve request body. <c>Action</c> is one of
/// "resolve" / "dismiss" / "open" (open transitions filed → under_review
/// for the admin queue).
/// </summary>
public class ResolveDisputeRequest
{
    public string? Action { get; set; }
    public string? Resolution { get; set; }
}

public class DisputeResponse
{
    public required string Id { get; init; }
    public required string DeliveryId { get; init; }
    public required string FiledByUserId { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<string> PhotoUrls { get; init; } = Array.Empty<string>();
    public required string State { get; init; }
    public required DateTimeOffset FiledAt { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public string? ResolverAdminId { get; init; }
    public string? Resolution { get; init; }

    public static DisputeResponse From(Dispute d) => new()
    {
        Id = d.Id,
        DeliveryId = d.DeliveryId,
        FiledByUserId = d.FiledByUserId,
        Category = d.Category,
        Description = d.Description,
        PhotoUrls = d.PhotoUrls.ToList(),
        State = d.State,
        FiledAt = d.FiledAt,
        ReviewedAt = d.ReviewedAt,
        ResolverAdminId = d.ResolverAdminId,
        Resolution = d.Resolution
    };
}

public class DisputeListResponse
{
    public required IReadOnlyList<DisputeResponse> Items { get; init; }
    public required int Total { get; init; }
}
