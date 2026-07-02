using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Notifications;

/// <summary>
/// BUILD-OFFER-PUSH — the offer-submitted → push-notification trigger. When a
/// jeeber submits a bid on a request, the request's CUSTOMER (the requester) must
/// receive an FCM push so they can open the auction and compare offers. This is the
/// second missing backend link (the first being the chat push,
/// <see cref="ChatMessagePushNotifier"/>): all other rails — Flutter FCM token
/// registration via the gateway's <c>PUT /api/PushNotification/register</c>, the push
/// service at :10040 with real FCM creds — are already live.
///
/// <para>Reuses the EXISTING, deployed <see cref="ServicePushNotificationClient"/>
/// (the same typed client + base URL :10040 that <c>PushNotificationController</c>
/// and <see cref="ChatMessagePushNotifier"/> use) and its
/// <see cref="SentPayloadToUserRequest"/> contract — no new push contract is invented.</para>
///
/// <para>DEGRADE-DON'T-FAIL: this is best-effort. It NEVER throws and never affects
/// the offer-submit 201 — every failure (push-service blip, timeout) is logged and
/// swallowed. The FCM round-trip is bounded by a short timeout so a slow/down push
/// service cannot materially delay the 201. This is the identical contract the
/// AdvancePhase seat and the realtime new-offer fan-out already follow.</para>
///
/// <para>FCM DATA SHAPE: the push service at :10040 copies each top-level payload
/// entry (other than <c>title</c>/<c>body</c>, which become the FCM notification
/// block) into the FCM data map, stringifying each value. So the routing fields are
/// emitted as FLAT top-level string entries — each lands as its own FCM data key and
/// the Flutter client needs no nested-JSON hoist. Both the camel (<c>requestId</c>)
/// and snake (<c>request_id</c>) variants of the request id are carried because the
/// mobile deep-link routes <c>/orders/:id</c> from a <c>delivery_id</c> /
/// <c>order_id</c> / <c>requestId</c> fallback and reads whichever it finds.</para>
/// </summary>
public interface IOfferPushNotifier
{
    /// <summary>
    /// Best-effort: push a "new offer" notification to the request's customer
    /// (<paramref name="clientId"/>). Never throws.
    /// </summary>
    Task NotifyNewOfferAsync(
        string clientId,
        string requestId,
        string offerId,
        decimal fee,
        CancellationToken ct);
}

/// <inheritdoc />
public sealed class OfferPushNotifier : IOfferPushNotifier
{
    // Bounds the FCM round-trip so a slow/down push service cannot materially delay
    // the offer-submit 201 (the LAN-local push svc is normally <200ms).
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(2);

    private readonly ServicePushNotificationClient _push;
    private readonly ILogger<OfferPushNotifier> _logger;

    public OfferPushNotifier(
        ServicePushNotificationClient push,
        ILogger<OfferPushNotifier> logger)
    {
        _push = push;
        _logger = logger;
    }

    public async Task NotifyNewOfferAsync(
        string clientId,
        string requestId,
        string offerId,
        decimal fee,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(requestId))
            {
                return;
            }

            var payload = new Dictionary<string, object?>
            {
                ["title"] = "New offer on your request",
                ["body"] = BuildBody(fee),
                ["type"] = "offer",
                ["category"] = "delivery",
                // Both camel + snake variants — the mobile deep-link reads either
                // (routes /orders/:id from delivery_id/order_id/requestId fallback).
                ["requestId"] = requestId,
                ["request_id"] = requestId,
                ["offerId"] = offerId,
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PushTimeout);

            await _push.Send_notification_to_userAsync(
                clientId,
                new SentPayloadToUserRequest { Payload = payload },
                cts.Token);
        }
        catch (Exception ex)
        {
            // DEGRADE-DON'T-FAIL: the offer was already durable and the 201 is committed.
            _logger.LogWarning(ex,
                "Offer push for request {RequestId} (offer {OfferId}) to client {ClientId} failed; "
                + "offer submit stays 201.", requestId, offerId, clientId);
        }
    }

    private static string BuildBody(decimal fee)
        => fee > 0m
            ? $"You received a new offer for ${fee.ToString("0.##", CultureInfo.InvariantCulture)}. Tap to review."
            : "You received a new offer. Tap to review.";
}
