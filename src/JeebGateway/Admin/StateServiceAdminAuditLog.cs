using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JeebGateway.StateService.Audit;

namespace JeebGateway.Admin;

/// <summary>
/// Stateless product adapter over jeeb-state-service's generic append-only
/// audit stream. The gateway supplies Jeeb action/resource vocabulary but owns
/// no audit table, queue, or replay state.
/// </summary>
public sealed class StateServiceAdminAuditLog(IStateAuditClient state) : IAdminAuditLog
{
    private const string Application = "jeeb-gateway";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AdminAuditEntry> AppendAsync(AdminAuditAppend entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var resourceRef = string.IsNullOrWhiteSpace(entry.EntityId) ? "unscoped" : entry.EntityId.Trim();
        var request = new StateAuditAppend(
            Application,
            entry.AdminUserId,
            "admin",
            entry.Action,
            entry.EntityType,
            resourceRef,
            entry.RequestId,
            ToElement(entry.BeforeState),
            ToElement(entry.AfterState),
            null,
            null);
        var row = await state.AppendAsync(IdempotencyKey(entry), request, ct);
        return Map(row, string.IsNullOrWhiteSpace(entry.EntityId) ? null : row.ResourceRef);
    }

    public async Task<IReadOnlyList<AdminAuditEntry>> ListForEntityAsync(
        string entityType,
        string entityId,
        CancellationToken ct)
    {
        var rows = new List<AdminAuditEntry>();
        string? cursor = null;
        do
        {
            var page = await state.FindAsync(new StateAuditQuery(
                Application,
                null,
                null,
                entityType,
                entityId,
                200,
                cursor), ct);
            rows.AddRange(page.Items.Select(item => Map(item, item.ResourceRef)));
            cursor = page.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return rows.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    private static string IdempotencyKey(AdminAuditAppend entry)
    {
        if (string.IsNullOrWhiteSpace(entry.RequestId))
            return $"jeeb:admin-audit:{Guid.NewGuid():N}";

        // One inbound request/action/resource tuple names one immutable event.
        // The owner binds that identity to the complete body and returns 409 if
        // a caller ever reuses it with different before/after data.
        var canonical = JsonSerializer.Serialize(new
        {
            entry.RequestId,
            entry.AdminUserId,
            entry.Action,
            entry.EntityType,
            entry.EntityId
        }, Json);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return "jeeb:admin-audit:" + digest;
    }

    private static JsonElement? ToElement(IReadOnlyDictionary<string, object?>? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value, Json);

    private static IReadOnlyDictionary<string, object?>? ToDictionary(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(value.GetRawText(), Json);
    }

    private static AdminAuditEntry Map(StateAuditEvent row, string? entityId) => new()
    {
        Id = row.EventId.ToString("D"),
        AdminUserId = row.ActorRef,
        Action = row.Action,
        EntityType = row.ResourceType,
        EntityId = entityId,
        BeforeState = ToDictionary(row.Before),
        AfterState = ToDictionary(row.After),
        RequestId = row.RequestId,
        CreatedAt = row.CreatedAt
    };
}
