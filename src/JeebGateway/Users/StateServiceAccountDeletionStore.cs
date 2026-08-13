using System.Text.Json;
using JeebGateway.Migration;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Ownership;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Users;

// gwdbx W3-05 — dual-writes the GDPR account-deletion lifecycle to state-service /v1/work-items
// behind FeatureFlags:AccountDeletionMode. The gateway-local record stays authoritative; a mirror
// failure never fails the user-facing deletion request. Same shape as MirroringDataExportStore.
public sealed class StateServiceAccountDeletionStore : IAccountDeletionStore
{
    // Application scope the state-service credential grants this gateway.
    public const string Application = "jeeb-gateway";

    // G-28: holder-generic vocabulary — a GDPR erasure request, no product nouns.
    public const string WorkKind = "account-deletion";

    private static readonly TimeSpan MirrorBudget = TimeSpan.FromMilliseconds(3000);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IAccountDeletionStore _inner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<GwdbxMigrationOptions> _mode;
    private readonly ILogger<StateServiceAccountDeletionStore> _log;

    public StateServiceAccountDeletionStore(
        IAccountDeletionStore inner,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<GwdbxMigrationOptions> mode,
        ILogger<StateServiceAccountDeletionStore> log)
    {
        _inner = inner;
        _scopeFactory = scopeFactory;
        _mode = mode;
        _log = log;
    }

    // The authoritative gateway-local chain this decorator wraps; asserted by the resolution tests.
    internal IAccountDeletionStore Inner => _inner;

    // G-15 — one open deletion per user upstream, reproducing the one-record-per-user local rule.
    public static string IdempotencyKeyFor(string userId) => "account-deletion:" + userId;

    private bool Mirroring => _mode.CurrentValue.AccountDeletion >= GwdbxMigrationPhase.DualWriteLocalRead;

    public async Task<AccountDeletionRequest> RequestAsync(string userId, bool hasActiveDelivery, CancellationToken ct)
    {
        // AUTHORITATIVE first — the returned record is the gateway-local account_deletions row,
        // which owns the 30-day purge SLA, token revocation and the anonymization steps.
        var record = await _inner.RequestAsync(userId, hasActiveDelivery, ct);

        if (Mirroring)
        {
            await MirrorRequestAsync(record, ct);
        }
        return record;
    }

    // Reads stay local at dual-write-local-read; the upstream read cutover is a later rung.
    public Task<AccountDeletionRequest?> GetAsync(string userId, CancellationToken ct) =>
        _inner.GetAsync(userId, ct);

    // The state machine + purge lives in the inner store. AdvanceAsync yields no per-record
    // result, so the pending->scheduled dueAt refresh mirrors at the read cutover, not here.
    public Task AdvanceAsync(DateTimeOffset now, CancellationToken ct) => _inner.AdvanceAsync(now, ct);

    private async Task MirrorRequestAsync(AccountDeletionRequest record, CancellationToken ct)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(MirrorBudget);
            using var scope = _scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IStateOwnershipClient>();

            // Payload carries NO PII: the pseudonym hash and the lifecycle status only.
            var body = new WorkItemCreateRequestV1
            {
                Application = Application,
                Kind = WorkKind,
                SubjectRef = record.UserId,
                Payload = JsonSerializer.SerializeToElement(
                    new { status = record.Status, anonymizedUserHash = record.AnonymizedUserHash }, Json),
                // The 30-day purge deadline; null while active deliveries hold the clock.
                DueAt = record.ScheduledPurgeAt,
            };
            await client.CreateWorkItemAsync(body, IdempotencyKeyFor(record.UserId), budget.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "account-deletion mirror: create work item failed for {UserId} (key {Key}); the local " +
                "record is durable and the deletion request is unaffected.",
                record.UserId, IdempotencyKeyFor(record.UserId));
        }
    }
}
