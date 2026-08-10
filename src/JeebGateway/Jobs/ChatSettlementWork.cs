using System.Text.Json;
using JeebGateway.Conversations;
using JeebGateway.Requests;
using JeebGateway.StateService.Work;
using Microsoft.Extensions.Options;

namespace JeebGateway.Jobs;

public sealed class ChatSettlementExecutionOptions
{
    public const string SectionName = "ChatSettlementExecution";

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan PayloadRetention { get; init; } = TimeSpan.FromDays(30);
    public int MaxAttempts { get; init; } = 100;
}

public sealed record ChatSettlementWorkPayload(
    string RequestId,
    string WinningJeeberId);

public sealed record ChatSettlementWorkReservation(
    Guid WorkItemId,
    string Status,
    string RequestId,
    string WinningJeeberId);

public interface IChatSettlementWorkEnqueuer
{
    Task<ChatSettlementWorkReservation> EnqueueAsync(
        string requestId,
        string winningJeeberId,
        CancellationToken ct);
}

/// <summary>
/// Reserves the post-accept chat handoff in state-service before offer-service
/// is allowed to commit acceptance. The idempotency key is one-to-one with the
/// request, so replicas and client retries converge on the same durable work.
/// </summary>
public sealed class StateChatSettlementWorkEnqueuer(
    IStateWorkItemClient work,
    IOptions<ChatSettlementExecutionOptions> options,
    TimeProvider clock) : IChatSettlementWorkEnqueuer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ChatSettlementWorkReservation> EnqueueAsync(
        string requestId,
        string winningJeeberId,
        CancellationToken ct)
    {
        if (!ChatSettlementWorkPayloadCodec.TryNormalizeUuid(requestId, out var normalizedRequestId)
            || !ChatSettlementWorkPayloadCodec.TryNormalizeUuid(
                winningJeeberId,
                out var normalizedWinningJeeberId))
        {
            throw new ArgumentException(
                "Chat-settlement requestId and winningJeeberId must be canonical UUIDs.");
        }

        var subject = DurableWorkContract.SubjectForRequest(normalizedRequestId);
        var payload = JsonSerializer.SerializeToElement(
            new ChatSettlementWorkPayload(normalizedRequestId, normalizedWinningJeeberId),
            Json);
        var now = clock.GetUtcNow();
        var configured = options.Value;
        StateWorkItem item;

        try
        {
            item = await work.CreateAsync(
                $"chat-settlement:{normalizedRequestId}",
                new StateWorkItemCreate(
                    DurableWorkContract.Application,
                    DurableWorkContract.ChatSettlementKind,
                    subject,
                    payload,
                    DueAt: now,
                    MaxAttempts: Math.Clamp(configured.MaxAttempts, 1, 10_000),
                    RetainPayloadUntil: now + PositiveOrDefault(
                        configured.PayloadRetention,
                        TimeSpan.FromDays(30))),
                ct);
        }
        catch (StateWorkConflictException)
        {
            item = await work.GetLatestAsync(
                       DurableWorkContract.Application,
                       DurableWorkContract.ChatSettlementKind,
                       subject,
                       ct)
                   ?? throw;
        }

        EnsureExpectedReservation(
            item,
            subject,
            normalizedRequestId,
            normalizedWinningJeeberId);
        return new ChatSettlementWorkReservation(
            item.WorkItemId,
            item.Status,
            normalizedRequestId,
            normalizedWinningJeeberId);
    }

    private static void EnsureExpectedReservation(
        StateWorkItem item,
        string expectedSubject,
        string expectedRequestId,
        string expectedWinningJeeberId)
    {
        if (!string.Equals(item.Application, DurableWorkContract.Application, StringComparison.Ordinal)
            || !string.Equals(item.Kind, DurableWorkContract.ChatSettlementKind, StringComparison.Ordinal)
            || !string.Equals(item.SubjectRef, expectedSubject, StringComparison.Ordinal)
            || !ChatSettlementWorkPayloadCodec.TryRead(item.Payload, out var payload)
            || !string.Equals(payload.RequestId, expectedRequestId, StringComparison.Ordinal)
            || !string.Equals(payload.WinningJeeberId, expectedWinningJeeberId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "State-service returned a conflicting chat-settlement reservation.");
        }

        if (item.Status is "failed" or "cancelled")
        {
            throw new InvalidOperationException(
                $"Chat-settlement reservation {item.WorkItemId:D} is terminal ({item.Status}).");
        }
    }

    private static TimeSpan PositiveOrDefault(TimeSpan value, TimeSpan fallback) =>
        value > TimeSpan.Zero ? value : fallback;
}

/// <summary>
/// Reconciles one accepted delivery from the delivery owner into chat-service.
/// The handler is replay-safe: chat settlement is convergent, and the state
/// owner records completion with the claim lease/version CAS.
/// </summary>
public sealed class ChatSettlementWorkHandler(
    IRequestsStore requests,
    IAcceptChatSettler settler,
    IOptions<ChatSettlementExecutionOptions> options,
    TimeProvider clock) : IDurableWorkItemHandler
{
    public string Kind => DurableWorkContract.ChatSettlementKind;

    public async Task<DurableWorkExecutionResult> ExecuteAsync(
        StateWorkItem item,
        CancellationToken ct)
    {
        if (!string.Equals(item.Application, DurableWorkContract.Application, StringComparison.Ordinal)
            || !string.Equals(item.Kind, DurableWorkContract.ChatSettlementKind, StringComparison.Ordinal)
            || !ChatSettlementWorkPayloadCodec.TryRead(item.Payload, out var payload))
        {
            return DurableWorkExecutionResult.Failed(
                "chat-settlement payload is invalid");
        }

        var request = await requests.GetAsync(payload.RequestId, ct);
        if (request is null)
            return WaitOrFail(item, "chat-settlement:delivery-not-visible");

        if (!string.Equals(request.Id, payload.RequestId, StringComparison.OrdinalIgnoreCase))
        {
            return DurableWorkExecutionResult.Failed(
                "chat-settlement delivery owner returned a mismatched request");
        }

        if (RequestStatus.IsPreAcceptance(request.Status)
            || string.Equals(request.Status, RequestStatus.Scheduled, StringComparison.Ordinal))
        {
            return WaitOrFail(item, "chat-settlement:waiting-delivery-accept");
        }

        if (string.IsNullOrWhiteSpace(request.JeeberId))
        {
            if (request.Status is RequestStatus.Expired)
            {
                return DurableWorkExecutionResult.Failed(
                    "chat-settlement delivery expired without an accepted winner");
            }

            return WaitOrFail(item, "chat-settlement:waiting-delivery-winner");
        }

        if (!ChatSettlementWorkPayloadCodec.TryNormalizeUuid(
                request.JeeberId,
                out var ownerWinningJeeberId))
        {
            return DurableWorkExecutionResult.Failed(
                "chat-settlement delivery owner returned an invalid winner");
        }

        if (!string.Equals(
                ownerWinningJeeberId,
                payload.WinningJeeberId,
                StringComparison.Ordinal))
        {
            return DurableWorkExecutionResult.Failed(
                "chat-settlement delivery winner contradicts the reserved winner");
        }

        if (!IsAcceptedOrLater(request.Status))
        {
            return DurableWorkExecutionResult.Failed(
                $"chat-settlement delivery status '{request.Status}' is not reconcilable");
        }

        AcceptChatSettleResult settled;
        try
        {
            settled = await settler.SettleAsync(request, payload.WinningJeeberId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RetryOrFail(
                item,
                $"chat-settlement chat owner call failed ({ex.GetType().Name})");
        }

        if (settled.Status is AcceptChatSettleStatus.Skipped)
            return WaitOrFail(item, "chat-settlement:chat-capability-disabled");
        if (settled.Status is AcceptChatSettleStatus.Unresolved)
            return WaitOrFail(item, "chat-settlement:conversation-not-visible");

        var result = JsonSerializer.SerializeToElement(new
        {
            requestId = payload.RequestId,
            winningJeeberId = payload.WinningJeeberId,
            conversationId = settled.ConversationId,
            alreadySettled = settled.AlreadySettled,
        });
        return DurableWorkExecutionResult.Completed(result);
    }

    private DurableWorkExecutionResult WaitOrFail(StateWorkItem item, string reason)
    {
        var retryAt = NextRetryAt(item);
        return retryAt < Deadline(item)
            ? DurableWorkExecutionResult.Deferred(reason, retryAt)
            : DurableWorkExecutionResult.Failed(
                $"{reason}; bounded reconciliation window expired");
    }

    private DurableWorkExecutionResult RetryOrFail(StateWorkItem item, string error)
    {
        var retryAt = NextRetryAt(item);
        return retryAt < Deadline(item) && item.Attempts < item.MaxAttempts
            ? DurableWorkExecutionResult.Retry(error, retryAt)
            : DurableWorkExecutionResult.Failed(
                $"{error}; bounded reconciliation window expired");
    }

    private DateTimeOffset NextRetryAt(StateWorkItem item)
    {
        var configured = options.Value;
        var initial = PositiveOrDefault(
            configured.InitialRetryDelay,
            TimeSpan.FromMinutes(1));
        var maximum = PositiveOrDefault(
            configured.MaxRetryDelay,
            TimeSpan.FromHours(1));
        if (maximum < initial)
            maximum = initial;

        var delay = initial;
        for (var attempt = 1; attempt < Math.Clamp(item.Attempts, 1, 32); attempt++)
        {
            if (delay >= maximum)
                break;
            delay = delay.Ticks > maximum.Ticks / 2
                ? maximum
                : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maximum.Ticks));
        }
        return clock.GetUtcNow() + delay;
    }

    private DateTimeOffset Deadline(StateWorkItem item) =>
        item.CreatedAt + PositiveOrDefault(options.Value.MaxWait, TimeSpan.FromDays(7));

    private static bool IsAcceptedOrLater(string status) => status is
        RequestStatus.Accepted
        or RequestStatus.PickedUp
        or RequestStatus.HeadingOff
        or RequestStatus.AtDoor
        or RequestStatus.Delivered
        or RequestStatus.Rated
        or RequestStatus.Disputed
        or RequestStatus.CancellationRequested
        or RequestStatus.Cancelled;

    private static TimeSpan PositiveOrDefault(TimeSpan value, TimeSpan fallback) =>
        value > TimeSpan.Zero ? value : fallback;
}

internal static class ChatSettlementWorkPayloadCodec
{
    public static bool TryRead(JsonElement element, out ChatSettlementWorkPayload payload)
    {
        payload = null!;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        string? requestId = null;
        string? winningJeeberId = null;
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            count++;
            if (property.Value.ValueKind != JsonValueKind.String)
                return false;

            switch (property.Name)
            {
                case "requestId" when requestId is null:
                    requestId = property.Value.GetString();
                    break;
                case "winningJeeberId" when winningJeeberId is null:
                    winningJeeberId = property.Value.GetString();
                    break;
                default:
                    return false;
            }
        }

        if (count != 2
            || !TryNormalizeUuid(requestId, out var normalizedRequestId)
            || !TryNormalizeUuid(winningJeeberId, out var normalizedWinningJeeberId))
        {
            return false;
        }

        payload = new ChatSettlementWorkPayload(
            normalizedRequestId,
            normalizedWinningJeeberId);
        return true;
    }

    public static bool TryNormalizeUuid(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null
            || !Guid.TryParseExact(value, "D", out var parsed)
            || !string.Equals(value, parsed.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalized = parsed.ToString("D");
        return true;
    }
}
