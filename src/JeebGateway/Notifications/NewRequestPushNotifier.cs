using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Push;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Notifications;

/// <summary>
/// BUILD-NEWREQ-PUSH — the request-created → "finding jeebers" push trigger. When a
/// customer creates a delivery request, every available jeeber must be nudged so they
/// can open the request and bid. This is the third missing backend push link (after the
/// chat push <see cref="ChatMessagePushNotifier"/> and the offer push
/// <see cref="OfferPushNotifier"/>): the FCM rails — Flutter topic subscription on
/// device registration (jeebers land on <see cref="JeebPushTopicMap.JeebersTopic"/> via
/// the gateway's role→topic map) and the push relay at :10040 — are already live.
///
/// <para>TOPIC BLAST vs FILTERED FAN-OUT: this fires ONE push to the
/// <c>jeeb_jeebers</c> FCM topic, which reaches ALL subscribed jeebers regardless of
/// geography, active-availability, or tier eligibility. That is the deliberate MVP
/// shape — it needs no per-jeeber recipient resolution and no availability query on the
/// create hot path. The documented FOLLOW-UP is a geo/availability-filtered fan-out:
/// resolve the eligible jeeber set (radius around pickup, on-shift, tier-capable) and
/// send per-user pushes (as <see cref="OfferPushNotifier"/> already does to a single
/// user) instead of a blanket topic broadcast. Until that lands, over-notification is
/// accepted in exchange for zero added create latency.</para>
///
/// <para>Reuses the EXISTING, deployed <see cref="ServicePushNotificationClient"/> (the
/// same typed client + base URL :10040 that <see cref="ChatMessagePushNotifier"/> and
/// <see cref="OfferPushNotifier"/> use) via the hand-written topic seam
/// <c>Send_notification_to_topicAsync</c> — no new push contract is invented.</para>
///
/// <para>DEGRADE-DON'T-FAIL: best-effort. It NEVER throws and never affects the
/// create 201 — every failure (relay blip, timeout) is logged and swallowed, and the
/// FCM round-trip is bounded by a short timeout so a slow/down relay cannot materially
/// delay the 201. Identical contract to the offer/chat push seats.</para>
///
/// <para>FCM DATA SHAPE: the relay at :10040 copies each top-level payload entry
/// (other than <c>title</c>/<c>body</c>, which become the FCM notification block) into
/// the FCM data map, stringifying each value. Routing fields are therefore emitted as
/// FLAT top-level string entries — each lands as its own FCM data key, no nested-JSON
/// hoist on the client. Both the camel (<c>requestId</c>) and snake (<c>request_id</c>)
/// variants of the request id are carried because the mobile deep-link routes
/// <c>/orders/:id</c> from a <c>delivery_id</c> / <c>order_id</c> / <c>requestId</c>
/// fallback and reads whichever it finds.</para>
/// </summary>
public interface INewRequestPushNotifier
{
    /// <summary>
    /// Best-effort: broadcast a "new delivery request" notification to the jeebers
    /// topic so available jeebers can bid. Never throws. A blank
    /// <paramref name="requestId"/> is a no-op.
    /// </summary>
    Task NotifyNewRequestAsync(
        string requestId,
        string? tierId,
        string? description,
        CancellationToken ct);
}

/// <inheritdoc />
public sealed class NewRequestPushNotifier : INewRequestPushNotifier
{
    // Bounds the FCM round-trip so a slow/down relay cannot materially delay the
    // create 201 (the LAN-local push svc is normally <200ms).
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(2);

    // FR: the request description is a preview, not the full order — cap it so a long
    // transcript/compose body does not blow up the FCM notification body.
    private const int BodyPreviewMaxLength = 80;

    private readonly ServicePushNotificationClient _push;
    private readonly JeebGateway.Tiers.ITiersStore _tiers;
    private readonly ILogger<NewRequestPushNotifier> _logger;

    public NewRequestPushNotifier(
        ServicePushNotificationClient push,
        JeebGateway.Tiers.ITiersStore tiers,
        ILogger<NewRequestPushNotifier> logger)
    {
        _push = push;
        _tiers = tiers;
        _logger = logger;
    }

    public async Task NotifyNewRequestAsync(
        string requestId,
        string? tierId,
        string? description,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return;
            }

            // Resolve the human tier LABEL for the body suffix from the gateway's
            // in-process tier catalog (the same store served at GET /v1/tiers). This
            // is a cheap in-memory lookup — no network hop on the create hot path.
            // When the id does not resolve to a catalog row (an opaque UUID, or a
            // code from a divergent taxonomy), the label is null and the suffix is
            // DROPPED — a raw id/UUID is never shown to the jeeber. The raw tierId
            // still travels as its own flat machine field below for client-side
            // filtering, which is unaffected by display resolution.
            var tierLabel = await ResolveTierLabelAsync(tierId, ct);

            var payload = new Dictionary<string, object?>
            {
                ["title"] = "New delivery request",
                ["body"] = BuildBody(description, tierLabel),
                ["type"] = "new_request",
                ["category"] = "delivery",
                // F5 (JEBV4-302) AUDIENCE SCOPE HINT — this new-request broadcast is for
                // JEEBERS ONLY (the reverse-auction "finding jeebers" fan-out). It is
                // published to the jeeb_jeebers FCM topic, but a relay/notification-service
                // defect resolves that topic send to ALL users and persists a receiver row
                // per user, leaking one customer's order text into other customers' inboxes.
                // These FLAT hints let a FIXED relay scope persisted-receiver rows + delivery
                // to the jeeber audience (and drop non-jeeber receivers) without any further
                // gateway change — additive and forward-compatible, exactly like the
                // `priority` hint above; it never degrades today's delivery and lights up as
                // soon as the relay honours it. The authoritative customer-facing fix is the
                // gateway inbox filter in JeebNotificationsInboxController (drops these rows
                // for non-jeeber callers); the relay membership bug is escalated to infra.
                ["audience"] = "jeebers",
                ["audience_role"] = JeebGateway.Users.Roles.Jeeber,
                // High-priority delivery hint. A new-request "finding jeebers" push is
                // time-sensitive (the reverse auction is open only briefly), so it must
                // wake the device rather than be batched. NOTE: this is a FLAT hint the
                // relay copies into the FCM data map; the actual FCM `android.priority` /
                // apns-priority elevation is owned by the push-notification-service
                // (:10040) topic path, which must read this hint to honour it (issue-24 /
                // SVC-6). Emitting it here is additive and forward-compatible — it never
                // degrades delivery and lights up as soon as the relay consumes it.
                ["priority"] = "high",
                // Both camel + snake variants — the mobile deep-link reads either
                // (routes /orders/:id from delivery_id/order_id/requestId fallback).
                ["requestId"] = requestId,
                ["request_id"] = requestId,
                // tierId is carried flat too so the jeeber client can pre-filter/label
                // without a second round-trip. Null when the request has no tier. This
                // is the RAW id (machine field) — distinct from the resolved display
                // label baked into the body above.
                ["tierId"] = tierId,
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PushTimeout);

            // TOPIC BLAST — reaches every subscribed jeeber. See the class doc-comment:
            // the documented next step is a geo/availability-filtered per-user fan-out.
            await _push.Send_notification_to_topicAsync(
                JeebPushTopicMap.JeebersTopic,
                new SentPayloadToTopicRequest { Payload = payload },
                cts.Token);
        }
        catch (Exception ex)
        {
            // DEGRADE-DON'T-FAIL: the request row was already durable and the 201 is
            // committed. A push blip must never surface to the create path.
            _logger.LogWarning(ex,
                "New-request push for request {RequestId} (tier {TierId}) to the jeebers topic "
                + "failed; create stays 201.", requestId, tierId ?? "(none)");
        }
    }

    /// <summary>
    /// Best-effort resolve of a tier id to its human display name via the gateway's
    /// in-process tier catalog. feat/tier-unify-names: the id is first canonicalized
    /// through <see cref="JeebGateway.Tiers.LegacyTierCodes"/>, so legacy codes
    /// (flash/express/standard/on_the_way/eco) resolve to their aliased catalog row's
    /// display name instead of silently dropping the suffix. Returns null when the id
    /// is blank or does not match a catalog row — the caller then DROPS the body
    /// suffix rather than render a raw id/UUID. Never throws: a catalog hiccup
    /// degrades to "no suffix", never to a failed push (the whole notify path is
    /// degrade-don't-fail).
    /// </summary>
    private async Task<string?> ResolveTierLabelAsync(string? tierId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tierId))
        {
            return null;
        }

        try
        {
            var tier = await _tiers.GetAsync(
                JeebGateway.Tiers.LegacyTierCodes.Canonicalize(tierId), ct);
            return string.IsNullOrWhiteSpace(tier?.Name) ? null : tier!.Name.Trim();
        }
        catch (Exception ex)
        {
            // The catalog lookup must never break the push. Fall back to "no suffix".
            _logger.LogDebug(ex,
                "Tier-label resolve failed for tier {TierId}; dropping the body suffix.", tierId);
            return null;
        }
    }

    /// <summary>
    /// Body = the description preview (trimmed to <see cref="BodyPreviewMaxLength"/>
    /// chars) plus a " • {tier}" suffix ONLY when a human tier LABEL was resolved
    /// (<see cref="ResolveTierLabelAsync"/>). When no label is available the suffix
    /// is dropped — a raw tier id/UUID is never surfaced in the notification body.
    /// </summary>
    private static string BuildBody(string? description, string? tierLabel)
    {
        var preview = Trim(description);
        if (string.IsNullOrWhiteSpace(preview))
        {
            preview = "A customer needs a delivery. Tap to view and bid.";
        }

        return string.IsNullOrWhiteSpace(tierLabel)
            ? preview
            : $"{preview} • {tierLabel.Trim()}";
    }

    private static string Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= BodyPreviewMaxLength
            ? trimmed
            : trimmed.Substring(0, BodyPreviewMaxLength);
    }
}
