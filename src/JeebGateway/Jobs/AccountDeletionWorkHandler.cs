using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Requests;
using JeebGateway.StateService.Work;
using JeebGateway.Tokens;
using JeebGateway.Users;

namespace JeebGateway.Jobs;

public sealed class AccountDeletionExecutionOptions
{
    public const string SectionName = "AccountDeletionExecution";
    public TimeSpan ActiveDeliveryRetryInterval { get; init; } = TimeSpan.FromHours(6);
}

public sealed class AccountDeletionWorkHandler(
    IUsersStore users,
    IRequestsStore requests,
    ITokenService tokens,
    IFinancialLedgerAnonymizer ledger,
    TimeProvider clock,
    Microsoft.Extensions.Options.IOptions<AccountDeletionExecutionOptions> options)
    : IDurableWorkItemHandler
{
    public const string WaitingForDeliveryMarker = "account-deletion:waiting-active-delivery";
    public const string PurgeScheduledMarker = "account-deletion:purge-scheduled";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Kind => DurableWorkContract.AccountDeletionKind;

    public async Task<DurableWorkExecutionResult> ExecuteAsync(
        StateWorkItem item,
        CancellationToken ct)
    {
        var payload = DeserializePayload(item);
        var deliveryHash = payload?.EffectiveDeliveryAnonymizedUserHash;
        if (payload is null
            || string.IsNullOrWhiteSpace(payload.UserId)
            || string.IsNullOrWhiteSpace(deliveryHash))
            return DurableWorkExecutionResult.Failed("account-deletion payload is invalid");

        // Crash-safe request-path recovery. Every operation below is required to
        // be idempotent at its owning service because a lease may expire after a
        // side effect but before the terminal CAS is recorded.
        await tokens.RevokeAllForUserAsync(
            payload.UserId,
            RevocationReason.AccountDeleted,
            ct);

        if (await requests.CountActiveForClientAsync(payload.UserId, ct) > 0)
        {
            var delay = options.Value.ActiveDeliveryRetryInterval > TimeSpan.Zero
                ? options.Value.ActiveDeliveryRetryInterval
                : TimeSpan.FromHours(6);
            return DurableWorkExecutionResult.Deferred(
                WaitingForDeliveryMarker,
                clock.GetUtcNow() + delay);
        }

        var anonymizedRequests = await requests.AnonymizeForClientAsync(
            payload.UserId,
            deliveryHash,
            ct);
        try
        {
            _ = await ledger.AnonymizeForUserAsync(
                payload.UserId,
                string.Empty,
                ct);
        }
        catch (WalletLedgerCloseConflictException)
        {
            var delay = options.Value.ActiveDeliveryRetryInterval > TimeSpan.Zero
                ? options.Value.ActiveDeliveryRetryInterval
                : TimeSpan.FromHours(6);
            return DurableWorkExecutionResult.Deferred(
                "account-deletion:waiting-wallet-closeout",
                clock.GetUtcNow() + delay);
        }

        var now = clock.GetUtcNow();
        var alreadyScheduled = string.Equals(
            item.LastError,
            PurgeScheduledMarker,
            StringComparison.Ordinal);
        var purgeAt = alreadyScheduled
            ? item.DueAt
            : payload.HadActiveDeliveryAtRequest
                ? now + AccountDeletionPolicy.PurgeDelay
                : item.CreatedAt + AccountDeletionPolicy.PurgeDelay;

        if (purgeAt > now)
            return DurableWorkExecutionResult.Deferred(PurgeScheduledMarker, purgeAt);

        await users.PurgePiiAsync(payload.UserId, ct);
        var result = JsonSerializer.SerializeToElement(new
        {
            status = AccountDeletionStatus.Completed,
            scheduledPurgeAt = purgeAt,
            completedAt = clock.GetUtcNow(),
            anonymizedRequests
        });
        return DurableWorkExecutionResult.Completed(result);
    }

    // internal, not private: the legacy-hash tolerance is asserted against the LIVE handler.
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
