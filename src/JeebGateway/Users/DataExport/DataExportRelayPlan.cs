using System.Text.Json;
using JeebGateway.StateService.Ownership;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// gwdbx W1-07 — backfill contract for relaying pre-mirror <c>data_exports</c> rows into
/// state-service work-items. Pure (no Npgsql, no HTTP); the runner is tools/DataExportRelay.
/// </summary>
public static class DataExportRelayPlan
{
    // Only OPEN rows travel. Terminal rows are archive-only: their upstream item would be born
    // dead and their bytes are PII.
    public static readonly IReadOnlyList<string> RelayStatuses = new[]
    {
        DataExportStatus.Queued,
        DataExportStatus.Processing,
        DataExportStatus.Ready,
    };

    // G-20 — explicit narrow column list: payload, download_token and failure_reason are never
    // read, so PII cannot reach a work item, a log or an argv.
    public const string SelectOpenSql = """
        SELECT id, user_id, status, format, due_by
        FROM data_exports
        WHERE status = ANY(@RelayStatuses)
        ORDER BY due_by ASC
        """;

    public const string RelayStatusesParameter = "RelayStatuses";

    /// <summary>False means "leave the row in the archive dump".</summary>
    public static bool ShouldRelay(string status) => DataExportStatus.IsOpen(status);

    // Byte-exact replay of the key MirroringDataExportStore would have used, so a row that later
    // mirrors itself collides with its own backfill instead of duplicating.
    public static string IdempotencyKeyFor(RelayRow row) =>
        MirroringDataExportStore.IdempotencyKeyFor(row.UserId);

    /// <summary>
    /// Same body as the live mirror leg — the relay replays a call that never happened rather
    /// than acting as a second, divergent producer.
    /// </summary>
    public static WorkItemCreateRequestV1 BuildWorkItem(RelayRow row)
    {
        if (!ShouldRelay(row.Status))
        {
            throw new InvalidOperationException(
                $"data-export relay refused a terminal row: export {row.ExportId} is '{row.Status}'.");
        }

        return new WorkItemCreateRequestV1
        {
            Application = MirroringDataExportStore.Application,
            Kind = MirroringDataExportStore.WorkKind,
            SubjectRef = row.UserId,
            Payload = JsonSerializer.SerializeToElement(
                new { exportId = row.ExportId, format = row.Format },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            DueAt = row.DueBy,
        };
    }

    // uq_data_exports_user_open allows at most one open row per user. Two would mean the index is
    // gone, and the shared key would silently relay only one of them.
    public static void AssertOneOpenRowPerUser(IReadOnlyCollection<RelayRow> rows)
    {
        var clash = rows
            .GroupBy(r => r.UserId, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (clash is not null)
        {
            throw new InvalidOperationException(
                $"data-export relay refused to run: user {clash.Key} has {clash.Count()} open rows, "
                + "so uq_data_exports_user_open is not holding and one row would be dropped.");
        }
    }

    /// <summary>A relayable row: no payload, no token and no failure text, by design.</summary>
    public sealed record RelayRow(
        string ExportId, string UserId, string Status, string Format, DateTimeOffset DueBy);
}
