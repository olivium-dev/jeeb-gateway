using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Auth.Capabilities;
using JeebGateway.service.ServiceNotification;
using JeebGateway.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace JeebGateway.Controllers;

/// <summary>
/// Stateless relay for the durable settlement outbox owned by
/// unified-payment-gateway. Notification-service deduplicates on the stable
/// notification id; this process owns no callback row, retry, or dedupe memo.
/// </summary>
[ApiController]
[Route("svc-callbacks/settlements")]
public sealed class SettlementEventCallbacksController : ControllerBase
{
    private const string NotificationClient = "ServiceNotificationClient";
    private const string RecordedEvent = "settlement.recorded";
    private const string PaidEvent = "settlement.paid";
    private const string DisputedEvent = "settlement.disputed";
    private const string ResolvedEvent = "settlement.resolved";
    private const string NotificationType = "jeeb.settlement_paid";
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<SettlementEventCallbacksController> _log;
    private readonly IConfiguration _configuration;

    public SettlementEventCallbacksController(
        IHttpClientFactory clients,
        ILogger<SettlementEventCallbacksController> log,
        IConfiguration? configuration = null)
    {
        _clients = clients;
        _log = log;
        _configuration = configuration ?? new ConfigurationBuilder().Build();
    }

    [HttpPost("events")]
    [AllowAnonymous]
    [PublicEndpoint(
        "Generic UPG settlement outbox callback. No service authentication by architecture; "
        + "the configured private owner network limits reachability.")]
    public async Task<IActionResult> Dispatch(
        [FromBody] SettlementEventCallbackV1? callback,
        CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!PrivateCallbackIngressPolicy.IsTrusted(HttpContext, _configuration))
            return Problem(
                "Settlement callbacks are admitted only from the configured private owner network.",
                statusCode: StatusCodes.Status403Forbidden);

        var validation = Validate(callback);
        if (validation is not null) return Problem(validation, statusCode: StatusCodes.Status400BadRequest);
        ArgumentNullException.ThrowIfNull(callback);

        var eventId = callback.EventId.GetValueOrDefault().ToString("D");
        var eventHeader = Request.Headers["X-Event-Id"].ToString();
        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
        if (!string.Equals(eventHeader, eventId, StringComparison.OrdinalIgnoreCase))
            return Problem("X-Event-Id must match eventId.", statusCode: StatusCodes.Status400BadRequest);
        if (idempotencyKey.Length is < 1 or > 200
            || !string.Equals(idempotencyKey, eventId, StringComparison.OrdinalIgnoreCase))
            return Problem("Idempotency-Key must match eventId and be at most 200 characters.",
                statusCode: StatusCodes.Status400BadRequest);

        var correlationId = Request.Headers["X-Correlation-Id"].ToString();
        if (string.IsNullOrWhiteSpace(correlationId)) correlationId = eventId;
        if (correlationId.Length > 200)
            return Problem("X-Correlation-Id must be at most 200 characters.",
                statusCode: StatusCodes.Status400BadRequest);

        // UPG owns the durable read model for every settlement event. The
        // gateway has product notification policy only for paid batches; the
        // other events are intentionally acknowledged without creating local
        // rows, dedupe state, or speculative provider messages.
        if (!string.Equals(callback.EventType, PaidEvent, StringComparison.Ordinal))
        {
            _log.LogInformation(
                "event=settlement.callback_accepted settlement_event_id={EventId} event_type={EventType} correlation_id={CorrelationId}",
                eventId, callback.EventType, correlationId);
            return Accepted(new { eventId = callback.EventId, dispatched = 0 });
        }

        var client = _clients.CreateClient(NotificationClient);
        if (client.BaseAddress is null)
            return Problem("Notification-service is not configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var amount = ReadAmount(callback.Money!.NetAmount);
        var notification = BuildNotification(callback, eventId, amount);
        using var request = new HttpRequestMessage(HttpMethod.Post, "notifications/text_message")
        {
            Content = new StringContent(
                JsonConvert.SerializeObject(notification), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept-Language", "en");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        request.Headers.TryAddWithoutValidation("X-Event-Id", eventHeader);
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "event=settlement.callback_failed settlement_event_id={EventId} upstream_status={Status} correlation_id={CorrelationId}",
                    eventId, (int)response.StatusCode, correlationId);
                return Problem("Settlement notification dispatch failed; retry the outbox event.",
                    statusCode: StatusCodes.Status502BadGateway);
            }

            _log.LogInformation(
                "event=settlement.callback_dispatched settlement_event_id={EventId} provider_id={ProviderId} correlation_id={CorrelationId}",
                eventId, callback.ProviderId, correlationId);
            return Accepted(new { eventId = callback.EventId, dispatched = 1 });
        }
        catch (Exception error) when (error is HttpRequestException
                                      || (error is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _log.LogWarning(error,
                "event=settlement.callback_failed settlement_event_id={EventId} correlation_id={CorrelationId}",
                eventId, correlationId);
            return Problem("Settlement notification dispatch failed; retry the outbox event.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static string? Validate(SettlementEventCallbackV1? callback)
    {
        if (callback?.EventId is null || callback.EventId == Guid.Empty)
            return "eventId must be a non-empty UUID.";
        if (callback.EventType is not (RecordedEvent or PaidEvent or DisputedEvent or ResolvedEvent))
            return "eventType must be a supported settlement outbox event.";
        if (callback.OccurredAt == default)
            return "occurred_at is required.";
        if (callback.Aggregate?.Id is null || callback.Aggregate.Id == Guid.Empty)
            return "aggregate.id must be a non-empty UUID.";
        if (!Bounded(callback.ProviderId, 128)) return "providerId is required and must be at most 128 characters.";
        if (!Bounded(callback.ActorId, 128)) return "actorId is required and must be at most 128 characters.";

        return callback.EventType switch
        {
            PaidEvent => ValidatePaid(callback),
            RecordedEvent => ValidateRecordEvent(callback, "pending", "intent", requireReason: false),
            DisputedEvent => ValidateRecordEvent(callback, "disputed", null, requireReason: true),
            ResolvedEvent => ValidateRecordEvent(callback, "resolved", null, requireReason: true),
            _ => "eventType must be a supported settlement outbox event.",
        };
    }

    private static string? ValidatePaid(SettlementEventCallbackV1 callback)
    {
        if (!string.Equals(callback.Aggregate!.Type, "settlement_batch", StringComparison.Ordinal)
            || callback.Aggregate.Version is < 1)
            return "aggregate must identify a versioned settlement_batch.";
        if (callback.BatchId is null || callback.BatchId == Guid.Empty
            || callback.BatchId != callback.Aggregate.Id)
            return "batch_id must match aggregate.id.";
        if (!string.IsNullOrWhiteSpace(callback.DeliveryId))
            return "delivery_id must be null for settlement.paid.";
        if (callback.Money is null
            || !PositiveAmount(callback.Money.NetAmount))
            return "money.net_amount must be a positive bounded decimal.";
        if (!ValidCurrency(callback.Money.Currency))
            return "money.currency must be a three-letter uppercase code.";
        if (!Bounded(callback.PaymentReference, 256))
            return "payment_reference is required and must be at most 256 characters.";
        if (callback.Period is null
            || !DateOnly.TryParseExact(callback.Period.Start, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(callback.Period.End, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end)
            || end < start)
            return "period must contain an ordered yyyy-MM-dd start and end.";
        return null;
    }

    private static string? ValidateRecordEvent(
        SettlementEventCallbackV1 callback,
        string requiredStatus,
        string? requiredPreviousStatus,
        bool requireReason)
    {
        if (!string.Equals(callback.Aggregate!.Type, "cod_settlement", StringComparison.Ordinal)
            || callback.Aggregate.Version is < 1)
            return "aggregate must identify a versioned cod_settlement.";
        if (!Bounded(callback.DeliveryId, 128))
            return "delivery_id is required and must be at most 128 characters.";
        if (callback.Money is null
            || !PositiveAmount(callback.Money.GrossAmount)
            || !NonNegativeAmount(callback.Money.CommissionAmount)
            || !NonNegativeAmount(callback.Money.NetAmount))
            return "money must contain bounded gross_amount, commission_amount, and net_amount values.";
        if (!ValidCurrency(callback.Money.Currency))
            return "money.currency must be a three-letter uppercase code.";
        if (!string.Equals(callback.Status, requiredStatus, StringComparison.Ordinal))
            return $"status must be {requiredStatus}.";
        if (requiredPreviousStatus is not null
            && !string.Equals(callback.PreviousStatus, requiredPreviousStatus, StringComparison.Ordinal))
            return $"previous_status must be {requiredPreviousStatus}.";
        if (requiredPreviousStatus is null && !Bounded(callback.PreviousStatus, 64))
            return "previous_status is required and must be at most 64 characters.";
        if (requireReason && !Bounded(callback.Reason, 1000))
            return "reason is required and must be at most 1000 characters.";
        if (string.Equals(callback.EventType, RecordedEvent, StringComparison.Ordinal))
        {
            if (!NonNegativeAmount(callback.CommissionRate))
                return "commission_rate must be a bounded decimal.";
            if (callback.SnapshotSequence is null or < 1)
                return "snapshot_sequence must be a positive integer.";
        }
        return null;
    }

    private static bool Bounded(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= max;

    private static bool ValidCurrency(string? currency) =>
        currency is { Length: 3 }
        && currency.All(character => character is >= 'A' and <= 'Z');

    private static bool PositiveAmount(JsonElement? value) =>
        value is not null
        && TryReadAmount(value.Value, out var amount)
        && amount > 0m
        && amount <= 999_999_999.99m;

    private static bool NonNegativeAmount(JsonElement? value) =>
        value is not null
        && TryReadAmount(value.Value, out var amount)
        && amount >= 0m
        && amount <= 999_999_999.99m;

    private static decimal ReadAmount(JsonElement? value) =>
        value is not null && TryReadAmount(value.Value, out var amount) ? amount : 0m;

    private static bool TryReadAmount(JsonElement value, out decimal amount)
    {
        amount = 0m;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(
            value.GetString(),
            NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    private static Text_messageNotification BuildNotification(
        SettlementEventCallbackV1 callback, string eventId, decimal amount)
    {
        var providerId = callback.ProviderId!.Trim();
        var batchId = callback.Aggregate!.Id!.Value.ToString("D");
        var copy = $"Your {callback.Money!.Currency!} {amount.ToString("0.00", CultureInfo.InvariantCulture)} settlement was paid.";
        return new Text_messageNotification
        {
            Sender = "jeeb-gateway",
            Receiver = providerId,
            Notification_id = eventId,
            Title = "Settlement paid",
            Subtitle = "paid",
            Description = copy,
            Media_links = Array.Empty<string>(),
            Notification_type = NotificationType,
            SenderProfilePicture = string.Empty,
            Nickname = "Jeeb",
            AdditionalProperties = new Dictionary<string, object>
            {
                ["metadata"] = new Dictionary<string, object>
                {
                    ["event_type"] = PaidEvent,
                    ["event_id"] = eventId,
                    ["settlement_id"] = batchId,
                    ["deep_link"] = $"jeeb://wallet/settlements/{Uri.EscapeDataString(batchId)}",
                },
            },
            Payload = new Text_messagePayload
            {
                Message_id = eventId,
                Member_id = batchId,
                Delivered_to = new[] { providerId },
                Is_masked = false,
                Message_type = NotificationType,
                Message_text = copy,
                Created_at = callback.OccurredAt,
                SourceMemberID = callback.ActorId!.Trim(),
                SourceSessionID = batchId,
                ChannelID = batchId,
                DestinationUserID = providerId,
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["settlementId"] = batchId,
                    ["eventId"] = eventId,
                    ["currency"] = callback.Money!.Currency!,
                    ["netAmount"] = amount.ToString("0.00", CultureInfo.InvariantCulture),
                    ["version"] = callback.Aggregate.Version!.Value,
                    ["deepLink"] = $"jeeb://wallet/settlements/{Uri.EscapeDataString(batchId)}",
                },
            },
        };
    }
}

public sealed record SettlementEventCallbackV1(
    [property: JsonPropertyName("event_id")] Guid? EventId,
    [property: JsonPropertyName("event_type")] string? EventType,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("aggregate")] SettlementEventAggregateV1? Aggregate,
    [property: JsonPropertyName("provider_id")] string? ProviderId,
    [property: JsonPropertyName("delivery_id")] string? DeliveryId,
    [property: JsonPropertyName("batch_id")] Guid? BatchId,
    [property: JsonPropertyName("money")] SettlementEventMoneyV1? Money,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("previous_status")] string? PreviousStatus,
    [property: JsonPropertyName("commission_rate")] JsonElement? CommissionRate,
    [property: JsonPropertyName("snapshot_sequence")] int? SnapshotSequence,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("payment_reference")] string? PaymentReference,
    [property: JsonPropertyName("period")] SettlementEventPeriodV1? Period,
    [property: JsonPropertyName("actor_id")] string? ActorId);

public sealed record SettlementEventAggregateV1(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("id")] Guid? Id,
    [property: JsonPropertyName("version")] int? Version);

public sealed record SettlementEventMoneyV1(
    [property: JsonPropertyName("gross_amount")] JsonElement? GrossAmount,
    [property: JsonPropertyName("commission_amount")] JsonElement? CommissionAmount,
    [property: JsonPropertyName("net_amount")] JsonElement? NetAmount,
    [property: JsonPropertyName("currency")] string? Currency);

public sealed record SettlementEventPeriodV1(
    [property: JsonPropertyName("start")] string? Start,
    [property: JsonPropertyName("end")] string? End);
