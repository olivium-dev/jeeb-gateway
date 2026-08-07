using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Cases;
using JeebGateway.Services.Clients;
using JeebGateway.service.ServiceNotification;
using JeebGateway.service.ServicePushNotification;
using JeebGateway.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
[Route("svc-callbacks/cases")]
public sealed class CaseEventCallbacksController : ControllerBase
{
    private readonly ICaseDeliveryClient _delivery;
    private readonly ServiceNotificationClient _notifications;
    private readonly ServicePushNotificationClient _push;
    private readonly IPushDispatchRecoveryClient _pushRecovery;
    private readonly ILogger<CaseEventCallbacksController> _log;
    private readonly IConfiguration _configuration;

    public CaseEventCallbacksController(ICaseDeliveryClient delivery,
        ServiceNotificationClient notifications, ServicePushNotificationClient push,
        IPushDispatchRecoveryClient pushRecovery,
        ILogger<CaseEventCallbacksController> log,
        IConfiguration? configuration = null)
    {
        _delivery = delivery; _notifications = notifications; _push = push;
        _pushRecovery = pushRecovery; _log = log;
        _configuration = configuration ?? new ConfigurationBuilder().Build();
    }

    [HttpPost("events")]
    [HttpPost("/v1/case-events")]
    [AllowAnonymous]
    [PublicEndpoint("Canonical jeeb-state-service case outbox callback; unauthenticated but admitted only from the configured private owner network.")]
    public async Task<IActionResult> Dispatch([FromBody] GenericCaseCallbackV1? callback, CancellationToken ct)
    {
        var remoteAddress = HttpContext.Connection.RemoteIpAddress;
        if (!PrivateCallbackIngressPolicy.IsTrusted(HttpContext, _configuration))
        {
            CaseTelemetry.CallbackDispatches.Add(1,
                new("event_type", callback?.EventType ?? "unknown"), new("outcome", "non_loopback_rejected"));
            _log.LogWarning(
                "event=case.callback_rejected reason=non_loopback remote_ip={RemoteIp} correlation_id={CorrelationId}",
                remoteAddress?.ToString() ?? "missing", HttpContext.TraceIdentifier);
            return Problem(
                "Case callbacks are admitted only from the configured private owner network.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (callback is null || callback.EventId == Guid.Empty || callback.Case is null
            || string.IsNullOrWhiteSpace(callback.EventType))
            return Problem("The canonical case callback envelope is required.", statusCode: 400);

        var eventHeader = Request.Headers["X-Event-Id"].ToString();
        if (!string.IsNullOrWhiteSpace(eventHeader)
            && (!Guid.TryParse(eventHeader, out var headerId) || headerId != callback.EventId))
            return Problem("X-Event-Id must match eventId.", statusCode: 400);

        using var activity = CaseTelemetry.Activities.StartActivity("case.callback.dispatch", ActivityKind.Consumer);
        activity?.SetTag("case.id", callback.Case.CaseId);
        activity?.SetTag("case.event_id", callback.EventId);
        activity?.SetTag("case.event_type", callback.EventType);

        try
        {
            if (IsInternalNote(callback))
                return Accepted(new { eventId = callback.EventId, dispatched = 0 });

            var recipients = await ResolveRecipientsAsync(callback, ct);
            var notificationType = NotificationType(callback);
            var (title, message) = Copy(callback);
            var degradedPushes = 0;
            foreach (var recipient in recipients)
            {
                var notificationId = DeterministicNotificationId(callback.EventId, recipient.UserId);
                var deepLink = callback.Case.Kind == GenericCaseKinds.Dispute
                    ? $"jeeb://disputes/{callback.Case.CaseId:D}"
                    : $"jeeb://support/tickets/{callback.Case.CaseId:D}";
                var downstreamIdempotencyKey = notificationId.ToString("D");
                await _notifications.Create_text_message_notifications_text_message_postAsync(
                    "en",
                    new Text_messageNotification
                    {
                        Sender = "jeeb-gateway", Receiver = recipient.UserId,
                        Notification_id = notificationId.ToString("D"), Title = title,
                        Subtitle = callback.Case.Status, Description = message,
                        Media_links = Array.Empty<string>(), Notification_type = notificationType,
                        SenderProfilePicture = string.Empty, Nickname = "Jeeb Support",
                        AdditionalProperties = new Dictionary<string, object>
                        {
                            ["metadata"] = new Dictionary<string, object>
                            {
                                ["event_type"] = notificationType,
                                ["case_id"] = callback.Case.CaseId.ToString("D"),
                                ["case_kind"] = callback.Case.Kind,
                                ["event_id"] = callback.EventId.ToString("D"),
                                ["deep_link"] = deepLink,
                            },
                        },
                        Payload = new Text_messagePayload
                        {
                            Message_id = callback.EventId.ToString("D"),
                            Member_id = callback.Case.CaseId.ToString("D"),
                            Delivered_to = new[] { recipient.UserId }, Is_masked = false,
                            Message_type = notificationType, Message_text = message,
                            Created_at = callback.OccurredAt, SourceMemberID = callback.Actor.Ref,
                            SourceSessionID = callback.Case.CaseId.ToString("D"),
                            ChannelID = callback.Case.CaseId.ToString("D"),
                            DestinationUserID = recipient.UserId,
                            AdditionalProperties = new Dictionary<string, object>
                            {
                                ["caseId"] = callback.Case.CaseId.ToString("D"),
                                ["caseKind"] = callback.Case.Kind,
                                ["businessType"] = notificationType,
                                ["eventType"] = callback.EventType,
                                ["deepLink"] = deepLink,
                            },
                        },
                    }, ct);

                try
                {
                    await _push.Send_notification_to_userAsync(recipient.UserId,
                        new SentPayloadToUserRequest
                        {
                            AdditionalProperties = new Dictionary<string, object>
                            {
                                ["idempotency_key"] = downstreamIdempotencyKey,
                            },
                            Payload = new Dictionary<string, object?>
                            {
                                ["notificationId"] = notificationId.ToString("D"),
                                ["idempotencyKey"] = downstreamIdempotencyKey,
                                ["type"] = notificationType,
                                ["eventType"] = callback.EventType,
                                ["caseId"] = callback.Case.CaseId.ToString("D"),
                                ["caseKind"] = callback.Case.Kind,
                                ["status"] = callback.Case.Status,
                                ["recipientRole"] = recipient.Role,
                                ["deepLink"] = deepLink,
                                ["title"] = title,
                                ["body"] = message,
                                ["data"] = callback.Data,
                            },
                        }, ct);
                }
                catch (Exception pushError) when (pushError is not OperationCanceledException)
                {
                    var dispatch = await ResolvePushOutcomeAsync(
                        downstreamIdempotencyKey, callback, recipient, pushError, ct);
                    if (dispatch.State == "failed")
                    {
                        degradedPushes++;
                        _log.LogWarning(
                            "event=case.callback_push_degraded case_event_id={EventId} recipient_id={RecipientId} "
                            + "push_key={PushKey} push_state={PushState} correlation_id={CorrelationId}",
                            callback.EventId, recipient.UserId, downstreamIdempotencyKey, dispatch.State,
                            Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier);
                    }
                }
            }

            var dispatched = recipients.Count;
            CaseTelemetry.CallbackDispatches.Add(1,
                new("event_type", callback.EventType),
                new("outcome", degradedPushes == 0 ? "success" : "degraded"));
            _log.LogInformation(
                "event=case.callback_dispatched case_id={CaseId} case_event_id={EventId} "
                + "case_event_type={EventType} recipient_count={RecipientCount} correlation_id={CorrelationId}",
                callback.Case.CaseId, callback.EventId, callback.EventType, dispatched,
                Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier);
            return Accepted(new { eventId = callback.EventId, dispatched, degradedPushes });
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        {
            CaseTelemetry.CallbackDispatches.Add(1,
                new("event_type", callback.EventType), new("outcome", "upstream_timeout"));
            _log.LogError(error,
                "event=case.callback_failed reason=upstream_timeout case_id={CaseId} "
                + "case_event_id={EventId} correlation_id={CorrelationId}",
                callback.Case.CaseId, callback.EventId,
                Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier);
            return Problem("Case notification dispatch timed out; retry the outbox event.", statusCode: 502);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            CaseTelemetry.CallbackDispatches.Add(1,
                new("event_type", callback.EventType), new("outcome", "failed"));
            _log.LogError(error,
                "event=case.callback_failed case_id={CaseId} case_event_id={EventId} correlation_id={CorrelationId}",
                callback.Case.CaseId, callback.EventId,
                Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier);
            return Problem("Case notification dispatch failed; retry the outbox event.", statusCode: 502);
        }
    }

    private async Task<PushDispatchStatusV1> ResolvePushOutcomeAsync(
        string idempotencyKey, GenericCaseCallbackV1 callback, CaseRecipient recipient,
        Exception pushError, CancellationToken ct)
    {
        PushDispatchStatusV1 dispatch;
        try
        {
            dispatch = await _pushRecovery.GetAsync(idempotencyKey, staleAfterSeconds: 300, ct);
        }
        catch (Exception recoveryError) when (recoveryError is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Push outcome for case event {callback.EventId:D} and recipient {recipient.UserId} is undeterminable.",
                new AggregateException(pushError, recoveryError));
        }

        if (dispatch.State is "succeeded" or "failed")
            return dispatch;

        throw new InvalidOperationException(
            $"Push outcome for case event {callback.EventId:D} and recipient {recipient.UserId} remains {dispatch.State}.",
            pushError);
    }

    private async Task<IReadOnlyList<CaseRecipient>> ResolveRecipientsAsync(
        GenericCaseCallbackV1 callback, CancellationToken ct)
    {
        if (callback.Case.Kind != GenericCaseKinds.Dispute)
            return new[] { new CaseRecipient(callback.Case.RequesterRef, "requester") };
        if (callback.Case.Subject.Type != "delivery")
            throw new InvalidOperationException("A dispute callback must reference a delivery subject.");
        var context = await _delivery.GetDeliveryCaseContextAsync(callback.Case.Subject.Ref, ct)
            ?? throw new InvalidOperationException("The callback delivery no longer exists.");
        var recipients = new List<CaseRecipient>
        {
            new(context.PartyIds.ClientId, "client"),
        };
        if (!string.IsNullOrWhiteSpace(context.PartyIds.CourierId))
            recipients.Add(new CaseRecipient(context.PartyIds.CourierId, "jeeber"));
        return recipients.DistinctBy(item => item.UserId).ToArray();
    }

    private static bool IsInternalNote(GenericCaseCallbackV1 callback) =>
        callback.EventType == "case.message_added"
        && callback.Data.ValueKind == System.Text.Json.JsonValueKind.Object
        && callback.Data.TryGetProperty("messageType", out var type)
        && type.GetString() == "internal_note";

    private static string NotificationType(GenericCaseCallbackV1 callback)
    {
        var prefix = callback.Case.Kind == GenericCaseKinds.Dispute ? "jeeb.dispute" : "jeeb.support";
        if (callback.EventType == "case.created") return prefix + ".created";
        if (callback.EventType == "case.message_added") return prefix + ".reply";
        return prefix + "." + callback.Case.Status;
    }

    private static (string Title, string Body) Copy(GenericCaseCallbackV1 callback) =>
        callback.Case.Status switch
        {
            GenericCaseStatuses.Fixed => ("Case marked fixed", "Jeeb support marked your case fixed."),
            GenericCaseStatuses.Closed => ("Case closed", "Your case has been closed."),
            GenericCaseStatuses.Pending => ("Dispute updated", "Your delivery dispute has been updated."),
            _ when callback.EventType == "case.message_added" => ("New case reply", "Your case has a new reply."),
            _ => ("Case updated", "Your support case has been updated."),
        };

    internal static Guid DeterministicNotificationId(Guid eventId, string recipientUserId)
    {
        var material = Encoding.UTF8.GetBytes($"{eventId:D}\n{recipientUserId.Trim()}");
        var bytes = SHA256.HashData(material)[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    private sealed record CaseRecipient(string UserId, string Role);

}
