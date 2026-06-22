using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Push;

/// <summary>
/// SEND-ON-EVENT (iter6) — the single best-effort seam that turns a real
/// in-app event (new chat message, new offer, offer accepted, delivery status
/// change) into an FCM push to the correct RECIPIENT.
///
/// <para>
/// It forwards to the generic push-notification service (<c>:10040</c>) through
/// the already-registered NSwag <see cref="ServicePushNotificationClient"/> —
/// the SAME proven path the manual/admin <c>PushNotificationController</c>
/// endpoints use (token register → backend send with the real <c>jeeb-5a293</c>
/// credentials → push lands in the shade when backgrounded). The ONLY thing this
/// adds is auto-firing that path from real event handlers, with the recipient
/// resolved per event (never the actor themselves).
/// </para>
///
/// <para>
/// PAYLOAD SHAPE. The push service reads <c>payload["title"]</c> /
/// <c>payload["body"]</c> for the visible notification and stringifies the WHOLE
/// payload into the FCM <c>data</c> map (<c>data = {k: str(v) for k,v in payload}</c>).
/// So the payload here is a FLAT string→string dictionary: <c>title</c>, <c>body</c>,
/// plus small routing keys (<c>type</c>, <c>conversationId</c>, <c>requestId</c>,
/// <c>deliveryId</c>, …). Keep it small — it round-trips into FCM data verbatim.
/// </para>
///
/// <para>
/// DEGRADE-DON'T-FAIL. Every call is wrapped: a missing recipient, a 404
/// (recipient has no registered device — the common case for a user who never
/// logged in on a push build), an unreachable push service, or any other fault
/// is logged and swallowed. A push hiccup must NEVER turn a successful chat
/// send / offer / accept / status transition into a 5xx — identical to the
/// existing realtime fan-out contract.
/// </para>
/// </summary>
public interface IEventPushNotifier
{
    /// <summary>
    /// Best-effort: push <paramref name="title"/>/<paramref name="body"/> (+ small
    /// <paramref name="data"/> routing keys) to <paramref name="recipientUserId"/>'s
    /// registered device(s). No-op (logged) when the recipient is empty/blank.
    /// Never throws.
    /// </summary>
    Task NotifyUserAsync(
        string? recipientUserId,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data,
        CancellationToken ct);
}

/// <inheritdoc />
public sealed class EventPushNotifier : IEventPushNotifier
{
    private readonly ServicePushNotificationClient _push;
    private readonly ILogger<EventPushNotifier> _logger;

    public EventPushNotifier(
        ServicePushNotificationClient push,
        ILogger<EventPushNotifier> logger)
    {
        _push = push;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyUserAsync(
        string? recipientUserId,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipientUserId))
        {
            // No counterparty resolved (e.g. pre-accept transition with no jeeber
            // bound yet, or a self-only conversation) — nothing to push.
            _logger.LogDebug("Event push skipped: no recipient user id (title={Title}).", title);
            return;
        }

        // Flat string payload. title/body drive the visible notification; the rest
        // ride along in FCM data for deep-link routing on tap.
        var payload = new Dictionary<string, object>
        {
            ["title"] = title,
            ["body"] = body,
        };

        if (data is not null)
        {
            foreach (var kv in data)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value is null)
                {
                    continue;
                }
                // Never let a stray data key clobber the visible title/body.
                if (kv.Key is "title" or "body")
                {
                    continue;
                }
                payload[kv.Key] = kv.Value;
            }
        }

        try
        {
            var request = new SentPayloadToUserRequest { Payload = payload };
            await _push.Send_notification_to_userAsync(recipientUserId, request, ct);
            _logger.LogInformation(
                "Event push sent to user {RecipientId} (title={Title}).",
                recipientUserId, title);
        }
        catch (Exception ex)
        {
            // Best-effort, exactly like the realtime fan-out: the event already
            // committed. A 404 (recipient has no registered device) is the common
            // benign case; any transport/credential fault is also swallowed here.
            _logger.LogWarning(
                ex,
                "Event push to user {RecipientId} failed (title={Title}); swallowing (best-effort).",
                recipientUserId, title);
        }
    }
}
