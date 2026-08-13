using System.Text.Json;
using JeebGateway.Migration;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Ownership;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Admin;

// gwdbx W1-03 — dual-writes the admin trail to state-service /v1/audit-events behind FeatureFlags:AdminAuditMode.
// Local admin_actions stays authoritative; Idempotency-Key = admin_actions.id (G-15); mirror failure logs, never throws (A11).
public sealed class MirroringAdminAuditLog : IAdminAuditLog
{
    // Application scope the state-service credential grants this gateway.
    public const string Application = "jeeb-gateway";

    // admin_actions only ever records admins; upstream actorRole is required.
    private const string ActorRole = "admin";

    // resourceRef is a required 1..500-char token upstream, but entity_id is nullable locally.
    private const string UnknownResourceRef = "-";

    // Bounded retry on top of the client's own Polly pipeline, then give up to the W1-04 backfill.
    private const int MirrorAttempts = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MirrorBudget = TimeSpan.FromMilliseconds(3000);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IAdminAuditLog _inner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<GwdbxMigrationOptions> _mode;
    private readonly ILogger<MirroringAdminAuditLog> _log;

    public MirroringAdminAuditLog(
        IAdminAuditLog inner,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<GwdbxMigrationOptions> mode,
        ILogger<MirroringAdminAuditLog> log)
    {
        _inner = inner;
        _scopeFactory = scopeFactory;
        _mode = mode;
        _log = log;
    }

    public async Task<AdminAuditEntry> AppendAsync(AdminAuditAppend entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // AUTHORITATIVE first — the returned row is the admin_actions row.
        var row = await _inner.AppendAsync(entry, ct);

        if (_mode.CurrentValue.AdminAudit >= GwdbxMigrationPhase.DualWriteLocalRead)
        {
            await MirrorAsync(entry, row, ct);
        }

        return row;
    }

    // Local read at dual-write-local-read; the upstream read cutover is W1-05.
    public Task<IReadOnlyList<AdminAuditEntry>> ListForEntityAsync(
        string entityType, string entityId, CancellationToken ct) =>
        _inner.ListForEntityAsync(entityType, entityId, ct);

    // Best-effort upstream POST; never throws for a mirror failure.
    private async Task MirrorAsync(AdminAuditAppend entry, AdminAuditEntry row, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(MirrorBudget);

        var body = new AuditEventAppendRequestV1
        {
            Application = Application,
            ActorRef = row.AdminUserId,
            ActorRole = ActorRole,
            Action = row.Action,
            ResourceType = row.EntityType,
            // Prefer the raw append id: admin_actions.entity_id degrades to NULL for
            // non-GUID ids (dsp_*/case_*), and the mirror must not lose them.
            ResourceRef = Coalesce(entry.EntityId, row.EntityId) ?? UnknownResourceRef,
            RequestId = row.RequestId,
            Before = ToJson(row.BeforeState),
            After = ToJson(row.AfterState),
            OccurredAt = row.CreatedAt,
        };

        // G-15 — the idempotency key IS admin_actions.id.
        var idempotencyKey = row.Id;

        for (var attempt = 1; attempt <= MirrorAttempts; attempt++)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var client = scope.ServiceProvider.GetRequiredService<IStateOwnershipClient>();
                await client.AppendAuditEventAsync(body, idempotencyKey, budget.Token);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The CALLER aborted, not our budget: the local row is committed, W1-04 replays.
                return;
            }
            catch (Exception ex)
            {
                if (attempt < MirrorAttempts && !budget.IsCancellationRequested)
                {
                    _log.LogDebug(ex,
                        "admin-audit mirror attempt {Attempt}/{Attempts} failed for admin_actions.id={Id}; retrying.",
                        attempt, MirrorAttempts, idempotencyKey);
                    try
                    {
                        await Task.Delay(RetryDelay, budget.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Budget or caller expired mid-backoff — fall through to the WARN.
                    }

                    continue;
                }

                _log.LogWarning(ex,
                    "admin-audit mirror to state-service /v1/audit-events gave up after {Attempts} attempt(s) " +
                    "for admin_actions.id={Id} action={Action} entityType={EntityType}; the local row is durable " +
                    "and the W1-04 backfill relays the same idempotency key.",
                    attempt, idempotencyKey, row.Action, row.EntityType);
                return;
            }
        }
    }

    private static string? Coalesce(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) ? (string.IsNullOrWhiteSpace(second) ? null : second) : first;

    private static JsonElement? ToJson(IReadOnlyDictionary<string, object?>? state) =>
        state is null ? null : JsonSerializer.SerializeToElement(state, Json);
}
