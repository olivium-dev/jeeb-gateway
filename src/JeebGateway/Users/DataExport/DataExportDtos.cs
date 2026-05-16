namespace JeebGateway.Users.DataExport;

/// <summary>
/// Body for POST /users/me/data-export. Both fields are optional; the
/// default format is JSON. The format is persisted on the request row
/// so a future PDF backfill can re-render without the user re-requesting.
/// </summary>
public class DataExportRequestBody
{
    public string? Format { get; set; }
}

public class DataExportResponse
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string Status { get; init; }
    public required string Format { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public required DateTimeOffset DueBy { get; init; }
    public DateTimeOffset? ReadyAt { get; init; }
    public DateTimeOffset? LinkExpiresAt { get; init; }

    /// <summary>
    /// Relative download URL the client should fetch once <see cref="Status"/>
    /// is <c>ready</c>. Null otherwise. The token in the URL is the
    /// only authentication needed — see the download endpoint contract.
    /// </summary>
    public string? DownloadUrl { get; init; }

    public long? PayloadSizeBytes { get; init; }
}
