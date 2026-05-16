namespace JeebGateway.Users.DataExport;

/// <summary>
/// Coordinates the data-export lifecycle (T-backend-042). The in-memory
/// implementation is the MVP wiring; production will back this with a
/// Postgres table and a worker that runs <see cref="ClaimNextAsync"/>.
/// </summary>
public interface IDataExportStore
{
    /// <summary>
    /// Idempotent. If the user already has an open export (queued /
    /// processing / ready) the existing row is returned unchanged so a
    /// retry doesn't double-queue. Otherwise a fresh <c>queued</c> row is
    /// created with <c>DueBy = now + SLA</c>.
    /// </summary>
    Task<DataExportRequest> RequestAsync(string userId, string format, CancellationToken ct);

    Task<DataExportRequest?> GetLatestForUserAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Looks up an export by the opaque download token presented at the
    /// download endpoint. Returns null when the token is unknown, the row
    /// is no longer in <c>ready</c>, or the link's validity window has
    /// elapsed — so the controller can return 404 without leaking which
    /// failure mode applied.
    /// </summary>
    Task<DataExportRequest?> GetByDownloadTokenAsync(string token, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Worker entry point: returns the next <c>queued</c> row and marks
    /// it <c>processing</c> in the same step. Returns null when nothing
    /// is queued. Used by <see cref="DataExportProcessor"/>.
    /// </summary>
    Task<DataExportRequest?> ClaimNextAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Moves a processing row to <c>ready</c>, attaches the payload, and
    /// returns the minted download token. Throws when the row is missing
    /// or has already moved to a terminal state.
    /// </summary>
    Task<string> MarkReadyAsync(
        string exportId,
        byte[] payload,
        string contentType,
        DateTimeOffset now,
        TimeSpan linkValidity,
        CancellationToken ct);

    Task MarkFailedAsync(string exportId, string reason, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Records that the user downloaded the export. Clears the payload
    /// so the bytes are not retained beyond delivery. Idempotent — a
    /// second download attempt against the same token returns false and
    /// the controller surfaces 404.
    /// </summary>
    Task<bool> MarkDeliveredAsync(string exportId, DateTimeOffset now, CancellationToken ct);
}
