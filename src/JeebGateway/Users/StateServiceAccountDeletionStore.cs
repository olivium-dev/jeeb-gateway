using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Jobs;
using JeebGateway.StateService.Work;
using JeebGateway.Tokens;

namespace JeebGateway.Users;

/// <summary>
/// Stateless account-deletion facade. The durable request and its due-work
/// lifecycle live in state-service; the gateway retains no projection.
/// </summary>
public sealed class StateServiceAccountDeletionStore(
    IStateWorkItemClient work,
    ITokenService tokens) : IAccountDeletionStore
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

        if (latest is null)
        {
            var payload = JsonSerializer.SerializeToElement(
                new AccountDeletionWorkPayload(
                    userId,
                    AccountDeletionPolicy.HashUserId(userId),
                    hasActiveDelivery),
                Json);
            try
            {
                latest = await work.CreateAsync(
                    $"account-deletion:{subject}",
                    new StateWorkItemCreate(
                        DurableWorkContract.Application,
                        DurableWorkContract.AccountDeletionKind,
                        subject,
                        payload,
                        DueAt: null,
                        MaxAttempts: 100,
                        RetainPayloadUntil: null),
                    ct);
            }
            catch (StateWorkConflictException)
            {
                latest = await work.GetLatestAsync(
                    DurableWorkContract.Application,
                    DurableWorkContract.AccountDeletionKind,
                    subject,
                    ct);
                if (latest is null)
                    throw;
            }
        }

        // Immediate session invalidation remains request-path security work.
        // The leased executor repeats it idempotently so a crash after durable
        // enqueue but before this call is recovered.
        await tokens.RevokeAllForUserAsync(userId, RevocationReason.AccountDeleted, ct);
        return Map(latest, userId);
    }

    public async Task<AccountDeletionRequest?> GetAsync(string userId, CancellationToken ct)
    {
        var latest = await work.GetLatestAsync(
            DurableWorkContract.Application,
            DurableWorkContract.AccountDeletionKind,
            DurableWorkContract.SubjectForUser(userId),
            ct);
        return latest is null ? null : Map(latest, userId);
    }

    public Task AdvanceAsync(DateTimeOffset now, CancellationToken ct) =>
        throw new NotSupportedException(
            "Account deletion is advanced only by the authenticated external sweep endpoint.");

    private static AccountDeletionRequest Map(StateWorkItem item, string fallbackUserId)
    {
        var payload = DeserializePayload(item);
        var scheduled = string.Equals(
            item.LastError,
            AccountDeletionWorkHandler.PurgeScheduledMarker,
            StringComparison.Ordinal);
        var completed = item.Status == "completed";
        var status = completed
            ? AccountDeletionStatus.Completed
            : item.Status is "failed" or "cancelled"
                ? AccountDeletionStatus.Failed
                : scheduled || payload?.HadActiveDeliveryAtRequest == false
                    ? AccountDeletionStatus.Scheduled
                    : AccountDeletionStatus.PendingActiveDelivery;

        var scheduledAt = scheduled
            ? item.DueAt
            : payload?.HadActiveDeliveryAtRequest == false
                ? item.CreatedAt + AccountDeletionPolicy.PurgeDelay
                : DateTimeOffsetResult(item.Result, "scheduledPurgeAt");

        return new AccountDeletionRequest
        {
            UserId = payload?.UserId ?? fallbackUserId,
            Status = status,
            RequestedAt = item.CreatedAt,
            ScheduledPurgeAt = scheduledAt,
            CompletedAt = completed ? item.CompletedAt : null,
            // Compatibility field for gateway-owned delivery/request records.
            // Wallet-service owns and persists a separate ledger pseudonym.
            AnonymizedUserHash = payload?.EffectiveDeliveryAnonymizedUserHash
                                   ?? AccountDeletionPolicy.HashUserId(fallbackUserId)
        };
    }

    internal static AccountDeletionWorkPayload? DeserializePayload(StateWorkItem item)
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

    private static DateTimeOffset? DateTimeOffsetResult(JsonElement result, string property) =>
        result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty(property, out var value)
        && value.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : null;
}

public sealed record AccountDeletionWorkPayload(
    string UserId,
    string? DeliveryAnonymizedUserHash,
    bool HadActiveDeliveryAtRequest)
{
    // Existing state records used this property name. Read it during the
    // cutover, but never write it for newly-created work.
    [JsonPropertyName("anonymizedUserHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyAnonymizedUserHash { get; init; }

    [JsonIgnore]
    public string? EffectiveDeliveryAnonymizedUserHash =>
        DeliveryAnonymizedUserHash ?? LegacyAnonymizedUserHash;
}
