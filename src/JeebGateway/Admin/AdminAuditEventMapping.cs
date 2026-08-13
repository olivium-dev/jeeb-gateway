using System.Text.Json;
using JeebGateway.StateService.Ownership;

namespace JeebGateway.Admin;

// gwdbx W1-03/W1-04 — the ONE admin_actions -> /v1/audit-events body mapping.
// A backfill replay that hashes differently from the mirror 409s, so both share this.
public static class AdminAuditEventMapping
{
    // Application scope the state-service credential grants this gateway.
    public const string Application = "jeeb-gateway";

    // admin_actions only ever records admins; upstream actorRole is required.
    public const string ActorRole = "admin";

    // resourceRef is a required 1..500-char token upstream, but entity_id is nullable locally.
    public const string UnknownResourceRef = "-";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // rawEntityId is the pre-INSERT append value: admin_actions.entity_id degrades to NULL
    // for non-GUID ids (dsp_*/case_*), which only the live mirror still holds.
    public static AuditEventAppendRequestV1 ToAppendRequest(AdminAuditEntry row, string? rawEntityId = null)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new AuditEventAppendRequestV1
        {
            Application = Application,
            ActorRef = row.AdminUserId,
            ActorRole = ActorRole,
            Action = row.Action,
            ResourceType = row.EntityType,
            ResourceRef = Coalesce(rawEntityId, row.EntityId) ?? UnknownResourceRef,
            RequestId = row.RequestId,
            Before = ToJson(row.BeforeState),
            After = ToJson(row.AfterState),
            OccurredAt = row.CreatedAt,
        };
    }

    private static string? Coalesce(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) ? (string.IsNullOrWhiteSpace(second) ? null : second) : first;

    private static JsonElement? ToJson(IReadOnlyDictionary<string, object?>? state) =>
        state is null ? null : JsonSerializer.SerializeToElement(state, Json);
}
