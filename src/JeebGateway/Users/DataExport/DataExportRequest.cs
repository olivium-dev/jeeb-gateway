namespace JeebGateway.Users.DataExport;

/// <summary>
/// Lifecycle states for a data-export request (T-backend-042, GDPR-like
/// right of access).
/// </summary>
///   queued — user requested the export, the background processor has not
///     yet picked it up. The 72-hour SLA clock starts here.
///   processing — processor has claimed the row and is gathering the
///     profile/orders/ratings/chat-history payload.
///   ready — payload is available; the download token has been minted and
///     the user has been (or will be) notified. The link is valid until
///     <see cref="DataExportRequest.LinkExpiresAt"/>.
///   delivered — the user has downloaded the export at least once. The
///     row is retained so the audit trail survives, but the bytes are
///     dropped to keep PII out of cold storage.
///   expired — download link's validity window elapsed without a fetch.
///   failed — processor encountered an unrecoverable error; the row is
///     left so an operator can investigate and a fresh request can be
///     opened.
public static class DataExportStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Ready = "ready";
    public const string Delivered = "delivered";
    public const string Expired = "expired";
    public const string Failed = "failed";

    public static readonly IReadOnlySet<string> OpenStates = new HashSet<string>(StringComparer.Ordinal)
    {
        Queued,
        Processing,
        Ready
    };

    public static readonly IReadOnlySet<string> TerminalStates = new HashSet<string>(StringComparer.Ordinal)
    {
        Delivered,
        Expired,
        Failed
    };

    public static bool IsOpen(string status) => OpenStates.Contains(status);
}

/// <summary>
/// Output format the user picked when requesting the export. JSON is the
/// MVP path (the packager emits a single document containing every
/// section). PDF is accepted at the API boundary so the contract matches
/// the AC, but the processor falls back to JSON until the renderer lands.
/// </summary>
public static class DataExportFormat
{
    public const string Json = "json";
    public const string Pdf = "pdf";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Json,
        Pdf
    };
}

public class DataExportRequest
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string Status { get; set; }
    public required string Format { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>
    /// 72-hour SLA deadline. The processor MUST move this row out of the
    /// open states by <c>DueBy</c>; the deadline survives a process restart
    /// because it is stamped at queue time rather than derived from "now".
    /// </summary>
    public required DateTimeOffset DueBy { get; init; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>
    /// Single-use, opaque, URL-safe token the user presents to download
    /// the export. Minted only on transition to <c>ready</c>; null
    /// otherwise so a never-leaked row can't be exfiltrated.
    /// </summary>
    public string? DownloadToken { get; set; }

    /// <summary>
    /// Wall-clock time after which <see cref="DownloadToken"/> is no
    /// longer accepted by the download endpoint. Independent from
    /// <see cref="DueBy"/> — the link's validity is the post-delivery
    /// window, not the SLA. Null until <see cref="ReadyAt"/> is set.
    /// </summary>
    public DateTimeOffset? LinkExpiresAt { get; set; }

    /// <summary>
    /// Packaged payload bytes, retained only while the row is in
    /// <c>ready</c>. Cleared on transition to <c>delivered</c> /
    /// <c>expired</c> so we are not warehousing PII for users who have
    /// already received their export.
    /// </summary>
    public byte[]? Payload { get; set; }

    public string? PayloadContentType { get; set; }
    public long? PayloadSizeBytes { get; set; }
}
