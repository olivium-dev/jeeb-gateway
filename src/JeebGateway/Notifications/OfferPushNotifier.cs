using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Requests;
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
        CancellationToken ct,
        OfferReceivedNotificationContext? context = null);

    Task NotifyNewOfferAsync(
        OfferReceivedNotificationContext context,
        string clientId,
        string requestId,
        string offerId,
        decimal fee,
        CancellationToken ct);

    /// <summary>
    /// sprint-009 Lane E — best-effort: push the "your offer was accepted" notification
    /// to the WINNING jeeber (<paramref name="winnerJeeberId"/>) after the client closes
    /// the auction. Renders the existing <c>jeeb.offer_accepted</c> catalog template and
    /// carries a <c>jeeb://offers/{offerId}</c> deep link. Never throws; a blank recipient
    /// is a no-op.
    /// </summary>
    Task NotifyOfferAcceptedAsync(
        string winnerJeeberId,
        string requestId,
        string offerId,
        CancellationToken ct);

    /// <summary>
    /// sprint-009 Lane E — best-effort: push the "your offer wasn't selected" notification
    /// to a LOSING bidder (<paramref name="loserJeeberId"/>) for their now-rejected offer
    /// (<paramref name="offerId"/>). Renders the <c>jeeb.offer_rejected</c> catalog template
    /// and carries a <c>jeeb://offers/{offerId}</c> deep link. Never throws; a blank
    /// recipient is a no-op.
    /// </summary>
    Task NotifyOfferLostAsync(
        string loserJeeberId,
        string requestId,
        string offerId,
        CancellationToken ct);

    /// <summary>c1/W3 (CONTRACT §3) — best-effort: their offer was withdrawn because the wallet no
    /// longer covers the 10% fee. Wire <c>type=offer_withdrawn_insufficient_balance</c>, <c>jeeb://wallet</c>.</summary>
    /// <remarks>Both emitters (sweeper forced withdraw, accept auto-withdraw) call THIS method so
    /// their payloads cannot drift. Never throws; blank recipient = no-op.</remarks>
    Task NotifyOfferWithdrawnInsufficientBalanceAsync(
        string jeeberId,
        string requestId,
        string offerId,
        CancellationToken ct);
}

/// <inheritdoc />
public sealed class OfferPushNotifier : IOfferPushNotifier
{
    // Bounds EACH recipient's FCM round-trip.
    //
    // Was 2s, on the stated assumption that "the LAN-local push svc is normally <200ms".
    // JEBV4-345 measured that assumption false for the chat sibling and raised ITS copy to
    // 10s — and this one, the fan-out's, the expiry notifier's and the callback seat's were
    // left behind, because each seat owned a private copy of the number. Re-measured
    // 2026-07-28: <200ms is what a push with NO device row costs (404 in ~14ms); a push to a
    // registered recipient costs 2.53-3.97s across 10 consecutive calls, so 10 out of 10
    // healthy sends blew this cap. The full distribution and the reasoning for 10s live on
    // PushSendBudget; there is now ONE value and no seat-local copy to drift.
    //
    // Raising it is only safe because the offer seats no longer await this on the request
    // path — see IDetachedPushDispatcher and the call sites in RequestOffersController /
    // OffersController / JeebOffersController.
    private static readonly TimeSpan PushTimeout = PushSendBudget.PerRecipient;

    /// <summary>
    /// b02 step 6b (owner ruling D3 = retire) — the loser-bidder copy, relocated here VERBATIM
    /// from the retired <c>jeeb.offer_rejected</c> catalog entry.
    ///
    /// <para><b>Why it moved instead of dying with the taxonomy.</b> The catalog entry was retired
    /// because the notification centre has no route for that type (405, where every served type
    /// answers 422), so no inbox row of it can exist. But this PUSH never needed the centre — it
    /// renders copy locally and dispatches through the push microservice. Retiring an unroutable
    /// notification-centre taxonomy must not silently degrade a live user-facing push into the
    /// catalog's product-neutral fallback ("You have a new notification for jeeb.offer_rejected").
    /// So the copy lives next to its only caller.</para>
    ///
    /// <para>EN only: the loser push has always rendered with the catalog's DEFAULT locale
    /// (<c>Render(templateKey)</c> was called with no locale), so English is what shipped and
    /// English is what still ships. This is deliberately behaviour-preserving, NOT an opinion that
    /// the notification should be unlocalized — localizing it is a separate, visible change.</para>
    /// </summary>
    internal static readonly NotificationTemplate OfferLostTemplate = new(
        "Offer Not Selected",
        "Your offer wasn't selected this time. Keep an eye out for new delivery requests.");

    /// <summary>
    /// Deep-link template for the loser push, relocated from the retired
    /// <see cref="NotificationDeepLinkResolver"/> entry for the same reason as
    /// <see cref="OfferLostTemplate"/>. A losing bidder lands on the (now terminal) offer.
    /// </summary>
    internal static string OfferLostDeepLink(string offerId) => $"jeeb://offers/{offerId}";

    private readonly ServicePushNotificationClient _push;
    private readonly INotificationRecordWriter _recordWriter;
    private readonly IGenericEventDispatcher _events;
    private readonly Func<string, CancellationToken, Task<DeliveryRequest?>> _getRequest;
    private readonly ILogger<OfferPushNotifier> _logger;

    public OfferPushNotifier(
        ServicePushNotificationClient push,
        INotificationRecordWriter recordWriter,
        IGenericEventDispatcher events,
        IRequestsStore requests,
        ILogger<OfferPushNotifier> logger)
        : this(push, recordWriter, events, requests.GetAsync, logger)
    {
    }

    public OfferPushNotifier(
        ServicePushNotificationClient push,
        ILogger<OfferPushNotifier> logger)
        : this(
            push,
            DisabledNotificationRecordWriter.Instance,
            NullGenericEventDispatcher.Instance,
            (_, _) => Task.FromResult<DeliveryRequest?>(null),
            logger)
    {
    }

    internal OfferPushNotifier(
        ServicePushNotificationClient push,
        INotificationRecordWriter recordWriter,
        Func<string, CancellationToken, Task<DeliveryRequest?>> getRequest,
        ILogger<OfferPushNotifier> logger)
        : this(push, recordWriter, NullGenericEventDispatcher.Instance, getRequest, logger)
    {
    }

    internal OfferPushNotifier(
        ServicePushNotificationClient push,
        INotificationRecordWriter recordWriter,
        IGenericEventDispatcher events,
        Func<string, CancellationToken, Task<DeliveryRequest?>> getRequest,
        ILogger<OfferPushNotifier> logger)
    {
        _push = push;
        _recordWriter = recordWriter;
        _events = events;
        _getRequest = getRequest;
        _logger = logger;
    }

    public Task NotifyNewOfferAsync(
        OfferReceivedNotificationContext context,
        string clientId,
        string requestId,
        string offerId,
        decimal fee,
        CancellationToken ct)
        => NotifyNewOfferAsync(clientId, requestId, offerId, fee, ct, context);

    public async Task NotifyNewOfferAsync(
        string clientId,
        string requestId,
        string offerId,
        decimal fee,
        CancellationToken ct,
        OfferReceivedNotificationContext? context = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(requestId))
            {
                return;
            }

            var notificationCorrelationId = NotificationCorrelationId.Create(
                OfferReceivedNotificationRecord.TemplateKey,
                clientId,
                offerId);
            var copy = RenderNewOffer(fee);
            NotificationRecordWriteOutcome? handover = null;

            if (context is not null)
            {
                var record = new OfferReceivedNotificationRecord
                {
                    Sender = "jeeb-gateway",
                    Receiver = clientId,
                    NotificationCorrelationId = notificationCorrelationId,
                    Title = copy.Title,
                    Description = copy.Body,
                    Payload = new OfferReceivedNotificationPayload
                    {
                        UserId = clientId,
                        OfferId = offerId,
                        ClientName = string.Empty,
                        PickupLocation = context.PickupAddress ?? string.Empty,
                        DeliveryLocation = context.DropoffAddress ?? string.Empty,
                        // Jeeb owns one money fact on an offer. The shared schema
                        // has two money slots, so both echo the same decimal verbatim.
                        OfferAmount = fee,
                        DeliveryFee = fee,
                        EstimatedDuration = context.EtaMinutes.ToString(CultureInfo.InvariantCulture),
                        CreatedAt = context.CreatedAt,
                    },
                };
                NotificationDurableWriteTelemetry.FieldAbsent.Add(
                    1,
                    new("field", "client_name"),
                    new("templateKey", OfferReceivedNotificationRecord.TemplateKey));
                handover = await TryWriteOfferReceivedAsync(record, offerId, ct);
            }

            if (UpstreamOwnsPush(handover))
            {
                return;
            }

            var payload = new Dictionary<string, object?>
            {
                ["title"] = copy.Title,
                ["body"] = copy.Body,
                ["type"] = "offer",
                ["category"] = "delivery",
                // Both camel + snake variants — the mobile deep-link reads either
                // (routes /orders/:id from delivery_id/order_id/requestId fallback).
                ["requestId"] = requestId,
                ["request_id"] = requestId,
                ["offerId"] = offerId,
                ["notificationId"] = notificationCorrelationId,
                ["notification_id"] = notificationCorrelationId,
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PushTimeout);

            var accepted = await _push.Send_notification_to_userAsync(
                clientId,
                new SentPayloadToUserRequest { Payload = payload },
                cts.Token);

            // Log the push service's OWN accounting, not a bare "ACCEPTED". See
            // PushAcceptance: a 201 means FCM took at least one of this user's device rows,
            // and most of those rows are dead. It is not delivery.
            _logger.LogInformation(
                "Offer push accepted for request {RequestId} (offer {OfferId}) to client "
                + "{ClientId}: {Accounting}.",
                requestId, offerId, clientId, PushAcceptance.Describe(accepted));
        }
        catch (ApiException ex) when (IsDirectDispatchDisabled(ex))
        {
            // Expected steady state: notification-service is the sole push producer, so the
            // guard's 503 is not a failure worth a WARN + stack on every offer.
            _logger.LogDebug(
                "Offer push direct dispatch for request {RequestId} (offer {OfferId}) skipped: "
                + "guard armed, notification-service is the sole producer.", requestId, offerId);
        }
        catch (Exception ex)
        {
            // DEGRADE-DON'T-FAIL: the offer was already durable and the 201 is committed.
            _logger.LogWarning(ex,
                "Offer push for request {RequestId} (offer {OfferId}) to client {ClientId} failed; "
                + "offer submit stays 201.", requestId, offerId, clientId);
        }
    }

    /// <summary>
    /// GW-OFFER-503 — true for the synthetic 503 <see cref="JeebGateway.Services.Clients.GatewayDirectPushDispatchGuardHandler"/>
    /// returns while the gateway is deliberately NOT a push producer. That is the expected steady
    /// state (notification-service owns these pushes off the durable record write), so it is logged
    /// at Debug instead of WARN+stack per offer. The guard is permanent; real failures still take
    /// the WARN path unchanged.
    /// </summary>
    private static bool IsDirectDispatchDisabled(ApiException ex)
        => ex.StatusCode == (int)System.Net.HttpStatusCode.ServiceUnavailable
           && ex.Response?.Contains(
               JeebGateway.Services.Clients.GatewayDirectPushDispatchGuardHandler.DisabledProblemCode,
               StringComparison.Ordinal) == true;

    private static NotificationTemplate RenderNewOffer(decimal fee) => new(
        "New offer on your request",
        fee > 0m
            ? $"You received a new offer for ${fee.ToString("0.##", CultureInfo.InvariantCulture)}. Tap to review."
            : "You received a new offer. Tap to review.");

    // sprint-009 Lane E — the winner/loser accept-lifecycle pushes. Both mirror the
    // NotifyNewOfferAsync contract exactly (PushSendBudget CTS, flat top-level payload, never-throws)
    // and differ only in recipient, template, and the `type` discriminator the mobile
    // client routes on (offer_accepted vs offer_lost). The title/body come from the
    // gateway-owned JeebNotificationCatalog (jeeb.offer_accepted / jeeb.offer_rejected)
    // and a jeeb://offers/{offerId} deep link is carried flat so the client can navigate.
    public async Task NotifyOfferAcceptedAsync(
        string winnerJeeberId, string requestId, string offerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(winnerJeeberId) || string.IsNullOrWhiteSpace(offerId))
        {
            return;
        }

        var templateKey = OfferAcceptedNotificationRecord.TemplateKey;
        var template = JeebNotificationCatalog.Render(templateKey);
        var notificationCorrelationId = NotificationCorrelationId.Create(
            templateKey,
            winnerJeeberId,
            offerId);
        NotificationRecordWriteOutcome? handover = null;

        try
        {
            var request = await _getRequest(requestId, ct);
            if (request?.AcceptedFee is not null)
            {
                var record = new OfferAcceptedNotificationRecord
                {
                    Sender = "jeeb-gateway",
                    Receiver = winnerJeeberId,
                    NotificationCorrelationId = notificationCorrelationId,
                    Title = template.Title,
                    Description = template.Body,
                    Payload = new OfferAcceptedNotificationPayload
                    {
                        UserId = request.ClientId,
                        OfferId = offerId,
                        ClientName = string.Empty,
                        PickupLocation = request.PickupAddress ?? string.Empty,
                        DeliveryLocation = request.DropoffAddress ?? string.Empty,
                        AcceptedAmount = request.AcceptedFee.Value,
                        JeeberId = winnerJeeberId,
                        CreatedAt = request.AcceptedAt ?? request.CreatedAt,
                    },
                };
                NotificationDurableWriteTelemetry.FieldAbsent.Add(
                    1,
                    new("field", "client_name"),
                    new("templateKey", templateKey));
                handover = await TryWriteOfferAcceptedAsync(record, offerId, ct);
            }
            else
            {
                NotificationDurableWriteTelemetry.Skipped.Add(
                    1,
                    new("type", templateKey),
                    new("reason", "accepted_amount_absent"),
                    new("entityId", offerId));
                _logger.LogWarning(
                    "event={event} type={type} reason={reason} recipientId={recipientId} " +
                    "entityId={entityId} ncid={ncid}",
                    "notif.durable_write.skipped",
                    templateKey,
                    "accepted_amount_absent",
                    winnerJeeberId,
                    offerId,
                    notificationCorrelationId);
            }
        }
        catch (Exception ex)
        {
            LogDurableWriteFailure(
                ex,
                templateKey,
                winnerJeeberId,
                offerId,
                notificationCorrelationId);
        }

        if (UpstreamOwnsPush(handover))
        {
            return;
        }

        await SendLifecycleAsync(
            winnerJeeberId,
            requestId,
            offerId,
            templateKey,
            type: "offer_accepted",
            ct,
            template,
            notificationCorrelationId);
    }

    /// <summary>
    /// SINGLE PRODUCER — true once the durable write handed this event to notification-service,
    /// which produces the push off that same POST, so the gateway must not send it again.
    ///
    /// <para>2026-08-14: one offer produced TWO FCM cards 26 ms apart because both legs ran
    /// unconditionally. Only <see cref="JeebGateway.Services.Clients.GatewayDirectPushDispatchGuardHandler"/>'s
    /// 503 was suppressing the second, and <c>PushDispatchMode=upstream-authority</c> FORCES that
    /// guard off for an unrelated seat — so the guard flag cannot arbitrate this path.</para>
    ///
    /// <para>Owner is notification-service in every rung: these two types are the only offer types
    /// with a centre route, and one upstream POST yields both the row and the push, so a
    /// gateway-produced offer push would mean no inbox row. The second send is never ISSUED —
    /// this is a producer removal, not a dedupe window.</para>
    ///
    /// <para><see cref="NotificationRecordWriteClassification.Unproven"/> is deliberately NOT a
    /// fallback: the POST went out and the read-back could not prove the row absent, so sending
    /// anyway re-opens the duplicate window. Null (no write attempted, or the writer threw) and
    /// <see cref="NotificationRecordWriteClassification.Disabled"/> leave no upstream producer,
    /// so the direct client still sends — matching the sibling seats, which also fall back only
    /// when the hand-over seam declined outright.</para>
    /// </summary>
    private static bool UpstreamOwnsPush(NotificationRecordWriteOutcome? handover)
        => handover is not null
           && handover.Classification is not (NotificationRecordWriteClassification.Disabled
               or NotificationRecordWriteClassification.SkippedSilent);

    // b02 step 6b — the template key is GONE from JeebNotificationCatalog (retired: the centre
    // 405s it, so no inbox row of that type can exist). Copy and deep link are therefore passed
    // in explicitly. There is no durable-write attempt here and never was: this is a push-only
    // notification, which is exactly why retiring the taxonomy costs the user nothing.
    public Task NotifyOfferLostAsync(
        string loserJeeberId, string requestId, string offerId, CancellationToken ct)
        => SendLifecycleAsync(
            loserJeeberId, requestId, offerId,
            templateKey: RetiredOfferLostTemplateKey, type: "offer_lost", ct,
            renderedTemplate: OfferLostTemplate,
            deepLinkOverride: OfferLostDeepLink(offerId));

    /// <summary>
    /// The retired key, kept ONLY as the log/telemetry label for this push so operator dashboards
    /// and log greps that key on it keep working. It is intentionally NOT in
    /// <see cref="JeebNotificationCatalog"/> and must not be re-added there.
    /// </summary>
    internal const string RetiredOfferLostTemplateKey = "jeeb.offer_rejected";

    /// <summary>CONTRACT §3 wire discriminator — mobile routes on THIS, never on `category`.</summary>
    internal const string OfferWithdrawnInsufficientBalanceType =
        "offer_withdrawn_insufficient_balance";

    /// <summary>Opaque generic-event envelope; deliberately NOT a catalog TemplateKey
    /// (this push has no notification-centre route, exactly like offer_lost).</summary>
    internal const string OfferWithdrawnInsufficientBalanceEventType =
        "jeeb.offer_withdrawn_insufficient_balance";

    /// <summary>CONTRACT §3: the top-up destination, not the (now dead) offer.</summary>
    internal const string WalletDeepLink = "jeeb://wallet";

    /// <summary>CONTRACT §5 rows P-1/P-2, EN — these BYTES are the contract: an older build shows them
    /// verbatim, so they must match the mobile l10n copy. Gateway-rendered, no locale; AR is mobile-side.</summary>
    internal static readonly NotificationTemplate OfferWithdrawnInsufficientBalanceTemplate = new(
        "Offer withdrawn — top up to keep bidding",
        "Your winning offer was withdrawn because your wallet no longer covers the 10% platform fee. Tap to top up.");

    // Reuses the lifecycle send helper verbatim: same CTS budget, same flat payload, same
    // never-throws contract — only recipient, copy, type, deep link and category differ.
    public Task NotifyOfferWithdrawnInsufficientBalanceAsync(
        string jeeberId, string requestId, string offerId, CancellationToken ct)
        => SendLifecycleAsync(
            jeeberId, requestId, offerId,
            templateKey: OfferWithdrawnInsufficientBalanceEventType,
            type: OfferWithdrawnInsufficientBalanceType, ct,
            renderedTemplate: OfferWithdrawnInsufficientBalanceTemplate,
            deepLinkOverride: WalletDeepLink,
            genericEventType: OfferWithdrawnInsufficientBalanceEventType,
            genericEventCategory: PushSilencePolicy.CategoryWallet);

    private async Task SendLifecycleAsync(
        string recipientId,
        string requestId,
        string offerId,
        string templateKey,
        string type,
        CancellationToken ct,
        NotificationTemplate? renderedTemplate = null,
        string? notificationCorrelationId = null,
        string? deepLinkOverride = null,
        string? genericEventType = null,
        string? genericEventCategory = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(recipientId) || string.IsNullOrWhiteSpace(offerId))
            {
                return;
            }

            var template = renderedTemplate ?? JeebNotificationCatalog.Render(templateKey);

            // deepLinkOverride carries the link for a notification whose taxonomy is no longer in
            // the resolver (b02 step 6b retired jeeb.offer_rejected). Without it the resolver would
            // return the inbox root for that type and the loser push would lose its destination.
            var deepLink = deepLinkOverride
                           ?? NotificationDeepLinkResolver.Resolve(templateKey, offerId);

            var payload = new Dictionary<string, object?>
            {
                ["title"] = template.Title,
                ["body"] = template.Body,
                ["type"] = type,
                ["category"] = "delivery",
                // Both camel + snake variants — the mobile deep-link reads either.
                ["requestId"] = requestId,
                ["request_id"] = requestId,
                ["offerId"] = offerId,
                // Ready-to-navigate deep link (jeeb://offers/{offerId}); flat so the client
                // needs no nested-JSON hoist. Mirrors the inbox deepLink contract.
                ["deepLink"] = deepLink,
            };
            if (notificationCorrelationId is not null)
            {
                payload["notificationId"] = notificationCorrelationId;
                payload["notification_id"] = notificationCorrelationId;
            }

            // Types with no notification-centre route (offer_lost; the c1 wallet withdraw) take
            // the generic seam — the only way they survive the gateway ceasing to be a producer.
            var eventType = genericEventType
                ?? (string.Equals(templateKey, RetiredOfferLostTemplateKey, StringComparison.Ordinal)
                    ? JeebGenericEventTypes.OfferLostEventType
                    : null);
            if (eventType is not null)
            {
                var handover = await _events.DispatchAsync(
                    eventType,
                    recipientId,
                    offerId,
                    template.Title,
                    template.Body,
                    payload.ToDictionary(
                        kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty, StringComparer.Ordinal),
                    // GenericEventDispatcher.BuildRecord overwrites data["category"] with this, so
                    // the upstream route carries `wallet` while the direct fallback keeps `delivery`.
                    genericEventCategory ?? PushSilencePolicy.CategoryOfferLost,
                    ct);

                if (handover.Classification
                    != GenericEventDispatchClassification.SkippedDirectDispatchArmed)
                {
                    return;
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PushTimeout);

            var accepted = await _push.Send_notification_to_userAsync(
                recipientId,
                new SentPayloadToUserRequest { Payload = payload },
                cts.Token);

            // The push service's own device-row accounting, not a bare "ACCEPTED".
            _logger.LogInformation(
                "Offer {Type} push accepted for request {RequestId} (offer {OfferId}) to "
                + "{RecipientId}: {Accounting}.",
                type, requestId, offerId, recipientId, PushAcceptance.Describe(accepted));
        }
        catch (ApiException ex) when (IsDirectDispatchDisabled(ex))
        {
            _logger.LogDebug(
                "Offer {Type} direct dispatch for request {RequestId} (offer {OfferId}) skipped: "
                + "guard armed, notification-service is the sole producer.", type, requestId, offerId);
        }
        catch (Exception ex)
        {
            // DEGRADE-DON'T-FAIL: the accept saga already committed and the 200 is emitted.
            _logger.LogWarning(ex,
                "Offer {Type} push for request {RequestId} (offer {OfferId}) to {RecipientId} failed; "
                + "accept stays 200.", type, requestId, offerId, recipientId);
        }
    }

    // Null return = the write never landed an event upstream, so the direct client is still
    // the only producer left. See UpstreamOwnsPush.
    private async Task<NotificationRecordWriteOutcome?> TryWriteOfferReceivedAsync(
        OfferReceivedNotificationRecord record,
        string offerId,
        CancellationToken requestToken)
    {
        try
        {
            return await _recordWriter.WriteOfferReceivedAsync(record, requestToken);
        }
        catch (Exception ex)
        {
            LogDurableWriteFailure(
                ex,
                OfferReceivedNotificationRecord.TemplateKey,
                record.Receiver,
                offerId,
                record.NotificationCorrelationId);
            return null;
        }
    }

    private async Task<NotificationRecordWriteOutcome?> TryWriteOfferAcceptedAsync(
        OfferAcceptedNotificationRecord record,
        string offerId,
        CancellationToken requestToken)
    {
        try
        {
            return await _recordWriter.WriteOfferAcceptedAsync(record, requestToken);
        }
        catch (Exception ex)
        {
            LogDurableWriteFailure(
                ex,
                OfferAcceptedNotificationRecord.TemplateKey,
                record.Receiver,
                offerId,
                record.NotificationCorrelationId);
            return null;
        }
    }

    private void LogDurableWriteFailure(
        Exception exception,
        string templateKey,
        string recipientId,
        string entityId,
        string notificationCorrelationId)
    {
        NotificationDurableWriteTelemetry.Outcomes.Add(
            1,
            new("classification", "unproven"),
            new("templateKey", templateKey));
        _logger.LogError(
            exception,
            "event={event} classification={classification} templateKey={templateKey} " +
            "recipientId={recipientId} entityId={entityId} ncid={ncid} upstreamStatus={upstreamStatus}",
            "notif.durable_write.failed",
            "unproven",
            templateKey,
            recipientId,
            entityId,
            notificationCorrelationId,
            null);
    }

    private sealed class DisabledNotificationRecordWriter : INotificationRecordWriter
    {
        internal static readonly DisabledNotificationRecordWriter Instance = new();

        public Task<NotificationRecordWriteOutcome> WriteOfferReceivedAsync(
            OfferReceivedNotificationRecord record,
            CancellationToken requestToken)
            => Task.FromResult(
                new NotificationRecordWriteOutcome(
                    NotificationRecordWriteClassification.Disabled,
                    null));

        public Task<NotificationRecordWriteOutcome> WriteOfferAcceptedAsync(
            OfferAcceptedNotificationRecord record,
            CancellationToken requestToken)
            => Disabled();

        // b02 step 6a — this notifier only ever writes the two offer types; the other six exist on
        // the interface for the service-callback seat. They are implemented as Disabled rather than
        // throwing so that substituting this stand-in can never turn a missing write into a crash.
        public Task<NotificationRecordWriteOutcome> WriteDeliveryStatusUpdatedAsync(
            DeliveryStatusUpdatedNotificationRecord record,
            CancellationToken requestToken)
            => Disabled();

        public Task<NotificationRecordWriteOutcome> WriteSettlementPaidAsync(
            SettlementPaidNotificationRecord record,
            CancellationToken requestToken)
            => Disabled();

        public Task<NotificationRecordWriteOutcome> WriteKycApprovedAsync(
            KycApprovedNotificationRecord record,
            CancellationToken requestToken)
            => Disabled();

        public Task<NotificationRecordWriteOutcome> WriteKycRejectedAsync(
            KycRejectedNotificationRecord record,
            CancellationToken requestToken)
            => Disabled();

        public Task<NotificationRecordWriteOutcome> WriteDisputeResolvedAsync(
            DisputeResolvedNotificationRecord record,
            CancellationToken requestToken)
            => Disabled();

        public Task<NotificationRecordWriteOutcome> WriteRatingAutoRevealedAsync(
            RatingAutoRevealedNotificationRecord record,
            CancellationToken requestToken)
            => Disabled();

        private static Task<NotificationRecordWriteOutcome> Disabled()
            => Task.FromResult(
                new NotificationRecordWriteOutcome(
                    NotificationRecordWriteClassification.Disabled,
                    null));
    }
}
