using System.Collections.Concurrent;
using System.Text.Json;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Ownership;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Services.Dispatch;

// W1-09 — the notification outbox re-homed onto jeeb-state-service /v1/work-items
// (kind=notification-dispatch). Drain-and-switch: no dual-write, no shadow-compare.
public sealed class StateServiceNotificationDispatchOutbox : INotificationDispatchOutbox
{
    public const string Application = "jeeb-gateway";
    public const string WorkItemKind = "notification-dispatch";

    // G-15: THE idempotency key for this domain. The notification's own key when it has one,
    // else "notification-dispatch:<entryId>" — it is subjectRef AND the create Idempotency-Key.
    public const string SyntheticKeyPrefix = WorkItemKind + ":";

    private const int WorkItemMaxAttempts = 3;
    private const int ClaimLeaseSeconds = 120;
    private const int ClaimLimit = 50;

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    // Lease bookkeeping for claimed items: complete/fail need the lease token + version CAS.
    private readonly ConcurrentDictionary<Guid, ClaimedLease> _leases = new();
    private readonly IStateOwnershipClient _state;
    private readonly ILogger<StateServiceNotificationDispatchOutbox> _log;
    private readonly string _workerId = "jeeb-gateway-" + Environment.MachineName;

    public StateServiceNotificationDispatchOutbox(
        IStateOwnershipClient state,
        ILogger<StateServiceNotificationDispatchOutbox> log)
    {
        _state = state;
        _log = log;
    }

    /// <summary>The state-service subjectRef for an entry: its idempotency key, entry id when null.</summary>
    public static string SubjectRefFor(NotificationDispatchEntry entry) =>
        string.IsNullOrWhiteSpace(entry.IdempotencyKey)
            ? entry.Id.ToString("D")
            : entry.IdempotencyKey!;

    /// <summary>The Idempotency-Key sent to POST /v1/work-items — kind-scoped so it cannot
    /// collide with another domain's key on the shared ownership surface.</summary>
    public static string WorkItemIdempotencyKeyFor(NotificationDispatchEntry entry) =>
        SyntheticKeyPrefix + SubjectRefFor(entry);

    public async Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct = default)
    {
        var existing = await _state.GetLatestWorkItemAsync(Application, WorkItemKind, idempotencyKey, ct);
        return existing is not null;
    }

    public async Task<NotificationDispatchEntry> AddAsync(
        NotificationDispatchEntry entry, CancellationToken ct = default)
    {
        var body = new WorkItemCreateRequestV1
        {
            Application = Application,
            Kind = WorkItemKind,
            SubjectRef = SubjectRefFor(entry),
            Payload = JsonSerializer.SerializeToElement(OutboxPayloadV1.From(entry), PayloadJson),
            DueAt = entry.NextAttemptAt,
            MaxAttempts = WorkItemMaxAttempts,
        };
        var record = await _state.CreateWorkItemAsync(body, WorkItemIdempotencyKeyFor(entry), ct);
        _log.LogInformation(
            "Notification dispatch enqueued as work item. EntryId={EntryId} WorkItemId={WorkItemId} Kind={Kind}",
            entry.Id, record.WorkItemId, WorkItemKind);
        return entry;
    }

    public async Task<IReadOnlyList<NotificationDispatchEntry>> GetDueAsync(
        DateTimeOffset now, CancellationToken ct = default)
    {
        var claimed = await _state.ClaimWorkItemsAsync(
            new WorkClaimRequestV1
            {
                Application = Application,
                Kinds = new[] { WorkItemKind },
                WorkerId = _workerId,
                LeaseSeconds = ClaimLeaseSeconds,
                Limit = ClaimLimit,
            },
            ct);

        var entries = new List<NotificationDispatchEntry>(claimed.Count);
        foreach (var record in claimed)
        {
            var entry = ToEntry(record);
            if (entry is null) continue;
            if (record.LeaseToken is { } lease)
                _leases[entry.Id] = new ClaimedLease(record.WorkItemId, lease, record.Version);
            entries.Add(entry);
        }
        return entries;
    }

    public async Task MarkDeliveredAsync(Guid entryId, CancellationToken ct = default)
    {
        if (!_leases.TryRemove(entryId, out var lease))
        {
            LogNoLease(entryId, "complete");
            return;
        }
        await _state.CompleteWorkItemAsync(
            lease.WorkItemId,
            new WorkCompleteRequestV1 { LeaseToken = lease.LeaseToken, ExpectedVersion = lease.Version },
            ct);
    }

    public async Task RecordFailureAsync(
        Guid entryId, string error, int maxAttempts, TimeSpan retryDelay, CancellationToken ct = default)
    {
        if (!_leases.TryRemove(entryId, out var lease))
        {
            LogNoLease(entryId, "fail");
            return;
        }
        // maxAttempts is fixed at create time, so state-service owns the DLQ transition here.
        await _state.FailWorkItemAsync(
            lease.WorkItemId,
            new WorkFailRequestV1
            {
                LeaseToken = lease.LeaseToken,
                ExpectedVersion = lease.Version,
                Error = error,
                // A non-future retryAt is a 400; null tells state-service to fail the item now,
                // which is what a zero retry delay (fire-once nudge) means.
                RetryAt = retryDelay > TimeSpan.Zero ? DateTimeOffset.UtcNow.Add(retryDelay) : null,
            },
            ct);
    }

    public Task<IReadOnlyList<NotificationDispatchEntry>> GetDlqAsync(CancellationToken ct = default)
    {
        // The ownership surface exposes no dead-letter listing and this program adds zero
        // new state-service endpoints, so the diagnostics view is empty in this mode.
        _log.LogWarning(
            "Notification DLQ listing is unavailable while the outbox is served by state-service work items.");
        return Task.FromResult<IReadOnlyList<NotificationDispatchEntry>>(Array.Empty<NotificationDispatchEntry>());
    }

    // Diagnostics-only on the interface; there is no synchronous upstream count to serve it.
    public int PendingCount => 0;

    // W1-10 BLOCKER: complete/fail need a claim lease, but the in-process dispatchers enqueue and
    // push inline without ever claiming, so this branch is the ONLY one they reach.
    private void LogNoLease(Guid entryId, string operation) =>
        _log.LogWarning(
            "No state-service lease held for notification entry {EntryId}; skipping {Operation}. "
            + "The work item stays queued and claimable — a claimer would re-send it. "
            + "W1-10 must not flip NotificationOutboxMode until the claim side owns dispatch.",
            entryId, operation);

    private NotificationDispatchEntry? ToEntry(WorkItemRecordV1 record)
    {
        try
        {
            var payload = record.Payload.Deserialize<OutboxPayloadV1>(PayloadJson);
            return payload?.ToEntry(record);
        }
        catch (JsonException ex)
        {
            _log.LogError(ex,
                "Unreadable notification-dispatch work-item payload. WorkItemId={WorkItemId}", record.WorkItemId);
            return null;
        }
    }

    private readonly record struct ClaimedLease(Guid WorkItemId, Guid LeaseToken, int Version);

    // The dispatch job as it travels through the work-item payload. IdempotencyKey rides
    // end-to-end (§4.1 rider) so the claimer can hand it to push-notification PushDispatch.
    internal sealed record OutboxPayloadV1(
        Guid Id,
        string TemplateKey,
        string Locale,
        Dictionary<string, string> Parameters,
        Guid RecipientUserId,
        string? IdempotencyKey,
        DateTimeOffset CreatedAt)
    {
        public static OutboxPayloadV1 From(NotificationDispatchEntry entry) => new(
            entry.Id, entry.TemplateKey, entry.Locale, entry.Parameters,
            entry.RecipientUserId, entry.IdempotencyKey, entry.CreatedAt);

        public NotificationDispatchEntry ToEntry(WorkItemRecordV1 record) => new()
        {
            Id = Id,
            TemplateKey = TemplateKey,
            Locale = Locale,
            Parameters = Parameters ?? new Dictionary<string, string>(),
            RecipientUserId = RecipientUserId,
            IdempotencyKey = IdempotencyKey,
            CreatedAt = CreatedAt,
            AttemptCount = record.Attempts,
            NextAttemptAt = record.DueAt,
            LastError = record.LastError,
        };
    }
}
