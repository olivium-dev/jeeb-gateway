using System.Text.Json;
using JeebGateway.Jobs;
using JeebGateway.StateService.Work;

namespace JeebGateway.Users;

public interface IAccountDeletionWorkflow
{
    Task<AccountDeletionRequest> RequestAsync(string userId, bool hasActiveDelivery, CancellationToken ct);

    Task<AccountDeletionRequest?> GetLatestForUserAsync(string userId, CancellationToken ct);
}

/// <summary>
/// Stateless account-deletion facade. The erasure request, its 30-day purge deadline and its
/// retry budget live in state-service work items; the gateway keeps nothing in process memory.
/// </summary>
public sealed class StateAccountDeletionWorkflow(
    IStateWorkItemClient work) : IAccountDeletionWorkflow
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AccountDeletionRequest> RequestAsync(
        string userId,
        bool hasActiveDelivery,
        CancellationToken ct)
    {
        var subject = DurableWorkContract.SubjectForUser(userId);
        var latest = await work.GetLatestAsync(
            DurableWorkContract.Application,
            DurableWorkContract.AccountDeletionKind,
            subject,
            ct);
        if (latest is not null && IsOpen(latest))
            return Map(latest, userId);

        // One open erasure per user: a terminal predecessor opens a new key, an open one is reused.
        var predecessor = latest is null ? "initial" : $"after:{latest.WorkItemId:N}";
        var idempotencyKey = $"account-deletion:{subject}:{predecessor}";
        var payload = JsonSerializer.SerializeToElement(
            new AccountDeletionWorkPayload(
                userId,
                AccountDeletionPolicy.HashUserId(userId),
                hasActiveDelivery),
            Json);

        try
        {
            var created = await work.CreateAsync(
                idempotencyKey,
                new StateWorkItemCreate(
                    DurableWorkContract.Application,
                    DurableWorkContract.AccountDeletionKind,
                    subject,
                    payload,
                    // Due immediately: the first sweep revokes tokens and anonymizes, then defers
                    // the item to the purge deadline, which is where the 30-day clock is held.
                    DueAt: null,
                    MaxAttempts: 10,
                    RetainPayloadUntil: null),
                ct);
            return Map(created, userId);
        }
        catch (StateWorkConflictException)
        {
            // A concurrent caller won the same predecessor key; return the winning open request.
            latest = await work.GetLatestAsync(
                DurableWorkContract.Application,
                DurableWorkContract.AccountDeletionKind,
                subject,
                ct);
            if (latest is not null && IsOpen(latest))
                return Map(latest, userId);
            throw;
        }
    }

    public async Task<AccountDeletionRequest?> GetLatestForUserAsync(string userId, CancellationToken ct)
    {
        var latest = await work.GetLatestAsync(
            DurableWorkContract.Application,
            DurableWorkContract.AccountDeletionKind,
            DurableWorkContract.SubjectForUser(userId),
            ct);
        return latest is null ? null : Map(latest, userId);
    }

    private AccountDeletionRequest Map(StateWorkItem item, string fallbackUserId)
    {
        var payload = DeserializePayload(item);
        var userId = payload?.UserId ?? fallbackUserId;
        var hadActiveDelivery = payload?.HadActiveDeliveryAtRequest ?? false;

        // The handler records which phase it reached in last_error, and holds the purge deadline
        // in due_at. Both survive a gateway restart because state-service owns them.
        var waiting = string.Equals(
            item.LastError, AccountDeletionWorkHandler.WaitingForDeliveryMarker, StringComparison.Ordinal);
        var purgeScheduled = string.Equals(
            item.LastError, AccountDeletionWorkHandler.PurgeScheduledMarker, StringComparison.Ordinal);

        var status = item.Status switch
        {
            "completed" => AccountDeletionStatus.Completed,
            "failed" or "cancelled" => AccountDeletionStatus.Failed,
            _ when waiting => AccountDeletionStatus.PendingActiveDelivery,
            _ when purgeScheduled => AccountDeletionStatus.Scheduled,
            // Not executed yet: the request itself decides whether the clock has started.
            _ => hadActiveDelivery
                ? AccountDeletionStatus.PendingActiveDelivery
                : AccountDeletionStatus.Scheduled,
        };

        DateTimeOffset? scheduledPurgeAt = status switch
        {
            AccountDeletionStatus.PendingActiveDelivery => null,
            AccountDeletionStatus.Scheduled => purgeScheduled
                ? item.DueAt
                : item.CreatedAt + AccountDeletionPolicy.PurgeDelay,
            AccountDeletionStatus.Completed => item.DueAt,
            _ => null,
        };

        return new AccountDeletionRequest
        {
            UserId = userId,
            Status = status,
            RequestedAt = item.CreatedAt,
            ScheduledPurgeAt = scheduledPurgeAt,
            CompletedAt = item.Status == "completed" ? item.CompletedAt : null,
            AnonymizedUserHash = payload?.EffectiveDeliveryAnonymizedUserHash
                                 ?? AccountDeletionPolicy.HashUserId(userId),
        };
    }

    // queued/leased are the open rungs; everything else is terminal and opens a fresh request.
    private static bool IsOpen(StateWorkItem item) => item.Status is "queued" or "leased";

    private static AccountDeletionWorkPayload? DeserializePayload(StateWorkItem item)
    {
        try
        {
            return item.Payload.ValueKind == JsonValueKind.Object
                ? item.Payload.Deserialize<AccountDeletionWorkPayload>(Json)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
