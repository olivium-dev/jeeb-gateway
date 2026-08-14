using System.Text.Json;
using JeebGateway.StateService.Ownership;

namespace JeebGateway.Users;

/// <summary>
/// gwdbx W3-07 PREP — backfill contract for relaying pre-mirror <c>account_deletions</c> rows
/// into state-service work-items. Pure (no Npgsql, no HTTP); the runner is tools/AccountDeletionRelay.
/// </summary>
public static class AccountDeletionRelayPlan
{
    // Only OPEN rows travel. Completed rows are archive-only: their purge already ran, so an
    // upstream item would be born dead.
    public static readonly IReadOnlyList<string> RelayStatuses = new[]
    {
        AccountDeletionStatus.PendingActiveDelivery,
        AccountDeletionStatus.Scheduled,
    };

    // Narrow column list: completed_at / side-effect bookkeeping never travel; the hash is the
    // stable pseudonym the live mirror already sends, not PII.
    public const string SelectOpenSql = """
        SELECT user_id, status, anonymized_user_hash, scheduled_purge_at
        FROM account_deletions
        WHERE status::text = ANY(@RelayStatuses)
        ORDER BY requested_at ASC
        """;

    public const string RelayStatusesParameter = "RelayStatuses";

    /// <summary>False means "leave the row in the archive dump".</summary>
    public static bool ShouldRelay(string status) =>
        status == AccountDeletionStatus.PendingActiveDelivery
        || status == AccountDeletionStatus.Scheduled;

    // Byte-exact replay of the key StateServiceAccountDeletionStore would have used, so a row
    // that later mirrors itself collides with its own backfill instead of duplicating.
    public static string IdempotencyKeyFor(RelayRow row) =>
        StateServiceAccountDeletionStore.IdempotencyKeyFor(row.UserId);

    /// <summary>Same body as the live mirror leg — a replay of the call that never happened.</summary>
    public static WorkItemCreateRequestV1 BuildWorkItem(RelayRow row)
    {
        if (!ShouldRelay(row.Status))
        {
            throw new InvalidOperationException(
                $"account-deletion relay refused a terminal row: user {row.UserId} is '{row.Status}'.");
        }

        return new WorkItemCreateRequestV1
        {
            Application = StateServiceAccountDeletionStore.Application,
            Kind = StateServiceAccountDeletionStore.WorkKind,
            SubjectRef = row.UserId,
            // Property names MUST stay byte-identical to the mirror's anonymous payload.
            Payload = JsonSerializer.SerializeToElement(
                new { status = row.Status, anonymizedUserHash = row.AnonymizedUserHash },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            DueAt = row.ScheduledPurgeAt,
        };
    }

    /// <summary>One open account_deletions row, as read by the runner's narrow SELECT.</summary>
    public sealed record RelayRow(
        string UserId, string Status, string AnonymizedUserHash, DateTimeOffset? ScheduledPurgeAt);
}
