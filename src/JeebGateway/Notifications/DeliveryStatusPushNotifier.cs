using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Notifications;

/// <summary>
/// The delivery-status → push trigger, on STACK B (the push microservice at :10040).
///
/// <para><b>Why this class exists.</b> Delivery status changes were composed inside
/// <c>DeliveriesController</c> and handed to <c>IPushNotificationService</c> —
/// the in-gateway push stack. That stack cannot deliver, for three independent
/// reasons, each alone sufficient:</para>
/// <list type="number">
///   <item>The only <c>IPushTransport</c> registration is
///   <c>InMemoryPushTransport</c>, which enqueues to an in-process
///   <c>ConcurrentQueue</c> and returns — and is then counted <c>Delivered</c>.
///   As of b05/GW1 W0.6 there is no configuration that can change this: the
///   in-gateway direct-to-provider transport and its switch were DELETED by owner
///   ruling (the gateway must never speak to a push provider itself), so this is
///   not fixable by configuration and there is no flag left to flip.</item>
///   <item><c>IDeviceTokenStore.RegisterAsync</c> has ZERO production callers, so
///   even with a real transport the send short-circuits on <c>NoDevices</c>.
///   Flipping the flag would not work either.</item>
///   <item>The payload carried no <c>type</c>/<c>category</c> discriminator and a
///   camelCase-only <c>deliveryId</c>, neither of which the mobile client reads —
///   so it would have been dropped receiver-side even if it had arrived.</item>
/// </list>
///
/// <para>All three are addressed here by composing the SAME typed
/// <see cref="ServicePushNotificationClient"/> that chat, offers, new-request and
/// expiry already use — the path whose arrival is PROVEN on physical hardware —
/// and by emitting the id/discriminator contract the mobile handler actually
/// parses (<c>type=delivery</c> plus a snake_case <c>delivery_id</c>).</para>
///
/// <para><b>Architecture rule (locked).</b> The gateway NEVER sends push itself; it
/// always goes through the push microservice. This class is the delivery-status
/// leg of that rule.</para>
///
/// <para><b>DEGRADE-DON'T-FAIL.</b> Never throws. A status transition has already
/// committed by the time this runs; a push-service blip must never surface to the
/// caller or roll anything back. Every failure is logged and swallowed, and each
/// recipient's send is independently bounded so one dead token cannot consume the
/// next recipient's budget.</para>
/// </summary>
public interface IDeliveryStatusPushNotifier
{
    /// <summary>
    /// Best-effort: push a delivery status change to each recipient. Never throws.
    /// </summary>
    Task NotifyAsync(DeliveryStatusPushNotification notification, CancellationToken ct);
}

/// <summary>
/// One delivery-status push, fully resolved by the caller. Every value is captured
/// eagerly at the call site because the caller mutates/returns the delivery row the
/// instant the notify call returns — a lazy read here would race the transition.
/// </summary>
/// <param name="DeliveryId">The delivery id. In this system delivery id == request id.</param>
/// <param name="RequestId">The request id this delivery materialised from.</param>
/// <param name="PreviousStatus">Status before the transition.</param>
/// <param name="Status">Status after the transition (the PUSH-facing vocabulary token,
/// which may differ from the gateway-local read-model value).</param>
/// <param name="Recipients">User ids to notify. Deduplicated and blank-filtered here.</param>
/// <param name="Title">Shade title.</param>
/// <param name="Body">Shade body.</param>
/// <param name="GpsTrackingActive">Whether live GPS tracking is running for this delivery.</param>
/// <param name="CancelledBy">Who cancelled, for the cancellation leg. Empty otherwise.</param>
/// <param name="PendingApproval">Whether the cancellation awaits admin approval.</param>
public sealed record DeliveryStatusPushNotification(
    string DeliveryId,
    string RequestId,
    string PreviousStatus,
    string Status,
    IReadOnlyList<string> Recipients,
    string Title,
    string Body,
    bool GpsTrackingActive = false,
    string? CancelledBy = null,
    bool? PendingApproval = null);

/// <inheritdoc />
public sealed class DeliveryStatusPushNotifier : IDeliveryStatusPushNotifier
{
    /// <summary>
    /// Bounds EACH recipient's send. Deliberately per-recipient, not per-fan-out: a
    /// single shared deadline lets the first recipient's ~3s FCM round trip leave the
    /// second with nothing, which is a silent total loss of push for that user.
    ///
    /// <para><b>PUBLIC on purpose.</b> The per-recipient budget only holds if the token the
    /// CALLER passes in outlives the whole fan-out — <see cref="NotifyAsync"/> links each
    /// recipient's timeout to it, so a caller deadline shorter than
    /// <c>recipients x this</c> re-imposes exactly the shared deadline this field exists to
    /// avoid, and the recipient that loses its push is always the LAST one composed. Both
    /// delivery-status call sites compose <c>[ClientId, JeeberId]</c> in that order, so the
    /// truncated audience is always the JEEBER. Detached callers must size their ceiling
    /// from this value: see <c>DeliveriesController.DetachedPushBudgetFor</c>.</para>
    /// </summary>
    /// <remarks>
    /// Now sourced from <see cref="PushSendBudget.PerRecipient"/> (10s) rather than a
    /// seat-local 8s. 8s did clear the measured healthy call (2.53-3.97s across 10 consecutive
    /// sends on 2026-07-28) so this seat was not broken — but the cost is linear in the
    /// recipient's device-row count and those rows only ever accumulate, so a seat-local
    /// number is a seat-local drift risk. <c>DeliveriesController.DetachedPushBudgetFor</c>
    /// derives from this field, so its ceiling scales with it automatically.
    /// </remarks>
    public static readonly TimeSpan PerRecipientTimeout = PushSendBudget.PerRecipient;

    private readonly ServicePushNotificationClient _push;
    private readonly ILogger<DeliveryStatusPushNotifier> _logger;

    public DeliveryStatusPushNotifier(
        ServicePushNotificationClient push,
        ILogger<DeliveryStatusPushNotifier> logger)
    {
        _push = push;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyAsync(DeliveryStatusPushNotification n, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(n.DeliveryId))
            {
                _logger.LogWarning(
                    "Delivery-status push SKIPPED: no delivery id on the {Status} transition, "
                    + "so no recipient surface could key off it.", n.Status);
                return;
            }

            var recipients = n.Recipients
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (recipients.Length == 0)
            {
                _logger.LogInformation(
                    "Delivery-status push: delivery {DeliveryId} ({PreviousStatus} -> {Status}) "
                    + "has no resolvable recipient; no push emitted.",
                    n.DeliveryId, n.PreviousStatus, n.Status);
                return;
            }

            var payload = BuildPayload(n);

            foreach (var recipient in recipients)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(PerRecipientTimeout);

                try
                {
                    var accepted = await _push.Send_notification_to_userAsync(
                        recipient,
                        new SentPayloadToUserRequest { Payload = payload },
                        cts.Token);

                    // Log the SUCCESS, not only the failure. An empty log window is
                    // otherwise ambiguous between "sent fine" and "never attempted" —
                    // and every prior investigation of this path read that window and
                    // concluded the wrong one. Carry the push service's OWN device-row
                    // accounting rather than the bare word "ACCEPTED", which reads as
                    // delivery and is not (see PushAcceptance).
                    _logger.LogInformation(
                        "Delivery-status push accepted by push service for recipient "
                        + "{RecipientUserId} on delivery {DeliveryId} ({PreviousStatus} -> {Status}): "
                        + "{Accounting}.",
                        recipient, n.DeliveryId, n.PreviousStatus, n.Status,
                        PushAcceptance.Describe(accepted));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Delivery-status push to recipient {RecipientUserId} on delivery "
                        + "{DeliveryId} ({PreviousStatus} -> {Status}) failed; the transition stands.",
                        recipient, n.DeliveryId, n.PreviousStatus, n.Status);
                }
            }
        }
        catch (Exception ex)
        {
            // DEGRADE-DON'T-FAIL: the transition already committed upstream.
            _logger.LogWarning(ex,
                "Delivery-status push fan-out for delivery {DeliveryId} failed; the transition stands.",
                n.DeliveryId);
        }
    }

    /// <summary>
    /// Builds the FLAT string payload the relay copies into the FCM <c>data</c> map.
    ///
    /// <para>FCM <c>data</c> must be a flat string→string map. The relay at :10040 copies
    /// each top-level payload entry other than <c>title</c>/<c>body</c> (which become the
    /// notification block) into <c>data</c>, stringifying each value — so a NESTED
    /// dictionary arrives client-side as one key holding stringified pseudo-JSON. Every
    /// routing field is therefore emitted flat.</para>
    ///
    /// <para><b>The three keys this whole change turns on.</b> The old in-gateway payload
    /// had none of them:</para>
    /// <list type="bullet">
    ///   <item><c>type=delivery</c> — without a discriminator
    ///   <c>NotificationCategory.fromData</c> resolves <c>other</c> and the mobile
    ///   handler drops the message before any refresh signal.</item>
    ///   <item><c>delivery_id</c> — the mobile id alias list is
    ///   <c>delivery_id|order_id|requestId|request_id</c>. camelCase <c>deliveryId</c>
    ///   is NOT in it, so the id-guarded refresh branch returned early. The camelCase
    ///   key is kept alongside for the older APKs that read it, and both request-id
    ///   spellings travel too, so a future payload trim cannot silently re-inert
    ///   this path.</item>
    ///   <item><c>category=delivery</c> — the legacy discriminator, kept so a client
    ///   reading either key resolves the same category.</item>
    /// </list>
    ///
    /// <para><b>Visibility.</b> No <c>silent</c> key. The relay treats <c>silent</c> as a
    /// transport switch: absent/falsy sends a notification block AND data (so the OS posts
    /// a shade entry even for a force-quit app); truthy sends data only, which posts nothing
    /// and which iOS drops outright for a force-quit app. A delivery status change is
    /// exactly the event the user wants to see, and the owner ruling is that the shade is
    /// acceptable — so the key is omitted deliberately.</para>
    /// </summary>
    internal static Dictionary<string, object?> BuildPayload(DeliveryStatusPushNotification n)
    {
        var payload = new Dictionary<string, object?>
        {
            ["title"] = n.Title,
            ["body"] = n.Body,
            ["type"] = "delivery",
            ["category"] = "delivery",
            // A status transition is time-sensitive — the customer is watching the
            // chip advance. FLAT hint the relay copies into the FCM data map; the
            // actual android.priority elevation is owned by the push service.
            ["priority"] = "high",
            ["delivery_id"] = n.DeliveryId,
            ["deliveryId"] = n.DeliveryId,
            ["requestId"] = n.RequestId,
            ["request_id"] = n.RequestId,
            ["status"] = n.Status,
            ["previousStatus"] = n.PreviousStatus,
            ["previous_status"] = n.PreviousStatus,
            ["gpsTrackingActive"] = n.GpsTrackingActive ? "true" : "false",
        };

        if (!string.IsNullOrEmpty(n.CancelledBy))
        {
            payload["cancelledBy"] = n.CancelledBy;
        }

        if (n.PendingApproval.HasValue)
        {
            payload["pendingApproval"] = n.PendingApproval.Value ? "true" : "false";
        }

        return payload;
    }
}
