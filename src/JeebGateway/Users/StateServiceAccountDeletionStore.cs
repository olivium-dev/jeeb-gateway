using System.Text.Json;
using JeebGateway.Migration;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Ownership;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Users;

// gwdbx W3-05 — mirrors the GDPR account-deletion lifecycle to state-service /v1/work-items behind
// FeatureFlags:AccountDeletionMode, and from dual-write-upstream-read up SERVES the read from there.
// Below that rung the gateway-local record stays authoritative and a mirror failure never fails the
// user-facing deletion request. Same shape as MirroringDataExportStore.
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

    /// <summary>
    /// G-15 for the lifecycle. /v1/work-items has no update route the gateway may call without the
    /// claim/lease rail, so each transition is its own item and the READ takes the LATEST one.
    /// </summary>
    public static string TransitionKeyFor(string userId, string status) =>
        "account-deletion:" + userId + ":" + status;

    private bool Mirroring => _mode.CurrentValue.AccountDeletion >= GwdbxMigrationPhase.DualWriteLocalRead;

    private bool UpstreamReads =>
        GwdbxMigrationOptions.RequiresUpstream(_mode.CurrentValue.AccountDeletion);

    public async Task<AccountDeletionRequest> RequestAsync(string userId, bool hasActiveDelivery, CancellationToken ct)
    {
        // AUTHORITATIVE first — the returned record is the gateway-local account_deletions row,
        // which owns the 30-day purge SLA, token revocation and the anonymization steps.
        var record = await _inner.RequestAsync(userId, hasActiveDelivery, ct);

        if (Mirroring)
        {
            // From the read rung up the caller is handed a record it will next read OUT of upstream,
            // so a swallowed mirror failure would make the deletion vanish on the very next GET.
            await MirrorAsync(record, IdempotencyKeyFor(record.UserId), rethrow: UpstreamReads, ct);
        }
        return record;
    }

    public async Task<AccountDeletionRequest?> GetAsync(string userId, CancellationToken ct)
    {
        if (!UpstreamReads)
        {
            return await _inner.GetAsync(userId, ct);
        }

        // FAIL CLOSED. The local chain ends in an in-memory store that empties on every bounce, so
        // answering a GDPR status read from it would report "no deletion requested" during an outage.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(MirrorBudget);
        using var scope = _scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IStateOwnershipClient>();

        var latest = await client.GetLatestWorkItemAsync(Application, WorkKind, userId, budget.Token);
        return latest is null ? null : Map(userId, latest);
    }

    // The state machine + purge lives in the inner store; the purge worker drives it. Every row it
    // moved is mirrored as its own transition item so the upstream read cannot freeze at creation.
    public async Task<IReadOnlyList<AccountDeletionRequest>> AdvanceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var advanced = await _inner.AdvanceAsync(now, ct);
        if (!Mirroring || advanced.Count == 0)
        {
            return advanced;
        }

        foreach (var record in advanced)
        {
            await MirrorAsync(record, TransitionKeyFor(record.UserId, record.Status), rethrow: false, ct);
        }
        return advanced;
    }

    private async Task MirrorAsync(
        AccountDeletionRequest record, string idempotencyKey, bool rethrow, CancellationToken ct)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(MirrorBudget);
            using var scope = _scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IStateOwnershipClient>();

            // Payload carries NO PII: the pseudonym hash and the lifecycle stamps only.
            var body = new WorkItemCreateRequestV1
            {
                Application = Application,
                Kind = WorkKind,
                SubjectRef = record.UserId,
                Payload = JsonSerializer.SerializeToElement(
                    new DeletionPayload
                    {
                        Status = record.Status,
                        RequestedAt = record.RequestedAt,
                        ScheduledPurgeAt = record.ScheduledPurgeAt,
                        CompletedAt = record.CompletedAt,
                        AnonymizedUserHash = record.AnonymizedUserHash,
                    },
                    Json),
                // The 30-day purge deadline; null while active deliveries hold the clock.
                DueAt = record.ScheduledPurgeAt,
            };
            await client.CreateWorkItemAsync(body, idempotencyKey, budget.Token);
        }
        catch (Exception ex)
        {
            if (rethrow) throw;

            _log.LogWarning(ex,
                "account-deletion mirror: create work item failed for {UserId} (key {Key}); the local " +
                "record is durable and the deletion request is unaffected.",
                record.UserId, idempotencyKey);
        }
    }

    // Upstream is the record of truth at this rung, so every field comes from the work item. The
    // payload is the gateway's own shape (written by MirrorAsync), never a foreign schema.
    private static AccountDeletionRequest Map(string userId, WorkItemRecordV1 item)
    {
        DeletionPayload? payload = null;
        try
        {
            payload = item.Payload.ValueKind == JsonValueKind.Object
                ? item.Payload.Deserialize<DeletionPayload>(Json)
                : null;
        }
        catch (JsonException)
        {
            // A payload the gateway cannot read must not masquerade as "no deletion requested".
            payload = null;
        }

        return new AccountDeletionRequest
        {
            UserId = userId,
            Status = payload?.Status ?? AccountDeletionStatus.PendingActiveDelivery,
            RequestedAt = payload?.RequestedAt ?? item.CreatedAt,
            ScheduledPurgeAt = payload?.ScheduledPurgeAt,
            CompletedAt = payload?.CompletedAt,
            AnonymizedUserHash = payload?.AnonymizedUserHash ?? AccountDeletionPolicy.HashUserId(userId),
        };
    }

    private sealed class DeletionPayload
    {
        public string Status { get; set; } = AccountDeletionStatus.PendingActiveDelivery;
        public DateTimeOffset RequestedAt { get; set; }
        public DateTimeOffset? ScheduledPurgeAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string AnonymizedUserHash { get; set; } = string.Empty;
    }
}
